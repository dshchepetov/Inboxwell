using System.Collections.ObjectModel;
using System.Net;
using Gomail.Core;
using Gomail_App.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace Gomail_App;

public sealed partial class ComposeWindow : Window
{
    private readonly MainPageViewModel viewModel;
    private readonly Draft? existingDraft;
    private readonly string? replyToRemoteId;
    private readonly string? providerThreadId;
    private readonly ObservableCollection<ComposeAttachmentItem> attachments = new();
    private readonly DispatcherTimer autosave = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<MailAddress> knownAddresses = Array.Empty<MailAddress>();
    private Guid draftId;
    private bool dirty;
    private bool saving;
    private bool allowClose;
    private bool initialized;

    public ComposeWindow(
        MainPageViewModel viewModel,
        string? recipient,
        string? subject,
        string? body,
        string? replyToRemoteId,
        string? providerThreadId,
        Draft? existingDraft)
    {
        this.viewModel = viewModel;
        this.existingDraft = existingDraft;
        this.replyToRemoteId = replyToRemoteId;
        this.providerThreadId = providerThreadId;
        draftId = existingDraft?.Id ?? Guid.NewGuid();

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ComposeTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1040, 760));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 680;
            presenter.PreferredMinimumHeight = 560;
        }
        AppWindow.Closing += Window_Closing;
        ComposeRoot.Loaded += async (_, _) => await InitializeAsync(recipient, subject, body);
        autosave.Tick += async (_, _) => await SaveNowAsync();
        AttachmentsList.ItemsSource = attachments;
    }

    private async Task InitializeAsync(string? recipient, string? subject, string? body)
    {
        var accounts = viewModel.Accounts.Where(static item => item.Model is not null).ToArray();
        FromPicker.ItemsSource = accounts;
        FromPicker.DisplayMemberPath = nameof(AccountItem.MailboxDisplay);
        var preferredAccountId = existingDraft?.AccountId ?? viewModel.SelectedAccount?.Model?.Id;
        FromPicker.SelectedItem = accounts.FirstOrDefault(item => item.Model?.Id == preferredAccountId) ?? accounts.FirstOrDefault();

        ToBox.Text = existingDraft is null ? recipient ?? string.Empty : FormatAddresses(existingDraft.To);
        CcBox.Text = existingDraft is null ? string.Empty : FormatAddresses(existingDraft.Cc);
        BccBox.Text = existingDraft is null ? string.Empty : FormatAddresses(existingDraft.Bcc);
        SubjectBox.Text = existingDraft?.Subject ?? subject ?? string.Empty;
        BodyEditor.Document.SetText(TextSetOptions.None, existingDraft?.PlainTextBody ?? body ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(CcBox.Text)) ShowCc();
        if (!string.IsNullOrWhiteSpace(BccBox.Text)) ShowBcc();
        foreach (var item in existingDraft?.Attachments ?? Array.Empty<OutgoingAttachment>())
        {
            if (File.Exists(item.LocalPath)) attachments.Add(new ComposeAttachmentItem(item.LocalPath));
        }

        await LoadAddressesAsync();
        await LoadSignaturesAsync();
        initialized = true;
        dirty = false;
        SaveStatus.Text = existingDraft is null ? "Compose stays open while you read mail" : "Draft loaded";
        autosave.Start();
        BeginEntranceAnimation();
        ToBox.Focus(FocusState.Programmatic);
    }

    private void BeginEntranceAnimation()
    {
        var storyboard = new Storyboard();
        var opacity = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(210), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var offset = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(260), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(opacity, ComposeRoot);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(offset, ComposeTransform);
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
        var offset = new DoubleAnimation { To = 7, Duration = TimeSpan.FromMilliseconds(150), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        Storyboard.SetTarget(opacity, ComposeRoot);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(offset, ComposeTransform);
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
        autosave.Stop();
        dirty = true;
        await SaveNowAsync();
        await BeginExitAnimationAsync();
        allowClose = true;
        Close();
    }

    private async void FromPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || FromPicker.SelectedItem is null) return;
        await LoadAddressesAsync();
        await LoadSignaturesAsync();
        MarkDirty();
    }

    private async Task LoadAddressesAsync()
    {
        knownAddresses = await viewModel.GetKnownAddressesAsync((FromPicker.SelectedItem as AccountItem)?.Model?.Id);
    }

    private async Task LoadSignaturesAsync()
    {
        if (FromPicker.SelectedItem is not AccountItem { Model: { } account }) return;
        var choices = new List<SignatureChoice> { new("No signature", null) };
        var isReply = !string.IsNullOrWhiteSpace(existingDraft?.ReplyToRemoteId ?? replyToRemoteId);
        var selectedIndex = 0;
        foreach (var signature in await viewModel.GetSignaturesAsync(account.Id))
        {
            choices.Add(new SignatureChoice(signature.Name, signature));
            if ((isReply && signature.IsDefaultForReplies) || (!isReply && signature.IsDefaultForNew)) selectedIndex = choices.Count - 1;
        }
        SignaturePicker.ItemsSource = choices;
        SignaturePicker.DisplayMemberPath = nameof(SignatureChoice.Name);
        SignaturePicker.SelectedIndex = selectedIndex;
    }

    private void SignaturePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SignaturePicker.SelectedItem is SignatureChoice { Signature: { } signature })
        {
            SignaturePreview.Visibility = Visibility.Visible;
            SignaturePreviewText.Text = signature.PlainText;
        }
        else
        {
            SignaturePreview.Visibility = Visibility.Collapsed;
            SignaturePreviewText.Text = string.Empty;
        }
        MarkDirty();
    }

    private void RecipientBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            var fragment = sender.Text.Split(new[] { ',', ';' }).LastOrDefault()?.Trim() ?? string.Empty;
            sender.ItemsSource = fragment.Length < 2
                ? Array.Empty<string>()
                : knownAddresses
                    .Where(address => address.DisplayName.Contains(fragment, StringComparison.CurrentCultureIgnoreCase) || address.Address.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    .Take(8)
                    .Select(FormatAddress)
                    .ToArray();
        }
        MarkDirty();
    }

    private void RecipientBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not string selected) return;
        var parts = sender.Text.Split(new[] { ',', ';' }).ToList();
        if (parts.Count == 0) parts.Add(selected);
        else parts[^1] = " " + selected;
        sender.Text = string.Join(",", parts).TrimStart();
    }

    private void Field_TextChanged(object sender, TextChangedEventArgs e) => MarkDirty();
    private void BodyEditor_TextChanged(object sender, RoutedEventArgs e) => MarkDirty();

    private void MarkDirty()
    {
        if (!initialized) return;
        dirty = true;
        SaveStatus.Text = "Unsaved changes";
    }

    private void ToggleCc_Click(object sender, RoutedEventArgs e) => ShowCc();
    private void ToggleBcc_Click(object sender, RoutedEventArgs e) => ShowBcc();
    private void ShowCc() { CcRow.Height = new GridLength(44); CcBox.Focus(FocusState.Programmatic); }
    private void ShowBcc() { BccRow.Height = new GridLength(44); BccBox.Focus(FocusState.Programmatic); }

    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var selected = await picker.PickMultipleFilesAsync();
        foreach (var file in selected)
        {
            if (attachments.All(item => !item.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)))
                attachments.Add(new ComposeAttachmentItem(file.Path));
        }
        MarkDirty();
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ComposeAttachmentItem item) attachments.Remove(item);
        MarkDirty();
    }

    private void Bold_Click(object sender, RoutedEventArgs e)
    {
        var format = BodyEditor.Document.Selection.CharacterFormat;
        format.Bold = format.Bold == FormatEffect.On ? FormatEffect.Off : FormatEffect.On;
        BodyEditor.Focus(FocusState.Programmatic);
    }

    private void Italic_Click(object sender, RoutedEventArgs e)
    {
        var format = BodyEditor.Document.Selection.CharacterFormat;
        format.Italic = format.Italic == FormatEffect.On ? FormatEffect.Off : FormatEffect.On;
        BodyEditor.Focus(FocusState.Programmatic);
    }

    private void Underline_Click(object sender, RoutedEventArgs e)
    {
        var format = BodyEditor.Document.Selection.CharacterFormat;
        format.Underline = format.Underline == UnderlineType.None ? UnderlineType.Single : UnderlineType.None;
        BodyEditor.Focus(FocusState.Programmatic);
    }

    private void Bullet_Click(object sender, RoutedEventArgs e)
    {
        BodyEditor.Document.Selection.SetText(TextSetOptions.None, "• ");
        BodyEditor.Focus(FocusState.Programmatic);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) { if (BodyEditor.Document.CanUndo()) BodyEditor.Document.Undo(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { if (BodyEditor.Document.CanRedo()) BodyEditor.Document.Redo(); }

    private async void SaveAndClose_Click(object sender, RoutedEventArgs e)
    {
        dirty = true;
        await SaveNowAsync();
        await CloseAfterActionAsync();
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();
    private async void SendShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SendAsync();
    }

    private async Task SendAsync()
    {
        var draft = Snapshot(includeSignature: true);
        if (draft.To.Count + draft.Cc.Count + draft.Bcc.Count == 0)
        {
            ShowMessage("Add at least one valid email address. Your draft is still safe.", InfoBarSeverity.Warning);
            dirty = true;
            await SaveNowAsync();
            ToBox.Focus(FocusState.Programmatic);
            return;
        }

        SaveStatus.Text = "Sending…";
        try
        {
            var remaining = await viewModel.QueueDraftForSendAsync(draft);
            if (remaining?.DeliveryState == DraftDeliveryState.Failed)
            {
                ShowMessage(remaining.LastError ?? "The mail server rejected this message. It remains in Drafts.", InfoBarSeverity.Error);
                return;
            }
            viewModel.StatusText = remaining is null ? "Message sent" : "Message queued and will send automatically";
            dirty = false;
            await CloseAfterActionAsync();
        }
        catch (Exception exception)
        {
            await viewModel.SaveDraftAsync(draft with { DeliveryState = DraftDeliveryState.Draft, LastError = exception.Message });
            ShowMessage(exception.Message + " The message remains in Drafts.", InfoBarSeverity.Error);
        }
    }

    private async Task CloseAfterActionAsync()
    {
        autosave.Stop();
        await BeginExitAnimationAsync();
        allowClose = true;
        Close();
    }

    private async Task SaveNowAsync()
    {
        if (!dirty || saving || FromPicker.SelectedItem is not AccountItem) return;
        var draft = Snapshot(includeSignature: false);
        if (!HasContent(draft)) return;
        saving = true;
        SaveStatus.Text = "Saving…";
        try
        {
            await viewModel.SaveDraftAsync(draft);
            dirty = false;
            SaveStatus.Text = $"Saved · {DateTime.Now:HH:mm}";
        }
        catch (Exception exception)
        {
            SaveStatus.Text = "Could not save";
            ShowMessage(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            saving = false;
        }
    }

    private Draft Snapshot(bool includeSignature)
    {
        var account = ((AccountItem)FromPicker.SelectedItem).Model!;
        BodyEditor.Document.GetText(TextGetOptions.None, out var plainBody);
        plainBody = plainBody.TrimEnd('\r');
        if (includeSignature && SignaturePicker.SelectedItem is SignatureChoice { Signature: { } signature })
            plainBody = plainBody.TrimEnd() + "\n\n" + signature.PlainText;

        return new Draft
        {
            Id = draftId,
            AccountId = account.Id,
            RemoteId = existingDraft?.RemoteId,
            To = ParseAddresses(ToBox.Text),
            Cc = ParseAddresses(CcBox.Text),
            Bcc = ParseAddresses(BccBox.Text),
            Subject = SubjectBox.Text.Trim(),
            PlainTextBody = plainBody,
            HtmlBody = $"<p>{WebUtility.HtmlEncode(plainBody).Replace("\n", "<br>")}</p>",
            ReplyToRemoteId = existingDraft?.ReplyToRemoteId ?? replyToRemoteId,
            ProviderThreadId = existingDraft?.ProviderThreadId ?? providerThreadId,
            Attachments = attachments.Select(item => new OutgoingAttachment(item.FileName, ContentType(item.Path), item.Path)).ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow,
            DeliveryState = DraftDeliveryState.Draft
        };
    }

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        ComposeInfoBar.Message = message;
        ComposeInfoBar.Severity = severity;
        ComposeInfoBar.IsOpen = true;
    }

    private static bool HasContent(Draft draft) =>
        draft.To.Count > 0 || draft.Cc.Count > 0 || draft.Bcc.Count > 0 ||
        !string.IsNullOrWhiteSpace(draft.Subject) || !string.IsNullOrWhiteSpace(draft.PlainTextBody) || draft.Attachments.Count > 0;

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

    private static string FormatAddress(MailAddress address) =>
        string.IsNullOrWhiteSpace(address.Name) ? address.Address : $"{address.Name} <{address.Address}>";

    private static string FormatAddresses(IEnumerable<MailAddress> addresses) => string.Join(", ", addresses.Select(FormatAddress));

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
}

internal sealed record ComposeAttachmentItem(string Path)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

internal sealed record SignatureChoice(string Name, Signature? Signature);
