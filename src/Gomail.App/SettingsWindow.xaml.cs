using Gomail.Core;
using Gomail_App.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System.Reflection;
using Windows.ApplicationModel;
using Windows.Graphics;
using Windows.Storage;

namespace Gomail_App;

public sealed partial class SettingsWindow : Window
{
    private readonly MainPageViewModel viewModel;
    private readonly string initialPage;
    private readonly IDictionary<string, object> localSettings = ApplicationData.Current.LocalSettings.Values;
    private bool allowClose;
    private bool loadingSignature;

    public event EventHandler? SettingsChanged;
    public event EventHandler? AddAccountRequested;

    public SettingsWindow(MainPageViewModel viewModel, string initialPage = "general")
    {
        this.viewModel = viewModel;
        this.initialPage = initialPage;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(SettingsTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1120, 760));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 720;
            presenter.PreferredMinimumHeight = 600;
        }
        AppWindow.Closing += Window_Closing;
        SettingsRoot.Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        NotificationsToggle.IsOn = !localSettings.TryGetValue("notificationsEnabled", out var notifications) || notifications is true;
        CloseToTrayToggle.IsOn = !localSettings.TryGetValue("closeToTray", out var tray) || tray is true;
        SelectByTag(ReadingPanePicker, localSettings["readingPanePosition"] as string ?? "right");
        SelectByTag(ThemePicker, localSettings["themePreference"] as string ?? "system");
        MicrosoftIdBox.Text = localSettings["microsoftClientId"] as string ?? string.Empty;
        GmailIdBox.Text = localSettings["gmailClientId"] as string ?? string.Empty;
        GmailSecretBox.Password = localSettings["gmailClientSecret"] as string ?? string.Empty;
        LoadAboutInformation();
        CompactNavigation.SelectedIndex = 0;
        var navigationItem = SettingsNav.Items.OfType<ListViewItem>().FirstOrDefault(item => item.Tag as string == initialPage);
        if (navigationItem is not null) SettingsNav.SelectedItem = navigationItem;
        var compactItem = CompactNavigation.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == initialPage);
        if (compactItem is not null) CompactNavigation.SelectedItem = compactItem;
        ShowPage(initialPage);
        RefreshAccounts();
        await LoadSignatureAccountsAsync();
        BeginEntranceAnimation();
    }

    private void BeginEntranceAnimation()
    {
        var storyboard = new Storyboard();
        var opacity = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(210), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var offset = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(250), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(opacity, SettingsRoot);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(offset, SettingsTransform);
        Storyboard.SetTargetProperty(offset, "TranslateY");
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(offset);
        storyboard.Begin();
    }

    private async Task BeginExitAnimationAsync()
    {
        var completion = new TaskCompletionSource();
        var storyboard = new Storyboard();
        var opacity = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(130), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        var offset = new DoubleAnimation { To = 6, Duration = TimeSpan.FromMilliseconds(140), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        Storyboard.SetTarget(opacity, SettingsRoot);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(offset, SettingsTransform);
        Storyboard.SetTargetProperty(offset, "TranslateY");
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(offset);
        storyboard.Completed += (_, _) => completion.TrySetResult();
        storyboard.Begin();
        await completion.Task;
    }

    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowClose) return;
        args.Cancel = true;
        await CloseAnimatedAsync();
    }

    private async Task CloseAnimatedAsync()
    {
        await BeginExitAnimationAsync();
        allowClose = true;
        Close();
    }

    private void SettingsNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsNav.SelectedItem is ListViewItem { Tag: string tag }) ShowPage(tag);
    }

    private void CompactNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CompactNavigation.SelectedItem is ComboBoxItem { Tag: string tag }) ShowPage(tag);
    }

    private void ShowPage(string tag)
    {
        if (GeneralPage is null || AccountsPage is null || SignaturesPage is null || IntegrationsPage is null || PrivacyPage is null || AboutPage is null) return;
        GeneralPage.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        AccountsPage.Visibility = tag == "accounts" ? Visibility.Visible : Visibility.Collapsed;
        SignaturesPage.Visibility = tag == "signatures" ? Visibility.Visible : Visibility.Collapsed;
        IntegrationsPage.Visibility = tag == "integrations" ? Visibility.Visible : Visibility.Collapsed;
        PrivacyPage.Visibility = tag == "privacy" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadAboutInformation()
    {
        try
        {
            var identity = Package.Current.Id;
            var version = identity.Version;
            var packageVersion = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            var displayVersion = version.Revision == 0
                ? $"{version.Major}.{version.Minor}.{version.Build}"
                : packageVersion;
            AboutVersionText.Text = $"Version {displayVersion}";
            AboutPackageVersionText.Text = packageVersion;
            AboutArchitectureText.Text = identity.Architecture.ToString();
        }
        catch
        {
            var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            AboutVersionText.Text = $"Version {assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
            AboutPackageVersionText.Text = assemblyVersion.ToString();
            AboutArchitectureText.Text = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        }
    }

    private void RefreshAccounts()
    {
        var selectedId = (AccountsList.SelectedItem as AccountItem)?.Model?.Id;
        var items = viewModel.Accounts.Where(static item => item.Model is not null).ToArray();
        AccountsList.ItemsSource = items;
        AccountsList.SelectedItem = items.FirstOrDefault(item => item.Model?.Id == selectedId) ?? items.FirstOrDefault();
    }

    private async void ReconnectAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountItem { Model: { } account }) return;
        SettingsStatus.Text = $"Reconnecting {account.Email}…";
        try
        {
            await viewModel.ReconnectAccountAsync(account);
            SettingsStatus.Text = "Account connected and synchronized";
            RefreshAccounts();
        }
        catch (Exception exception)
        {
            SettingsStatus.Text = "Could not reconnect: " + exception.Message;
        }
    }

    private async void ToggleAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountItem { Model: { } account }) return;
        await viewModel.SetAccountEnabledAsync(account, !account.IsEnabled);
        SettingsStatus.Text = account.IsEnabled ? "Account disabled" : "Account enabled";
        RefreshAccounts();
    }

    private async void RemoveAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountItem { Model: { } account }) return;
        var confirmation = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = "Remove this account?",
            Content = $"{account.Email} and its local cached mail will be removed from Inboxwell. Messages on the server are not deleted.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        await viewModel.DeleteAccountAsync(account.Id);
        SettingsStatus.Text = "Account removed";
        RefreshAccounts();
        await LoadSignatureAccountsAsync();
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e) => AddAccountRequested?.Invoke(this, EventArgs.Empty);

    private async Task LoadSignatureAccountsAsync()
    {
        var accounts = viewModel.Accounts.Where(static item => item.Model is not null).ToArray();
        SignatureAccountPicker.ItemsSource = accounts;
        SignatureAccountPicker.DisplayMemberPath = nameof(AccountItem.ManagementDisplay);
        SignatureAccountPicker.SelectedItem = accounts.FirstOrDefault();
        await LoadSignaturesAsync();
    }

    private async void SignatureAccountPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => await LoadSignaturesAsync();

    private async Task LoadSignaturesAsync()
    {
        if (SignatureAccountPicker.SelectedItem is not AccountItem { Model: { } account }) return;
        loadingSignature = true;
        var choices = new List<SignatureChoice> { new("New signature", null) };
        choices.AddRange((await viewModel.GetSignaturesAsync(account.Id)).Select(item => new SignatureChoice(item.Name, item)));
        SignaturePicker.ItemsSource = choices;
        SignaturePicker.DisplayMemberPath = nameof(SignatureChoice.Name);
        SignaturePicker.SelectedIndex = 0;
        ClearSignatureEditor();
        loadingSignature = false;
    }

    private void SignaturePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingSignature) return;
        if (SignaturePicker.SelectedItem is SignatureChoice { Signature: { } signature })
        {
            SignatureNameBox.Text = signature.Name;
            SignatureBodyBox.Text = signature.PlainText;
            DefaultNewCheck.IsChecked = signature.IsDefaultForNew;
            DefaultReplyCheck.IsChecked = signature.IsDefaultForReplies;
        }
        else
        {
            ClearSignatureEditor();
        }
    }

    private void ClearSignatureEditor()
    {
        SignatureNameBox.Text = "Personal";
        SignatureBodyBox.Text = string.Empty;
        DefaultNewCheck.IsChecked = true;
        DefaultReplyCheck.IsChecked = true;
    }

    private async void SaveSignature_Click(object sender, RoutedEventArgs e)
    {
        if (SignatureAccountPicker.SelectedItem is not AccountItem { Model: { } account } || string.IsNullOrWhiteSpace(SignatureBodyBox.Text))
        {
            SettingsStatus.Text = "Choose a mailbox and enter the signature text";
            return;
        }
        var existing = (SignaturePicker.SelectedItem as SignatureChoice)?.Signature;
        await viewModel.SaveSignatureAsync(new Signature
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            AccountId = account.Id,
            Name = string.IsNullOrWhiteSpace(SignatureNameBox.Text) ? "Signature" : SignatureNameBox.Text.Trim(),
            PlainText = SignatureBodyBox.Text,
            Html = $"<p>{System.Net.WebUtility.HtmlEncode(SignatureBodyBox.Text).Replace("\n", "<br>")}</p>",
            IsDefaultForNew = DefaultNewCheck.IsChecked == true,
            IsDefaultForReplies = DefaultReplyCheck.IsChecked == true
        });
        SettingsStatus.Text = "Signature saved";
        await LoadSignaturesAsync();
    }

    private async void DeleteSignature_Click(object sender, RoutedEventArgs e)
    {
        if (SignaturePicker.SelectedItem is not SignatureChoice { Signature: { } signature }) return;
        await viewModel.DeleteSignatureAsync(signature.Id);
        SettingsStatus.Text = "Signature deleted";
        await LoadSignaturesAsync();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        localSettings["notificationsEnabled"] = NotificationsToggle.IsOn;
        localSettings["closeToTray"] = CloseToTrayToggle.IsOn;
        localSettings["readingPanePosition"] = SelectedTag(ReadingPanePicker, "right");
        localSettings["themePreference"] = SelectedTag(ThemePicker, "system");
        localSettings["microsoftClientId"] = MicrosoftIdBox.Text.Trim();
        localSettings["gmailClientId"] = GmailIdBox.Text.Trim();
        localSettings["gmailClientSecret"] = GmailSecretBox.Password;
        ApplyTheme(SelectedTag(ThemePicker, "system"));
        SettingsStatus.Text = "Settings saved";
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        await Task.Delay(220);
    }

    private async void Close_Click(object sender, RoutedEventArgs e) => await CloseAnimatedAsync();

    private async void OpenSourceCode_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/dshchepetov/Inboxwell"));

    private static string SelectedTag(ComboBox picker, string fallback) => (picker.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private static void SelectByTag(ComboBox picker, string tag)
    {
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == tag) ?? picker.Items[0];
    }

    private void ApplyTheme(string theme)
    {
        var requested = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        SettingsRoot.RequestedTheme = requested;
        if (App.Window.Content is FrameworkElement mainRoot) mainRoot.RequestedTheme = requested;
    }
}
