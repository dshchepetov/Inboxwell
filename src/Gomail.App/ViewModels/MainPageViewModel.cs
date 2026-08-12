using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gomail.Core;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Windows.Storage;

namespace Gomail_App.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    private readonly IMailStore store;
    private readonly ISyncCoordinator sync;
    private readonly IMailProviderRegistry providers;
    private readonly IConnectivity connectivity;
    private readonly IHtmlSanitizer htmlSanitizer;
    private readonly IDictionary<string, object> localSettings = ApplicationData.Current.LocalSettings.Values;
    private bool suppressSelectionChanges;
    private CancellationTokenSource? markReadCancellation;

    public MainPageViewModel(IMailStore store, ISyncCoordinator sync, IMailProviderRegistry providers, IConnectivity connectivity, IHtmlSanitizer htmlSanitizer)
    {
        this.store = store;
        this.sync = sync;
        this.providers = providers;
        this.connectivity = connectivity;
        this.htmlSanitizer = htmlSanitizer;
        connectivity.Changed += (_, online) =>
        {
            IsOnline = online;
            StatusText = online ? "Online" : "Offline · changes will sync later";
        };
        IsOnline = connectivity.IsOnline;
    }

    public ObservableCollection<AccountItem> Accounts { get; } = new();
    public ObservableCollection<FolderItem> Folders { get; } = new();
    public ObservableCollection<ConversationItem> Conversations { get; } = new();
    public ObservableCollection<MessageItem> Messages { get; } = new();

    [ObservableProperty] public partial AccountItem? SelectedAccount { get; set; }
    [ObservableProperty] public partial FolderItem? SelectedFolder { get; set; }
    [ObservableProperty] public partial ConversationItem? SelectedConversation { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsOnline { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusText { get; set; } = "Ready";
    [ObservableProperty] public partial string ListTitle { get; set; } = "Inbox";
    [ObservableProperty] public partial string ResultsText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasSelection { get; set; }
    [ObservableProperty] public partial bool HasNoSelection { get; set; } = true;

    public async Task InitializeAsync()
    {
        IsBusy = true;
        suppressSelectionChanges = true;
        try
        {
            await ReloadAccountsAsync();
            await LoadFoldersAsync();
            await LoadConversationsAsync();
            StatusText = connectivity.IsOnline ? "Up to date" : "Offline · showing local mail";
        }
        finally
        {
            suppressSelectionChanges = false;
            IsBusy = false;
        }
    }

    public async Task ReloadAccountsAsync(Guid? selectAccountId = null)
    {
        var previousId = selectAccountId ?? SelectedAccount?.Model?.Id ?? ReadGuidSetting("selectedAccountId");
        Accounts.Clear();
        Accounts.Add(AccountItem.Unified());
        foreach (var account in await store.GetAccountsAsync())
        {
            Accounts.Add(new AccountItem(account));
        }
        SelectedAccount = Accounts.FirstOrDefault(item => item.Model?.Id == previousId) ?? Accounts[0];
    }

    public async Task ReloadLocalAsync()
    {
        if (IsBusy) return;
        await LoadFoldersAsync();
        await LoadConversationsAsync();
    }

    public async Task AddAccountAsync(MailAccount account, string? password = null)
    {
        if ((await store.GetAccountsAsync()).Any(existing => existing.Provider == account.Provider && existing.Email.Equals(account.Email, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("This mailbox is already connected.");
        }
        await store.UpsertAccountAsync(account);
        if (!string.IsNullOrWhiteSpace(password))
        {
            var secretStore = App.Services.GetService(typeof(ISecretStore)) as ISecretStore
                ?? throw new InvalidOperationException("The Windows credential store is unavailable.");
            await secretStore.SetAsync($"account:{account.Id:N}:password", password);
        }

        var result = await providers.Get(account.Provider).TestConnectionAsync(account);
        if (!result.Success)
        {
            await store.DeleteAccountAsync(account.Id);
            throw new MailProviderException(result.Error ?? "Could not connect to this mailbox.");
        }

        await sync.SyncAccountAsync(account.Id, true);
        await ReloadAccountsAsync(account.Id);
        await LoadFoldersAsync();
        await LoadConversationsAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!connectivity.IsOnline)
        {
            StatusText = "Offline · changes will sync later";
            return;
        }

        IsBusy = true;
        StatusText = "Syncing…";
        try
        {
            if (SelectedAccount?.Model is { } account && !account.IsDemo)
            {
                await sync.SyncAccountAsync(account.Id);
            }
            else
            {
                await sync.SyncAllAsync();
            }
            await LoadFoldersAsync();
            await LoadConversationsAsync();
            StatusText = "Up to date";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            await LoadConversationsAsync();
            return;
        }

        IsBusy = true;
        try
        {
            var request = new SearchRequest(
                SearchText.Trim(),
                SelectedAccount?.Model?.Id,
                SelectedFolder?.Model?.Id,
                connectivity.IsOnline,
                200);
            var result = await store.SearchAsync(request);

            if (connectivity.IsOnline)
            {
                var accounts = SelectedAccount?.Model is { } selected
                    ? new[] { selected }
                    : (await store.GetAccountsAsync()).Where(static account => account.IsEnabled && !account.IsDemo).ToArray();
                var remoteMessages = new List<MailMessage>();
                foreach (var account in accounts)
                {
                    var provider = providers.Get(account.Provider);
                    if (!provider.Capabilities.SupportsServerSearch) continue;
                    try
                    {
                        remoteMessages.AddRange(await provider.SearchAsync(account, request with { AccountId = account.Id, Limit = 100 }));
                    }
                    catch
                    {
                        // Local results remain available if one server cannot search.
                    }
                }
                if (remoteMessages.Count > 0)
                {
                    await store.UpsertBatchAsync(new SyncBatch
                    {
                        Messages = remoteMessages,
                        Conversations = remoteMessages.GroupBy(static message => message.ConversationId)
                            .Select(BuildSearchConversation)
                            .ToArray()
                    });
                    result = await store.SearchAsync(request with { IncludeServer = false });
                }
            }
            Replace(Conversations, CreateConversationItems(result));
            ListTitle = $"Search: {SearchText.Trim()}";
            ResultsText = $"{result.Count} results";
            SelectedConversation = Conversations.FirstOrDefault();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static MailConversation BuildSearchConversation(IGrouping<Guid, MailMessage> group)
    {
        var ordered = group.OrderBy(static message => message.ReceivedAt).ToArray();
        var latest = ordered[^1];
        return new MailConversation
        {
            Id = group.Key,
            AccountId = latest.AccountId,
            ThreadKey = latest.ProviderThreadId ?? latest.InternetMessageId ?? latest.ConversationId.ToString("N"),
            ProviderThreadId = latest.ProviderThreadId,
            Subject = latest.Subject,
            Snippet = latest.Snippet,
            Participants = ordered.Select(static message => message.From).DistinctBy(static address => address.Address, StringComparer.OrdinalIgnoreCase).ToArray(),
            LastMessageAt = latest.ReceivedAt,
            MessageCount = ordered.Length,
            UnreadCount = ordered.Count(static message => !message.Flags.HasFlag(MailFlags.Read)),
            IsStarred = ordered.Any(static message => message.Flags.HasFlag(MailFlags.Starred)),
            HasAttachments = ordered.Any(static message => message.Attachments.Count > 0),
            Labels = ordered.SelectMany(static message => message.Labels).Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    [RelayCommand]
    private async Task ClearSearchAsync()
    {
        SearchText = string.Empty;
        await LoadConversationsAsync();
    }

    [RelayCommand]
    private Task ToggleReadAsync() => QueueSelectedAsync(
        SelectedConversation?.Model.UnreadCount > 0 ? PendingOperationKind.MarkRead : PendingOperationKind.MarkUnread);

    [RelayCommand]
    private Task ToggleStarAsync() => QueueSelectedAsync(
        SelectedConversation?.Model.IsStarred == true ? PendingOperationKind.Unstar : PendingOperationKind.Star);

    [RelayCommand]
    private Task DeleteAsync() => QueueSelectedAsync(PendingOperationKind.Delete);

    [RelayCommand]
    private Task ArchiveAsync() => QueueSelectedAsync(PendingOperationKind.Archive);

    public Task MoveSelectedAsync(MailFolder destination) => QueueSelectedAsync(PendingOperationKind.Move, destination);

    public Task<IReadOnlyList<MailFolder>> GetFoldersForAccountAsync(Guid accountId) => store.GetFoldersAsync(accountId);

    private async Task QueueSelectedAsync(PendingOperationKind kind, MailFolder? explicitDestination = null)
    {
        var conversation = SelectedConversation?.Model;
        if (conversation is null)
        {
            return;
        }

        var messages = await store.GetMessagesAsync(conversation.Id);
        foreach (var message in messages)
        {
            var folders = await store.GetFoldersAsync(message.AccountId);
            var source = folders.FirstOrDefault(folder => folder.Id == message.FolderId);
            var archive = explicitDestination ?? folders.FirstOrDefault(folder => folder.SpecialKind == SpecialFolderKind.Archive)
                ?? folders.FirstOrDefault(folder => folder.SpecialKind == SpecialFolderKind.AllMail);
            _ = uint.TryParse(message.RemoteId, out var uid);
            var payload = JsonSerializer.Serialize(new
            {
                folderRemoteId = source?.RemoteId,
                destinationRemoteId = archive?.RemoteId ?? "archive",
                uid,
                folderId = archive?.Id.ToString("N")
            });
            await sync.QueueAsync(new PendingOperation
            {
                Id = Guid.NewGuid(),
                AccountId = message.AccountId,
                Kind = kind,
                TargetRemoteId = message.RemoteId,
                PayloadJson = payload,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        await LoadConversationsAsync();
    }

    public Task<IReadOnlyList<Draft>> GetDraftsAsync(Guid? accountId = null) => store.GetDraftsAsync(accountId);

    public async Task SaveDraftAsync(Draft draft)
    {
        await store.UpsertDraftAsync(draft with
        {
            DeliveryState = DraftDeliveryState.Draft,
            LastError = null,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        StatusText = "Draft saved";
    }

    public Task DeleteDraftAsync(Guid draftId) => store.DeleteDraftAsync(draftId);

    public async Task<Draft> PrepareDraftForEditingAsync(Draft draft)
    {
        if (draft.DeliveryState == DraftDeliveryState.Sending)
        {
            throw new InvalidOperationException("This message is currently being sent.");
        }
        await store.CancelPendingOperationsAsync(draft.AccountId, PendingOperationKind.Send, draft.Id.ToString("N"));
        var editable = draft with { DeliveryState = DraftDeliveryState.Draft, LastError = null, UpdatedAt = DateTimeOffset.UtcNow };
        await store.UpsertDraftAsync(editable);
        return editable;
    }

    public async Task<Draft?> QueueDraftForSendAsync(Draft draft)
    {
        var account = await store.GetAccountAsync(draft.AccountId) ?? throw new InvalidOperationException("Sending account was not found.");
        long attachmentBytes = 0;
        foreach (var attachment in draft.Attachments)
        {
            if (!File.Exists(attachment.LocalPath)) throw new FileNotFoundException($"The attachment {attachment.FileName} is no longer available.", attachment.LocalPath);
            attachmentBytes += new FileInfo(attachment.LocalPath).Length;
        }
        if (providers.Get(account.Provider).Capabilities.MaximumSendBytes is { } maximum && attachmentBytes > maximum)
        {
            throw new InvalidOperationException($"The attachments are too large for {account.Provider}. The current limit is about {maximum / (1024 * 1024)} MB.");
        }
        var queued = draft with
        {
            DeliveryState = DraftDeliveryState.Queued,
            LastError = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await store.UpsertDraftAsync(queued);

        var outgoing = new OutgoingMessage
        {
            ClientMessageId = draft.Id,
            AccountId = draft.AccountId,
            To = draft.To,
            Cc = draft.Cc,
            Bcc = draft.Bcc,
            Subject = draft.Subject,
            PlainTextBody = draft.PlainTextBody,
            HtmlBody = draft.HtmlBody,
            ReplyToRemoteId = draft.ReplyToRemoteId,
            ProviderThreadId = draft.ProviderThreadId,
            Attachments = draft.Attachments
        };
        await sync.QueueAsync(new PendingOperation
        {
            Id = Guid.NewGuid(),
            AccountId = draft.AccountId,
            Kind = PendingOperationKind.Send,
            TargetRemoteId = draft.Id.ToString("N"),
            PayloadJson = JsonSerializer.Serialize(outgoing, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CreatedAt = DateTimeOffset.UtcNow
        });

        var remaining = await store.GetDraftAsync(draft.Id);
        StatusText = remaining switch
        {
            null => "Message sent",
            { DeliveryState: DraftDeliveryState.Failed } => "Send failed",
            _ when !connectivity.IsOnline => "Queued · will send when online",
            _ => "Queued for sending"
        };
        return remaining;
    }

    public Task<IReadOnlyList<Signature>> GetSignaturesAsync(Guid accountId) => store.GetSignaturesAsync(accountId);
    public Task SaveSignatureAsync(Signature signature) => store.UpsertSignatureAsync(signature);
    public Task DeleteSignatureAsync(Guid signatureId) => store.DeleteSignatureAsync(signatureId);
    public async Task DeleteAccountAsync(Guid accountId)
    {
        var account = await store.GetAccountAsync(accountId);
        await store.DeleteAccountAsync(accountId);
        if (account is not null)
        {
            var secretStore = App.Services.GetRequiredService<ISecretStore>();
            var prefix = $"account:{account.Id:N}:";
            await secretStore.RemoveAsync(prefix + "password");
            await secretStore.RemoveAsync(prefix + "msal-cache");
            await secretStore.RemoveAsync(prefix + "google:token");
        }
        await App.Services.GetRequiredService<Gomail_App.Services.IAttachmentService>().ClearEncryptedCacheAsync();
        suppressSelectionChanges = true;
        try
        {
            SelectedAccount = null;
            SelectedFolder = null;
            SelectedConversation = null;
            await ReloadAccountsAsync();
            await LoadFoldersAsync();
            await LoadConversationsAsync();
        }
        finally
        {
            suppressSelectionChanges = false;
        }
    }

    public async Task SetAccountEnabledAsync(MailAccount account, bool enabled)
    {
        await store.UpsertAccountAsync(account with { IsEnabled = enabled });
        await ReloadAccountsAsync(account.Id);
    }

    public async Task ReconnectAccountAsync(MailAccount account)
    {
        var result = await providers.Get(account.Provider).TestConnectionAsync(account);
        if (!result.Success) throw new MailProviderException(result.Error ?? "The account could not be reconnected.");
        await sync.SyncAccountAsync(account.Id);
    }

    public async Task<IReadOnlyList<MailAddress>> GetKnownAddressesAsync(Guid? accountId = null)
    {
        var conversations = await store.GetConversationsAsync(accountId, limit: 1500);
        var accounts = await store.GetAccountsAsync();
        return conversations.SelectMany(static item => item.Participants)
            .Concat(accounts.Select(static account => new MailAddress(account.DisplayName, account.Email)))
            .Where(static address => !string.IsNullOrWhiteSpace(address.Address))
            .DistinctBy(static address => address.Address, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static address => address.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private async Task LoadFoldersAsync()
    {
        var previousId = SelectedFolder?.Model?.Id ?? ReadGuidSetting("selectedFolderId");
        var folders = await store.GetFoldersAsync(SelectedAccount?.Model?.Id);
        var items = new List<FolderItem>();
        if (SelectedAccount?.Model is null)
        {
            items.Add(FolderItem.UnifiedInbox(folders.Where(static folder => folder.SpecialKind == SpecialFolderKind.Inbox).Sum(static folder => folder.UnreadCount)));
            items.Add(FolderItem.UnifiedStarred());
            items.Add(FolderItem.AllMail());
        }
        else
        {
            items.AddRange(folders.OrderBy(static folder => folder.SpecialKind == SpecialFolderKind.Inbox ? 0 : 1).Select(static folder => new FolderItem(folder)));
            items.Add(FolderItem.AllMail());
        }
        Replace(Folders, items);
        SelectedFolder = Folders.FirstOrDefault(item => item.Model?.Id == previousId) ?? Folders[0];
    }

    private async Task LoadConversationsAsync()
    {
        var previousId = SelectedConversation?.Model.Id ?? ReadGuidSetting("selectedConversationId");
        IReadOnlyList<MailConversation> conversations;
        if (SelectedAccount?.Model is null && SelectedFolder?.UnifiedKind == SpecialFolderKind.Inbox)
        {
            var inboxes = (await store.GetFoldersAsync()).Where(static folder => folder.SpecialKind == SpecialFolderKind.Inbox).ToArray();
            var combined = new List<MailConversation>();
            foreach (var inbox in inboxes) combined.AddRange(await store.GetConversationsAsync(folderId: inbox.Id, limit: 300));
            conversations = combined.DistinctBy(static item => item.Id).OrderByDescending(static item => item.LastMessageAt).Take(300).ToArray();
        }
        else
        {
            conversations = await store.GetConversationsAsync(
                SelectedAccount?.Model?.Id,
                SelectedFolder?.Model?.Id,
                300);
            if (SelectedAccount?.Model is null && SelectedFolder?.UnifiedKind == SpecialFolderKind.Starred)
            {
                conversations = conversations.Where(static item => item.IsStarred).ToArray();
            }
        }
        Replace(Conversations, CreateConversationItems(conversations));
        ListTitle = SelectedFolder?.DisplayName ?? "Inbox";
        ResultsText = $"{conversations.Count} conversations";
        SelectedConversation = Conversations.FirstOrDefault(item => item.Model.Id == previousId) ?? Conversations.FirstOrDefault();
    }

    private async Task LoadMessagesAsync()
    {
        HasSelection = SelectedConversation is not null;
        HasNoSelection = !HasSelection;
        var selected = SelectedConversation;
        if (selected is null)
        {
            Messages.Clear();
            return;
        }
        var conversationId = selected.Model.Id;
        var messages = await store.GetMessagesAsync(conversationId);
        if (connectivity.IsOnline)
        {
            var hydrated = new List<MailMessage>(messages.Count);
            foreach (var message in messages)
            {
                if (string.IsNullOrWhiteSpace(message.TextBody) && string.IsNullOrWhiteSpace(message.HtmlBody))
                {
                    try
                    {
                        var account = await store.GetAccountAsync(message.AccountId);
                        if (account is not null && !account.IsDemo)
                        {
                            var full = await providers.Get(account.Provider).HydrateMessageAsync(account, message);
                            await store.UpsertBatchAsync(new SyncBatch { Messages = new[] { full } });
                            hydrated.Add(full);
                            continue;
                        }
                    }
                    catch
                    {
                        // The cached snippet remains readable while the server is unavailable.
                    }
                }
                hydrated.Add(message);
            }
            messages = hydrated;
        }
        if (SelectedConversation?.Model.Id == conversationId)
        {
            var accountLabels = Accounts
                .Where(static item => item.Model is not null)
                .ToDictionary(static item => item.Model!.Id, static item => $"{item.DisplayName} · {item.Model!.Email}");
            Replace(Messages, messages.Select(item => new MessageItem(
                item,
                htmlSanitizer,
                accountLabels.GetValueOrDefault(item.AccountId, "Mailbox"))));
        }
    }

    private IEnumerable<ConversationItem> CreateConversationItems(IEnumerable<MailConversation> conversations)
    {
        var accountLabels = Accounts
            .Where(static item => item.Model is not null)
            .ToDictionary(static item => item.Model!.Id, static item => $"{item.DisplayName} · {item.Model!.Email}");
        var showAccount = SelectedAccount?.Model is null && Accounts.Count(static item => item.Model is not null) > 1;
        return conversations.Select(item => new ConversationItem(
            item,
            accountLabels.GetValueOrDefault(item.AccountId, "Mailbox"),
            showAccount));
    }

    partial void OnSelectedAccountChanged(AccountItem? value)
    {
        SaveGuidSetting("selectedAccountId", value?.Model?.Id);
        if (!suppressSelectionChanges && value is not null && Accounts.Count > 0)
        {
            _ = ChangeAccountAsync();
        }
    }

    private async Task ChangeAccountAsync()
    {
        await LoadFoldersAsync();
        await LoadConversationsAsync();
    }

    partial void OnSelectedFolderChanged(FolderItem? value)
    {
        SaveGuidSetting("selectedFolderId", value?.Model?.Id);
        if (!suppressSelectionChanges && value is not null && Folders.Count > 0)
        {
            _ = LoadConversationsAsync();
        }
    }

    partial void OnSelectedConversationChanged(ConversationItem? value)
    {
        SaveGuidSetting("selectedConversationId", value?.Model.Id);
        markReadCancellation?.Cancel();
        markReadCancellation?.Dispose();
        markReadCancellation = new CancellationTokenSource();
        _ = LoadMessagesAsync();
        if (value?.Model.UnreadCount > 0) _ = MarkSelectedReadAfterDelayAsync(value.Model.Id, markReadCancellation.Token);
    }

    private async Task MarkSelectedReadAfterDelayAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1.2), cancellationToken);
            if (SelectedConversation?.Model.Id == conversationId && SelectedConversation.Model.UnreadCount > 0)
            {
                await QueueSelectedAsync(PendingOperationKind.MarkRead);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Guid? ReadGuidSetting(string key) =>
        localSettings.TryGetValue(key, out var value) && value is string text && Guid.TryParseExact(text, "N", out var parsed) ? parsed : null;

    private void SaveGuidSetting(string key, Guid? value)
    {
        if (value is null) localSettings.Remove(key);
        else localSettings[key] = value.Value.ToString("N");
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }
}

public sealed class AccountItem
{
    public AccountItem(MailAccount account)
    {
        Model = account;
        DisplayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Email : account.DisplayName;
        Subtitle = account.Provider switch
        {
            ProviderKind.Microsoft365 => "Microsoft 365",
            ProviderKind.Gmail => "Gmail",
            ProviderKind.Imap => "IMAP",
            _ => "Sample mailbox"
        };
        Initials = string.Concat(DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(static part => char.ToUpperInvariant(part[0])));
    }

    private AccountItem()
    {
        DisplayName = "All accounts";
        Subtitle = "Unified inbox";
        Initials = "∞";
    }

    public MailAccount? Model { get; }
    public string DisplayName { get; }
    public string Subtitle { get; }
    public string Initials { get; }
    public string Email => Model?.Email ?? string.Empty;
    public string MailboxDisplay => Model is null ? DisplayName : $"{DisplayName} · {Model.Email}";
    public string SyncStatus => Model is null
        ? "All connected mailboxes"
        : !Model.IsEnabled
            ? "Disabled"
            : !string.IsNullOrWhiteSpace(Model.LastSyncError)
                ? "Needs attention"
                : Model.LastSuccessfulSync is { } synced
                    ? $"Synced {synced.ToLocalTime():g}"
                    : "Ready to sync";
    public string ManagementDisplay => Model is null ? DisplayName : $"{DisplayName} · {Model.Email} · {(Model.IsEnabled ? "Enabled" : "Disabled")}";
    public static AccountItem Unified() => new();
}

public sealed class FolderItem
{
    public FolderItem(MailFolder folder)
    {
        Model = folder;
        DisplayName = folder.Name;
        Badge = FormatBadge(folder.UnreadCount);
        Glyph = folder.SpecialKind switch
        {
            SpecialFolderKind.Inbox => "\uE715",
            SpecialFolderKind.Starred => "\uE734",
            SpecialFolderKind.Drafts => "\uE70B",
            SpecialFolderKind.Sent => "\uE724",
            SpecialFolderKind.Archive => "\uE7B8",
            SpecialFolderKind.Spam => "\uE7BA",
            SpecialFolderKind.Trash => "\uE74D",
            _ => "\uE8B7"
        };
    }

    private FolderItem()
    {
        DisplayName = "All mail";
        Glyph = "\uE715";
        Badge = string.Empty;
    }

    private FolderItem(string displayName, string glyph, SpecialFolderKind unifiedKind, int unread = 0)
    {
        DisplayName = displayName;
        Glyph = glyph;
        UnifiedKind = unifiedKind;
        Badge = FormatBadge(unread);
    }

    public MailFolder? Model { get; }
    public string DisplayName { get; }
    public string Glyph { get; }
    public string Badge { get; }
    public bool HasBadge => !string.IsNullOrEmpty(Badge);
    public bool HasCircularBadge => Badge.Length == 1;
    public bool HasWideBadge => Badge.Length > 1;
    public SpecialFolderKind? UnifiedKind { get; }
    private static string FormatBadge(int unread) => unread switch
    {
        <= 0 => string.Empty,
        > 99 => "99+",
        _ => unread.ToString()
    };
    public static FolderItem AllMail() => new();
    public static FolderItem UnifiedInbox(int unread) => new("Inbox", "\uE715", SpecialFolderKind.Inbox, unread);
    public static FolderItem UnifiedStarred() => new("Starred", "\uE734", SpecialFolderKind.Starred);
}

public sealed class ConversationItem
{
    public ConversationItem(MailConversation model, string accountLabel = "Mailbox", bool showAccount = false)
    {
        Model = model;
        Sender = model.Participants.FirstOrDefault()?.DisplayName ?? "Unknown sender";
        Initials = string.Concat(Sender.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(static part => char.ToUpperInvariant(part[0])));
        Time = FormatTime(model.LastMessageAt);
        Count = model.MessageCount > 1 ? model.MessageCount.ToString() : string.Empty;
        UnreadMark = model.UnreadCount > 0 ? "●" : string.Empty;
        Star = model.IsStarred ? "★" : string.Empty;
        Attachment = model.HasAttachments ? "\uE723" : string.Empty;
        AccountLabel = accountLabel;
        ShowAccount = showAccount;
    }

    public MailConversation Model { get; }
    public string Sender { get; }
    public string Initials { get; }
    public string Subject => Model.Subject;
    public string Snippet => Model.Snippet;
    public string Time { get; }
    public string Count { get; }
    public string UnreadMark { get; }
    public string Star { get; }
    public string Attachment { get; }
    public string AccountLabel { get; }
    public bool ShowAccount { get; }

    private static string FormatTime(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        if (local.Date == today) return local.ToString("HH:mm");
        if (local.Date >= today.AddDays(-6)) return local.ToString("ddd");
        return local.Year == today.Year ? local.ToString("d MMM") : local.ToString("d MMM yyyy");
    }
}

public sealed partial class MessageItem : ObservableObject
{
    public MessageItem(MailMessage model, IHtmlSanitizer htmlSanitizer, string accountLabel = "Mailbox")
    {
        Model = model;
        Sender = model.From.DisplayName;
        Address = model.From.Address;
        Initials = string.Concat(Sender.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(static part => char.ToUpperInvariant(part[0])));
        Date = model.ReceivedAt.ToLocalTime().ToString("dddd, d MMMM · HH:mm");
        Body = !string.IsNullOrWhiteSpace(model.TextBody) ? model.TextBody : model.Snippet;
        OriginalHtmlBody = model.HtmlBody ?? string.Empty;
        HtmlBody = string.IsNullOrWhiteSpace(model.HtmlBody) ? string.Empty : htmlSanitizer.Sanitize(model.HtmlBody);
        HasHtml = !string.IsNullOrWhiteSpace(HtmlBody);
        HasPlainBody = !string.IsNullOrWhiteSpace(Body);
        ShowPlain = HasPlainBody;
        ShowHtml = HasHtml && !HasPlainBody;
        CanShowFormatted = HasHtml && HasPlainBody;
        ExternalImagesBlocked = HtmlBody.Contains("data-gomail-src", StringComparison.OrdinalIgnoreCase);
        Attachments = model.Attachments.Select(attachment => new AttachmentItem(model, attachment)).ToArray();
        HtmlHeight = Math.Clamp(112 + (OriginalHtmlBody.Length / 90d * 18), 132, 720);
        AccountLabel = accountLabel;
        FromLine = FormatAddress(model.From);
        ToLine = FormatAddresses(model.To);
        CcLine = FormatAddresses(model.Cc);
        BccLine = FormatAddresses(model.Bcc);
        HasCc = model.Cc.Count > 0;
        HasBcc = model.Bcc.Count > 0;
        RecipientSummary = model.To.Count == 0 ? "recipient details" : $"to {CompactRecipients(model.To)}";
    }

    public MailMessage Model { get; }
    public string Sender { get; }
    public string Address { get; }
    public string Initials { get; }
    public string Date { get; }
    public string Body { get; }
    public string OriginalHtmlBody { get; }
    public string HtmlBody { get; }
    public bool HasHtml { get; }
    public bool HasPlainBody { get; }
    public bool ExternalImagesBlocked { get; }
    public double HtmlHeight { get; }
    [ObservableProperty] public partial bool ShowPlain { get; set; }
    [ObservableProperty] public partial bool ShowHtml { get; set; }
    [ObservableProperty] public partial bool CanShowFormatted { get; set; }
    public IReadOnlyList<AttachmentItem> Attachments { get; }
    public string AccountLabel { get; }
    public string FromLine { get; }
    public string ToLine { get; }
    public string CcLine { get; }
    public string BccLine { get; }
    public string RecipientSummary { get; }
    public bool HasCc { get; }
    public bool HasBcc { get; }

    private static string FormatAddress(MailAddress address) =>
        string.IsNullOrWhiteSpace(address.Name) ? address.Address : $"{address.Name} <{address.Address}>";

    private static string FormatAddresses(IEnumerable<MailAddress> addresses) =>
        string.Join(", ", addresses.Select(FormatAddress));

    private static string CompactRecipients(IReadOnlyList<MailAddress> addresses)
    {
        var first = addresses[0].DisplayName;
        return addresses.Count == 1 ? first : $"{first} +{addresses.Count - 1}";
    }
}

public sealed class AttachmentItem
{
    public AttachmentItem(MailMessage message, MailAttachment attachment)
    {
        Message = message;
        Attachment = attachment;
        DisplayName = $"📎 {attachment.FileName}";
        Size = FormatSize(attachment.Size);
    }

    public MailMessage Message { get; }
    public MailAttachment Attachment { get; }
    public string DisplayName { get; }
    public string Size { get; }

    private static string FormatSize(long value) => value switch
    {
        <= 0 => string.Empty,
        < 1024 => $"{value} B",
        < 1024 * 1024 => $"{value / 1024d:0.#} KB",
        _ => $"{value / (1024d * 1024):0.#} MB"
    };
}
