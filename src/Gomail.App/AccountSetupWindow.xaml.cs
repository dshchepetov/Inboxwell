using Gomail.Core;
using Gomail_App.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace Gomail_App;

public sealed partial class AccountSetupWindow : Window
{
    private readonly MainPageViewModel viewModel;
    private ProviderKind selectedProvider;
    private bool allowClose;
    private bool isConnecting;

    public event EventHandler? AccountConnected;

    public AccountSetupWindow(MainPageViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(SetupTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(980, 720));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 680;
            presenter.PreferredMinimumHeight = 600;
        }
        AppWindow.Closing += Window_Closing;
    }

    private async Task FadePageAsync(UIElement outgoing, UIElement incoming)
    {
        var fadeOut = new Storyboard();
        var outAnimation = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(110) };
        Storyboard.SetTarget(outAnimation, outgoing);
        Storyboard.SetTargetProperty(outAnimation, "Opacity");
        fadeOut.Children.Add(outAnimation);
        fadeOut.Begin();
        await Task.Delay(115);
        outgoing.Visibility = Visibility.Collapsed;
        incoming.Visibility = Visibility.Visible;
        incoming.Opacity = 0;
        var fadeIn = new Storyboard();
        var inAnimation = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(180), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(inAnimation, incoming);
        Storyboard.SetTargetProperty(inAnimation, "Opacity");
        fadeIn.Children.Add(inAnimation);
        fadeIn.Begin();
    }

    private Task CloseAnimatedAsync()
    {
        allowClose = true;
        Close();
        return Task.CompletedTask;
    }

    private void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        allowClose = true;
    }

    private async void Provider_Click(object sender, RoutedEventArgs e)
    {
        if (isConnecting) return;
        selectedProvider = ((sender as Button)?.Tag as string) switch
        {
            "gmail" => ProviderKind.Gmail,
            "imap" => ProviderKind.Imap,
            _ => ProviderKind.Microsoft365
        };

        var isImap = selectedProvider == ProviderKind.Imap;
        var isGmail = selectedProvider == ProviderKind.Gmail;
        ImapFields.Visibility = isImap ? Visibility.Visible : Visibility.Collapsed;
        IdentityGrid.Visibility = isGmail ? Visibility.Collapsed : Visibility.Visible;
        OAuthNotice.IsOpen = !isImap;
        FormTitle.Text = selectedProvider switch
        {
            ProviderKind.Gmail => "Connect Gmail",
            ProviderKind.Imap => "Connect an IMAP mailbox",
            _ => "Connect Microsoft 365"
        };
        FormSubtitle.Text = isImap ? "Enter the server details supplied by your mail provider." : "Inboxwell will open a secure sign-in page in your browser.";
        OAuthNotice.Title = selectedProvider == ProviderKind.Gmail ? "Google OAuth" : "Microsoft OAuth";
        OAuthNotice.Message = "Inboxwell never receives your account password. The resulting token is protected by Windows Credential Manager.";
        ConnectButton.Content = isImap ? "Test & connect" : "Continue to sign in";
        await FadePageAsync(ProviderPage, AccountFormPage);
        if (isGmail)
        {
            await ConnectSelectedProviderAsync();
        }
        else
        {
            EmailBox.Focus(FocusState.Programmatic);
        }
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        SetupInfoBar.IsOpen = false;
        await FadePageAsync(AccountFormPage, ProviderPage);
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e) => await CloseAnimatedAsync();

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ConnectSelectedProviderAsync();

    private async Task ConnectSelectedProviderAsync()
    {
        if (isConnecting) return;
        isConnecting = true;
        var accountId = Guid.NewGuid();
        var isGmail = selectedProvider == ProviderKind.Gmail;
        var email = isGmail ? $"pending-{accountId:N}@oauth.local" : EmailBox.Text.Trim();
        if (!isGmail && (string.IsNullOrWhiteSpace(email) || !email.Contains('@')))
        {
            ShowError("Enter a valid email address.");
            EmailBox.Focus(FocusState.Programmatic);
            isConnecting = false;
            return;
        }
        if (selectedProvider == ProviderKind.Imap && (string.IsNullOrWhiteSpace(ImapHostBox.Text) || string.IsNullOrWhiteSpace(SmtpHostBox.Text)))
        {
            ShowError("Incoming and outgoing server names are required.");
            isConnecting = false;
            return;
        }

        ConnectButton.IsEnabled = false;
        SetupStatus.Text = selectedProvider == ProviderKind.Imap ? "Testing encrypted connection…" : "Opening secure sign-in…";
        try
        {
            var account = new MailAccount
            {
                Id = accountId,
                Provider = selectedProvider,
                Email = email,
                DisplayName = isGmail ? "Gmail" : string.IsNullOrWhiteSpace(DisplayNameBox.Text) ? email.Split('@')[0] : DisplayNameBox.Text.Trim(),
                Color = selectedProvider switch
                {
                    ProviderKind.Gmail => "#D9574F",
                    ProviderKind.Imap => "#37A276",
                    _ => "#3D73E8"
                },
                Settings = selectedProvider == ProviderKind.Imap
                    ? new Dictionary<string, string>
                    {
                        ["username"] = string.IsNullOrWhiteSpace(UsernameBox.Text) ? email : UsernameBox.Text.Trim(),
                        ["imapHost"] = ImapHostBox.Text.Trim(),
                        ["imapPort"] = ParsePort(ImapPortBox.Text, 993).ToString(),
                        ["imapSecurity"] = SelectedSecurity(ImapSecurityPicker, "ssl"),
                        ["smtpHost"] = SmtpHostBox.Text.Trim(),
                        ["smtpPort"] = ParsePort(SmtpPortBox.Text, 587).ToString(),
                        ["smtpSecurity"] = SelectedSecurity(SmtpSecurityPicker, "starttls"),
                        ["cacheDays"] = "90"
                    }
                    : new Dictionary<string, string>()
            };
            await viewModel.AddAccountAsync(account, selectedProvider == ProviderKind.Imap ? PasswordBox.Password : null);
            viewModel.StatusText = "Account connected";
            SetupStatus.Text = "Connected successfully";
            AccountConnected?.Invoke(this, EventArgs.Empty);
            await Task.Delay(280);
            await CloseAnimatedAsync();
        }
        catch (Exception exception)
        {
            var hint = selectedProvider == ProviderKind.Imap
                ? string.Empty
                : " Try signing in again.";
            ShowError(exception.Message + hint);
            SetupStatus.Text = "Connection needs attention";
        }
        finally
        {
            isConnecting = false;
            if (!allowClose) ConnectButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        SetupInfoBar.Message = message;
        SetupInfoBar.Severity = InfoBarSeverity.Error;
        SetupInfoBar.IsOpen = true;
    }

    private void SetupRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ProviderColumnOne is null || ProviderColumnTwo is null || ProviderColumnThree is null ||
            ProviderSecondRow is null || ProviderThirdRow is null || GmailCard is null || ImapCard is null ||
            IdentitySecondColumn is null || IdentitySecondRow is null || EmailBox is null ||
            IncomingPortColumn is null || IncomingSecurityColumn is null || IncomingPortRow is null || IncomingSecurityRow is null ||
            OutgoingPortColumn is null || OutgoingSecurityColumn is null || OutgoingPortRow is null || OutgoingSecurityRow is null) return;
        var compact = e.NewSize.Width < 800;
        ProviderColumnOne.Width = new GridLength(1, GridUnitType.Star);
        ProviderColumnTwo.Width = new GridLength(compact ? 0 : 1, compact ? GridUnitType.Pixel : GridUnitType.Star);
        ProviderColumnThree.Width = new GridLength(compact ? 0 : 1, compact ? GridUnitType.Pixel : GridUnitType.Star);
        ProviderSecondRow.Height = compact ? GridLength.Auto : new GridLength(0);
        ProviderThirdRow.Height = compact ? GridLength.Auto : new GridLength(0);
        Grid.SetRow(GmailCard, compact ? 1 : 0);
        Grid.SetColumn(GmailCard, compact ? 0 : 1);
        Grid.SetRow(ImapCard, compact ? 2 : 0);
        Grid.SetColumn(ImapCard, compact ? 0 : 2);

        var narrowForm = e.NewSize.Width < 760;
        IdentitySecondColumn.Width = narrowForm ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        IdentitySecondRow.Height = narrowForm ? GridLength.Auto : new GridLength(0);
        Grid.SetRow(EmailBox, narrowForm ? 1 : 0);
        Grid.SetColumn(EmailBox, narrowForm ? 0 : 1);
        ArrangeServerFields(narrowForm, ImapPortBox, ImapSecurityPicker, IncomingPortColumn, IncomingSecurityColumn, IncomingPortRow, IncomingSecurityRow);
        ArrangeServerFields(narrowForm, SmtpPortBox, SmtpSecurityPicker, OutgoingPortColumn, OutgoingSecurityColumn, OutgoingPortRow, OutgoingSecurityRow);
    }

    private static void ArrangeServerFields(
        bool narrow,
        FrameworkElement port,
        FrameworkElement security,
        ColumnDefinition portColumn,
        ColumnDefinition securityColumn,
        RowDefinition portRow,
        RowDefinition securityRow)
    {
        portColumn.Width = narrow ? new GridLength(0) : new GridLength(100);
        securityColumn.Width = narrow ? new GridLength(0) : new GridLength(150);
        portRow.Height = narrow ? GridLength.Auto : new GridLength(0);
        securityRow.Height = narrow ? GridLength.Auto : new GridLength(0);
        Grid.SetRow(port, narrow ? 1 : 0);
        Grid.SetColumn(port, narrow ? 0 : 1);
        Grid.SetRow(security, narrow ? 2 : 0);
        Grid.SetColumn(security, narrow ? 0 : 2);
    }

    private static int ParsePort(string text, int fallback) => int.TryParse(text, out var value) && value is > 0 and <= 65535 ? value : fallback;
    private static string SelectedSecurity(ComboBox picker, string fallback) => (picker.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;
}
