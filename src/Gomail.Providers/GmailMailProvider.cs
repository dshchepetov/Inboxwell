using System.Runtime.CompilerServices;
using System.Text;
using Gomail.Core;
using Google;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using MimeKit;
using GmailMessage = Google.Apis.Gmail.v1.Data.Message;
using GmailPart = Google.Apis.Gmail.v1.Data.MessagePart;
using GmailThread = Google.Apis.Gmail.v1.Data.Thread;

namespace Gomail.Providers;

public sealed class GmailMailProvider : IMailProvider
{
    private readonly IGmailAuthenticationService authentication;
    private readonly IHtmlSanitizer htmlSanitizer;

    public GmailMailProvider(IGmailAuthenticationService authentication, IHtmlSanitizer htmlSanitizer)
    {
        this.authentication = authentication;
        this.htmlSanitizer = htmlSanitizer;
    }

    public ProviderKind Kind => ProviderKind.Gmail;

    public ProviderCapabilities Capabilities { get; } = new(false, true, true, true, true, true, true, 25L * 1024 * 1024);

    public async Task<ConnectionTestResult> TestConnectionAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        try
        {
            using var service = await CreateServiceAsync(account, cancellationToken);
            var profile = await service.Users.GetProfile("me").ExecuteAsync(cancellationToken);
            return new ConnectionTestResult(true, Email: profile.EmailAddress);
        }
        catch (Exception exception)
        {
            return new ConnectionTestResult(false, Error: FriendlyError(exception));
        }
    }

    public async IAsyncEnumerable<SyncBatch> InitialSyncAsync(
        MailAccount account,
        DateTimeOffset bodyCutoff,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var batch in InitialSyncCoreAsync(account, bodyCutoff, null, 0, null, cancellationToken))
        {
            yield return batch;
        }
    }

    private async IAsyncEnumerable<SyncBatch> InitialSyncCoreAsync(
        MailAccount account,
        DateTimeOffset bodyCutoff,
        string? startPageToken,
        int startFetched,
        string? initialHistoryId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var service = await CreateServiceAsync(account, cancellationToken);
        var folders = await GetFoldersAsync(service, account, cancellationToken);
        var folderMap = folders.ToDictionary(static folder => folder.RemoteId, StringComparer.Ordinal);
        var profile = await service.Users.GetProfile("me").ExecuteAsync(cancellationToken);
        var limit = Math.Clamp(account.GetIntSetting("syncMessageLimit", 5000), 100, 50_000);
        var historyId = initialHistoryId ?? profile.HistoryId?.ToString() ?? "0";
        var fetched = startFetched;
        string? pageToken = startPageToken;

        do
        {
            var request = service.Users.Threads.List("me");
            request.MaxResults = 100;
            request.IncludeSpamTrash = true;
            request.PageToken = pageToken;
            var page = await request.ExecuteAsync(cancellationToken);
            var messages = new List<MailMessage>();
            var conversations = new List<MailConversation>();

            // Gmail does not offer a batch endpoint for full thread payloads. Fetch a
            // bounded group concurrently: this keeps us below the per-user burst quota
            // while avoiding one network round-trip per thread in strict sequence.
            var candidates = (page.Threads ?? new List<GmailThread>())
                .Take(Math.Max(0, limit - fetched))
                .ToArray();
            foreach (var wave in candidates.Chunk(12))
            {
                if (fetched >= limit)
                {
                    break;
                }

                var loadedThreads = await Task.WhenAll(wave.Select(async item =>
                {
                    var threadRequest = service.Users.Threads.Get("me", item.Id);
                    threadRequest.Format = UsersResource.ThreadsResource.GetRequest.FormatEnum.Full;
                    return await threadRequest.ExecuteAsync(cancellationToken);
                }));
                foreach (var thread in loadedThreads)
                {
                var threadMessages = (thread.Messages ?? Array.Empty<GmailMessage>())
                    .Select(message => ConvertMessage(account, message, folderMap, bodyCutoff))
                    .ToArray();
                messages.AddRange(threadMessages);
                conversations.Add(BuildConversation(account, thread.Id, threadMessages));
                fetched += threadMessages.Length;
                }
            }

            var nextPageToken = fetched >= limit ? null : page.NextPageToken;
            yield return new SyncBatch
            {
                Folders = fetched <= messages.Count ? folders : Array.Empty<MailFolder>(),
                Conversations = conversations,
                Messages = messages,
                NextCursor = string.IsNullOrWhiteSpace(nextPageToken)
                    ? null
                    : new SyncCursor(account.Id, "mail", EncodeInitialProgress(historyId, nextPageToken, fetched), DateTimeOffset.UtcNow),
                IsComplete = false
            };
            pageToken = nextPageToken;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        yield return new SyncBatch
        {
            NextCursor = new SyncCursor(account.Id, "mail", historyId, DateTimeOffset.UtcNow),
            IsComplete = true
        };
    }

    public async IAsyncEnumerable<SyncBatch> IncrementalSyncAsync(
        MailAccount account,
        SyncCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (TryParseInitialProgress(cursor.Value, out var progress))
        {
            await foreach (var batch in InitialSyncCoreAsync(
                account,
                DateTimeOffset.UtcNow.AddDays(-90),
                progress.PageToken,
                progress.Fetched,
                progress.HistoryId,
                cancellationToken))
            {
                yield return batch;
            }
            yield break;
        }

        if (!ulong.TryParse(cursor.Value, out var historyId) || historyId == 0)
        {
            await foreach (var batch in InitialSyncAsync(account, DateTimeOffset.UtcNow.AddDays(-90), cancellationToken))
            {
                yield return batch;
            }
            yield break;
        }

        using var service = await CreateServiceAsync(account, cancellationToken);
        var folders = await GetFoldersAsync(service, account, cancellationToken);
        var folderMap = folders.ToDictionary(static folder => folder.RemoteId, StringComparer.Ordinal);
        var historyDelta = await ReadHistoryAsync(service, historyId, cancellationToken);
        if (historyDelta is null)
        {
            await foreach (var batch in InitialSyncAsync(account, DateTimeOffset.UtcNow.AddDays(-90), cancellationToken))
            {
                yield return batch;
            }
            yield break;
        }

        var messageIds = historyDelta.MessageIds;
        var deletedIds = historyDelta.DeletedIds;
        var newestHistoryId = historyDelta.NewestHistoryId;

        foreach (var chunk in messageIds.Chunk(50))
        {
            var messages = new List<MailMessage>();
            foreach (var wave in chunk.Chunk(12))
            {
                var loadedMessages = await Task.WhenAll(wave.Select(async id =>
                {
                    var request = service.Users.Messages.Get("me", id);
                    request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
                    return await request.ExecuteAsync(cancellationToken);
                }));
                messages.AddRange(loadedMessages.Select(message =>
                    ConvertMessage(account, message, folderMap, DateTimeOffset.UtcNow.AddDays(-90))));
            }
            yield return new SyncBatch
            {
                Conversations = messages.GroupBy(static message => message.ProviderThreadId).Select(group => BuildConversation(account, group.Key ?? group.First().RemoteId, group.ToArray())).ToArray(),
                Messages = messages,
                DeletedRemoteMessageIds = deletedIds.ToArray(),
                IsComplete = false
            };
            deletedIds.Clear();
        }

        yield return new SyncBatch
        {
            DeletedRemoteMessageIds = deletedIds.ToArray(),
            NextCursor = new SyncCursor(account.Id, "mail", newestHistoryId.ToString(), DateTimeOffset.UtcNow),
            IsComplete = true
        };
    }

    private static async Task<HistoryDelta?> ReadHistoryAsync(
        GmailService service,
        ulong historyId,
        CancellationToken cancellationToken)
    {
        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        var deletedIds = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;
        var newestHistoryId = historyId;

        try
        {
            do
            {
                var request = service.Users.History.List("me");
                request.StartHistoryId = historyId;
                request.PageToken = pageToken;
                var page = await request.ExecuteAsync(cancellationToken);
                foreach (var history in page.History ?? Array.Empty<History>())
                {
                    if (history.Id.HasValue) newestHistoryId = Math.Max(newestHistoryId, history.Id.Value);
                    foreach (var added in history.MessagesAdded ?? Array.Empty<HistoryMessageAdded>())
                        if (!string.IsNullOrWhiteSpace(added.Message?.Id)) messageIds.Add(added.Message.Id);
                    foreach (var deleted in history.MessagesDeleted ?? Array.Empty<HistoryMessageDeleted>())
                        if (!string.IsNullOrWhiteSpace(deleted.Message?.Id)) deletedIds.Add(deleted.Message.Id);
                    foreach (var changed in history.LabelsAdded ?? Array.Empty<HistoryLabelAdded>())
                        if (!string.IsNullOrWhiteSpace(changed.Message?.Id)) messageIds.Add(changed.Message.Id);
                    foreach (var changed in history.LabelsRemoved ?? Array.Empty<HistoryLabelRemoved>())
                        if (!string.IsNullOrWhiteSpace(changed.Message?.Id)) messageIds.Add(changed.Message.Id);
                }
                pageToken = page.NextPageToken;
                if (page.HistoryId.HasValue) newestHistoryId = Math.Max(newestHistoryId, page.HistoryId.Value);
            }
            while (!string.IsNullOrWhiteSpace(pageToken));

            return new HistoryDelta(messageIds, deletedIds, newestHistoryId);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private sealed record HistoryDelta(HashSet<string> MessageIds, HashSet<string> DeletedIds, ulong NewestHistoryId);
    private sealed record InitialProgress(string HistoryId, string PageToken, int Fetched);

    private static string EncodeInitialProgress(string historyId, string pageToken, int fetched) =>
        $"gmail-initial|{historyId}|{fetched}|{pageToken}";

    private static bool TryParseInitialProgress(string value, out InitialProgress progress)
    {
        var parts = value.Split('|', 4, StringSplitOptions.None);
        if (parts.Length == 4 && parts[0] == "gmail-initial" && int.TryParse(parts[2], out var fetched) && fetched >= 0 && !string.IsNullOrWhiteSpace(parts[3]))
        {
            progress = new InitialProgress(parts[1], parts[3], fetched);
            return true;
        }
        progress = null!;
        return false;
    }

    public async Task ExecuteAsync(MailAccount account, PendingOperation operation, CancellationToken cancellationToken = default)
    {
        using var service = await CreateServiceAsync(account, cancellationToken);
        switch (operation.Kind)
        {
            case PendingOperationKind.MarkRead:
                await ModifyAsync(service, operation.TargetRemoteId, null, new[] { "UNREAD" }, cancellationToken);
                break;
            case PendingOperationKind.MarkUnread:
                await ModifyAsync(service, operation.TargetRemoteId, new[] { "UNREAD" }, null, cancellationToken);
                break;
            case PendingOperationKind.Star:
                await ModifyAsync(service, operation.TargetRemoteId, new[] { "STARRED" }, null, cancellationToken);
                break;
            case PendingOperationKind.Unstar:
                await ModifyAsync(service, operation.TargetRemoteId, null, new[] { "STARRED" }, cancellationToken);
                break;
            case PendingOperationKind.Archive:
                await ModifyAsync(service, operation.TargetRemoteId, null, new[] { "INBOX" }, cancellationToken);
                break;
            case PendingOperationKind.Move:
                var movePayload = ProviderUtilities.DeserializePayload<RemoteOperationPayload>(operation);
                var destination = movePayload?.DestinationRemoteId ?? throw new MailProviderException("Gmail destination label is missing.");
                if (destination == "TRASH")
                {
                    await service.Users.Messages.Trash("me", operation.TargetRemoteId).ExecuteAsync(cancellationToken);
                }
                else
                {
                    var remove = !string.IsNullOrWhiteSpace(movePayload.FolderRemoteId) && movePayload.FolderRemoteId != "ALL" && movePayload.FolderRemoteId != destination
                        ? new[] { movePayload.FolderRemoteId }
                        : null;
                    await ModifyAsync(service, operation.TargetRemoteId, new[] { destination }, remove, cancellationToken);
                }
                break;
            case PendingOperationKind.MarkSpam:
                await ModifyAsync(service, operation.TargetRemoteId, new[] { "SPAM" }, new[] { "INBOX" }, cancellationToken);
                break;
            case PendingOperationKind.ApplyLabel:
                var addPayload = ProviderUtilities.DeserializePayload<RemoteOperationPayload>(operation);
                await ModifyAsync(service, operation.TargetRemoteId, new[] { addPayload?.LabelId ?? throw new MailProviderException("Gmail label is missing.") }, null, cancellationToken);
                break;
            case PendingOperationKind.RemoveLabel:
                var removePayload = ProviderUtilities.DeserializePayload<RemoteOperationPayload>(operation);
                await ModifyAsync(service, operation.TargetRemoteId, null, new[] { removePayload?.LabelId ?? throw new MailProviderException("Gmail label is missing.") }, cancellationToken);
                break;
            case PendingOperationKind.Delete:
                await service.Users.Messages.Trash("me", operation.TargetRemoteId).ExecuteAsync(cancellationToken);
                break;
            default:
                throw new MailProviderException($"Gmail does not support the queued action {operation.Kind}.");
        }
    }

    public async Task<SendResult> SendAsync(MailAccount account, OutgoingMessage outgoing, CancellationToken cancellationToken = default)
    {
        try
        {
            using var service = await CreateServiceAsync(account, cancellationToken);
            var mime = await BuildMimeMessageAsync(account, outgoing, cancellationToken);
            await using var stream = new MemoryStream();
            await mime.WriteToAsync(stream, cancellationToken);
            var message = new GmailMessage
            {
                Raw = Base64UrlEncode(stream.ToArray()),
                ThreadId = outgoing.ProviderThreadId
            };
            var sent = await service.Users.Messages.Send(message, "me").ExecuteAsync(cancellationToken);
            return new SendResult(true, sent.Id);
        }
        catch (Exception exception)
        {
            return new SendResult(false, Error: FriendlyError(exception));
        }
    }

    public async Task<IReadOnlyList<MailMessage>> SearchAsync(MailAccount account, SearchRequest request, CancellationToken cancellationToken = default)
    {
        using var service = await CreateServiceAsync(account, cancellationToken);
        var folders = await GetFoldersAsync(service, account, cancellationToken);
        var folderMap = folders.ToDictionary(static folder => folder.RemoteId, StringComparer.Ordinal);
        var list = service.Users.Messages.List("me");
        list.Q = request.Text;
        list.MaxResults = Math.Clamp(request.Limit, 1, 500);
        list.IncludeSpamTrash = true;
        var page = await list.ExecuteAsync(cancellationToken);
        var result = new List<MailMessage>();
        foreach (var item in page.Messages ?? Array.Empty<GmailMessage>())
        {
            var get = service.Users.Messages.Get("me", item.Id);
            get.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
            result.Add(ConvertMessage(account, await get.ExecuteAsync(cancellationToken), folderMap, DateTimeOffset.MinValue));
        }
        return result;
    }

    public async Task DownloadAttachmentAsync(
        MailAccount account,
        MailMessage message,
        MailAttachment attachment,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        using var service = await CreateServiceAsync(account, cancellationToken);
        byte[]? bytes = null;
        try
        {
            var body = await service.Users.Messages.Attachments.Get("me", message.RemoteId, attachment.RemoteId).ExecuteAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(body.Data))
            {
                bytes = Base64UrlDecode(body.Data);
            }
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == System.Net.HttpStatusCode.NotFound || exception.HttpStatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            // Small MIME parts can be embedded directly and use a part id rather than an attachment id.
        }

        if (bytes is null)
        {
            var request = service.Users.Messages.Get("me", message.RemoteId);
            request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
            var source = await request.ExecuteAsync(cancellationToken);
            var part = FindPart(source.Payload, attachment)
                ?? throw new MailProviderException("The attachment is no longer present in this Gmail message.");
            if (!string.IsNullOrWhiteSpace(part.Body?.Data))
            {
                bytes = Base64UrlDecode(part.Body.Data);
            }
            else if (!string.IsNullOrWhiteSpace(part.Body?.AttachmentId))
            {
                var body = await service.Users.Messages.Attachments.Get("me", message.RemoteId, part.Body.AttachmentId).ExecuteAsync(cancellationToken);
                bytes = Base64UrlDecode(body.Data ?? string.Empty);
            }
        }

        if (bytes is null)
        {
            throw new MailProviderException("Gmail returned an empty attachment.");
        }
        await destination.WriteAsync(bytes, cancellationToken);
    }

    public async Task<MailMessage> HydrateMessageAsync(MailAccount account, MailMessage message, CancellationToken cancellationToken = default)
    {
        using var service = await CreateServiceAsync(account, cancellationToken);
        var folders = await GetFoldersAsync(service, account, cancellationToken);
        var request = service.Users.Messages.Get("me", message.RemoteId);
        request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
        return ConvertMessage(account, await request.ExecuteAsync(cancellationToken), folders.ToDictionary(static folder => folder.RemoteId, StringComparer.Ordinal), DateTimeOffset.MinValue);
    }

    private async Task<GmailService> CreateServiceAsync(MailAccount account, CancellationToken cancellationToken)
    {
        var credential = await authentication.GetCredentialAsync(account, cancellationToken);
        return new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Inboxwell"
        });
    }

    private static async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(GmailService service, MailAccount account, CancellationToken cancellationToken)
    {
        var response = await service.Users.Labels.List("me").ExecuteAsync(cancellationToken);
        var visibleSystemLabels = new HashSet<string>(StringComparer.Ordinal)
        {
            "INBOX", "DRAFT", "SENT", "SPAM", "TRASH", "STARRED", "ALL"
        };
        var folders = (response.Labels ?? Array.Empty<Label>())
            .Where(label => string.Equals(label.Type, "user", StringComparison.OrdinalIgnoreCase) || visibleSystemLabels.Contains(label.Id))
            .Select(label => new MailFolder
        {
            Id = ProviderUtilities.StableGuid(account.Id, $"label:{label.Id}"),
            AccountId = account.Id,
            RemoteId = label.Id,
            Name = label.Name,
            SpecialKind = label.Id switch
            {
                "INBOX" => SpecialFolderKind.Inbox,
                "DRAFT" => SpecialFolderKind.Drafts,
                "SENT" => SpecialFolderKind.Sent,
                "SPAM" => SpecialFolderKind.Spam,
                "TRASH" => SpecialFolderKind.Trash,
                "STARRED" => SpecialFolderKind.Starred,
                "ALL" => SpecialFolderKind.AllMail,
                _ => SpecialFolderKind.None
            },
            UnreadCount = label.MessagesUnread ?? 0,
            TotalCount = label.MessagesTotal ?? 0
        }).ToList();
        if (folders.All(static folder => folder.RemoteId != "ALL"))
        {
            folders.Add(new MailFolder
            {
                Id = ProviderUtilities.StableGuid(account.Id, "label:ALL"),
                AccountId = account.Id,
                RemoteId = "ALL",
                Name = "All Mail",
                SpecialKind = SpecialFolderKind.AllMail
            });
        }
        return folders;
    }

    private MailMessage ConvertMessage(MailAccount account, GmailMessage source, IReadOnlyDictionary<string, MailFolder> folders, DateTimeOffset bodyCutoff)
    {
        var headers = (source.Payload?.Headers ?? Array.Empty<MessagePartHeader>())
            .GroupBy(static header => header.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last().Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        var receivedAt = source.InternalDate.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(source.InternalDate.Value) : DateTimeOffset.MinValue;
        var labelIds = source.LabelIds?.ToArray() ?? Array.Empty<string>();
        var folderRemoteId = labelIds.Contains("INBOX", StringComparer.Ordinal) ? "INBOX" : labelIds.FirstOrDefault(id => folders.ContainsKey(id)) ?? "ALL";
        var folder = folders.TryGetValue(folderRemoteId, out var knownFolder)
            ? knownFolder
            : new MailFolder { Id = ProviderUtilities.StableGuid(account.Id, $"label:{folderRemoteId}"), AccountId = account.Id, RemoteId = folderRemoteId, Name = folderRemoteId };
        var from = ParseMailbox(Header(headers, "From"));
        var to = ParseMailboxes(Header(headers, "To"));
        var threadKey = ConversationThreader.CreateThreadKey(account.Id, source.ThreadId, Header(headers, "Message-ID"), Header(headers, "In-Reply-To"), ParseReferences(Header(headers, "References")), Header(headers, "Subject"), new[] { from }.Concat(to));
        var conversationId = ProviderUtilities.StableGuid(account.Id, threadKey);
        var messageId = ProviderUtilities.StableGuid(account.Id, $"gmail:{source.Id}");
        var bodies = receivedAt >= bodyCutoff ? ExtractBodies(source.Payload) : (Text: (string?)null, Html: (string?)null);
        var attachments = ConvertAttachments(account, messageId, source.Payload);

        return new MailMessage
        {
            Id = messageId,
            AccountId = account.Id,
            FolderId = folder.Id,
            ConversationId = conversationId,
            RemoteId = source.Id,
            ProviderThreadId = source.ThreadId,
            InternetMessageId = Header(headers, "Message-ID"),
            InReplyTo = Header(headers, "In-Reply-To"),
            References = ParseReferences(Header(headers, "References")),
            From = from,
            To = to,
            Cc = ParseMailboxes(Header(headers, "Cc")),
            Bcc = ParseMailboxes(Header(headers, "Bcc")),
            Subject = Header(headers, "Subject") ?? "(no subject)",
            Snippet = source.Snippet ?? string.Empty,
            TextBody = bodies.Text ?? ProviderUtilities.StripHtml(bodies.Html),
            HtmlBody = string.IsNullOrWhiteSpace(bodies.Html) ? null : htmlSanitizer.Sanitize(bodies.Html),
            SentAt = DateTimeOffset.TryParse(Header(headers, "Date"), out var sentAt) ? sentAt : receivedAt,
            ReceivedAt = receivedAt,
            Flags = (!labelIds.Contains("UNREAD", StringComparer.Ordinal) ? MailFlags.Read : MailFlags.None) |
                    (labelIds.Contains("STARRED", StringComparer.Ordinal) ? MailFlags.Starred : MailFlags.None) |
                    (labelIds.Contains("DRAFT", StringComparer.Ordinal) ? MailFlags.Draft : MailFlags.None) |
                    (attachments.Length > 0 ? MailFlags.HasAttachments : MailFlags.None),
            Labels = labelIds,
            Attachments = attachments
        };
    }

    private static MailConversation BuildConversation(MailAccount account, string threadId, IReadOnlyList<MailMessage> messages)
    {
        var ordered = messages.OrderBy(static message => message.ReceivedAt).ToArray();
        var latest = ordered.LastOrDefault() ?? throw new InvalidOperationException("A Gmail thread must contain at least one message.");
        return new MailConversation
        {
            Id = latest.ConversationId,
            AccountId = account.Id,
            ThreadKey = ConversationThreader.CreateThreadKey(account.Id, threadId, latest.InternetMessageId, latest.InReplyTo, latest.References, latest.Subject, ordered.Select(static x => x.From)),
            ProviderThreadId = threadId,
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

    private static async Task ModifyAsync(GmailService service, string messageId, IEnumerable<string>? add, IEnumerable<string>? remove, CancellationToken cancellationToken)
    {
        var request = new ModifyMessageRequest
        {
            AddLabelIds = add?.ToArray(),
            RemoveLabelIds = remove?.ToArray()
        };
        await service.Users.Messages.Modify(request, "me", messageId).ExecuteAsync(cancellationToken);
    }

    private static (string? Text, string? Html) ExtractBodies(GmailPart? part)
    {
        if (part is null)
        {
            return (null, null);
        }
        string? text = null;
        string? html = null;
        if (!string.IsNullOrWhiteSpace(part.Body?.Data))
        {
            var decoded = Encoding.UTF8.GetString(Base64UrlDecode(part.Body.Data));
            if (part.MimeType.Equals("text/plain", StringComparison.OrdinalIgnoreCase)) text = decoded;
            if (part.MimeType.Equals("text/html", StringComparison.OrdinalIgnoreCase)) html = decoded;
        }
        foreach (var child in part.Parts ?? Array.Empty<GmailPart>())
        {
            var nested = ExtractBodies(child);
            text ??= nested.Text;
            html ??= nested.Html;
        }
        return (text, html);
    }

    private static MailAttachment[] ConvertAttachments(MailAccount account, Guid messageId, GmailPart? root)
    {
        var result = new List<MailAttachment>();
        Walk(root);
        return result.ToArray();

        void Walk(GmailPart? part)
        {
            if (part is null) return;
            if (!string.IsNullOrWhiteSpace(part.Filename) || !string.IsNullOrWhiteSpace(part.Body?.AttachmentId))
            {
                var remoteId = part.Body?.AttachmentId ?? part.PartId ?? result.Count.ToString();
                result.Add(new MailAttachment
                {
                    Id = ProviderUtilities.StableGuid(account.Id, $"gmail-attachment:{messageId:N}:{remoteId}"),
                    MessageId = messageId,
                    RemoteId = remoteId,
                    FileName = string.IsNullOrWhiteSpace(part.Filename) ? $"attachment-{result.Count + 1}" : part.Filename,
                    ContentType = part.MimeType ?? "application/octet-stream",
                    Size = part.Body?.Size ?? 0,
                    IsInline = part.Headers?.Any(header => header.Name.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase) && header.Value.Contains("inline", StringComparison.OrdinalIgnoreCase)) == true,
                    ContentId = part.Headers?.FirstOrDefault(header => header.Name.Equals("Content-ID", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('<', '>')
                });
            }
            foreach (var child in part.Parts ?? Array.Empty<GmailPart>()) Walk(child);
        }
    }

    private static GmailPart? FindPart(GmailPart? root, MailAttachment attachment)
    {
        if (root is null) return null;
        if (string.Equals(root.Body?.AttachmentId, attachment.RemoteId, StringComparison.Ordinal) ||
            string.Equals(root.PartId, attachment.RemoteId, StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(attachment.ContentId) &&
             root.Headers?.Any(header => header.Name.Equals("Content-ID", StringComparison.OrdinalIgnoreCase) &&
                                         string.Equals(header.Value?.Trim('<', '>'), attachment.ContentId, StringComparison.OrdinalIgnoreCase)) == true) ||
            (!string.IsNullOrWhiteSpace(root.Filename) && string.Equals(root.Filename, attachment.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            return root;
        }

        foreach (var child in root.Parts ?? Array.Empty<GmailPart>())
        {
            var found = FindPart(child, attachment);
            if (found is not null) return found;
        }
        return null;
    }

    private static async Task<MimeMessage> BuildMimeMessageAsync(MailAccount account, OutgoingMessage outgoing, CancellationToken cancellationToken)
    {
        var message = new MimeMessage { Subject = outgoing.Subject, MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId() };
        message.From.Add(new MailboxAddress(account.DisplayName, account.Email));
        AddAddresses(message.To, outgoing.To);
        AddAddresses(message.Cc, outgoing.Cc);
        AddAddresses(message.Bcc, outgoing.Bcc);
        if (!string.IsNullOrWhiteSpace(outgoing.ReplyToRemoteId))
        {
            message.InReplyTo = outgoing.ReplyToRemoteId;
            message.References.Add(outgoing.ReplyToRemoteId);
        }
        if (outgoing.IsImportant)
        {
            message.Importance = MimeKit.MessageImportance.High;
            message.Priority = MimeKit.MessagePriority.Urgent;
        }
        var builder = new BodyBuilder { HtmlBody = outgoing.HtmlBody, TextBody = outgoing.PlainTextBody };
        foreach (var attachment in outgoing.Attachments)
        {
            await using var stream = File.OpenRead(attachment.LocalPath);
            var bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            builder.Attachments.Add(attachment.FileName, bytes, ContentType.Parse(attachment.ContentType));
        }
        message.Body = builder.ToMessageBody();
        return message;
    }

    private static void AddAddresses(InternetAddressList target, IEnumerable<MailAddress> addresses)
    {
        foreach (var address in addresses) target.Add(new MailboxAddress(address.Name, address.Address));
    }

    private static MailAddress ParseMailbox(string? value)
    {
        if (MailboxAddress.TryParse(value, out var mailbox)) return new MailAddress(mailbox.Name ?? string.Empty, mailbox.Address);
        return new MailAddress(string.Empty, value ?? string.Empty);
    }

    private static MailAddress[] ParseMailboxes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<MailAddress>();
        try
        {
            return InternetAddressList.Parse(value).Mailboxes.Select(static mailbox => new MailAddress(mailbox.Name ?? string.Empty, mailbox.Address)).ToArray();
        }
        catch (ParseException)
        {
            return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseMailbox).ToArray();
        }
    }

    private static string? Header(IReadOnlyDictionary<string, string> headers, string name) => headers.TryGetValue(name, out var value) ? value : null;
    private static string[] ParseReferences(string? value) => string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string Base64UrlEncode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string FriendlyError(Exception exception) => exception switch
    {
        GoogleApiException google => google.Error?.Message ?? google.Message,
        _ => exception.Message
    };
}
