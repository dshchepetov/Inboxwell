using System.Security.Cryptography;
using Gomail.Core;
using Windows.Security.Credentials;

namespace Gomail_App.Services;

public sealed class WindowsCredentialSecretStore : ISecretStore
{
    private const string UserName = "gomail";
    private readonly PasswordVault vault = new();

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var credential = vault.Retrieve(key, UserName);
            credential.RetrievePassword();
            return Task.FromResult<string?>(credential.Password);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RemoveAsync(key, cancellationToken);
        vault.Add(new PasswordCredential(key, UserName, value));
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            vault.Remove(vault.Retrieve(key, UserName));
        }
        catch
        {
            // PasswordVault throws when an entry does not exist.
        }
        return Task.CompletedTask;
    }

    public async Task<string> GetOrCreateKeyAsync(string key, int bytes = 32, CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(key, cancellationToken);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var created = Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));
        await SetAsync(key, created, cancellationToken);
        return created;
    }
}
