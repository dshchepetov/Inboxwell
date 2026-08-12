namespace Gomail.Core;

public sealed class MailProviderRegistry : IMailProviderRegistry
{
    private readonly IReadOnlyDictionary<ProviderKind, IMailProvider> providers;

    public MailProviderRegistry(IEnumerable<IMailProvider> providers)
    {
        this.providers = providers.ToDictionary(static provider => provider.Kind);
    }

    public IReadOnlyCollection<IMailProvider> All => providers.Values.ToArray();

    public IMailProvider Get(ProviderKind kind) => providers.TryGetValue(kind, out var provider)
        ? provider
        : throw new KeyNotFoundException($"No provider is registered for {kind}.");
}
