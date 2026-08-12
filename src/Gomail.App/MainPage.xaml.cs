using Gomail.Core;
using Gomail_App.Services;
using Gomail_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Text;
using System.Net;
using System.Globalization;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace Gomail_App;

public sealed partial class MainPage : Page
{
    private readonly DispatcherTimer syncTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly IAttachmentService attachmentService = App.Services.GetRequiredService<IAttachmentService>();
    private readonly Dictionary<Guid, WeakReference<WebView2>> messageHtmlViews = new();
    private readonly HashSet<Guid> pendingHtmlNavigations = new();
    private readonly List<Window> childWindows = new();
    private ReadingPanePlacement preferredReadingPane;
    public MainPageViewModel ViewModel { get; } = App.Services.GetRequiredService<MainPageViewModel>();

    public MainPage()
    {
        InitializeComponent();
        var settings = ApplicationData.Current.LocalSettings.Values;
        preferredReadingPane = settings.TryGetValue("readingPanePosition", out var value) && value as string == "bottom"
            ? ReadingPanePlacement.Bottom
            : ReadingPanePlacement.Right;
        Loaded += OnLoaded;
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainPageViewModel.SelectedConversation) && InlineReplyCard is not null)
            {
                InlineReplyCard.Visibility = Visibility.Collapsed;
                InlineReplyEditor.Document.SetText(TextSetOptions.None, string.Empty);
            }
        };
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout(ActualWidth, ActualHeight, animate: false);
        PageRoot.Opacity = 0;
        PageTransform.TranslateY = 8;
        var storyboard = new Storyboard();
        var opacity = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var offset = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(260), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(opacity, PageRoot);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(offset, PageTransform);
        Storyboard.SetTargetProperty(offset, "TranslateY");
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(offset);
        storyboard.Begin();
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width, e.NewSize.Height, animate: true);

    private void ReadingPaneRight_Click(object sender, RoutedEventArgs e)
    {
        preferredReadingPane = ReadingPanePlacement.Right;
        ApplicationData.Current.LocalSettings.Values["readingPanePosition"] = "right";
        ApplyResponsiveLayout(ActualWidth, ActualHeight, animate: true);
    }

    private void ReadingPaneBottom_Click(object sender, RoutedEventArgs e)
    {
        preferredReadingPane = ReadingPanePlacement.Bottom;
        ApplicationData.Current.LocalSettings.Values["readingPanePosition"] = "bottom";
        ApplyResponsiveLayout(ActualWidth, ActualHeight, animate: true);
    }

    private void ApplyResponsiveLayout(double width, double height, bool animate)
    {
        if (Workspace is null) return;

        var compact = width < 1060;
        SidebarColumn.Width = new GridLength(compact ? 204 : 238);
        SidebarFooter.Visibility = Visibility.Visible;

        var bottom = preferredReadingPane == ReadingPanePlacement.Bottom || width < 1160;
        ViewModeIcon.Glyph = bottom ? "\uE90D" : "\uE90E";
        RightPaneMenuItem.IsChecked = !bottom;
        BottomPaneMenuItem.IsChecked = bottom;
        ToolTipService.SetToolTip(ViewModeButton, bottom
            ? "Reading pane below · click to change"
            : "Reading pane on the right · click to change");

        if (bottom)
        {
            ConversationColumn.Width = new GridLength(1, GridUnitType.Star);
            PaneDividerColumn.Width = new GridLength(0);
            ReaderColumn.Width = new GridLength(0);
            ConversationRow.Height = new GridLength(Math.Clamp(height * 0.38, 286, 360));
            PaneDividerRow.Height = new GridLength(1);
            ReaderRow.Height = new GridLength(1, GridUnitType.Star);
            Place(ConversationPane, 0, 0);
            Place(PaneDivider, 1, 0);
            Place(ReaderPane, 2, 0);
        }
        else
        {
            ConversationColumn.Width = new GridLength(Math.Clamp(width * 0.285, 340, 420));
            PaneDividerColumn.Width = new GridLength(1);
            ReaderColumn.Width = new GridLength(1, GridUnitType.Star);
            ConversationRow.Height = new GridLength(1, GridUnitType.Star);
            PaneDividerRow.Height = new GridLength(0);
            ReaderRow.Height = new GridLength(0);
            Place(ConversationPane, 0, 0);
            Place(PaneDivider, 0, 1);
            Place(ReaderPane, 0, 2);
        }

        // Grid repositioning remains smooth on its own. Replaying a whole-workspace
        // opacity animation for every resize event caused the visible white flash.
    }

    private static void Place(FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetRowSpan(element, 1);
        Grid.SetColumnSpan(element, 1);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await App.Initialization;
            await ViewModel.InitializeAsync();
            syncTimer.Tick += async (_, _) =>
            {
                if (!ViewModel.IsBusy) await ViewModel.ReloadLocalAsync();
            };
            syncTimer.Start();
            _ = ViewModel.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("Inboxwell could not open its local mailbox", exception.Message);
        }
    }

    private async void Search_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (ViewModel.IsBusy)
        {
            return;
        }

        try
        {
            await ViewModel.SearchCommand.ExecuteAsync(null);
        }
        catch (Exception exception)
        {
            App.Services.GetRequiredService<LocalDiagnosticsService>().LogException("Search", exception);
            ViewModel.StatusText = "Search could not be completed";
            await ShowErrorAsync(
                "Search could not be completed",
                "Inboxwell kept your mailbox open. Try again, or open Diagnostics if the problem continues.");
        }
    }

    private async void ConversationList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.ItemIndex < Math.Max(0, ViewModel.Conversations.Count - 24))
        {
            return;
        }

        try
        {
            await ViewModel.LoadMoreConversationsAsync();
        }
        catch (Exception exception)
        {
            App.Services.GetRequiredService<LocalDiagnosticsService>().LogException("Load older conversations", exception);
        }
    }

    private async void ComposeShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ShowComposerAsync();
    }

    private void SearchShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
    }

    private async void RefreshShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    private void ReplyShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenInlineReply();
    }

    private async void Attachment_OpenClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not AttachmentItem item) return;
        try
        {
            ViewModel.StatusText = $"Downloading {item.Attachment.FileName}…";
            await attachmentService.OpenAsync(item.Message, item.Attachment);
            ViewModel.StatusText = "Attachment opened";
        }
        catch (Exception exception)
        {
            ViewModel.StatusText = "Attachment could not be opened";
            await ShowErrorAsync("Could not open attachment", exception.Message);
        }
    }

    private async void Attachment_SaveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not AttachmentItem item) return;
        try
        {
            var extension = Path.GetExtension(item.Attachment.FileName);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".bin";
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                SuggestedFileName = Path.GetFileNameWithoutExtension(item.Attachment.FileName)
            };
            picker.FileTypeChoices.Add("File", new List<string> { extension });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            ViewModel.StatusText = $"Saving {item.Attachment.FileName}…";
            await attachmentService.SaveAsAsync(item.Message, item.Attachment, file.Path);
            ViewModel.StatusText = $"Saved to {file.Path}";
        }
        catch (Exception exception)
        {
            ViewModel.StatusText = "Attachment could not be saved";
            await ShowErrorAsync("Could not save attachment", exception.Message);
        }
    }

    private async void MessageHtml_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WebView2 webView || webView.Tag is not MessageItem item) return;
        try
        {
            await webView.EnsureCoreWebView2Async();
            var settings = webView.CoreWebView2.Settings;
            settings.IsScriptEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            webView.CoreWebView2.NewWindowRequested += (core, args) =>
            {
                args.Handled = true;
                if (TryExternalUri(args.Uri, out var uri)) _ = Windows.System.Launcher.LaunchUriAsync(uri);
            };
            messageHtmlViews[item.Model.Id] = new WeakReference<WebView2>(webView);
            pendingHtmlNavigations.Add(item.Model.Id);
            webView.NavigateToString(await attachmentService.ResolveInlineImagesAsync(item.Model, item.HtmlBody));
        }
        catch (Exception exception)
        {
            ViewModel.StatusText = $"HTML view unavailable: {exception.Message}";
            ShowPlainMessageFallback(item);
        }
    }

    private async void MessageHtml_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (sender.Tag is not MessageItem item) return;
        // CoreWebView2 completes its own initial about:blank navigation before
        // NavigateToString. Only inspect the navigation that contains the email.
        if (!pendingHtmlNavigations.Remove(item.Model.Id)) return;
        if (!args.IsSuccess)
        {
            ShowPlainMessageFallback(item);
            return;
        }

        try
        {
            var heightValue = await sender.ExecuteScriptAsync(
                "Math.max(document.body.scrollHeight, document.documentElement.scrollHeight)");
            if (double.TryParse(heightValue.Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
            {
                item.HtmlHeight = Math.Clamp(height + 10, 132, 1600);
            }

            var textLengthValue = await sender.ExecuteScriptAsync("document.body.innerText.trim().length");
            if (int.TryParse(textLengthValue.Trim('"'), out var textLength) && textLength == 0 && item.HasPlainBody)
            {
                ShowPlainMessageFallback(item);
            }
        }
        catch
        {
            // The estimated height remains valid when WebView script inspection is unavailable.
        }
    }

    private static void ShowPlainMessageFallback(MessageItem item)
    {
        if (!item.HasPlainBody) return;
        item.ShowHtml = false;
        item.ShowPlain = true;
        item.CanShowFormatted = item.HasHtml;
    }

    private async void MessageHtml_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (sender.Tag is MessageItem item && pendingHtmlNavigations.Contains(item.Model.Id)) return;
        if (args.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return;
        args.Cancel = true;
        if (TryExternalUri(args.Uri, out var uri)) await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private void Message_ShowFormattedClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not MessageItem item) return;
        item.ShowPlain = false;
        item.ShowHtml = true;
        item.CanShowFormatted = false;
    }

    private static bool TryExternalUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
            candidate.Scheme is "https" or "http" or "mailto")
        {
            uri = candidate;
            return true;
        }
        uri = null!;
        return false;
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        var window = new AccountSetupWindow(ViewModel);
        window.AccountConnected += async (_, _) => await ViewModel.ReloadLocalAsync();
        TrackChildWindow(window);
        window.Activate();
    }

    private async void LegacyAddAccount_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
        picker.Items.Add(new ComboBoxItem { Content = "Microsoft 365 / Exchange Online", Tag = ProviderKind.Microsoft365 });
        picker.Items.Add(new ComboBoxItem { Content = "Gmail", Tag = ProviderKind.Gmail });
        picker.Items.Add(new ComboBoxItem { Content = "IMAP / SMTP", Tag = ProviderKind.Imap });
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Choose a provider. Microsoft and Google sign in through your browser; Inboxwell never sees those passwords.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 430
        });
        panel.Children.Add(picker);

        var dialog = CreateDialog("Add an account", panel, "Continue", "Cancel");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var provider = (ProviderKind)((ComboBoxItem)picker.SelectedItem).Tag;
        if (provider == ProviderKind.Imap)
        {
            await ShowImapAccountDialogAsync();
        }
        else
        {
            await ShowOAuthAccountDialogAsync(provider);
        }
    }

    private void ManageAccounts_Click(object sender, RoutedEventArgs e)
    {
        var window = CreateSettingsWindow("accounts");
        TrackChildWindow(window);
        window.Activate();
    }

    private async void LegacyManageAccounts_Click(object sender, RoutedEventArgs e)
    {
        var accounts = ViewModel.Accounts.Where(static item => item.Model is not null).ToArray();
        if (accounts.Length == 0)
        {
            await ShowErrorAsync("No accounts", "There are no connected mailboxes to manage.");
            return;
        }

        var list = new ListView
        {
            ItemsSource = accounts,
            DisplayMemberPath = nameof(AccountItem.ManagementDisplay),
            SelectedIndex = 0,
            SelectionMode = ListViewSelectionMode.Single,
            MinWidth = 470,
            MaxHeight = 420
        };
        var reconnectStatus = new TextBlock { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var reconnect = new Button { Content = "Reconnect selected account", HorizontalAlignment = HorizontalAlignment.Left };
        reconnect.Click += async (_, _) =>
        {
            if (list.SelectedItem is not AccountItem { Model: { } selected }) return;
            reconnect.IsEnabled = false;
            reconnectStatus.Text = "Connecting…";
            try
            {
                await ViewModel.ReconnectAccountAsync(selected);
                reconnectStatus.Text = "Connected successfully.";
            }
            catch (Exception exception)
            {
                reconnectStatus.Text = exception.Message;
            }
            finally
            {
                reconnect.IsEnabled = true;
            }
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(list);
        content.Children.Add(reconnect);
        content.Children.Add(reconnectStatus);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Manage accounts",
            Content = content,
            PrimaryButtonText = "Remove",
            SecondaryButtonText = "Enable / disable",
            CloseButtonText = "Close"
        };
        var result = await dialog.ShowAsync();
        if (list.SelectedItem is not AccountItem { Model: { } account }) return;

        if (result == ContentDialogResult.Secondary)
        {
            await ViewModel.SetAccountEnabledAsync(account, !account.IsEnabled);
            ViewModel.StatusText = account.IsEnabled ? "Account disabled" : "Account enabled";
            return;
        }
        if (result != ContentDialogResult.Primary) return;

        var confirmation = CreateDialog(
            $"Remove {account.Email}?",
            new TextBlock { Text = "The local copy, cached attachments and saved credentials for this mailbox will be removed. Mail on the server is not deleted.", TextWrapping = TextWrapping.Wrap, MaxWidth = 470 },
            "Remove",
            "Cancel");
        if (await confirmation.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteAccountAsync(account.Id);
            ViewModel.StatusText = "Account removed";
        }
    }

    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var diagnostics = App.Services.GetRequiredService<LocalDiagnosticsService>();
            var report = await diagnostics.CreateReportAsync();
            var text = new TextBox
            {
                Text = report,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
                FontSize = 11,
                MinWidth = 650,
                MinHeight = 430
            };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Diagnostics",
                Content = new ScrollViewer { Content = text, MaxHeight = 560 },
                PrimaryButtonText = "Copy report",
                SecondaryButtonText = "Open data folder",
                CloseButtonText = "Close"
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var package = new DataPackage();
                package.SetText(report);
                Clipboard.SetContent(package);
                ViewModel.StatusText = "Diagnostics copied";
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await Windows.System.Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(diagnostics.DataDirectory));
            }
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("Diagnostics unavailable", exception.Message);
        }
    }

    private async Task ShowOAuthAccountDialogAsync(ProviderKind provider)
    {
        var name = Field("Your name");
        var email = Field("Email address");
        var panel = Form(
            new TextBlock
            {
                Text = provider == ProviderKind.Gmail
                    ? "A Google sign-in page will open after you continue."
                    : "A Microsoft sign-in page will open after you continue.",
                TextWrapping = TextWrapping.Wrap
            },
            name,
            email);

        var dialog = CreateDialog(provider == ProviderKind.Gmail ? "Connect Gmail" : "Connect Microsoft 365", panel, "Sign in", "Cancel");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(email.Text))
        {
            await ShowErrorAsync("Email address required", "Enter the address of the mailbox you want to connect.");
            return;
        }

        try
        {
            ViewModel.StatusText = "Connecting account…";
            await ViewModel.AddAccountAsync(new MailAccount
            {
                Id = Guid.NewGuid(),
                Provider = provider,
                Email = email.Text.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(name.Text) ? email.Text.Split('@')[0] : name.Text.Trim(),
                Color = provider == ProviderKind.Gmail ? "#D9574F" : "#3D73E8"
            });
            ViewModel.StatusText = "Account connected";
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("Could not connect the account", exception.Message + "\n\nYou can add OAuth app credentials in Settings → Integrations.");
        }
    }

    private async Task ShowImapAccountDialogAsync()
    {
        var name = Field("Your name");
        var email = Field("Email address");
        var username = Field("Username (usually your email)");
        var password = new PasswordBox { Header = "Password or app password", HorizontalAlignment = HorizontalAlignment.Stretch };
        var imapHost = Field("Incoming server", "imap.example.com");
        var imapPort = Field("Incoming port", "993");
        var smtpHost = Field("Outgoing server", "smtp.example.com");
        var smtpPort = Field("Outgoing port", "587");
        var incomingSecurity = SecurityPicker("SSL/TLS", "STARTTLS");
        var outgoingSecurity = SecurityPicker("STARTTLS", "SSL/TLS");

        var fields = Form(name, email, username, password, imapHost, imapPort, incomingSecurity, smtpHost, smtpPort, outgoingSecurity);
        var scroll = new ScrollViewer { Content = fields, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dialog = CreateDialog("Connect an IMAP mailbox", scroll, "Connect", "Cancel");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(email.Text) || string.IsNullOrWhiteSpace(imapHost.Text) || string.IsNullOrWhiteSpace(smtpHost.Text))
        {
            await ShowErrorAsync("Missing server details", "Email, incoming server and outgoing server are required.");
            return;
        }

        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Provider = ProviderKind.Imap,
            Email = email.Text.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(name.Text) ? email.Text.Split('@')[0] : name.Text.Trim(),
            Color = "#37A276",
            Settings = new Dictionary<string, string>
            {
                ["username"] = string.IsNullOrWhiteSpace(username.Text) ? email.Text.Trim() : username.Text.Trim(),
                ["imapHost"] = imapHost.Text.Trim(),
                ["imapPort"] = ParsePort(imapPort.Text, 993).ToString(),
                ["imapSecurity"] = SecurityValue(incomingSecurity),
                ["smtpHost"] = smtpHost.Text.Trim(),
                ["smtpPort"] = ParsePort(smtpPort.Text, 587).ToString(),
                ["smtpSecurity"] = SecurityValue(outgoingSecurity),
                ["cacheDays"] = "90"
            }
        };

        try
        {
            ViewModel.StatusText = "Testing encrypted connection…";
            await ViewModel.AddAccountAsync(account, password.Password);
            ViewModel.StatusText = "Account connected";
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("Could not connect the mailbox", exception.Message);
        }
    }

    private async void Compose_Click(object sender, RoutedEventArgs e) => await ShowComposerAsync();

    private void Reply_Click(object sender, RoutedEventArgs e) => OpenInlineReply();

    private void OpenInlineReply()
    {
        var message = ViewModel.Messages.LastOrDefault()?.Model;
        if (message is null) return;
        InlineRecipientText.Text = message.From.DisplayName;
        InlineImportantToggle.IsChecked = false;
        InlineReplyCard.Visibility = Visibility.Visible;
        InlineReplyCard.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
        InlineReplyEditor.Focus(FocusState.Programmatic);
    }

    private void InlineReplyDiscard_Click(object sender, RoutedEventArgs e)
    {
        InlineReplyEditor.Document.SetText(TextSetOptions.None, string.Empty);
        InlineReplyCard.Visibility = Visibility.Collapsed;
    }

    private void InlineBold_Click(object sender, RoutedEventArgs e)
    {
        var format = InlineReplyEditor.Document.Selection.CharacterFormat;
        format.Bold = format.Bold == FormatEffect.On ? FormatEffect.Off : FormatEffect.On;
        InlineReplyEditor.Focus(FocusState.Programmatic);
    }

    private void InlineItalic_Click(object sender, RoutedEventArgs e)
    {
        var format = InlineReplyEditor.Document.Selection.CharacterFormat;
        format.Italic = format.Italic == FormatEffect.On ? FormatEffect.Off : FormatEffect.On;
        InlineReplyEditor.Focus(FocusState.Programmatic);
    }

    private void InlineUnderline_Click(object sender, RoutedEventArgs e)
    {
        var format = InlineReplyEditor.Document.Selection.CharacterFormat;
        format.Underline = format.Underline == UnderlineType.None ? UnderlineType.Single : UnderlineType.None;
        InlineReplyEditor.Focus(FocusState.Programmatic);
    }

    private void InlineBullet_Click(object sender, RoutedEventArgs e)
    {
        InlineReplyEditor.Document.Selection.SetText(TextSetOptions.None, "• ");
        InlineReplyEditor.Focus(FocusState.Programmatic);
    }

    private Draft? CreateInlineReplyDraft()
    {
        var message = ViewModel.Messages.LastOrDefault()?.Model;
        if (message is null) return null;
        InlineReplyEditor.Document.GetText(TextGetOptions.None, out var body);
        body = body.TrimEnd('\r');
        var quoted = $"On {message.ReceivedAt.ToLocalTime():g}, {message.From.DisplayName} <{message.From.Address}> wrote:\n" +
            string.Join("\n", (message.TextBody ?? message.Snippet).Split('\n').Select(static line => "> " + line));
        var completeBody = string.IsNullOrWhiteSpace(body) ? string.Empty : body + "\n\n" + quoted;
        return new Draft
        {
            Id = Guid.NewGuid(),
            AccountId = message.AccountId,
            To = new[] { message.From },
            Subject = message.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? message.Subject : $"Re: {message.Subject}",
            PlainTextBody = completeBody,
            HtmlBody = $"<div style=\"font-family:'Segoe UI',Arial,sans-serif;line-height:1.5\">{WebUtility.HtmlEncode(body).Replace("\r", string.Empty).Replace("\n", "<br>")}<br><br><blockquote style=\"border-left:2px solid #d7dbe5;margin-left:0;padding-left:12px;color:#667085\">{WebUtility.HtmlEncode(quoted).Replace("\n", "<br>")}</blockquote></div>",
            ReplyToRemoteId = message.InternetMessageId ?? message.RemoteId,
            ProviderThreadId = message.ProviderThreadId,
            IsImportant = InlineImportantToggle.IsChecked == true,
            UpdatedAt = DateTimeOffset.UtcNow,
            DeliveryState = DraftDeliveryState.Draft
        };
    }

    private async void InlineReplySend_Click(object sender, RoutedEventArgs e) => await SendInlineReplyAsync();

    private async void InlineSendShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (InlineReplyCard.Visibility != Visibility.Visible) return;
        args.Handled = true;
        await SendInlineReplyAsync();
    }

    private async Task SendInlineReplyAsync()
    {
        var draft = CreateInlineReplyDraft();
        if (draft is null || string.IsNullOrWhiteSpace(draft.PlainTextBody)) return;
        InlineSendButton.IsEnabled = false;
        try
        {
            var remaining = await ViewModel.QueueDraftForSendAsync(draft);
            if (remaining?.DeliveryState == DraftDeliveryState.Failed)
            {
                await ShowErrorAsync("Reply could not be sent", remaining.LastError ?? "The server rejected this message. It remains in Drafts.");
                return;
            }
            InlineReplyEditor.Document.SetText(TextSetOptions.None, string.Empty);
            InlineReplyCard.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            await ViewModel.SaveDraftAsync(draft with { LastError = exception.Message });
            await ShowErrorAsync("Reply could not be sent", exception.Message + " The reply remains in Drafts.");
        }
        finally
        {
            InlineSendButton.IsEnabled = true;
        }
    }

    private async void InlineReplyExpand_Click(object sender, RoutedEventArgs e)
    {
        var draft = CreateInlineReplyDraft();
        if (draft is null) return;
        InlineReplyCard.Visibility = Visibility.Collapsed;
        await ShowComposerAsync(existingDraft: draft, threadContext: ViewModel.Messages.LastOrDefault()?.Model);
    }

    private async Task ReplyToSelectedAsync(bool forward)
    {
        var message = ViewModel.Messages.LastOrDefault()?.Model;
        if (message is not null)
        {
            if (forward)
            {
                await ShowComposerAsync(subject: message.Subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) ? message.Subject : $"Fwd: {message.Subject}", body: $"\n\n—— Forwarded message ——\nFrom: {message.From.DisplayName} <{message.From.Address}>\n\n{message.TextBody ?? message.Snippet}");
            }
            else
            {
                await ShowComposerAsync(
                    message.From.Address,
                    message.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? message.Subject : $"Re: {message.Subject}",
                    $"\n\nOn {message.ReceivedAt.ToLocalTime():g}, {message.From.DisplayName} <{message.From.Address}> wrote:\n" +
                    string.Join("\n", (message.TextBody ?? message.Snippet).Split('\n').Select(static line => "> " + line)),
                    replyToRemoteId: message.InternetMessageId ?? message.RemoteId,
                    providerThreadId: message.ProviderThreadId,
                    threadContext: message);
            }
        }
    }

    private async void Forward_Click(object sender, RoutedEventArgs e) => await ReplyToSelectedAsync(forward: true);

    private async void Move_Click(object sender, RoutedEventArgs e)
    {
        var conversation = ViewModel.SelectedConversation?.Model;
        if (conversation is null) return;
        var folders = (await ViewModel.GetFoldersForAccountAsync(conversation.AccountId))
            .Where(static folder => folder.SpecialKind != SpecialFolderKind.Drafts && folder.SpecialKind != SpecialFolderKind.Sent && folder.SpecialKind != SpecialFolderKind.AllMail)
            .ToArray();
        if (folders.Length == 0)
        {
            await ShowErrorAsync("No destination folders", "This account does not expose another folder to move the message to.");
            return;
        }
        var picker = new ComboBox
        {
            Header = "Destination",
            ItemsSource = folders,
            DisplayMemberPath = nameof(MailFolder.Name),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 400
        };
        var dialog = CreateDialog("Move conversation", picker, "Move", "Cancel");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && picker.SelectedItem is MailFolder destination)
        {
            await ViewModel.MoveSelectedAsync(destination);
            ViewModel.StatusText = $"Moved to {destination.Name}";
        }
    }

    private void Drafts_Click(object sender, RoutedEventArgs e)
    {
        var window = new DraftsWindow(ViewModel);
        window.DraftOpenRequested += async draft =>
        {
            try
            {
                var editable = await ViewModel.PrepareDraftForEditingAsync(draft);
                await ShowComposerAsync(existingDraft: editable);
            }
            catch (Exception exception)
            {
                await ShowErrorAsync("Draft cannot be opened", exception.Message);
            }
        };
        TrackChildWindow(window);
        window.Activate();
    }

    private async void DraftList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DraftRowItem row) return;
        try
        {
            var editable = await ViewModel.PrepareDraftForEditingAsync(row.Draft);
            await ShowComposerAsync(existingDraft: editable);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("Draft cannot be opened", exception.Message);
        }
    }

    private async void LegacyDrafts_Click(object sender, RoutedEventArgs e)
    {
        var drafts = await ViewModel.GetDraftsAsync(ViewModel.SelectedAccount?.Model?.Id);
        if (drafts.Count == 0)
        {
            var empty = CreateDialog("Drafts & outbox", new TextBlock { Text = "There are no saved or queued messages." }, string.Empty, "OK");
            await empty.ShowAsync();
            return;
        }

        var items = drafts.Select(static draft => new DraftPickerItem(draft)).ToArray();
        var list = new ListView
        {
            ItemsSource = items,
            DisplayMemberPath = nameof(DraftPickerItem.Display),
            SelectedIndex = 0,
            MinWidth = 520,
            MaxHeight = 430,
            SelectionMode = ListViewSelectionMode.Single
        };
        var panel = Form(
            new TextBlock { Text = "Open a draft to continue editing, or delete it locally.", TextWrapping = TextWrapping.Wrap },
            list);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Drafts & outbox",
            Content = panel,
            PrimaryButtonText = "Open",
            SecondaryButtonText = "Delete",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (list.SelectedItem is not DraftPickerItem selected)
        {
            return;
        }

        if (result == ContentDialogResult.Secondary)
        {
            await ViewModel.DeleteDraftAsync(selected.Draft.Id);
            ViewModel.StatusText = "Draft deleted";
        }
        else if (result == ContentDialogResult.Primary)
        {
            try
            {
                var editable = await ViewModel.PrepareDraftForEditingAsync(selected.Draft);
                await ShowComposerAsync(existingDraft: editable);
            }
            catch (Exception exception)
            {
                await ShowErrorAsync("Draft cannot be opened", exception.Message);
            }
        }
    }

    private Task ShowComposerAsync(
        string? recipient = null,
        string? subject = null,
        string? body = null,
        string? replyToRemoteId = null,
        string? providerThreadId = null,
        Draft? existingDraft = null,
        MailMessage? threadContext = null)
    {
        var accounts = ViewModel.Accounts.Where(static item => item.Model is not null).ToArray();
        if (accounts.Length == 0)
        {
            _ = ShowErrorAsync("No sending account", "Connect an account before writing a message.");
            return Task.CompletedTask;
        }

        var window = new ComposeWindow(
            ViewModel,
            recipient,
            subject,
            body,
            replyToRemoteId,
            providerThreadId,
            existingDraft,
            threadContext);
        TrackChildWindow(window);
        window.Activate();
        return Task.CompletedTask;
    }

    private async Task ShowLegacyComposerAsync(
        string? recipient = null,
        string? subject = null,
        string? body = null,
        string? replyToRemoteId = null,
        string? providerThreadId = null,
        Draft? existingDraft = null)
    {
        var accounts = ViewModel.Accounts.Where(static item => item.Model is not null).ToArray();
        if (accounts.Length == 0)
        {
            await ShowErrorAsync("No sending account", "Connect an account before writing a message.");
            return;
        }

        var preferredAccountId = existingDraft?.AccountId ?? ViewModel.SelectedAccount?.Model?.Id;
        var from = new ComboBox
        {
            Header = "From",
            DisplayMemberPath = nameof(AccountItem.DisplayName),
            ItemsSource = accounts,
            SelectedItem = accounts.FirstOrDefault(item => item.Model?.Id == preferredAccountId) ?? accounts[0],
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var to = new AutoSuggestBox
        {
            Header = "To",
            Text = existingDraft is null ? recipient ?? string.Empty : FormatAddresses(existingDraft.To),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            UpdateTextOnSelect = false
        };
        var cc = Field("Cc", existingDraft is null ? string.Empty : FormatAddresses(existingDraft.Cc));
        var bcc = Field("Bcc", existingDraft is null ? string.Empty : FormatAddresses(existingDraft.Bcc));
        var subjectBox = Field("Subject", existingDraft?.Subject ?? subject ?? string.Empty);
        var bodyBox = new TextBox
        {
            Header = "Message",
            Text = existingDraft?.PlainTextBody ?? body ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 250,
            VerticalContentAlignment = VerticalAlignment.Top,
            IsSpellCheckEnabled = true
        };
        var signatureHint = new TextBlock
        {
            Text = "Your default signature will be appended automatically when selected below.",
            FontSize = 11,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap
        };
        var signature = new ComboBox { Header = "Signature", HorizontalAlignment = HorizontalAlignment.Stretch };
        var attachmentPaths = existingDraft?.Attachments.Select(static item => item.LocalPath).Where(File.Exists).ToList() ?? new List<string>();
        var attachmentSummary = new TextBlock { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        attachmentSummary.Text = attachmentPaths.Count == 0 ? string.Empty : string.Join("  ·  ", attachmentPaths.Select(Path.GetFileName));
        var attachmentButton = new Button { Content = "Attach files", HorizontalAlignment = HorizontalAlignment.Left };
        var removeAttachmentButton = new Button { Content = "Remove last attachment", HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = attachmentPaths.Count > 0 };
        var saveStatus = new TextBlock { Text = existingDraft is null ? string.Empty : "Saved", FontSize = 11, Opacity = 0.65 };
        var draftId = existingDraft?.Id ?? Guid.NewGuid();
        var dirty = false;
        var saving = false;
        var knownAddresses = await ViewModel.GetKnownAddressesAsync(preferredAccountId);

        to.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var fragment = to.Text.Split(new[] { ',', ';' }).LastOrDefault()?.Trim() ?? string.Empty;
            to.ItemsSource = fragment.Length < 2
                ? Array.Empty<string>()
                : knownAddresses
                    .Where(address => address.DisplayName.Contains(fragment, StringComparison.CurrentCultureIgnoreCase) || address.Address.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    .Take(8)
                    .Select(static address => string.IsNullOrWhiteSpace(address.Name) ? address.Address : $"{address.Name} <{address.Address}>")
                    .ToArray();
        };
        to.SuggestionChosen += (_, args) =>
        {
            if (args.SelectedItem is not string selected) return;
            var parts = to.Text.Split(new[] { ',', ';' }).ToList();
            if (parts.Count == 0) parts.Add(selected);
            else parts[^1] = " " + selected;
            to.Text = string.Join(",", parts).TrimStart();
        };

        void MarkDirty(object? _, object __)
        {
            dirty = true;
            saveStatus.Text = "Unsaved changes";
        }

        to.TextChanged += MarkDirty;
        cc.TextChanged += MarkDirty;
        bcc.TextChanged += MarkDirty;
        subjectBox.TextChanged += MarkDirty;
        bodyBox.TextChanged += MarkDirty;
        from.SelectionChanged += MarkDirty;
        attachmentButton.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            var selected = await picker.PickMultipleFilesAsync();
            foreach (var file in selected)
            {
                if (!attachmentPaths.Contains(file.Path, StringComparer.OrdinalIgnoreCase)) attachmentPaths.Add(file.Path);
            }
            attachmentSummary.Text = attachmentPaths.Count == 0
                ? string.Empty
                : string.Join("  ·  ", attachmentPaths.Select(Path.GetFileName));
            removeAttachmentButton.IsEnabled = attachmentPaths.Count > 0;
            dirty = true;
            saveStatus.Text = "Unsaved changes";
        };
        removeAttachmentButton.Click += (_, _) =>
        {
            if (attachmentPaths.Count == 0) return;
            attachmentPaths.RemoveAt(attachmentPaths.Count - 1);
            attachmentSummary.Text = attachmentPaths.Count == 0 ? string.Empty : string.Join("  ·  ", attachmentPaths.Select(Path.GetFileName));
            removeAttachmentButton.IsEnabled = attachmentPaths.Count > 0;
            dirty = true;
            saveStatus.Text = "Unsaved changes";
        };

        async Task LoadSignaturesAsync()
        {
            signature.Items.Clear();
            signature.Items.Add(new ComboBoxItem { Content = "No signature", Tag = null });
            var selected = (AccountItem)from.SelectedItem;
            var signatures = await ViewModel.GetSignaturesAsync(selected.Model!.Id);
            var isReply = !string.IsNullOrWhiteSpace(existingDraft?.ReplyToRemoteId ?? replyToRemoteId);
            var selectedIndex = 0;
            foreach (var item in signatures)
            {
                signature.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item });
                if ((isReply && item.IsDefaultForReplies) || (!isReply && item.IsDefaultForNew)) selectedIndex = signature.Items.Count - 1;
            }
            signature.SelectedIndex = selectedIndex;
        }

        from.SelectionChanged += async (_, _) => await LoadSignaturesAsync();
        await LoadSignaturesAsync();

        Draft Snapshot(bool includeSignature)
        {
            var selectedAccount = ((AccountItem)from.SelectedItem).Model!;
            var messageBody = bodyBox.Text;
            if (includeSignature && signature.SelectedItem is ComboBoxItem { Tag: Signature selectedSignature })
            {
                messageBody = messageBody.TrimEnd() + "\n\n" + selectedSignature.PlainText;
            }
            return new Draft
            {
                Id = draftId,
                AccountId = selectedAccount.Id,
                RemoteId = existingDraft?.RemoteId,
                To = ParseAddresses(to.Text),
                Cc = ParseAddresses(cc.Text),
                Bcc = ParseAddresses(bcc.Text),
                Subject = subjectBox.Text.Trim(),
                PlainTextBody = messageBody,
                HtmlBody = $"<p>{System.Net.WebUtility.HtmlEncode(messageBody).Replace("\n", "<br>")}</p>",
                ReplyToRemoteId = existingDraft?.ReplyToRemoteId ?? replyToRemoteId,
                ProviderThreadId = existingDraft?.ProviderThreadId ?? providerThreadId,
                Attachments = attachmentPaths.Select(path => new OutgoingAttachment(Path.GetFileName(path), ContentType(path), path)).ToArray(),
                UpdatedAt = DateTimeOffset.UtcNow,
                DeliveryState = DraftDeliveryState.Draft
            };
        }

        static bool HasContent(Draft draft) =>
            draft.To.Count > 0 || draft.Cc.Count > 0 || draft.Bcc.Count > 0 ||
            !string.IsNullOrWhiteSpace(draft.Subject) || !string.IsNullOrWhiteSpace(draft.PlainTextBody) || draft.Attachments.Count > 0;

        async Task SaveNowAsync()
        {
            if (!dirty || saving)
            {
                return;
            }
            var snapshot = Snapshot(false);
            if (!HasContent(snapshot))
            {
                return;
            }
            saving = true;
            saveStatus.Text = "Saving…";
            try
            {
                await ViewModel.SaveDraftAsync(snapshot);
                dirty = false;
                saveStatus.Text = $"Saved · {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception exception)
            {
                saveStatus.Text = "Could not save: " + exception.Message;
            }
            finally
            {
                saving = false;
            }
        }

        var autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        autosave.Tick += async (_, _) => await SaveNowAsync();

        var attachmentButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        attachmentButtons.Children.Add(attachmentButton);
        attachmentButtons.Children.Add(removeAttachmentButton);
        var form = Form(from, to, cc, bcc, subjectBox, bodyBox, attachmentButtons, attachmentSummary, signatureHint, signature, saveStatus);
        var dialog = CreateDialog(existingDraft is null ? "New message" : "Edit draft", new ScrollViewer { Content = form, MaxHeight = 650 }, "Send", "Save & close");
        dialog.MinWidth = 680;
        var sendWithKeyboard = false;
        var sendAccelerator = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Enter, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        sendAccelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            sendWithKeyboard = true;
            dialog.Hide();
        };
        dialog.KeyboardAccelerators.Add(sendAccelerator);
        autosave.Start();
        var dialogResult = await dialog.ShowAsync();
        if (sendWithKeyboard) dialogResult = ContentDialogResult.Primary;
        autosave.Stop();

        if (dialogResult != ContentDialogResult.Primary)
        {
            dirty = true;
            await SaveNowAsync();
            return;
        }

        var draft = Snapshot(true);
        if (draft.To.Count + draft.Cc.Count + draft.Bcc.Count == 0)
        {
            dirty = true;
            await SaveNowAsync();
            await ShowErrorAsync("Recipient required", "Add at least one valid email address. The message remains in Drafts.");
            return;
        }

        Draft? remaining;
        try
        {
            remaining = await ViewModel.QueueDraftForSendAsync(draft);
        }
        catch (Exception exception)
        {
            dirty = true;
            await ViewModel.SaveDraftAsync(draft with { DeliveryState = DraftDeliveryState.Draft, LastError = exception.Message });
            await ShowErrorAsync("Message was not queued", exception.Message + " The message remains in Drafts.");
            return;
        }
        if (remaining?.DeliveryState == DraftDeliveryState.Failed)
        {
            await ShowErrorAsync("Message was not sent", remaining.LastError ?? "The mail server rejected the message. It remains in Drafts.");
        }
        else if (remaining is not null)
        {
            ViewModel.StatusText = "Message queued and will send automatically";
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = CreateSettingsWindow();
        TrackChildWindow(window);
        window.Activate();
    }

    private SettingsWindow CreateSettingsWindow(string initialPage = "general")
    {
        var window = new SettingsWindow(ViewModel, initialPage);
        window.AddAccountRequested += (_, _) => AddAccount_Click(window, new RoutedEventArgs());
        window.SettingsChanged += (_, _) =>
        {
            var value = ApplicationData.Current.LocalSettings.Values["readingPanePosition"] as string;
            preferredReadingPane = value == "bottom" ? ReadingPanePlacement.Bottom : ReadingPanePlacement.Right;
            ApplyResponsiveLayout(ActualWidth, ActualHeight, animate: true);
            ViewModel.RefreshMessagePresentation();
        };
        return window;
    }

    private void TrackChildWindow(Window window)
    {
        childWindows.Add(window);
        window.Closed += (_, _) => childWindows.Remove(window);
    }

    private async void LegacySettings_Click(object sender, RoutedEventArgs e)
    {
        var local = ApplicationData.Current.LocalSettings.Values;
        var microsoftId = Field("Microsoft desktop app client ID", local["microsoftClientId"] as string ?? string.Empty);
        var notifications = new ToggleSwitch
        {
            Header = "New mail notifications",
            IsOn = !local.TryGetValue("notificationsEnabled", out var notificationsValue) || notificationsValue is true
        };
        var closeToTray = new ToggleSwitch
        {
            Header = "Keep syncing when I close the window",
            OffContent = "Exit on close",
            OnContent = "Keep in system tray",
            IsOn = !local.TryGetValue("closeToTray", out var trayValue) || trayValue is true
        };
        var blockRemoteImages = new ToggleSwitch
        {
            Header = "Block external images",
            OffContent = "Load automatically",
            OnContent = "Blocked for privacy",
            IsOn = local.TryGetValue("blockRemoteImages", out var blockImagesValue) && blockImagesValue is true
        };

        var accountPicker = new ComboBox
        {
            Header = "Signature account",
            DisplayMemberPath = nameof(AccountItem.DisplayName),
            ItemsSource = ViewModel.Accounts.Where(static item => item.Model is not null).ToArray(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (accountPicker.Items.Count > 0) accountPicker.SelectedIndex = 0;
        var signaturePicker = new ComboBox { Header = "Existing signature", HorizontalAlignment = HorizontalAlignment.Stretch };
        var signatureName = Field("Signature name", "Personal");
        var signatureBody = new TextBox { Header = "Signature", AcceptsReturn = true, MinHeight = 95, TextWrapping = TextWrapping.Wrap };
        var defaultForNew = new CheckBox { Content = "Default for new messages", IsChecked = true };
        var defaultForReplies = new CheckBox { Content = "Default for replies", IsChecked = true };
        var deleteSignature = new Button { Content = "Delete selected signature", HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false };

        async Task LoadSignatureEditorAsync()
        {
            signaturePicker.Items.Clear();
            signaturePicker.Items.Add(new ComboBoxItem { Content = "New signature", Tag = null });
            if (accountPicker.SelectedItem is AccountItem { Model: { } account })
            {
                foreach (var item in await ViewModel.GetSignaturesAsync(account.Id))
                {
                    signaturePicker.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item });
                }
            }
            signaturePicker.SelectedIndex = 0;
        }

        signaturePicker.SelectionChanged += (_, _) =>
        {
            if (signaturePicker.SelectedItem is ComboBoxItem { Tag: Signature selected })
            {
                signatureName.Text = selected.Name;
                signatureBody.Text = selected.PlainText;
                defaultForNew.IsChecked = selected.IsDefaultForNew;
                defaultForReplies.IsChecked = selected.IsDefaultForReplies;
                deleteSignature.IsEnabled = true;
            }
            else
            {
                signatureName.Text = "Personal";
                signatureBody.Text = string.Empty;
                defaultForNew.IsChecked = true;
                defaultForReplies.IsChecked = true;
                deleteSignature.IsEnabled = false;
            }
        };
        accountPicker.SelectionChanged += async (_, _) => await LoadSignatureEditorAsync();
        deleteSignature.Click += async (_, _) =>
        {
            if (signaturePicker.SelectedItem is ComboBoxItem { Tag: Signature selected })
            {
                await ViewModel.DeleteSignatureAsync(selected.Id);
                await LoadSignatureEditorAsync();
            }
        };
        await LoadSignatureEditorAsync();

        var panel = Form(
            Section("General"),
            notifications,
            closeToTray,
            Section("Signatures"),
            accountPicker,
            signaturePicker,
            signatureName,
            signatureBody,
            defaultForNew,
            defaultForReplies,
            deleteSignature,
            Section("Integrations"),
            new TextBlock { Text = "Google OAuth is included in official Inboxwell builds. The Microsoft client ID below is only needed for Microsoft 365.", TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = 11 },
            microsoftId,
            Section("Privacy"),
            blockRemoteImages,
            new TextBlock { Text = "Mail and search index are encrypted locally with SQLCipher. Passwords and OAuth tokens are stored in Windows Credential Manager.", TextWrapping = TextWrapping.Wrap });

        var dialog = CreateDialog("Settings", new ScrollViewer { Content = panel, MaxHeight = 620 }, "Save", "Close");
        dialog.MinWidth = 580;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        local["microsoftClientId"] = microsoftId.Text.Trim();
        local["notificationsEnabled"] = notifications.IsOn;
        local["closeToTray"] = closeToTray.IsOn;
        local["blockRemoteImages"] = blockRemoteImages.IsOn;
        ViewModel.RefreshMessagePresentation();
        if (accountPicker.SelectedItem is AccountItem { Model: { } signatureAccount } && !string.IsNullOrWhiteSpace(signatureBody.Text))
        {
            var existingSignature = (signaturePicker.SelectedItem as ComboBoxItem)?.Tag as Signature;
            await ViewModel.SaveSignatureAsync(new Signature
            {
                Id = existingSignature?.Id ?? Guid.NewGuid(),
                AccountId = signatureAccount.Id,
                Name = string.IsNullOrWhiteSpace(signatureName.Text) ? "Signature" : signatureName.Text.Trim(),
                PlainText = signatureBody.Text,
                Html = $"<p>{System.Net.WebUtility.HtmlEncode(signatureBody.Text).Replace("\n", "<br>")}</p>",
                IsDefaultForNew = defaultForNew.IsChecked == true,
                IsDefaultForReplies = defaultForReplies.IsChecked == true
            });
        }
        ViewModel.StatusText = "Settings saved";
    }

    private ContentDialog CreateDialog(string title, object content, string primary, string close) => new()
    {
        XamlRoot = XamlRoot,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = close,
        DefaultButton = ContentDialogButton.Primary
    };

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = CreateDialog(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 }, string.Empty, "OK");
        await dialog.ShowAsync();
    }

    private static StackPanel Form(params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 430 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    private static TextBox Field(string header, string text = "") => new()
    {
        Header = header,
        Text = text,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static ComboBox SecurityPicker(string first, string second)
    {
        var picker = new ComboBox { Header = "Connection security", HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
        picker.Items.Add(first);
        picker.Items.Add(second);
        return picker;
    }

    private static string SecurityValue(ComboBox picker) =>
        (picker.SelectedItem?.ToString() ?? string.Empty).StartsWith("SSL", StringComparison.OrdinalIgnoreCase) ? "ssl" : "starttls";

    private static int ParsePort(string text, int fallback) => int.TryParse(text, out var value) && value is > 0 and <= 65535 ? value : fallback;

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".txt" => "text/plain",
        ".html" or ".htm" => "text/html",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".zip" => "application/zip",
        _ => "application/octet-stream"
    };

    private static IReadOnlyList<MailAddress> ParseAddresses(string text) => text
        .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(static value =>
        {
            try
            {
                var parsed = new System.Net.Mail.MailAddress(value);
                return new MailAddress(parsed.DisplayName, parsed.Address);
            }
            catch (FormatException)
            {
                return null;
            }
        })
        .OfType<MailAddress>()
        .DistinctBy(static address => address.Address, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string FormatAddresses(IEnumerable<MailAddress> addresses) =>
        string.Join(", ", addresses.Select(static address => string.IsNullOrWhiteSpace(address.Name) ? address.Address : $"{address.Name} <{address.Address}>"));

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable Display"),
        FontSize = 18,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(0, 8, 0, 0)
    };
}

internal sealed class DraftPickerItem
{
    public DraftPickerItem(Draft draft)
    {
        Draft = draft;
        var subject = string.IsNullOrWhiteSpace(draft.Subject) ? "(no subject)" : draft.Subject;
        var recipient = draft.To.FirstOrDefault()?.Address ?? "No recipient";
        var state = draft.DeliveryState switch
        {
            DraftDeliveryState.Queued => "Queued",
            DraftDeliveryState.Sending => "Sending",
            DraftDeliveryState.Failed => "Failed",
            _ => "Draft"
        };
        Display = $"{subject}  ·  {recipient}  ·  {state}  ·  {draft.UpdatedAt.ToLocalTime():g}";
    }

    public Draft Draft { get; }
    public string Display { get; }
}

internal enum ReadingPanePlacement
{
    Right,
    Bottom
}
