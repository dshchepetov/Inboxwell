using System.Runtime.CompilerServices;
using System.Text.Json;
using Gomail.Core;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MailFolder = Gomail.Core.MailFolder;

namespace Gomail.Providers;

public sealed class ImapMailProvider : IMailProvider
{
    private const MessageSummaryItems SummaryItems =
        MessageSummaryItems.UniqueId |
        MessageSummaryItems.Envelope |
        MessageSummaryItems.Flags |
        MessageSummaryItems.InternalDate |
        MessageSummaryItems.BodyStructure |
        MessageSummaryItems.References;

    private readonly ISecretStore secrets;
    private readonly IHtmlSanitizer htmlSanitizer;

    public ImapMailProvider(ISecretStore secrets, IHtmlSanitizer htmlSanitizer)
    {
        this.secrets = secrets;
        this.htmlSanitizer = htmlSanitizer;
    }

    public ProviderKind Kind => ProviderKind.Imap;

    public ProviderCapabilities Capabilities { get; } = new(true, false, false, true, true, true, true);

    public async Task<ConnectionTestResult> TestConnectionAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = await ConnectImapAsync(account, cancellationToken);
            var displayName = client.Capabilities.HasFlag(ImapCapabilities.Id) ? client.AuthenticationMechanisms.FirstOrDefault() : null;
            await client.DisconnectAsync(true, cancellationToken);
            return new ConnectionTestResult(true, displayName ?? account.Email);
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
        using var client = await ConnectImapAsync(account, cancellationToken);
        var folders = await GetFoldersAsync(client, account, cancellationToken);
        var messageLimit = Math.Clamp(account.GetIntSetting("syncMessageLimit", 5000), 100, 50_000);
        var uidCursors = new Dictionary<string, uint>(StringComparer.Ordinal);

        foreach (var folderModel in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = folderModel.SpecialKind == SpecialFolderKind.Inbox
                ? client.Inbox
                : await client.GetFolderAsync(folderModel.RemoteId, cancellationToken);
            if (!folder.Exists || folder.Attributes.HasFlag(FolderAttributes.NoSelect))
            {
                continue;
            }

            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            if (folder.Count == 0)
            {
                await folder.CloseAsync(false, cancellationToken);
                yield return new SyncBatch { Folders = new[] { folderModel }, IsComplete = false };
                continue;
            }

            var start = Math.Max(0, folder.Count - messageLimit);
            var summaries = await folder.FetchAsync(start, folder.Count - 1, SummaryItems, cancellationToken);
            uidCursors[folderModel.RemoteId] = summaries.Count == 0 ? 0 : summaries.Max(static summary => summary.UniqueId.Id);
            const int chunkSize = 100;
            for (var offset = 0; offset < summaries.Count; offset += chunkSize)
            {
                var chunk = summaries.Skip(offset).Take(chunkSize).ToArray();
                var messages = new List<MailMessage>(chunk.Length);
                foreach (var summary in chunk)
                {
                    messages.Add(await ConvertSummaryAsync(account, folderModel, folder, summary, bodyCutoff, cancellationToken));
                }

                yield return new SyncBatch
                {
                    Folders = offset == 0 ? new[] { folderModel with { TotalCount = folder.Count, UnreadCount = folder.Unread } } : Array.Empty<MailFolder>(),
                    Conversations = BuildConversations(account, messages),
                    Messages = messages,
                    IsComplete = false
                };
            }

            await folder.CloseAsync(false, cancellationToken);
        }

        yield return new SyncBatch
        {
            NextCursor = new SyncCursor(account.Id, "mail", JsonSerializer.Serialize(uidCursors), DateTimeOffset.UtcNow),
            IsComplete = true
        };
        await client.DisconnectAsync(true, cancellationToken);
    }

    public async IAsyncEnumerable<SyncBatch> IncrementalSyncAsync(
        MailAccount account,
        SyncCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Dictionary<string, uint>? uidCursors;
        try
        {
            uidCursors = JsonSerializer.Deserialize<Dictionary<string, uint>>(cursor.Value);
        }
        catch (JsonException)
        {
            uidCursors = null;
        }
        if (uidCursors is null)
        {
            await foreach (var batch in InitialSyncAsync(account, DateTimeOffset.UtcNow.AddDays(-account.GetIntSetting("cacheDays", 90)), cancellationToken)) yield return batch;
            yield break;
        }

        using var client = await ConnectImapAsync(account, cancellationToken);
        var folders = await GetFoldersAsync(client, account, cancellationToken);
        var updated = new Dictionary<string, uint>(uidCursors, StringComparer.Ordinal);
        foreach (var folderModel in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = folderModel.SpecialKind == SpecialFolderKind.Inbox ? client.Inbox : await client.GetFolderAsync(folderModel.RemoteId, cancellationToken);
            if (!folder.Exists || folder.Attributes.HasFlag(FolderAttributes.NoSelect)) continue;
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            var previousUid = uidCursors.TryGetValue(folderModel.RemoteId, out var value) ? value : 0;
            var start = Math.Max(0, folder.Count - 250);
            var summaries = folder.Count == 0
                ? Array.Empty<IMessageSummary>()
                : (await folder.FetchAsync(start, folder.Count - 1, SummaryItems, cancellationToken)).ToArray();
            var messages = new List<MailMessage>(summaries.Length);
            foreach (var summary in summaries)
            {
                var hydrateBody = summary.UniqueId.Id > previousUid;
                messages.Add(await ConvertSummaryAsync(
                    account,
                    folderModel,
                    folder,
                    summary,
                    hydrateBody ? DateTimeOffset.MinValue : DateTimeOffset.MaxValue,
                    cancellationToken));
            }
            updated[folderModel.RemoteId] = summaries.Length == 0 ? previousUid : Math.Max(previousUid, summaries.Max(static summary => summary.UniqueId.Id));
            yield return new SyncBatch
            {
                Folders = new[] { folderModel with { TotalCount = folder.Count, UnreadCount = folder.Unread } },
                Conversations = BuildConversations(account, messages),
                Messages = messages,
                IsComplete = false
            };
            await folder.CloseAsync(false, cancellationToken);
        }
        yield return new SyncBatch
        {
            NextCursor = new SyncCursor(account.Id, "mail", JsonSerializer.Serialize(updated), DateTimeOffset.UtcNow),
            IsComplete = true
        };
        await client.DisconnectAsync(true, cancellationToken);
    }

    public async Task ExecuteAsync(MailAccount account, PendingOperation operation, CancellationToken cancellationToken = default)
    {
        var payload = ProviderUtilities.DeserializePayload<RemoteOperationPayload>(operation) ?? new RemoteOperationPayload();
        if (string.IsNullOrWhiteSpace(payload.FolderRemoteId))
        {
            throw new MailProviderException("The IMAP action is missing its source folder.");
        }

        var uidValue = payload.Uid ?? (uint.TryParse(operation.TargetRemoteId, out var parsed) ? parsed : 0);
        if (uidValue == 0)
        {
            throw new MailProviderException("The IMAP action has an invalid message UID.");
        }

        using var client = await ConnectImapAsync(account, cancellationToken);
        var folder = await client.GetFolderAsync(payload.FolderRemoteId, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
        var uid = new UniqueId(uidValue);

        switch (operation.Kind)
        {
            case PendingOperationKind.MarkRead:
                await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
                break;
            case PendingOperationKind.MarkUnread:
                await folder.RemoveFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
                break;
            case PendingOperationKind.Star:
                await folder.AddFlagsAsync(uid, MessageFlags.Flagged, true, cancellationToken);
                break;
            case PendingOperationKind.Unstar:
                await folder.RemoveFlagsAsync(uid, MessageFlags.Flagged, true, cancellationToken);
                break;
            case PendingOperationKind.Delete:
                await folder.AddFlagsAsync(uid, MessageFlags.Deleted, true, cancellationToken);
                await folder.ExpungeAsync(new[] { uid }, cancellationToken);
                break;
            case PendingOperationKind.Move:
            case PendingOperationKind.Archive:
                if (string.IsNullOrWhiteSpace(payload.DestinationRemoteId))
                {
                    throw new MailProviderException("The IMAP move action is missing its destination folder.");
                }
                var destination = await client.GetFolderAsync(payload.DestinationRemoteId, cancellationToken);
                await folder.MoveToAsync(uid, destination, cancellationToken);
                break;
            case PendingOperationKind.Send:
                throw new MailProviderException("Queued sends are dispatched through SendAsync.");
            default:
                throw new MailProviderException($"IMAP does not support the queued action {operation.Kind}.");
        }

        await folder.CloseAsync(true, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    public async Task<SendResult> SendAsync(MailAccount account, OutgoingMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            var mime = await BuildMimeMessageAsync(account, message, cancellationToken);
            using var client = new SmtpClient();
            await client.ConnectAsync(
                account.GetSetting("smtpHost"),
                account.GetIntSetting("smtpPort", 587),
                SocketOptions(account.GetSetting("smtpSecurity", "starttls")),
                cancellationToken);
            await AuthenticateAsync(client, account, cancellationToken);
            var response = await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return new SendResult(true, mime.MessageId ?? response);
        }
        catch (Exception exception)
        {
            return new SendResult(false, Error: FriendlyError(exception));
        }
    }

    public async Task<IReadOnlyList<MailMessage>> SearchAsync(MailAccount account, SearchRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return Array.Empty<MailMessage>();
        }

        using var client = await ConnectImapAsync(account, cancellationToken);
        var folder = client.Inbox;
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        var query = SearchQuery.SubjectContains(request.Text)
            .Or(SearchQuery.BodyContains(request.Text))
            .Or(SearchQuery.FromContains(request.Text));
        var uids = await folder.SearchAsync(query, cancellationToken);
        var selected = uids.TakeLast(Math.Clamp(request.Limit, 1, 500)).ToArray();
        var result = new List<MailMessage>(selected.Length);
        if (selected.Length > 0)
        {
            var summaries = await folder.FetchAsync(selected, SummaryItems, cancellationToken);
            var folderModel = CreateFolder(account, folder);
            foreach (var summary in summaries)
            {
                result.Add(await ConvertSummaryAsync(account, folderModel, folder, summary, DateTimeOffset.MaxValue, cancellationToken));
            }
        }
        await folder.CloseAsync(false, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
        return result;
    }

    public async Task DownloadAttachmentAsync(
        MailAccount account,
        MailMessage message,
        MailAttachment attachment,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        if (!uint.TryParse(message.RemoteId, out var uidValue))
        {
            throw new MailProviderException("The IMAP message UID is invalid.");
        }

        using var client = await ConnectImapAsync(account, cancellationToken);
        var folders = await GetFoldersAsync(client, account, cancellationToken);
        var folderModel = folders.FirstOrDefault(folder => folder.Id == message.FolderId)
            ?? throw new MailProviderException("The folder containing this attachment is no longer available.");
        var folder = folderModel.SpecialKind == SpecialFolderKind.Inbox
            ? client.Inbox
            : await client.GetFolderAsync(folderModel.RemoteId, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        var mime = await folder.GetMessageAsync(new UniqueId(uidValue), cancellationToken);
        var entity = FindAttachment(mime, attachment)
            ?? throw new MailProviderException("The attachment is no longer present in this message.");

        if (entity is MimePart part)
        {
            if (part.Content is null) throw new MailProviderException("The attachment has no downloadable content.");
            await part.Content.DecodeToAsync(destination, cancellationToken);
        }
        else if (entity is MessagePart messagePart)
        {
            if (messagePart.Message is null) throw new MailProviderException("The attached message has no downloadable content.");
            await messagePart.Message.WriteToAsync(destination, cancellationToken);
        }
        else
        {
            throw new MailProviderException("This attachment type is not supported.");
        }

        await folder.CloseAsync(false, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    public async Task<MailMessage> HydrateMessageAsync(MailAccount account, MailMessage message, CancellationToken cancellationToken = default)
    {
        if (!uint.TryParse(message.RemoteId, out var uidValue)) throw new MailProviderException("The IMAP message UID is invalid.");
        using var client = await ConnectImapAsync(account, cancellationToken);
        var folders = await GetFoldersAsync(client, account, cancellationToken);
        var folderModel = folders.FirstOrDefault(folder => folder.Id == message.FolderId)
            ?? throw new MailProviderException("The folder containing this message is no longer available.");
        var folder = folderModel.SpecialKind == SpecialFolderKind.Inbox ? client.Inbox : await client.GetFolderAsync(folderModel.RemoteId, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        var summaries = await folder.FetchAsync(new[] { new UniqueId(uidValue) }, SummaryItems, cancellationToken);
        var summary = summaries.FirstOrDefault() ?? throw new MailProviderException("The message is no longer present on the server.");
        var hydrated = await ConvertSummaryAsync(account, folderModel, folder, summary, DateTimeOffset.MinValue, cancellationToken);
        await folder.CloseAsync(false, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
        return hydrated;
    }

    private async Task<ImapClient> ConnectImapAsync(MailAccount account, CancellationToken cancellationToken)
    {
        var client = new ImapClient();
        try
        {
            await client.ConnectAsync(
                account.GetSetting("imapHost"),
                account.GetIntSetting("imapPort", 993),
                SocketOptions(account.GetSetting("imapSecurity", "ssl")),
                cancellationToken);
            await AuthenticateAsync(client, account, cancellationToken);
            return client;
        }
        catch (Exception exception)
        {
            client.Dispose();
            throw new MailProviderException(FriendlyError(exception), exception is IOException or ServiceNotConnectedException, exception);
        }
    }

    private async Task AuthenticateAsync(MailService client, MailAccount account, CancellationToken cancellationToken)
    {
        var password = await secrets.GetAsync(account.SecretKey("password"), cancellationToken);
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new MailProviderException("No password or app password is stored for this account.");
        }
        await client.AuthenticateAsync(account.GetSetting("username", account.Email), password, cancellationToken);
    }

    private static SecureSocketOptions SocketOptions(string value) => value.Trim().ToLowerInvariant() switch
    {
        "ssl" or "tls" or "sslontconnect" => SecureSocketOptions.SslOnConnect,
        "starttls" => SecureSocketOptions.StartTls,
        "starttlswhenavailable" => SecureSocketOptions.StartTlsWhenAvailable,
        _ => throw new MailProviderException("An encrypted SSL/TLS connection is required.")
    };

    private static async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(ImapClient client, MailAccount account, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, MailFolder>(StringComparer.Ordinal);
        foreach (var folder in await client.GetFoldersAsync(client.PersonalNamespaces[0], false, cancellationToken))
        {
            result[folder.FullName] = CreateFolder(account, folder);
        }
        result[client.Inbox.FullName] = CreateFolder(account, client.Inbox) with { SpecialKind = SpecialFolderKind.Inbox };
        return result.Values.OrderBy(static folder => folder.SpecialKind).ThenBy(static folder => folder.Name).ToArray();
    }

    private static MailFolder CreateFolder(MailAccount account, IMailFolder folder) => new()
    {
        Id = ProviderUtilities.StableGuid(account.Id, $"folder:{folder.FullName}"),
        AccountId = account.Id,
        RemoteId = folder.FullName,
        Name = folder.Name,
        ParentRemoteId = folder.ParentFolder?.FullName,
        SpecialKind = GetSpecialKind(folder.Attributes),
        TotalCount = folder.Count,
        UnreadCount = folder.Unread
    };

    private async Task<MailMessage> ConvertSummaryAsync(
        MailAccount account,
        MailFolder folderModel,
        IMailFolder folder,
        IMessageSummary summary,
        DateTimeOffset bodyCutoff,
        CancellationToken cancellationToken)
    {
        var envelope = summary.Envelope;
        var receivedAt = summary.InternalDate ?? envelope?.Date ?? DateTimeOffset.MinValue;
        MimeMessage? mime = null;
        if (receivedAt >= bodyCutoff)
        {
            try
            {
                mime = await folder.GetMessageAsync(summary.UniqueId, cancellationToken);
            }
            catch (MessageNotFoundException)
            {
                // The message was removed after FETCH; metadata remains useful until reconciliation.
            }
        }

        var from = ConvertAddress(envelope?.From?.Mailboxes.FirstOrDefault());
        var to = ConvertAddresses(envelope?.To);
        var references = summary.References?.Select(static value => value).ToArray() ?? Array.Empty<string>();
        var threadKey = ConversationThreader.CreateThreadKey(
            account.Id,
            null,
            envelope?.MessageId,
            envelope?.InReplyTo,
            references,
            envelope?.Subject,
            new[] { from }.Concat(to));
        var conversationId = ProviderUtilities.StableGuid(account.Id, threadKey);
        var messageId = ProviderUtilities.StableGuid(account.Id, $"imap:{folderModel.RemoteId}:{summary.UniqueId.Id}");
        var attachments = mime is null ? ConvertAttachments(account, messageId, summary) : ConvertAttachments(account, messageId, mime);
        var textBody = mime?.TextBody;
        var htmlBody = mime?.HtmlBody;
        var snippetSource = textBody ?? ProviderUtilities.StripHtml(htmlBody);

        return new MailMessage
        {
            Id = messageId,
            AccountId = account.Id,
            FolderId = folderModel.Id,
            ConversationId = conversationId,
            RemoteId = summary.UniqueId.Id.ToString(),
            InternetMessageId = envelope?.MessageId,
            InReplyTo = envelope?.InReplyTo,
            References = references,
            From = from,
            To = to,
            Cc = ConvertAddresses(envelope?.Cc),
            Bcc = ConvertAddresses(envelope?.Bcc),
            Subject = envelope?.Subject ?? "(no subject)",
            Snippet = snippetSource.Length > 220 ? snippetSource[..220] : snippetSource,
            TextBody = textBody,
            HtmlBody = string.IsNullOrWhiteSpace(htmlBody) ? null : htmlSanitizer.Sanitize(htmlBody),
            SentAt = envelope?.Date ?? receivedAt,
            ReceivedAt = receivedAt,
            Flags = ConvertFlags(summary.Flags) | (attachments.Length > 0 ? MailFlags.HasAttachments : MailFlags.None),
            Attachments = attachments
        };
    }

    private static IReadOnlyList<MailConversation> BuildConversations(MailAccount account, IReadOnlyList<MailMessage> messages) =>
        messages.GroupBy(static message => message.ConversationId).Select(group =>
        {
            var ordered = group.OrderBy(static message => message.ReceivedAt).ToArray();
            var latest = ordered[^1];
            var threadKey = ConversationThreader.CreateThreadKey(account.Id, latest.ProviderThreadId, latest.InternetMessageId, latest.InReplyTo, latest.References, latest.Subject, ordered.Select(static x => x.From));
            return new MailConversation
            {
                Id = group.Key,
                AccountId = account.Id,
                ThreadKey = threadKey,
                Subject = latest.Subject,
                Snippet = latest.Snippet,
                Participants = ordered.Select(static message => message.From).DistinctBy(static address => address.Address, StringComparer.OrdinalIgnoreCase).ToArray(),
                LastMessageAt = latest.ReceivedAt,
                MessageCount = ordered.Length,
                UnreadCount = ordered.Count(static message => !message.Flags.HasFlag(MailFlags.Read)),
                IsStarred = ordered.Any(static message => message.Flags.HasFlag(MailFlags.Starred)),
                HasAttachments = ordered.Any(static message => message.Attachments.Count > 0),
                Labels = Array.Empty<string>()
            };
        }).ToArray();

    private static MailAttachment[] ConvertAttachments(MailAccount account, Guid messageId, MimeMessage message) =>
        message.Attachments.Select((entity, index) => new MailAttachment
        {
            Id = ProviderUtilities.StableGuid(account.Id, $"attachment:{messageId:N}:{index}"),
            MessageId = messageId,
            RemoteId = entity.ContentId ?? index.ToString(),
            FileName = entity.ContentDisposition?.FileName ?? entity.ContentType.Name ?? $"attachment-{index + 1}",
            ContentType = entity.ContentType.MimeType,
            Size = 0,
            IsInline = entity.ContentDisposition?.Disposition?.Equals("inline", StringComparison.OrdinalIgnoreCase) == true,
            ContentId = entity.ContentId
        }).ToArray();

    private static MailAttachment[] ConvertAttachments(MailAccount account, Guid messageId, IMessageSummary summary) =>
        summary.BodyParts.OfType<BodyPartBasic>()
            .Where(static part => part.IsAttachment || !string.IsNullOrWhiteSpace(part.FileName))
            .Select((part, index) => new MailAttachment
            {
                Id = ProviderUtilities.StableGuid(account.Id, $"attachment:{messageId:N}:{part.PartSpecifier ?? index.ToString()}"),
                MessageId = messageId,
                RemoteId = part.PartSpecifier ?? index.ToString(),
                FileName = part.FileName ?? $"attachment-{index + 1}",
                ContentType = part.ContentType.MimeType,
                Size = part.Octets,
                IsInline = !part.IsAttachment,
                ContentId = part.ContentId
            })
            .ToArray();

    private static MimeEntity? FindAttachment(MimeMessage message, MailAttachment attachment)
    {
        var attachments = message.Attachments.ToArray();
        if (int.TryParse(attachment.RemoteId, out var index) && index >= 0 && index < attachments.Length)
        {
            return attachments[index];
        }

        return attachments.FirstOrDefault(entity =>
            (!string.IsNullOrWhiteSpace(attachment.ContentId) && string.Equals(entity.ContentId, attachment.ContentId, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(entity.ContentDisposition?.FileName ?? entity.ContentType.Name, attachment.FileName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<MimeMessage> BuildMimeMessageAsync(MailAccount account, OutgoingMessage outgoing, CancellationToken cancellationToken)
    {
        var message = new MimeMessage
        {
            MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(),
            Subject = outgoing.Subject
        };
        message.From.Add(new MailboxAddress(account.DisplayName, account.Email));
        AddAddresses(message.To, outgoing.To);
        AddAddresses(message.Cc, outgoing.Cc);
        AddAddresses(message.Bcc, outgoing.Bcc);
        if (!string.IsNullOrWhiteSpace(outgoing.ReplyToRemoteId))
        {
            message.InReplyTo = outgoing.ReplyToRemoteId;
            message.References.Add(outgoing.ReplyToRemoteId);
        }

        var builder = new BodyBuilder { HtmlBody = outgoing.HtmlBody, TextBody = outgoing.PlainTextBody };
        foreach (var attachment in outgoing.Attachments)
        {
            if (!File.Exists(attachment.LocalPath))
            {
                throw new FileNotFoundException("An attachment could not be found.", attachment.LocalPath);
            }
            await using var stream = File.OpenRead(attachment.LocalPath);
            var bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            var entity = builder.Attachments.Add(attachment.FileName, bytes, ContentType.Parse(attachment.ContentType));
            if (attachment.IsInline)
            {
                entity.ContentDisposition = new ContentDisposition(ContentDisposition.Inline);
                entity.ContentId = attachment.ContentId ?? MimeKit.Utils.MimeUtils.GenerateMessageId();
            }
        }
        message.Body = builder.ToMessageBody();
        return message;
    }

    private static void AddAddresses(InternetAddressList target, IEnumerable<MailAddress> addresses)
    {
        foreach (var address in addresses)
        {
            target.Add(new MailboxAddress(address.Name, address.Address));
        }
    }

    private static MailAddress ConvertAddress(MailboxAddress? address) => address is null
        ? new MailAddress(string.Empty, string.Empty)
        : new MailAddress(address.Name ?? string.Empty, address.Address);

    private static MailAddress[] ConvertAddresses(InternetAddressList? addresses) =>
        addresses?.Mailboxes.Select(ConvertAddress).ToArray() ?? Array.Empty<MailAddress>();

    private static MailFlags ConvertFlags(MessageFlags? flags)
    {
        var source = flags ?? MessageFlags.None;
        var result = MailFlags.None;
        if (source.HasFlag(MessageFlags.Seen)) result |= MailFlags.Read;
        if (source.HasFlag(MessageFlags.Flagged)) result |= MailFlags.Starred;
        if (source.HasFlag(MessageFlags.Answered)) result |= MailFlags.Answered;
        if (source.HasFlag(MessageFlags.Draft)) result |= MailFlags.Draft;
        if (source.HasFlag(MessageFlags.Deleted)) result |= MailFlags.Deleted;
        return result;
    }

    private static SpecialFolderKind GetSpecialKind(FolderAttributes attributes)
    {
        if (attributes.HasFlag(FolderAttributes.Inbox)) return SpecialFolderKind.Inbox;
        if (attributes.HasFlag(FolderAttributes.Drafts)) return SpecialFolderKind.Drafts;
        if (attributes.HasFlag(FolderAttributes.Sent)) return SpecialFolderKind.Sent;
        if (attributes.HasFlag(FolderAttributes.Archive)) return SpecialFolderKind.Archive;
        if (attributes.HasFlag(FolderAttributes.Junk)) return SpecialFolderKind.Spam;
        if (attributes.HasFlag(FolderAttributes.Trash)) return SpecialFolderKind.Trash;
        if (attributes.HasFlag(FolderAttributes.Flagged)) return SpecialFolderKind.Starred;
        if (attributes.HasFlag(FolderAttributes.All)) return SpecialFolderKind.AllMail;
        return SpecialFolderKind.None;
    }

    private static string FriendlyError(Exception exception) => exception switch
    {
        AuthenticationException => "The server rejected the username or app password.",
        SslHandshakeException => "The server's TLS certificate could not be verified.",
        ServiceNotConnectedException => "The mail server connection was interrupted.",
        _ => exception.Message
    };
}
