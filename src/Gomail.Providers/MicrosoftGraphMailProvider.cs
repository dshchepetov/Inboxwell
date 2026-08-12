using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Gomail.Core;

namespace Gomail.Providers;

public sealed class MicrosoftGraphMailProvider : IMailProvider
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private readonly IMicrosoftAuthenticationService authentication;
    private readonly IHtmlSanitizer htmlSanitizer;
    private readonly HttpClient httpClient;

    public MicrosoftGraphMailProvider(IMicrosoftAuthenticationService authentication, IHtmlSanitizer htmlSanitizer, HttpClient? httpClient = null)
    {
        this.authentication = authentication;
        this.htmlSanitizer = htmlSanitizer;
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public ProviderKind Kind => ProviderKind.Microsoft365;

    public ProviderCapabilities Capabilities { get; } = new(true, false, true, true, true, true, true, 150L * 1024 * 1024);

    public async Task<ConnectionTestResult> TestConnectionAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = await GetJsonAsync(account, $"{GraphBase}/me?$select=displayName,mail,userPrincipalName", true, cancellationToken);
            var root = document.RootElement;
            var email = root.TryGetProperty("mail", out var mail) && !string.IsNullOrWhiteSpace(mail.GetString())
                ? mail.GetString()
                : root.GetProperty("userPrincipalName").GetString();
            return new ConnectionTestResult(true, root.GetProperty("displayName").GetString() ?? account.Email, Email: email);
        }
        catch (Exception exception)
        {
            return new ConnectionTestResult(false, Error: exception.Message);
        }
    }

    public async IAsyncEnumerable<SyncBatch> InitialSyncAsync(
        MailAccount account,
        DateTimeOffset bodyCutoff,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var folders = await GetFoldersAsync(account, cancellationToken);
        var cursors = new Dictionary<string, string>(StringComparer.Ordinal);
        var folderModels = folders.ToDictionary(static folder => folder.RemoteId, StringComparer.Ordinal);

        foreach (var folder in folders)
        {
            var endpoint = $"{GraphBase}/me/mailFolders/{Uri.EscapeDataString(folder.RemoteId)}/messages/delta" +
                           "?$select=id,conversationId,internetMessageId,subject,bodyPreview,body,from,toRecipients,ccRecipients,bccRecipients,receivedDateTime,sentDateTime,isRead,hasAttachments,flag,parentFolderId&$top=100";
            await foreach (var page in ReadDeltaPagesAsync(account, endpoint, folderModels, bodyCutoff, true, cancellationToken))
            {
                if (page.NextCursor is not null)
                {
                    cursors[folder.RemoteId] = page.NextCursor.Value;
                }
                yield return page with { Folders = page.Folders.Count == 0 ? new[] { folder } : page.Folders, NextCursor = null };
            }
        }

        yield return new SyncBatch
        {
            NextCursor = new SyncCursor(account.Id, "mail", JsonSerializer.Serialize(cursors), DateTimeOffset.UtcNow),
            IsComplete = true
        };
    }

    public async IAsyncEnumerable<SyncBatch> IncrementalSyncAsync(
        MailAccount account,
        SyncCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Dictionary<string, string>? cursors;
        try
        {
            cursors = JsonSerializer.Deserialize<Dictionary<string, string>>(cursor.Value);
        }
        catch (JsonException)
        {
            cursors = null;
        }

        if (cursors is null || cursors.Count == 0)
        {
            await foreach (var batch in InitialSyncAsync(account, DateTimeOffset.UtcNow.AddDays(-90), cancellationToken))
            {
                yield return batch;
            }
            yield break;
        }

        var folders = await GetFoldersAsync(account, cancellationToken);
        var folderModels = folders.ToDictionary(static folder => folder.RemoteId, StringComparer.Ordinal);
        var updated = new Dictionary<string, string>(cursors, StringComparer.Ordinal);
        foreach (var folder in folders.Where(folder => !cursors.ContainsKey(folder.RemoteId)))
        {
            var endpoint = $"{GraphBase}/me/mailFolders/{Uri.EscapeDataString(folder.RemoteId)}/messages/delta" +
                           "?$select=id,conversationId,internetMessageId,subject,bodyPreview,body,from,toRecipients,ccRecipients,bccRecipients,receivedDateTime,sentDateTime,isRead,hasAttachments,flag,parentFolderId&$top=100";
            await foreach (var page in ReadDeltaPagesAsync(account, endpoint, folderModels, DateTimeOffset.UtcNow.AddDays(-90), false, cancellationToken))
            {
                if (page.NextCursor is not null) updated[folder.RemoteId] = page.NextCursor.Value;
                yield return page with { Folders = page.Folders.Count == 0 ? new[] { folder } : page.Folders, NextCursor = null };
            }
        }
        foreach (var pair in cursors)
        {
            var pages = await ReadDeltaPagesSafeAsync(
                account,
                pair.Value,
                folderModels,
                DateTimeOffset.UtcNow.AddDays(-90),
                cancellationToken);
            if (pages is null)
            {
                await foreach (var batch in InitialSyncAsync(account, DateTimeOffset.UtcNow.AddDays(-90), cancellationToken))
                {
                    yield return batch;
                }
                yield break;
            }

            foreach (var page in pages)
            {
                if (page.NextCursor is not null)
                {
                    updated[pair.Key] = page.NextCursor.Value;
                }
                yield return page with { NextCursor = null };
            }
        }

        yield return new SyncBatch
        {
            NextCursor = new SyncCursor(account.Id, "mail", JsonSerializer.Serialize(updated), DateTimeOffset.UtcNow),
            IsComplete = true
        };
    }

    private async Task<IReadOnlyList<SyncBatch>?> ReadDeltaPagesSafeAsync(
        MailAccount account,
        string endpoint,
        IReadOnlyDictionary<string, MailFolder> folders,
        DateTimeOffset bodyCutoff,
        CancellationToken cancellationToken)
    {
        try
        {
            var pages = new List<SyncBatch>();
            await foreach (var page in ReadDeltaPagesAsync(account, endpoint, folders, bodyCutoff, false, cancellationToken))
            {
                pages.Add(page);
            }
            return pages;
        }
        catch (MailProviderException exception) when (!exception.IsTransient && exception.Message.Contains("cursor", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    public async Task ExecuteAsync(MailAccount account, PendingOperation operation, CancellationToken cancellationToken = default)
    {
        var target = Uri.EscapeDataString(operation.TargetRemoteId);
        switch (operation.Kind)
        {
            case PendingOperationKind.MarkRead:
                await SendJsonAsync(account, HttpMethod.Patch, $"{GraphBase}/me/messages/{target}", new { isRead = true }, cancellationToken);
                break;
            case PendingOperationKind.MarkUnread:
                await SendJsonAsync(account, HttpMethod.Patch, $"{GraphBase}/me/messages/{target}", new { isRead = false }, cancellationToken);
                break;
            case PendingOperationKind.Star:
                await SendJsonAsync(account, HttpMethod.Patch, $"{GraphBase}/me/messages/{target}", new { flag = new { flagStatus = "flagged" } }, cancellationToken);
                break;
            case PendingOperationKind.Unstar:
                await SendJsonAsync(account, HttpMethod.Patch, $"{GraphBase}/me/messages/{target}", new { flag = new { flagStatus = "notFlagged" } }, cancellationToken);
                break;
            case PendingOperationKind.Delete:
                await SendJsonAsync(account, HttpMethod.Delete, $"{GraphBase}/me/messages/{target}", null, cancellationToken);
                break;
            case PendingOperationKind.Move:
            case PendingOperationKind.Archive:
                var payload = ProviderUtilities.DeserializePayload<RemoteOperationPayload>(operation);
                var destinationId = operation.Kind == PendingOperationKind.Archive
                    ? payload?.DestinationRemoteId ?? "archive"
                    : payload?.DestinationRemoteId;
                if (string.IsNullOrWhiteSpace(destinationId))
                {
                    throw new MailProviderException("The Microsoft 365 move action is missing its destination folder.");
                }
                await SendJsonAsync(account, HttpMethod.Post, $"{GraphBase}/me/messages/{target}/move", new { destinationId }, cancellationToken);
                break;
            default:
                throw new MailProviderException($"Microsoft 365 does not support the queued action {operation.Kind}.");
        }
    }

    public async Task<SendResult> SendAsync(MailAccount account, OutgoingMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            var graphMessage = new
            {
                subject = message.Subject,
                importance = message.IsImportant ? "high" : "normal",
                body = new { contentType = "HTML", content = message.HtmlBody },
                toRecipients = Recipients(message.To),
                ccRecipients = Recipients(message.Cc),
                bccRecipients = Recipients(message.Bcc),
                internetMessageHeaders = string.IsNullOrWhiteSpace(message.ReplyToRemoteId)
                    ? Array.Empty<object>()
                    : new object[] { new { name = "In-Reply-To", value = message.ReplyToRemoteId }, new { name = "References", value = message.ReplyToRemoteId } }
            };
            if (message.Attachments.Count == 0)
            {
                await SendJsonAsync(account, HttpMethod.Post, $"{GraphBase}/me/sendMail", new { message = graphMessage, saveToSentItems = true }, cancellationToken);
                return new SendResult(true, message.ClientMessageId.ToString("N"));
            }

            using var draft = await SendJsonForJsonAsync(account, HttpMethod.Post, $"{GraphBase}/me/messages", graphMessage, cancellationToken);
            var draftId = draft.RootElement.GetProperty("id").GetString()
                ?? throw new MailProviderException("Microsoft Graph did not return a draft id.");
            foreach (var attachment in message.Attachments)
            {
                await UploadAttachmentAsync(account, draftId, attachment, cancellationToken);
            }
            await SendJsonAsync(account, HttpMethod.Post, $"{GraphBase}/me/messages/{Uri.EscapeDataString(draftId)}/send", null, cancellationToken);
            return new SendResult(true, draftId);
        }
        catch (Exception exception)
        {
            return new SendResult(false, Error: exception.Message);
        }
    }

    private async Task UploadAttachmentAsync(MailAccount account, string draftId, OutgoingAttachment attachment, CancellationToken cancellationToken)
    {
        if (!File.Exists(attachment.LocalPath))
        {
            throw new FileNotFoundException("An attachment could not be found.", attachment.LocalPath);
        }
        var info = new FileInfo(attachment.LocalPath);
        if (Capabilities.MaximumSendBytes is { } maximum && info.Length > maximum)
        {
            throw new MailProviderException($"{attachment.FileName} is larger than Microsoft 365's supported message size.");
        }

        var escapedDraftId = Uri.EscapeDataString(draftId);
        if (info.Length < 3L * 1024 * 1024)
        {
            var bytes = await File.ReadAllBytesAsync(attachment.LocalPath, cancellationToken);
            var payload = new Dictionary<string, object?>
            {
                ["@odata.type"] = "#microsoft.graph.fileAttachment",
                ["name"] = attachment.FileName,
                ["contentType"] = attachment.ContentType,
                ["contentBytes"] = Convert.ToBase64String(bytes),
                ["isInline"] = attachment.IsInline,
                ["contentId"] = attachment.ContentId
            };
            await SendJsonAsync(account, HttpMethod.Post, $"{GraphBase}/me/messages/{escapedDraftId}/attachments", payload, cancellationToken);
            return;
        }

        using var session = await SendJsonForJsonAsync(
            account,
            HttpMethod.Post,
            $"{GraphBase}/me/messages/{escapedDraftId}/attachments/createUploadSession",
            new
            {
                AttachmentItem = new
                {
                    attachmentType = "file",
                    name = attachment.FileName,
                    size = info.Length,
                    contentType = attachment.ContentType,
                    isInline = attachment.IsInline
                }
            },
            cancellationToken);
        var uploadUrl = session.RootElement.GetProperty("uploadUrl").GetString()
            ?? throw new MailProviderException("Microsoft Graph did not create an attachment upload session.");

        const int chunkSize = 3_276_800; // Ten 320 KiB blocks, as required by Graph upload sessions.
        await using var stream = File.OpenRead(attachment.LocalPath);
        var buffer = new byte[chunkSize];
        long offset = 0;
        while (offset < stream.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, stream.Length - offset)), cancellationToken);
            if (count == 0) throw new EndOfStreamException("The attachment changed while it was being uploaded.");
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
            request.Content = new ByteArrayContent(buffer, 0, count);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentLength = count;
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, stream.Length);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new GraphHttpException(response.StatusCode, ReadGraphError(content));
            }
            offset += count;
        }
    }

    public async Task<IReadOnlyList<MailMessage>> SearchAsync(MailAccount account, SearchRequest request, CancellationToken cancellationToken = default)
    {
        var query = Uri.EscapeDataString($"\"{request.Text.Replace("\"", string.Empty)}\"");
        using var document = await GetJsonAsync(account,
            $"{GraphBase}/me/messages?$search={query}&$top={Math.Clamp(request.Limit, 1, 250)}&$select=id,conversationId,internetMessageId,subject,bodyPreview,body,from,toRecipients,ccRecipients,bccRecipients,receivedDateTime,sentDateTime,isRead,hasAttachments,flag,parentFolderId",
            false,
            cancellationToken);
        var folders = await GetFoldersAsync(account, cancellationToken);
        var folderMap = folders.ToDictionary(static folder => folder.RemoteId, StringComparer.Ordinal);
        var result = new List<MailMessage>();
        foreach (var element in document.RootElement.GetProperty("value").EnumerateArray())
        {
            if (element.TryGetProperty("@removed", out _)) continue;
            result.Add(await HydrateAttachmentsAsync(account, ConvertMessage(account, element, folderMap, DateTimeOffset.MinValue), cancellationToken));
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
        var messageId = Uri.EscapeDataString(message.RemoteId);
        var attachmentId = Uri.EscapeDataString(attachment.RemoteId);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{GraphBase}/me/messages/{messageId}/attachments/{attachmentId}/$value");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await authentication.GetAccessTokenAsync(account, false, cancellationToken));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new GraphHttpException(response.StatusCode, ReadGraphError(content));
        }
        await response.Content.CopyToAsync(destination, cancellationToken);
    }

    public async Task<MailMessage> HydrateMessageAsync(MailAccount account, MailMessage message, CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(
            account,
            $"{GraphBase}/me/messages/{Uri.EscapeDataString(message.RemoteId)}?$select=id,conversationId,internetMessageId,subject,bodyPreview,body,from,toRecipients,ccRecipients,bccRecipients,receivedDateTime,sentDateTime,isRead,hasAttachments,flag,parentFolderId",
            false,
            cancellationToken);
        var folders = await GetFoldersAsync(account, cancellationToken);
        return await HydrateAttachmentsAsync(account, ConvertMessage(account, document.RootElement, folders.ToDictionary(static folder => folder.RemoteId, StringComparer.Ordinal), DateTimeOffset.MinValue), cancellationToken);
    }

    private async IAsyncEnumerable<SyncBatch> ReadDeltaPagesAsync(
        MailAccount account,
        string endpoint,
        IReadOnlyDictionary<string, MailFolder> folders,
        DateTimeOffset bodyCutoff,
        bool interactive,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var next = endpoint;
        while (!string.IsNullOrWhiteSpace(next))
        {
            JsonDocument document;
            try
            {
                document = await GetJsonAsync(account, next, interactive, cancellationToken);
            }
            catch (GraphHttpException exception) when (exception.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
            {
                throw new MailProviderException("The Microsoft Graph sync cursor expired.", false, exception);
            }

            using (document)
            {
                var messages = new List<MailMessage>();
                var deleted = new List<string>();
                foreach (var element in document.RootElement.GetProperty("value").EnumerateArray())
                {
                    if (element.TryGetProperty("@removed", out _))
                    {
                        if (element.TryGetProperty("id", out var deletedId) && deletedId.GetString() is { } id)
                        {
                            deleted.Add(id);
                        }
                        continue;
                    }
                    messages.Add(await HydrateAttachmentsAsync(account, ConvertMessage(account, element, folders, bodyCutoff), cancellationToken));
                }

                var delta = document.RootElement.TryGetProperty("@odata.deltaLink", out var deltaElement) ? deltaElement.GetString() : null;
                next = document.RootElement.TryGetProperty("@odata.nextLink", out var nextElement) ? nextElement.GetString() ?? string.Empty : string.Empty;
                yield return new SyncBatch
                {
                    Conversations = BuildConversations(account, messages),
                    Messages = messages,
                    DeletedRemoteMessageIds = deleted,
                    NextCursor = delta is null ? null : new SyncCursor(account.Id, "graph-folder", delta, DateTimeOffset.UtcNow),
                    IsComplete = string.IsNullOrWhiteSpace(next)
                };
            }
        }
    }

    private async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(MailAccount account, CancellationToken cancellationToken)
    {
        var folders = new List<MailFolder>();
        var foldersWithChildren = new Queue<string>();
        var specialFolders = new Dictionary<string, SpecialFolderKind>(StringComparer.Ordinal);
        foreach (var pair in new[]
        {
            ("inbox", SpecialFolderKind.Inbox),
            ("drafts", SpecialFolderKind.Drafts),
            ("sentitems", SpecialFolderKind.Sent),
            ("archive", SpecialFolderKind.Archive),
            ("junkemail", SpecialFolderKind.Spam),
            ("deleteditems", SpecialFolderKind.Trash)
        })
        {
            try
            {
                using var special = await GetJsonAsync(account, $"{GraphBase}/me/mailFolders/{pair.Item1}?$select=id", false, cancellationToken);
                if (special.RootElement.TryGetProperty("id", out var id) && id.GetString() is { } remoteId) specialFolders[remoteId] = pair.Item2;
            }
            catch (GraphHttpException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
            }
        }
        var next = $"{GraphBase}/me/mailFolders?$top=100&includeHiddenFolders=true";
        while (!string.IsNullOrWhiteSpace(next))
        {
            using var document = await GetJsonAsync(account, next, false, cancellationToken);
            AddGraphFolders(account, document.RootElement.GetProperty("value"), folders, foldersWithChildren, specialFolders);
            next = document.RootElement.TryGetProperty("@odata.nextLink", out var nextElement) ? nextElement.GetString() ?? string.Empty : string.Empty;
        }

        var expanded = new HashSet<string>(StringComparer.Ordinal);
        while (foldersWithChildren.TryDequeue(out var parentId))
        {
            if (!expanded.Add(parentId)) continue;
            next = $"{GraphBase}/me/mailFolders/{Uri.EscapeDataString(parentId)}/childFolders?$top=100&includeHiddenFolders=true";
            while (!string.IsNullOrWhiteSpace(next))
            {
                using var document = await GetJsonAsync(account, next, false, cancellationToken);
                AddGraphFolders(account, document.RootElement.GetProperty("value"), folders, foldersWithChildren, specialFolders);
                next = document.RootElement.TryGetProperty("@odata.nextLink", out var nextElement) ? nextElement.GetString() ?? string.Empty : string.Empty;
            }
        }
        return folders;
    }

    private static void AddGraphFolders(MailAccount account, JsonElement values, List<MailFolder> folders, Queue<string> foldersWithChildren, IReadOnlyDictionary<string, SpecialFolderKind> specialFolders)
    {
        foreach (var element in values.EnumerateArray())
        {
            var remoteId = element.GetProperty("id").GetString()!;
            if (folders.Any(folder => folder.RemoteId == remoteId)) continue;
            folders.Add(new MailFolder
            {
                Id = ProviderUtilities.StableGuid(account.Id, $"folder:{remoteId}"),
                AccountId = account.Id,
                RemoteId = remoteId,
                Name = element.GetProperty("displayName").GetString() ?? "Folder",
                SpecialKind = specialFolders.TryGetValue(remoteId, out var specialKind) ? specialKind : SpecialFolderKind.None,
                UnreadCount = element.TryGetProperty("unreadItemCount", out var unread) ? unread.GetInt32() : 0,
                TotalCount = element.TryGetProperty("totalItemCount", out var total) ? total.GetInt32() : 0,
                ParentRemoteId = element.TryGetProperty("parentFolderId", out var parent) ? parent.GetString() : null
            });
            if (element.TryGetProperty("childFolderCount", out var childCount) && childCount.GetInt32() > 0) foldersWithChildren.Enqueue(remoteId);
        }
    }

    private MailMessage ConvertMessage(MailAccount account, JsonElement element, IReadOnlyDictionary<string, MailFolder> folders, DateTimeOffset bodyCutoff)
    {
        var remoteId = element.GetProperty("id").GetString()!;
        var providerThreadId = element.TryGetProperty("conversationId", out var conversationElement) ? conversationElement.GetString() : null;
        var folderRemoteId = element.TryGetProperty("parentFolderId", out var folderElement) ? folderElement.GetString() ?? string.Empty : string.Empty;
        var folder = folders.TryGetValue(folderRemoteId, out var knownFolder)
            ? knownFolder
            : new MailFolder { Id = ProviderUtilities.StableGuid(account.Id, $"folder:{folderRemoteId}"), AccountId = account.Id, RemoteId = folderRemoteId, Name = "Folder" };
        var receivedAt = ParseGraphDate(element, "receivedDateTime");
        var internetMessageId = element.TryGetProperty("internetMessageId", out var messageIdElement) ? messageIdElement.GetString() : null;
        var subject = element.TryGetProperty("subject", out var subjectElement) ? subjectElement.GetString() ?? "(no subject)" : "(no subject)";
        var from = element.TryGetProperty("from", out var fromElement) ? ParseRecipient(fromElement) : new MailAddress(string.Empty, string.Empty);
        var to = ParseRecipients(element, "toRecipients");
        var threadKey = ConversationThreader.CreateThreadKey(account.Id, providerThreadId, internetMessageId, null, null, subject, new[] { from }.Concat(to));
        var conversationId = ProviderUtilities.StableGuid(account.Id, threadKey);
        var body = receivedAt >= bodyCutoff && element.TryGetProperty("body", out var bodyElement) ? bodyElement : default;
        var bodyContent = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("content", out var content) ? content.GetString() : null;
        var bodyType = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("contentType", out var contentType) ? contentType.GetString() : null;
        var isRead = element.TryGetProperty("isRead", out var isReadElement) && isReadElement.GetBoolean();
        var hasAttachments = element.TryGetProperty("hasAttachments", out var attachmentsElement) && attachmentsElement.GetBoolean();
        var isFlagged = element.TryGetProperty("flag", out var flagElement) && flagElement.TryGetProperty("flagStatus", out var status) && status.GetString() == "flagged";

        return new MailMessage
        {
            Id = ProviderUtilities.StableGuid(account.Id, $"graph:{remoteId}"),
            AccountId = account.Id,
            FolderId = folder.Id,
            ConversationId = conversationId,
            RemoteId = remoteId,
            ProviderThreadId = providerThreadId,
            InternetMessageId = internetMessageId,
            From = from,
            To = to,
            Cc = ParseRecipients(element, "ccRecipients"),
            Bcc = ParseRecipients(element, "bccRecipients"),
            Subject = subject,
            Snippet = element.TryGetProperty("bodyPreview", out var preview) ? preview.GetString() ?? string.Empty : string.Empty,
            TextBody = string.Equals(bodyType, "text", StringComparison.OrdinalIgnoreCase) ? bodyContent : ProviderUtilities.StripHtml(bodyContent),
            HtmlBody = string.Equals(bodyType, "html", StringComparison.OrdinalIgnoreCase) && bodyContent is not null ? htmlSanitizer.Sanitize(bodyContent) : null,
            SentAt = ParseGraphDate(element, "sentDateTime"),
            ReceivedAt = receivedAt,
            Flags = (isRead ? MailFlags.Read : MailFlags.None) | (isFlagged ? MailFlags.Starred : MailFlags.None) | (hasAttachments ? MailFlags.HasAttachments : MailFlags.None)
        };
    }

    private async Task<MailMessage> HydrateAttachmentsAsync(MailAccount account, MailMessage message, CancellationToken cancellationToken)
    {
        if (!message.Flags.HasFlag(MailFlags.HasAttachments))
        {
            return message;
        }

        using var document = await GetJsonAsync(
            account,
            $"{GraphBase}/me/messages/{Uri.EscapeDataString(message.RemoteId)}/attachments?$select=id,name,contentType,size,isInline,contentId",
            false,
            cancellationToken);
        var attachments = document.RootElement.GetProperty("value").EnumerateArray().Select(element =>
        {
            var remoteId = element.GetProperty("id").GetString()!;
            return new MailAttachment
            {
                Id = ProviderUtilities.StableGuid(account.Id, $"graph-attachment:{message.Id:N}:{remoteId}"),
                MessageId = message.Id,
                RemoteId = remoteId,
                FileName = element.TryGetProperty("name", out var name) ? name.GetString() ?? "attachment" : "attachment",
                ContentType = element.TryGetProperty("contentType", out var contentType) ? contentType.GetString() ?? "application/octet-stream" : "application/octet-stream",
                Size = element.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                IsInline = element.TryGetProperty("isInline", out var inline) && inline.GetBoolean(),
                ContentId = element.TryGetProperty("contentId", out var contentId) ? contentId.GetString() : null
            };
        }).ToArray();
        return message with { Attachments = attachments };
    }

    private static IReadOnlyList<MailConversation> BuildConversations(MailAccount account, IReadOnlyList<MailMessage> messages) =>
        messages.GroupBy(static message => message.ConversationId).Select(group =>
        {
            var ordered = group.OrderBy(static message => message.ReceivedAt).ToArray();
            var latest = ordered[^1];
            return new MailConversation
            {
                Id = group.Key,
                AccountId = account.Id,
                ThreadKey = ConversationThreader.CreateThreadKey(account.Id, latest.ProviderThreadId, latest.InternetMessageId, null, null, latest.Subject, ordered.Select(static x => x.From)),
                ProviderThreadId = latest.ProviderThreadId,
                Subject = latest.Subject,
                Snippet = latest.Snippet,
                Participants = ordered.Select(static message => message.From).DistinctBy(static address => address.Address, StringComparer.OrdinalIgnoreCase).ToArray(),
                LastMessageAt = latest.ReceivedAt,
                MessageCount = ordered.Length,
                UnreadCount = ordered.Count(static message => !message.Flags.HasFlag(MailFlags.Read)),
                IsStarred = ordered.Any(static message => message.Flags.HasFlag(MailFlags.Starred)),
                HasAttachments = ordered.Any(static message => message.Flags.HasFlag(MailFlags.HasAttachments))
            };
        }).ToArray();

    private async Task<JsonDocument> GetJsonAsync(MailAccount account, string url, bool interactive, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await authentication.GetAccessTokenAsync(account, interactive, cancellationToken));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new GraphHttpException(response.StatusCode, ReadGraphError(content));
        }
        return JsonDocument.Parse(content);
    }

    private async Task SendJsonAsync(MailAccount account, HttpMethod method, string url, object? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await authentication.GetAccessTokenAsync(account, false, cancellationToken));
        if (payload is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new GraphHttpException(response.StatusCode, ReadGraphError(content));
        }
    }

    private async Task<JsonDocument> SendJsonForJsonAsync(MailAccount account, HttpMethod method, string url, object? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await authentication.GetAccessTokenAsync(account, false, cancellationToken));
        if (payload is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new GraphHttpException(response.StatusCode, ReadGraphError(content));
        }
        return JsonDocument.Parse(content);
    }

    private static object[] Recipients(IEnumerable<MailAddress> addresses) => addresses.Select(static address => new { emailAddress = new { name = address.Name, address = address.Address } }).Cast<object>().ToArray();

    private static MailAddress ParseRecipient(JsonElement recipient)
    {
        if (!recipient.TryGetProperty("emailAddress", out var address))
        {
            return new MailAddress(string.Empty, string.Empty);
        }
        return new MailAddress(
            address.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
            address.TryGetProperty("address", out var email) ? email.GetString() ?? string.Empty : string.Empty);
    }

    private static MailAddress[] ParseRecipients(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var recipients) && recipients.ValueKind == JsonValueKind.Array
            ? recipients.EnumerateArray().Select(ParseRecipient).ToArray()
            : Array.Empty<MailAddress>();

    private static DateTimeOffset ParseGraphDate(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed : DateTimeOffset.MinValue;

    private static string ReadGraphError(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "Microsoft Graph request failed.";
        }
        catch
        {
            return "Microsoft Graph request failed.";
        }
    }

    private sealed class GraphHttpException : MailProviderException
    {
        public GraphHttpException(HttpStatusCode statusCode, string message)
            : base(message, statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            StatusCode = statusCode;
        }

        public HttpStatusCode StatusCode { get; }
    }
}
