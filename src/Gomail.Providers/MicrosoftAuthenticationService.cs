using Gomail.Core;
using Microsoft.Identity.Client;

namespace Gomail.Providers;

public sealed record MicrosoftAuthOptions(string ClientId)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !ClientId.Contains("YOUR_", StringComparison.OrdinalIgnoreCase);
}

public interface IMicrosoftAuthenticationService
{
    bool IsConfigured { get; }
    Task<string> GetAccessTokenAsync(MailAccount account, bool interactive = false, CancellationToken cancellationToken = default);
}

public sealed class MicrosoftAuthenticationService : IMicrosoftAuthenticationService
{
    private static readonly string[] Scopes = { "User.Read", "Mail.ReadWrite", "Mail.Send", "offline_access" };
    private readonly MicrosoftAuthOptions options;
    private readonly ISecretStore secrets;

    public MicrosoftAuthenticationService(MicrosoftAuthOptions options, ISecretStore secrets)
    {
        this.options = options;
        this.secrets = secrets;
    }

    public bool IsConfigured => options.IsConfigured;

    public async Task<string> GetAccessTokenAsync(MailAccount account, bool interactive = false, CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
        {
            throw new MailProviderException("Microsoft OAuth is not included in this Inboxwell build.");
        }

        var application = PublicClientApplicationBuilder
            .Create(options.ClientId)
            .WithAuthority(AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount)
            .WithDefaultRedirectUri()
            .Build();
        BindCache(application.UserTokenCache, account.SecretKey("msal-cache"));

        AuthenticationResult result;
        var cachedAccounts = await application.GetAccountsAsync();
        var cached = cachedAccounts.FirstOrDefault(candidate =>
            candidate.Username.Equals(account.Email, StringComparison.OrdinalIgnoreCase)) ?? cachedAccounts.FirstOrDefault();
        if (!interactive && cached is not null)
        {
            try
            {
                result = await application.AcquireTokenSilent(Scopes, cached).ExecuteAsync(cancellationToken);
                return result.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // Continue with the system browser.
            }
        }

        if (!interactive)
        {
            throw new MailProviderException("Microsoft 365 sign-in is required. Open Manage accounts and reconnect this mailbox.");
        }

        result = await application.AcquireTokenInteractive(Scopes)
            .WithUseEmbeddedWebView(false)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }

    private void BindCache(ITokenCache cache, string key)
    {
        cache.SetBeforeAccess(args =>
        {
            var encoded = secrets.GetAsync(key).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(encoded))
            {
                args.TokenCache.DeserializeMsalV3(Convert.FromBase64String(encoded));
            }
        });
        cache.SetAfterAccess(args =>
        {
            if (args.HasStateChanged)
            {
                var encoded = Convert.ToBase64String(args.TokenCache.SerializeMsalV3());
                secrets.SetAsync(key, encoded).GetAwaiter().GetResult();
            }
        });
    }
}
