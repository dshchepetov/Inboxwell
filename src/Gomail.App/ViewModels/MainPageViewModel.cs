using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gomail.Core;
using Gomail.Providers;
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
    private const int ConversationPageSize = 300;
    private bool suppressSelectionChanges;
    private bool canLoadMoreConversations = true;
    private bool isLoadingMoreConversations;
    private int conversationLimit = ConversationPageSize;
    private int conversationLoadVersion;
    private CancellationTokenSource? conversationLoadCancellation;
    private CancellationTokenSource? markReadCancellation;
    private CancellationTokenSource? messageLoadCancellation;

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
    public ObservableCollection<DraftRowItem> Drafts { get; } = new();
    public ObservableCollection<MessageItem> Messages { get; } = new();

    [ObservableProperty] public partial AccountItem? SelectedAccount { get; set; }
    [ObservableProperty] public partial FolderItem? SelectedFolder { get; set; }
    [ObservableProperty] public partial ConversationItem? SelectedConversation { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsRefreshing { get; set; }
    [ObservableProperty] public partial bool IsLoadingMessages { get; set; }
    [ObservableProperty] public partial bool IsOnline { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusText { get; set; } = "Ready";
    [ObservableProperty] public partial string ListTitle { get; set; } = "Inbox";
    [ObservableProperty] public partial string ResultsText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasSelection { get; set; }
    [ObservableProperty] public partial bool HasNoSelection { get; set; } = true;
    [ObservableProperty] public partial bool ShowingDrafts { get; set; }
    [ObservableProperty] public partial bool ShowingConversations { get; set; } = true;

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
        var accounts = await Task.Run(() => store.GetAccountsAsync());
        Accounts.Clear();
        Accounts.Add(AccountItem.Unified());
        foreach (var account in accounts.OrderBy(static account => account.MailboxName, StringComparer.CurrentCultureIgnoreCase))
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
        var existingAccounts = await Task.Run(() => store.GetAccountsAsync());
        if (existingAccounts.Any(existing => existing.Provider == account.Provider && existing.Email.Equals(account.Email, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("This mailbox is already connected.");
        }
        await Task.Run(() => store.UpsertAccountAsync(account));
        var secretStore = App.Services.GetRequiredService<ISecretStore>();
        if (!string.IsNullOrWhiteSpace(password))
        {
            await secretStore.SetAsync($"account:{account.Id:N}:password", password);
        }

        try
        {
            // OAuth and provider discovery stay off the dispatcher so a second
            // account never turns the setup window into "Not responding".
            var result = await Task.Run(() => providers.Get(account.Provider).TestConnectionAsync(account));
            if (!result.Success)
            {
                throw new MailProviderException(result.Error ?? "Could not connect to this mailbox.");
            }

            var resolvedEmail = string.IsNullOrWhiteSpace(result.Email) ? account.Email : result.Email;
            if (existingAccounts.Any(existing =>
                    existing.Provider == account.Provider &&
                    existing.Id != account.Id &&
                    existing.Email.Equals(resolvedEmail, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("This mailbox is already connected.");
            }

            var fallbackName = account.Provider == ProviderKind.Gmail && account.DisplayName == "Gmail"
                ? resolvedEmail.Split('@')[0]
                : account.DisplayName;
            account = account with
            {
                Email = resolvedEmail,
                DisplayName = string.IsNullOrWhiteSpace(result.DisplayName) ? fallbackName : result.DisplayName
            };
            await Task.Run(() => store.UpsertAccountAsync(account));
        }
        catch
        {
            await store.DeleteAccountAsync(account.Id);
            var prefix = $"account:{account.Id:N}:";
            await secretStore.RemoveAsync(prefix + "password");
            await secretStore.RemoveAsync(prefix + "msal-cache");
            await secretStore.RemoveAsync(prefix + "google:token");
            await secretStore.RemoveAsync(prefix + $"google:{account.Id:N}");
            throw;
        }

        await ReloadAccountsAsync(account.Id);
        StatusText = $"{account.DisplayName} connected · syncing mail…";
        _ = CompleteInitialAccountSyncAsync(account.Id);
    }

    private async Task CompleteInitialAccountSyncAsync(Guid accountId)
    {
        var syncTask = Task.Run(() => sync.SyncAccountAsync(accountId, true));
        while (await Task.WhenAny(syncTask, Task.Delay(1500)) != syncTask)
        {
            // Surface every committed batch instead of leaving the mailbox empty
            // until the complete history import has finished.
            if (SelectedAccount?.Model?.Id == accountId || SelectedAccount?.Model is null)
            {
                await LoadFoldersAsync();
                await LoadConversationsAsync();
            }
        }
        await syncTask;
        var account = await store.GetAccountAsync(accountId);
        StatusText = account?.LastSyncError is { Length: > 0 } error
            ? $"Sync needs attention · {error}"
            : "Mailbox connected and up to date";
        await ReloadAccountsAsync(accountId);
        await LoadFoldersAsync();
        await LoadConversationsAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        if (!connectivity.IsOnline)
        {
            StatusText = "Offline · changes will sync later";
            return;
        }

        IsRefreshing = true;
        StatusText = "Syncing…";
        try
        {
            Task syncTask;
            if (SelectedAccount?.Model is { } account && !account.IsDemo)
            {
                syncTask = Task.Run(() => sync.SyncAccountAsync(account.Id));
            }
            else
            {
                syncTask = Task.Run(() => sync.SyncAllAsync());
            }
            while (await Task.WhenAny(syncTask, Task.Delay(1500)) != syncTask)
            {
                // A first full import can run for a while. Reconcile committed
                // batches into the existing controls without flashing the page.
                await LoadFoldersAsync();
                await LoadConversationsAsync();
            }
            await syncTask;
            await LoadFoldersAsync();
            await LoadConversationsAsync();
            StatusText = "Up to date";
        }
        finally
        {
            IsRefreshing = false;
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
            var result = await Task.Run(() => store.SearchAsync(request));

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
                        remoteMessages.AddRange(await Task.Run(() => provider.SearchAsync(account, request with { AccountId = account.Id, Limit = 100 })));
                    }
                    catch
                    {
                        // Local results remain available if one server cannot search.
                    }
                }
                if (remoteMessages.Count > 0)
                {
                    await Task.Run(() => store.UpsertBatchAsync(new SyncBatch
                    {
                        Messages = remoteMessages,
                        Conversations = remoteMessages.GroupBy(static message => message.ConversationId)
                            .Select(BuildSearchConversation)
                            .ToArray()
                    }));
                    result = await Task.Run(() => store.SearchAsync(request with { IncludeServer = false }));
                }
            }
            Replace(Conversations, CreateConversationItems(result));
            canLoadMoreConversations = false;
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
            ThreadKey = ConversationThreader.CreateThreadKey(
                latest.AccountId,
                latest.ProviderThreadId,
                latest.InternetMessageId,
                latest.InReplyTo,
                latest.References,
                latest.Subject,
                ordered.Select(static message => message.From)),
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
        ResetConversationPaging();
        await LoadConversationsAsync();
    }

    [RelayCommand]
    private Task ToggleReadAsync() => QueueSelectedAsync(
        SelectedConversation?.Model.UnreadCount > 0 ? PendingOperationKind.MarkRead : PendingOperationKind.MarkUnread);

    public Task MarkSelectedReadAsync() => SelectedConversation?.Model.UnreadCount > 0
        ? QueueSelectedAsync(PendingOperationKind.MarkRead)
        : Task.CompletedTask;

    public Task MarkSelectedUnreadAsync() => SelectedConversation is { Model.UnreadCount: 0 }
        ? QueueSelectedAsync(PendingOperationKind.MarkUnread)
        : Task.CompletedTask;

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
            await Task.Run(() => sync.QueueAsync(new PendingOperation
            {
                Id = Guid.NewGuid(),
                AccountId = message.AccountId,
                Kind = kind,
                TargetRemoteId = message.RemoteId,
                PayloadJson = payload,
                CreatedAt = DateTimeOffset.UtcNow
            }));
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
            IsImportant = draft.IsImportant,
            Attachments = draft.Attachments
        };
        await Task.Run(() => sync.QueueAsync(new PendingOperation
        {
            Id = Guid.NewGuid(),
            AccountId = draft.AccountId,
            Kind = PendingOperationKind.Send,
            TargetRemoteId = draft.Id.ToString("N"),
            PayloadJson = JsonSerializer.Serialize(outgoing, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CreatedAt = DateTimeOffset.UtcNow
        }));

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
            await secretStore.RemoveAsync(prefix + $"google:{account.Id:N}");
        }
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

    public async Task RenameAccountAsync(MailAccount account, string? mailboxName)
    {
        var settings = new Dictionary<string, string>(account.Settings, StringComparer.OrdinalIgnoreCase);
        var normalizedName = mailboxName?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            settings.Remove("localMailboxName");
        }
        else
        {
            settings["localMailboxName"] = normalizedName;
        }

        await store.UpsertAccountAsync(account with { Settings = settings });
        await ReloadAccountsAsync(account.Id);
    }

    public async Task ReconnectAccountAsync(MailAccount account)
    {
        if (account.Provider == ProviderKind.Gmail)
        {
            var gmailAuthentication = App.Services.GetRequiredService<IGmailAuthenticationService>();
            await Task.Run(() => gmailAuthentication.ReauthorizeAsync(account));
        }
        var result = await Task.Run(() => providers.Get(account.Provider).TestConnectionAsync(account));
        if (!result.Success) throw new MailProviderException(result.Error ?? "The account could not be reconnected.");
        account = account with
        {
            Email = string.IsNullOrWhiteSpace(result.Email) ? account.Email : result.Email,
            DisplayName = string.IsNullOrWhiteSpace(result.DisplayName) ? account.DisplayName : result.DisplayName
        };
        await store.UpsertAccountAsync(account);
        await ReloadAccountsAsync(account.Id);
        await Task.Run(() => sync.SyncAccountAsync(account.Id));
        var synchronized = await store.GetAccountAsync(account.Id);
        if (synchronized?.LastSyncError is { Length: > 0 } error)
        {
            throw new MailProviderException(error);
        }
        StatusText = "Account connected and up to date";
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
        var previousKey = SelectedFolder?.Key
            ?? (localSettings.TryGetValue("selectedFolderKey", out var savedKey) ? savedKey as string : null)
            ?? ReadGuidSetting("selectedFolderId")?.ToString("N");
        var folders = await Task.Run(() => store.GetFoldersAsync(SelectedAccount?.Model?.Id));
        var items = new List<FolderItem>();
        if (SelectedAccount?.Model is null)
        {
            items.Add(FolderItem.UnifiedInbox(folders.Where(static folder => folder.SpecialKind == SpecialFolderKind.Inbox).Sum(static folder => folder.UnreadCount)));
            items.Add(FolderItem.Unified("Drafts", "\uE70B", SpecialFolderKind.Drafts));
            items.Add(FolderItem.Unified("Sent", "\uE724", SpecialFolderKind.Sent));
            items.Add(FolderItem.Unified("Archive", "\uE7B8", SpecialFolderKind.Archive));
            items.Add(FolderItem.UnifiedStarred());
            items.Add(FolderItem.Unified("Spam", "\uE7BA", SpecialFolderKind.Spam));
            items.Add(FolderItem.Unified("Trash", "\uE74D", SpecialFolderKind.Trash));
            items.Add(FolderItem.AllMail());
        }
        else
        {
            if (SelectedAccount.Model.Provider == ProviderKind.Gmail)
            {
                folders = folders.Where(static folder =>
                    !folder.RemoteId.StartsWith("CATEGORY_", StringComparison.Ordinal) &&
                    folder.RemoteId is not "IMPORTANT" and not "UNREAD" and not "CHAT").ToArray();
            }
            items.AddRange(folders.OrderBy(static folder => FolderSortOrder(folder.SpecialKind)).ThenBy(static folder => folder.Name, StringComparer.CurrentCultureIgnoreCase).Select(static folder => new FolderItem(folder)));
            if (folders.All(static folder => folder.SpecialKind != SpecialFolderKind.AllMail)) items.Add(FolderItem.AllMail());
        }
        Reconcile(Folders, items, static item => item.Key, static (left, right) => left.Model == right.Model && left.Badge == right.Badge);
        SelectedFolder = Folders.FirstOrDefault(item => item.Key == previousKey) ?? Folders[0];
    }

    private static int FolderSortOrder(SpecialFolderKind kind) => kind switch
    {
        SpecialFolderKind.Inbox => 0,
        SpecialFolderKind.Drafts => 1,
        SpecialFolderKind.Sent => 2,
        SpecialFolderKind.Archive => 3,
        SpecialFolderKind.Starred => 4,
        SpecialFolderKind.None => 5,
        SpecialFolderKind.Spam => 6,
        SpecialFolderKind.Trash => 7,
        _ => 8
    };

    private async Task LoadConversationsAsync(CancellationToken cancellationToken = default)
    {
        var loadVersion = Interlocked.Increment(ref conversationLoadVersion);
        var requestedAccount = SelectedAccount;
        var requestedFolder = SelectedFolder;
        if (SelectedFolder?.UnifiedKind == SpecialFolderKind.Drafts || SelectedFolder?.Model?.SpecialKind == SpecialFolderKind.Drafts)
        {
            await LoadDraftsAsync();
            return;
        }

        ShowingDrafts = false;
        ShowingConversations = true;
        var previousId = SelectedConversation?.Model.Id ?? ReadGuidSetting("selectedConversationId");
        IReadOnlyList<MailConversation> conversations;
        if (SelectedAccount?.Model is null && SelectedFolder?.UnifiedKind is { } unifiedKind && unifiedKind != SpecialFolderKind.Starred)
        {
            conversations = await Task.Run(async () =>
            {
                var matchingFolders = (await store.GetFoldersAsync(cancellationToken: cancellationToken)).Where(folder => folder.SpecialKind == unifiedKind).ToArray();
                var folderResults = await Task.WhenAll(matchingFolders.Select(folder => store.GetConversationsAsync(folderId: folder.Id, limit: conversationLimit, cancellationToken: cancellationToken)));
                return folderResults.SelectMany(static item => item).DistinctBy(static item => item.Id).OrderByDescending(static item => item.LastMessageAt).Take(conversationLimit).ToArray();
            }, cancellationToken);
        }
        else
        {
            conversations = await Task.Run(() => store.GetConversationsAsync(
                requestedAccount?.Model?.Id,
                requestedFolder?.Model?.Id,
                conversationLimit,
                cancellationToken), cancellationToken);
            if (requestedAccount?.Model is null && requestedFolder?.UnifiedKind == SpecialFolderKind.Starred)
            {
                conversations = conversations.Where(static item => item.IsStarred).ToArray();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (loadVersion != Volatile.Read(ref conversationLoadVersion) ||
            !ReferenceEquals(SelectedAccount, requestedAccount) ||
            !ReferenceEquals(SelectedFolder, requestedFolder))
        {
            return;
        }
        var items = CreateConversationItems(conversations).ToArray();
        Reconcile(
            Conversations,
            items,
            static item => item.Model.Id,
            ConversationItemsEquivalent,
            static (current, updated) => current.UpdateFrom(updated));
        ListTitle = requestedFolder?.DisplayName ?? "Inbox";
        canLoadMoreConversations = conversations.Count >= conversationLimit;
        ResultsText = canLoadMoreConversations
            ? $"{conversations.Count} loaded · scroll for older mail"
            : $"{conversations.Count} conversations";
        SelectedConversation = Conversations.FirstOrDefault(item => item.Model.Id == previousId) ?? Conversations.FirstOrDefault();
    }

    public async Task LoadMoreConversationsAsync()
    {
        if (ShowingDrafts || isLoadingMoreConversations || !canLoadMoreConversations || IsBusy)
        {
            return;
        }

        isLoadingMoreConversations = true;
        try
        {
            conversationLimit = Math.Min(conversationLimit + ConversationPageSize, 12_000);
            await LoadConversationsAsync();
        }
        finally
        {
            isLoadingMoreConversations = false;
        }
    }

    public async Task LoadDraftsAsync()
    {
        ShowingDrafts = true;
        ShowingConversations = false;
        SelectedConversation = null;
        Conversations.Clear();
        Messages.Clear();
        var drafts = await Task.Run(() => store.GetDraftsAsync(SelectedAccount?.Model?.Id));
        Reconcile(Drafts, drafts.Select(static draft => new DraftRowItem(draft)).ToArray(), static item => item.Draft.Id, static (left, right) => left.Draft == right.Draft);
        ListTitle = "Drafts & outbox";
        ResultsText = drafts.Count == 0 ? "No saved or queued messages" : $"{drafts.Count} saved or queued";
    }

    private async Task LoadMessagesAsync(CancellationToken cancellationToken)
    {
        HasSelection = SelectedConversation is not null;
        HasNoSelection = !HasSelection;
        var selected = SelectedConversation;
        if (selected is null)
        {
            Messages.Clear();
            IsLoadingMessages = false;
            return;
        }

        var conversationId = selected.Model.Id;
        Messages.Clear();
        IsLoadingMessages = true;
        var accountLabels = Accounts
            .Where(static item => item.Model is not null)
            .ToDictionary(static item => item.Model!.Id, static item => $"{item.DisplayName} · {item.Model!.Email}");

        try
        {
            var messages = await Task.Run(
                () => store.GetMessagesAsync(conversationId, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (SelectedConversation?.Model.Id != conversationId) return;

            // Show the local copy immediately. Network hydration continues below,
            // so clicking between conversations never leaves the previous body behind.
            Replace(Messages, messages.Select(item => new MessageItem(
                item,
                htmlSanitizer,
                accountLabels.GetValueOrDefault(item.AccountId, "Mailbox"),
                BlockRemoteImages)));

            if (!connectivity.IsOnline) return;
            var missingBodies = messages
                .Where(static message => string.IsNullOrWhiteSpace(message.TextBody) && string.IsNullOrWhiteSpace(message.HtmlBody))
                .ToArray();
            if (missingBodies.Length == 0) return;

            using var hydrateGate = new SemaphoreSlim(4, 4);
            var hydrateTasks = missingBodies.Select(message => Task.Run(async () =>
            {
                await hydrateGate.WaitAsync(cancellationToken);
                try
                {
                    var account = await store.GetAccountAsync(message.AccountId, cancellationToken);
                    if (account is not null && !account.IsDemo)
                    {
                        var full = await providers.Get(account.Provider)
                            .HydrateMessageAsync(account, message, cancellationToken);
                        await store.UpsertBatchAsync(new SyncBatch { Messages = new[] { full } }, cancellationToken);
                        return full;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Keep the cached snippet when this individual body cannot be fetched.
                }
                finally
                {
                    hydrateGate.Release();
                }
                return null;
            }, cancellationToken)).ToList();

            // Replace each message as soon as its body arrives instead of waiting
            // for the slowest message in a long conversation.
            while (hydrateTasks.Count > 0)
            {
                var completed = await Task.WhenAny(hydrateTasks);
                hydrateTasks.Remove(completed);
                var hydrated = await completed;
                cancellationToken.ThrowIfCancellationRequested();
                if (hydrated is null || SelectedConversation?.Model.Id != conversationId) continue;
                var index = Messages.ToList().FindIndex(item => item.Model.Id == hydrated.Id);
                if (index >= 0)
                {
                    Messages[index] = new MessageItem(
                        hydrated,
                        htmlSanitizer,
                        accountLabels.GetValueOrDefault(hydrated.AccountId, "Mailbox"),
                        BlockRemoteImages);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (SelectedConversation?.Model.Id == conversationId)
            {
                IsLoadingMessages = false;
            }
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
        ResetConversationPaging();
        await LoadFoldersAsync();
        await LoadConversationsAsync();
    }

    partial void OnSelectedFolderChanged(FolderItem? value)
    {
        SaveGuidSetting("selectedFolderId", value?.Model?.Id);
        if (value is null) localSettings.Remove("selectedFolderKey");
        else localSettings["selectedFolderKey"] = value.Key;
        if (!suppressSelectionChanges && value is not null && Folders.Count > 0)
        {
            ResetConversationPaging();
            conversationLoadCancellation?.Cancel();
            conversationLoadCancellation?.Dispose();
            conversationLoadCancellation = new CancellationTokenSource();
            _ = LoadConversationsSafelyAsync(conversationLoadCancellation.Token);
        }
    }

    private async Task LoadConversationsSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadConversationsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            StatusText = "This folder could not be opened";
        }
    }

    public void PrepareForFolderTransition()
    {
        if (SelectedConversation is not null) SelectedConversation = null;
        else Messages.Clear();
        HasSelection = false;
        HasNoSelection = true;
        IsLoadingMessages = false;
    }

    partial void OnSelectedConversationChanged(ConversationItem? value)
    {
        SaveGuidSetting("selectedConversationId", value?.Model.Id);
        markReadCancellation?.Cancel();
        markReadCancellation?.Dispose();
        markReadCancellation = new CancellationTokenSource();
        messageLoadCancellation?.Cancel();
        messageLoadCancellation?.Dispose();
        messageLoadCancellation = new CancellationTokenSource();
        _ = LoadMessagesAsync(messageLoadCancellation.Token);
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

    private void ResetConversationPaging()
    {
        conversationLimit = ConversationPageSize;
        canLoadMoreConversations = true;
    }

    public void RefreshMessagePresentation()
    {
        if (Messages.Count == 0) return;
        var refreshed = Messages.Select(item => new MessageItem(
            item.Model,
            htmlSanitizer,
            item.AccountLabel,
            BlockRemoteImages)).ToArray();
        Replace(Messages, refreshed);
    }

    private bool BlockRemoteImages =>
        localSettings.TryGetValue("blockRemoteImages", out var value) && value is true;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    private static bool ConversationItemsEquivalent(ConversationItem left, ConversationItem right) =>
        left.Model.Subject == right.Model.Subject &&
        left.Model.Snippet == right.Model.Snippet &&
        left.Model.LastMessageAt == right.Model.LastMessageAt &&
        left.Model.MessageCount == right.Model.MessageCount &&
        left.Model.UnreadCount == right.Model.UnreadCount &&
        left.Model.IsStarred == right.Model.IsStarred &&
        left.Model.HasAttachments == right.Model.HasAttachments &&
        left.AccountLabel == right.AccountLabel &&
        left.ShowAccount == right.ShowAccount;

    private static void Reconcile<T, TKey>(
        ObservableCollection<T> target,
        IReadOnlyList<T> source,
        Func<T, TKey> keySelector,
        Func<T, T, bool> equivalent,
        Action<T, T>? update = null)
        where TKey : notnull
    {
        for (var index = 0; index < source.Count; index++)
        {
            var desired = source[index];
            var desiredKey = keySelector(desired);
            var existingIndex = -1;
            for (var candidate = index; candidate < target.Count; candidate++)
            {
                if (EqualityComparer<TKey>.Default.Equals(keySelector(target[candidate]), desiredKey))
                {
                    existingIndex = candidate;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                target.Insert(index, desired);
            }
            else
            {
                if (existingIndex != index) target.Move(existingIndex, index);
                if (!equivalent(target[index], desired))
                {
                    if (update is null) target[index] = desired;
                    else update(target[index], desired);
                }
            }
        }

        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
    }
}

public sealed class AccountItem
{
    public AccountItem(MailAccount account)
    {
        Model = account;
        DisplayName = account.MailboxName;
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
    public string MailboxDisplay => Model is null || DisplayName.Equals(Model.Email, StringComparison.OrdinalIgnoreCase)
        ? DisplayName
        : $"{DisplayName} · {Model.Email}";
    public string SyncStatus => Model is null
        ? "All connected mailboxes"
        : !Model.IsEnabled
            ? "Disabled"
            : !string.IsNullOrWhiteSpace(Model.LastSyncError)
                ? "Needs attention"
                : Model.LastSuccessfulSync is { } synced
                    ? $"Synced {synced.ToLocalTime():g}"
                    : "Ready to sync";
    public string ManagementDisplay => Model is null ? DisplayName : $"{MailboxDisplay} · {(Model.IsEnabled ? "Enabled" : "Disabled")}";
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
    public string Key => Model?.Id.ToString("N") ?? UnifiedKind?.ToString() ?? "all";
    private static string FormatBadge(int unread) => unread switch
    {
        <= 0 => string.Empty,
        > 99 => "99+",
        _ => unread.ToString()
    };
    public static FolderItem AllMail() => new();
    public static FolderItem UnifiedInbox(int unread) => new("Inbox", "\uE715", SpecialFolderKind.Inbox, unread);
    public static FolderItem UnifiedStarred() => new("Starred", "\uE734", SpecialFolderKind.Starred);
    public static FolderItem Unified(string displayName, string glyph, SpecialFolderKind kind) => new(displayName, glyph, kind);
}

public sealed class ConversationItem : ObservableObject
{
    public ConversationItem(MailConversation model, string accountLabel = "Mailbox", bool showAccount = false)
    {
        Model = model;
        AccountLabel = accountLabel;
        ShowAccount = showAccount;
    }

    public MailConversation Model { get; private set; }
    public string Sender => Model.Participants.FirstOrDefault()?.DisplayName ?? "Unknown sender";
    public string Initials => string.Concat(Sender.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(static part => char.ToUpperInvariant(part[0])));
    public string Subject => Model.Subject;
    public string Snippet => Model.Snippet;
    public string Time => FormatTime(Model.LastMessageAt);
    public string Count => Model.MessageCount > 1 ? Model.MessageCount.ToString() : string.Empty;
    public bool IsUnread => Model.UnreadCount > 0;
    public bool IsRead => !IsUnread;
    public string Star => Model.IsStarred ? "★" : string.Empty;
    public string Attachment => Model.HasAttachments ? "\uE723" : string.Empty;
    public string AccountLabel { get; private set; }
    public bool ShowAccount { get; private set; }

    public void UpdateFrom(ConversationItem updated)
    {
        Model = updated.Model;
        AccountLabel = updated.AccountLabel;
        ShowAccount = updated.ShowAccount;
        OnPropertyChanged(nameof(Sender));
        OnPropertyChanged(nameof(Initials));
        OnPropertyChanged(nameof(Subject));
        OnPropertyChanged(nameof(Snippet));
        OnPropertyChanged(nameof(Time));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsUnread));
        OnPropertyChanged(nameof(IsRead));
        OnPropertyChanged(nameof(Star));
        OnPropertyChanged(nameof(Attachment));
        OnPropertyChanged(nameof(AccountLabel));
        OnPropertyChanged(nameof(ShowAccount));
    }

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
    public MessageItem(MailMessage model, IHtmlSanitizer htmlSanitizer, string accountLabel = "Mailbox", bool blockRemoteImages = false)
    {
        Model = model;
        Sender = model.From.DisplayName;
        Address = model.From.Address;
        Initials = string.Concat(Sender.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(static part => char.ToUpperInvariant(part[0])));
        Date = model.ReceivedAt.ToLocalTime().ToString("dddd, d MMMM · HH:mm");
        Body = !string.IsNullOrWhiteSpace(model.TextBody) ? model.TextBody : model.Snippet;
        HtmlBody = string.IsNullOrWhiteSpace(model.HtmlBody) ? string.Empty : htmlSanitizer.Sanitize(model.HtmlBody, allowExternalImages: !blockRemoteImages);
        HasHtml = !string.IsNullOrWhiteSpace(HtmlBody);
        HasPlainBody = !string.IsNullOrWhiteSpace(Body);
        ShowPlain = !HasHtml && HasPlainBody;
        ShowHtml = HasHtml;
        CanShowFormatted = false;
        Attachments = model.Attachments.Select(attachment => new AttachmentItem(model, attachment)).ToArray();
        HtmlHeight = Math.Clamp(112 + ((model.HtmlBody?.Length ?? 0) / 90d * 18), 132, 720);
        AccountLabel = accountLabel;
        FromLine = FormatAddress(model.From);
        ToLine = FormatAddresses(model.To);
        CcLine = FormatAddresses(model.Cc);
        BccLine = FormatAddresses(model.Bcc);
        HasCc = model.Cc.Count > 0;
        HasBcc = model.Bcc.Count > 0;
        RecipientSummary = model.To.Count == 0 ? "Recipient details" : $"To: {CompactRecipients(model.To)}";
        CompactCc = HasCc ? $"Cc: {CompactRecipients(model.Cc)}" : string.Empty;
        CompactBcc = HasBcc ? $"Bcc: {CompactRecipients(model.Bcc)}" : string.Empty;
    }

    public MailMessage Model { get; }
    public string Sender { get; }
    public string Address { get; }
    public string Initials { get; }
    public string Date { get; }
    public string Body { get; }
    public string HtmlBody { get; }
    public bool HasHtml { get; }
    public bool HasPlainBody { get; }
    [ObservableProperty] public partial double HtmlHeight { get; set; }
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
    public string CompactCc { get; }
    public string CompactBcc { get; }
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

public sealed class DraftRowItem
{
    public DraftRowItem(Draft draft)
    {
        Draft = draft;
        Subject = string.IsNullOrWhiteSpace(draft.Subject) ? "(No subject)" : draft.Subject;
        Recipients = draft.To.Count == 0 ? "No recipient yet" : "To: " + string.Join(", ", draft.To.Select(static item => item.DisplayName));
        State = draft.DeliveryState switch
        {
            DraftDeliveryState.Queued => "Queued",
            DraftDeliveryState.Sending => "Sending",
            DraftDeliveryState.Failed => "Needs attention",
            _ => "Draft"
        };
        Error = draft.LastError ?? string.Empty;
        Updated = draft.UpdatedAt.ToLocalTime().ToString("g");
    }

    public Draft Draft { get; }
    public string Subject { get; }
    public string Recipients { get; }
    public string State { get; }
    public string Error { get; }
    public string Updated { get; }
}
