using System.Text.Json;
using Gomail.Core;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Util.Store;
using System.Collections.Concurrent;

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
    Task<UserCredential> ReauthorizeAsync(MailAccount account, CancellationToken cancellationToken = default);
}

public sealed class GmailAuthenticationService : IGmailAuthenticationService
{
    private readonly GmailAuthOptions options;
    private readonly ISecretStore secrets;
    private readonly ConcurrentDictionary<Guid, UserCredential> credentials = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> accountGates = new();

    public GmailAuthenticationService(GmailAuthOptions options, ISecretStore secrets)
    {
        this.options = options;
        this.secrets = secrets;
    }

    public bool IsConfigured => options.IsConfigured;

    public Task<UserCredential> GetCredentialAsync(MailAccount account, CancellationToken cancellationToken = default) =>
        GetCredentialCoreAsync(account, false, cancellationToken);

    public Task<UserCredential> ReauthorizeAsync(MailAccount account, CancellationToken cancellationToken = default) =>
        GetCredentialCoreAsync(account, true, cancellationToken);

    private async Task<UserCredential> GetCredentialCoreAsync(MailAccount account, bool forceConsent, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
        {
            throw new MailProviderException("Google OAuth is not included in this Inboxwell build.");
        }

        var accountGate = accountGates.GetOrAdd(account.Id, static _ => new SemaphoreSlim(1, 1));
        await accountGate.WaitAsync(cancellationToken);
        try
        {
            var dataStore = new SecretGoogleDataStore(secrets, account.SecretKey("google"));
            var userKey = account.Id.ToString("N");
            TokenResponse? previousToken = null;
            if (forceConsent)
            {
                credentials.TryRemove(account.Id, out _);
                previousToken = await dataStore.GetAsync<TokenResponse>(userKey);
                await dataStore.DeleteAsync<TokenResponse>(userKey);
            }
            if (credentials.TryGetValue(account.Id, out var cached))
            {
                return cached;
            }

            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets { ClientId = options.ClientId, ClientSecret = options.ClientSecret },
                Scopes = new[] { GmailService.Scope.GmailModify, GmailService.Scope.GmailSettingsBasic, "openid", "email", "profile" },
                DataStore = dataStore,
                // A desktop mail client must let the user choose a different Google
                // identity when more than one mailbox is being connected.
                Prompt = forceConsent ? "consent select_account" : "select_account"
            });
            var receiver = new LocalServerCodeReceiver(
                "<!doctype html><html><head><meta charset=\"utf-8\"><title>Inboxwell connected</title></head>" +
                "<body style=\"font:16px system-ui;padding:40px;color:#172033\">" +
                "<h2>Inboxwell is connected</h2><p>You can close this tab and return to Inboxwell.</p>" +
                "<script>window.setTimeout(() => window.close(), 900);</script></body></html>");
            UserCredential credential;
            try
            {
                credential = await new AuthorizationCodeInstalledApp(flow, receiver)
                    .AuthorizeAsync(userKey, cancellationToken);
            }
            catch
            {
                if (previousToken is not null) await dataStore.StoreAsync(userKey, previousToken);
                throw;
            }
            credentials[account.Id] = credential;
            return credential;
        }
        finally
        {
            accountGate.Release();
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
