using Gomail.Core;
using Gomail.Data;
using Gomail.Providers;
using Gomail_App.Services;
using Gomail_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Gomail_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private readonly ServiceProvider services;

    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    public static IServiceProvider Services => ((App)Current).services;

    public static Task Initialization { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();

        var localFolder = ApplicationData.Current.LocalFolder.Path;
        var settings = ApplicationData.Current.LocalSettings.Values;
        var microsoftClientId = ReadSetting(settings, "microsoftClientId", "INBOXWELL_MICROSOFT_CLIENT_ID", "GOMAIL_MICROSOFT_CLIENT_ID");
        var gmailClientId = ReadSetting(settings, "gmailClientId", "INBOXWELL_GMAIL_CLIENT_ID", "GOMAIL_GMAIL_CLIENT_ID");
        var gmailClientSecret = ReadSetting(settings, "gmailClientSecret", "INBOXWELL_GMAIL_CLIENT_SECRET", "GOMAIL_GMAIL_CLIENT_SECRET");

        var collection = new ServiceCollection();
        collection.AddSingleton<ISecretStore, WindowsCredentialSecretStore>();
        collection.AddSingleton<IMailStore>(_ => new SqliteMailStore(Path.Combine(localFolder, "mail.db")));
        collection.AddSingleton<IConnectivity, NetworkConnectivity>();
        collection.AddSingleton<IClock, SystemClock>();
        collection.AddSingleton<Gomail.Core.IHtmlSanitizer, SecureEmailHtmlSanitizer>();
        collection.AddSingleton(new MicrosoftAuthOptions(microsoftClientId));
        collection.AddSingleton(new GmailAuthOptions(gmailClientId, gmailClientSecret));
        collection.AddSingleton<IMicrosoftAuthenticationService, MicrosoftAuthenticationService>();
        collection.AddSingleton<IGmailAuthenticationService, GmailAuthenticationService>();
        collection.AddSingleton<IMailProvider, DemoMailProvider>();
        collection.AddSingleton<IMailProvider, ImapMailProvider>();
        collection.AddSingleton<IMailProvider, MicrosoftGraphMailProvider>();
        collection.AddSingleton<IMailProvider, GmailMailProvider>();
        collection.AddSingleton<IMailProviderRegistry, MailProviderRegistry>();
        collection.AddSingleton<IAppNotifier, WindowsMailNotifier>();
        collection.AddSingleton<ISyncCoordinator, SyncCoordinator>();
        collection.AddSingleton<BackgroundMailSyncService>();
        collection.AddSingleton<IAttachmentService, EncryptedAttachmentService>();
        collection.AddSingleton<LocalDiagnosticsService>();
        collection.AddTransient<MainPageViewModel>();
        services = collection.BuildServiceProvider();
        var diagnostics = services.GetRequiredService<LocalDiagnosticsService>();
        UnhandledException += (_, eventArgs) => diagnostics.LogException("UI", eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception) diagnostics.LogException("AppDomain", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) => diagnostics.LogException("Task", eventArgs.Exception);
        Initialization = InitializeDataAsync();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();
    }

    private async Task InitializeDataAsync()
    {
        var store = services.GetRequiredService<IMailStore>();
        var secrets = services.GetRequiredService<ISecretStore>();
        var databaseKey = await secrets.GetOrCreateKeyAsync("gomail:database-key");
        await store.InitializeAsync(databaseKey);
        await services.GetRequiredService<IAttachmentService>().CleanTemporaryFilesAsync();

        var accounts = await store.GetAccountsAsync();
        var existingDemo = accounts.FirstOrDefault(static account =>
            account.Id == Guid.Parse("7d26db65-1531-4798-a732-777b22a7a1e9") && account.IsDemo);
        if (existingDemo is not null &&
            (existingDemo.Email != "hello@inboxwell.local" || existingDemo.DisplayName != "Inboxwell Demo"))
        {
            var migratedDemo = existingDemo with
            {
                Email = "hello@inboxwell.local",
                DisplayName = "Inboxwell Demo"
            };
            await store.UpsertAccountAsync(migratedDemo);
            await store.UpsertBatchAsync(DemoMailProvider.CreateDemoBatch(migratedDemo));
        }

        if (accounts.Count != 0)
        {
            services.GetRequiredService<BackgroundMailSyncService>().Start();
            return;
        }

        var demo = new MailAccount
        {
            Id = Guid.Parse("7d26db65-1531-4798-a732-777b22a7a1e9"),
            Provider = ProviderKind.Demo,
            Email = "hello@inboxwell.local",
            DisplayName = "Inboxwell Demo",
            Color = "#5B6CFF",
            IsDemo = true
        };
        await store.UpsertAccountAsync(demo);
        await store.UpsertBatchAsync(DemoMailProvider.CreateDemoBatch(demo));
        await store.UpsertSignatureAsync(new Signature
        {
            Id = Guid.Parse("b72d7229-08dc-4ee4-847d-c64b138552c9"),
            AccountId = demo.Id,
            Name = "Personal",
            PlainText = "Best,\nDenis",
            Html = "<p>Best,<br>Denis</p>",
            IsDefaultForNew = true,
            IsDefaultForReplies = true
        });
        services.GetRequiredService<BackgroundMailSyncService>().Start();
    }

    private static string ReadSetting(IDictionary<string, object> settings, string key, params string[] environmentNames)
    {
        if (settings.TryGetValue(key, out var value) && value is string text)
        {
            return text;
        }

        foreach (var environmentName in environmentNames)
        {
            var environmentValue = Environment.GetEnvironmentVariable(environmentName);
            if (!string.IsNullOrWhiteSpace(environmentValue))
            {
                return environmentValue;
            }
        }

        return string.Empty;
    }
}
