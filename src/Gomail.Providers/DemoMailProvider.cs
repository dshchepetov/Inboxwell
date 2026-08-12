using System.Runtime.CompilerServices;
using System.Text;
using Gomail.Core;

namespace Gomail.Providers;

public sealed class DemoMailProvider : IMailProvider
{
    public ProviderKind Kind => ProviderKind.Demo;

    public ProviderCapabilities Capabilities { get; } = new(true, true, true, true, true, true, true);

    public Task<ConnectionTestResult> TestConnectionAsync(MailAccount account, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConnectionTestResult(true, account.DisplayName));

    public async IAsyncEnumerable<SyncBatch> InitialSyncAsync(
        MailAccount account,
        DateTimeOffset bodyCutoff,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return CreateDemoBatch(account);
    }

    public IAsyncEnumerable<SyncBatch> IncrementalSyncAsync(MailAccount account, SyncCursor cursor, CancellationToken cancellationToken = default) =>
        InitialSyncAsync(account, DateTimeOffset.UtcNow.AddDays(-90), cancellationToken);

    public Task ExecuteAsync(MailAccount account, PendingOperation operation, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<SendResult> SendAsync(MailAccount account, OutgoingMessage message, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SendResult(true, $"demo-{message.ClientMessageId:N}"));

    public Task<IReadOnlyList<MailMessage>> SearchAsync(MailAccount account, SearchRequest request, CancellationToken cancellationToken = default)
    {
        var messages = CreateDemoBatch(account).Messages
            .Where(message => message.Subject.Contains(request.Text, StringComparison.OrdinalIgnoreCase) ||
                              message.Snippet.Contains(request.Text, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return Task.FromResult<IReadOnlyList<MailMessage>>(messages);
    }

    public async Task DownloadAttachmentAsync(MailAccount account, MailMessage message, MailAttachment attachment, Stream destination, CancellationToken cancellationToken = default)
    {
        var content = Encoding.UTF8.GetBytes($"Inboxwell demo attachment\r\n\r\nFile: {attachment.FileName}\r\nMessage: {message.Subject}\r\n");
        await destination.WriteAsync(content, cancellationToken);
    }

    public Task<MailMessage> HydrateMessageAsync(MailAccount account, MailMessage message, CancellationToken cancellationToken = default) =>
        Task.FromResult(message);

    public static SyncBatch CreateDemoBatch(MailAccount account)
    {
        var now = DateTimeOffset.Now;
        var inbox = Folder(account, "inbox", "Inbox", SpecialFolderKind.Inbox, 4, 12);
        var sent = Folder(account, "sent", "Sent", SpecialFolderKind.Sent, 0, 18);
        var archive = Folder(account, "archive", "Archive", SpecialFolderKind.Archive, 0, 84);
        var drafts = Folder(account, "drafts", "Drafts", SpecialFolderKind.Drafts, 0, 2);
        var starred = Folder(account, "starred", "Starred", SpecialFolderKind.Starred, 0, 7);

        var data = new[]
        {
            new DemoMessage("launch", "Elena Volkova", "elena@northstar.studio", "Launch copy review", "I queued a pass through the Russian strings and added notes where the tone could feel lighter.", now.AddMinutes(-8), false, true, new[] { "Work", "Review" }),
            new DemoMessage("invoice", "Marek Nowak", "marek@orbitsupply.pl", "Invoice and delivery window", "The revised invoice is attached. Delivery is still on track for Thursday morning.", now.AddMinutes(-42), false, true, new[] { "Finance" }),
            new DemoMessage("reservation", "Hotel Warszawa", "stay@hotelwarszawa.pl", "Your reservation is confirmed", "We look forward to welcoming you. Your confirmation and arrival details are inside.", now.AddHours(-2), true, false, new[] { "Travel" }),
            new DemoMessage("design", "Nina Park", "nina@commonform.io", "Re: Inboxwell interaction pass", "The quieter selected state works. I would keep the toolbar visible when the thread scrolls.", now.AddHours(-4), false, false, new[] { "Design" }),
            new DemoMessage("security", "Microsoft account team", "account-security-noreply@microsoft.com", "Sign-in activity", "A successful sign-in to your Microsoft account was recorded from Windows.", now.AddDays(-1), true, false, Array.Empty<string>()),
            new DemoMessage("flight", "LOT Polish Airlines", "notifications@lot.pl", "Check-in opens tomorrow", "Your trip to Copenhagen is almost here. Check-in opens at 06:15.", now.AddDays(-2), true, true, new[] { "Travel" })
        };

        var conversations = new List<MailConversation>();
        var messages = new List<MailMessage>();
        foreach (var item in data)
        {
            var threadKey = $"demo:{account.Id:N}:{item.Key}";
            var conversationId = ProviderUtilities.StableGuid(account.Id, threadKey);
            var messageId = ProviderUtilities.StableGuid(account.Id, $"message:{item.Key}");
            conversations.Add(new MailConversation
            {
                Id = conversationId,
                AccountId = account.Id,
                ThreadKey = threadKey,
                ProviderThreadId = item.Key,
                Subject = item.Subject,
                Snippet = item.Snippet,
                Participants = new[] { new MailAddress(item.SenderName, item.SenderEmail) },
                LastMessageAt = item.At,
                MessageCount = item.Key is "launch" or "design" ? 3 : 1,
                UnreadCount = item.IsRead ? 0 : 1,
                IsStarred = item.Key is "launch" or "flight",
                HasAttachments = item.HasAttachment,
                Labels = item.Labels
            });
            messages.Add(new MailMessage
            {
                Id = messageId,
                AccountId = account.Id,
                FolderId = inbox.Id,
                ConversationId = conversationId,
                RemoteId = item.Key,
                ProviderThreadId = item.Key,
                InternetMessageId = $"<{item.Key}@demo.inboxwell.app>",
                From = new MailAddress(item.SenderName, item.SenderEmail),
                To = new[] { new MailAddress(account.DisplayName, account.Email) },
                Subject = item.Subject,
                Snippet = item.Snippet,
                TextBody = item.Snippet + "\n\nThis is sample content stored locally so you can explore Inboxwell before connecting an account.",
                HtmlBody = $"<p>{item.Snippet}</p><p>This is sample content stored locally so you can explore <strong>Inboxwell</strong> before connecting an account.</p>",
                SentAt = item.At,
                ReceivedAt = item.At,
                Flags = (item.IsRead ? MailFlags.Read : MailFlags.None) |
                        (item.HasAttachment ? MailFlags.HasAttachments : MailFlags.None) |
                        (item.Key is "launch" or "flight" ? MailFlags.Starred : MailFlags.None),
                Labels = item.Labels,
                Attachments = item.HasAttachment
                    ? new[] { new MailAttachment { Id = ProviderUtilities.StableGuid(account.Id, $"attachment:{item.Key}"), MessageId = messageId, RemoteId = $"a-{item.Key}", FileName = item.Key == "invoice" ? "invoice-2026.pdf" : "project-notes.pdf", ContentType = "application/pdf", Size = 248_320 } }
                    : Array.Empty<MailAttachment>()
            });
        }

        return new SyncBatch
        {
            Folders = new[] { inbox, starred, drafts, sent, archive },
            Conversations = conversations,
            Messages = messages,
            NextCursor = new SyncCursor(account.Id, "mail", now.ToUnixTimeMilliseconds().ToString(), DateTimeOffset.UtcNow),
            IsComplete = true
        };
    }

    private static MailFolder Folder(MailAccount account, string remoteId, string name, SpecialFolderKind kind, int unread, int total) => new()
    {
        Id = ProviderUtilities.StableGuid(account.Id, $"folder:{remoteId}"),
        AccountId = account.Id,
        RemoteId = remoteId,
        Name = name,
        SpecialKind = kind,
        UnreadCount = unread,
        TotalCount = total
    };

    private sealed record DemoMessage(string Key, string SenderName, string SenderEmail, string Subject, string Snippet, DateTimeOffset At, bool IsRead, bool HasAttachment, IReadOnlyList<string> Labels);
}
