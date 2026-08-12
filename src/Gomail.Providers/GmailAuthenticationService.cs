using System.Text.Json;
using Gomail.Core;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Util.Store;

namespace Gomail.Providers;

public sealed record GmailAuthOptions(string ClientId, string ClientSecret)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !ClientId.Contains("YOUR_", StringComparison.OrdinalIgnoreCase);
}

public interface IGmailAuthenticationService
{
    bool IsConfigured { get; }
    Task<UserCredential> GetCredentialAsync(MailAccount account, CancellationToken cancellationToken = default);
}

public sealed class GmailAuthenticationService : IGmailAuthenticationService
{
    private readonly GmailAuthOptions options;
    private readonly ISecretStore secrets;
    private readonly Dictionary<Guid, UserCredential> credentials = new();
    private readonly SemaphoreSlim gate = new(1, 1);

    public GmailAuthenticationService(GmailAuthOptions options, ISecretStore secrets)
    {
        this.options = options;
        this.secrets = secrets;
    }

    public bool IsConfigured => options.IsConfigured;

    public async Task<UserCredential> GetCredentialAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
        {
            throw new MailProviderException("Google OAuth is not included in this Inboxwell build.");
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (credentials.TryGetValue(account.Id, out var cached))
            {
                return cached;
            }

            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                new ClientSecrets { ClientId = options.ClientId, ClientSecret = options.ClientSecret },
                new[] { GmailService.Scope.GmailModify },
                account.Id.ToString("N"),
                cancellationToken,
                new SecretGoogleDataStore(secrets, account.SecretKey("google")));
            credentials[account.Id] = credential;
            return credential;
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed class SecretGoogleDataStore : IDataStore
    {
        private readonly ISecretStore secrets;
        private readonly string prefix;

        public SecretGoogleDataStore(ISecretStore secrets, string prefix)
        {
            this.secrets = secrets;
            this.prefix = prefix;
        }

        public Task StoreAsync<T>(string key, T value) =>
            secrets.SetAsync(StorageKey(key), JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        public async Task<T?> GetAsync<T>(string key)
        {
            var json = await secrets.GetAsync(StorageKey(key));
            return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        public Task DeleteAsync<T>(string key) => secrets.RemoveAsync(StorageKey(key));

        public async Task ClearAsync()
        {
            await secrets.RemoveAsync(StorageKey("token"));
        }

        private string StorageKey(string key) => $"{prefix}:{key}";
    }
}
