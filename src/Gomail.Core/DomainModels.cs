using System.Collections.ObjectModel;

namespace Gomail.Core;

public enum ProviderKind
{
    Microsoft365,
    Gmail,
    Imap,
    Demo
}

public enum SpecialFolderKind
{
    None,
    Inbox,
    Drafts,
    Sent,
    Archive,
    Spam,
    Trash,
    Starred,
    AllMail
}

[Flags]
public enum MailFlags
{
    None = 0,
    Read = 1,
    Starred = 2,
    Answered = 4,
    Forwarded = 8,
    Draft = 16,
    Deleted = 32,
    HasAttachments = 64
}

public enum PendingOperationKind
{
    MarkRead,
    MarkUnread,
    Star,
    Unstar,
    Move,
    Archive,
    Delete,
    MarkSpam,
    ApplyLabel,
    RemoveLabel,
    SaveDraft,
    DeleteDraft,
    Send
}

public enum PendingOperationState
{
    Queued,
    Running,
    WaitingForRetry,
    Failed,
    Completed
}

public enum DraftDeliveryState
{
    Draft,
    Queued,
    Sending,
    Failed
}

public sealed record MailAddress(string Name, string Address)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Address : Name;
}

public sealed record MailAccount
{
    public required Guid Id { get; init; }
    public required ProviderKind Provider { get; init; }
    public required string Email { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Color { get; init; } = "#2F6FED";
    public bool IsEnabled { get; init; } = true;
    public bool IsDemo { get; init; }
    public DateTimeOffset? LastSuccessfulSync { get; init; }
    public string? LastSyncError { get; init; }
    public IReadOnlyDictionary<string, string> Settings { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
}

public sealed record MailFolder
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required string RemoteId { get; init; }
    public required string Name { get; init; }
    public SpecialFolderKind SpecialKind { get; init; }
    public int UnreadCount { get; init; }
    public int TotalCount { get; init; }
    public string? ParentRemoteId { get; init; }
}

public sealed record MailConversation
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required string ThreadKey { get; init; }
    public string? ProviderThreadId { get; init; }
    public string Subject { get; init; } = string.Empty;
    public string Snippet { get; init; } = string.Empty;
    public IReadOnlyList<MailAddress> Participants { get; init; } = Array.Empty<MailAddress>();
    public DateTimeOffset LastMessageAt { get; init; }
    public int MessageCount { get; init; }
    public int UnreadCount { get; init; }
    public bool IsStarred { get; init; }
    public bool HasAttachments { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
}

public sealed record MailMessage
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required Guid FolderId { get; init; }
    public required Guid ConversationId { get; init; }
    public required string RemoteId { get; init; }
    public string? ProviderThreadId { get; init; }
    public string? InternetMessageId { get; init; }
    public string? InReplyTo { get; init; }
    public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();
    public MailAddress From { get; init; } = new(string.Empty, string.Empty);
    public IReadOnlyList<MailAddress> To { get; init; } = Array.Empty<MailAddress>();
    public IReadOnlyList<MailAddress> Cc { get; init; } = Array.Empty<MailAddress>();
    public IReadOnlyList<MailAddress> Bcc { get; init; } = Array.Empty<MailAddress>();
    public string Subject { get; init; } = string.Empty;
    public string Snippet { get; init; } = string.Empty;
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }
    public DateTimeOffset SentAt { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public MailFlags Flags { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
    public IReadOnlyList<MailAttachment> Attachments { get; init; } = Array.Empty<MailAttachment>();
}

public sealed record MailAttachment
{
    public required Guid Id { get; init; }
    public required Guid MessageId { get; init; }
    public required string RemoteId { get; init; }
    public required string FileName { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public long Size { get; init; }
    public bool IsInline { get; init; }
    public string? ContentId { get; init; }
    public string? CachedPath { get; init; }
}

public sealed record Signature
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required string Name { get; init; }
    public string Html { get; init; } = string.Empty;
    public string PlainText { get; init; } = string.Empty;
    public bool IsDefaultForNew { get; init; }
    public bool IsDefaultForReplies { get; init; }
}

public sealed record Draft
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public string? RemoteId { get; init; }
    public IReadOnlyList<MailAddress> To { get; init; } = Array.Empty<MailAddress>();
    public IReadOnlyList<MailAddress> Cc { get; init; } = Array.Empty<MailAddress>();
    public IReadOnlyList<MailAddress> Bcc { get; init; } = Array.Empty<MailAddress>();
    public string Subject { get; init; } = string.Empty;
    public string HtmlBody { get; init; } = string.Empty;
    public string PlainTextBody { get; init; } = string.Empty;
    public string? ReplyToRemoteId { get; init; }
    public string? ProviderThreadId { get; init; }
    public IReadOnlyList<OutgoingAttachment> Attachments { get; init; } = Array.Empty<OutgoingAttachment>();
    public DateTimeOffset UpdatedAt { get; init; }
    public DraftDeliveryState DeliveryState { get; init; }
    public string? LastError { get; init; }
}

public sealed record OutgoingMessage
{
    public required Guid ClientMessageId { get; init; }
    public required Guid AccountId { get; init; }
    public IReadOnlyList<MailAddress> To { get; init; } = Array.Empty<MailAddress>();
    public IReadOnlyList<MailAddress> Cc { get; init; } = Array.Empty<MailAddress>();
    public IReadOnlyList<MailAddress> Bcc { get; init; } = Array.Empty<MailAddress>();
    public string Subject { get; init; } = string.Empty;
    public string HtmlBody { get; init; } = string.Empty;
    public string PlainTextBody { get; init; } = string.Empty;
    public string? ReplyToRemoteId { get; init; }
    public string? ProviderThreadId { get; init; }
    public IReadOnlyList<OutgoingAttachment> Attachments { get; init; } = Array.Empty<OutgoingAttachment>();
}

public sealed record OutgoingAttachment(string FileName, string ContentType, string LocalPath, bool IsInline = false, string? ContentId = null);

public sealed record SyncCursor(Guid AccountId, string Scope, string Value, DateTimeOffset UpdatedAt);

public sealed record PendingOperation
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required PendingOperationKind Kind { get; init; }
    public required string TargetRemoteId { get; init; }
    public string PayloadJson { get; init; } = "{}";
    public PendingOperationState State { get; init; } = PendingOperationState.Queued;
    public int AttemptCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public string? LastError { get; init; }
}

public sealed record SyncBatch
{
    public IReadOnlyList<MailFolder> Folders { get; init; } = Array.Empty<MailFolder>();
    public IReadOnlyList<MailConversation> Conversations { get; init; } = Array.Empty<MailConversation>();
    public IReadOnlyList<MailMessage> Messages { get; init; } = Array.Empty<MailMessage>();
    public IReadOnlyList<string> DeletedRemoteMessageIds { get; init; } = Array.Empty<string>();
    public SyncCursor? NextCursor { get; init; }
    public bool IsComplete { get; init; }
}

public sealed record SearchRequest(string Text, Guid? AccountId = null, Guid? FolderId = null, bool IncludeServer = true, int Limit = 100);

public sealed record ProviderCapabilities(
    bool SupportsFolders,
    bool SupportsLabels,
    bool SupportsNativeThreads,
    bool SupportsServerSearch,
    bool SupportsDrafts,
    bool SupportsArchive,
    bool SupportsSpam,
    long? MaximumSendBytes = null);

public sealed record ConnectionTestResult(bool Success, string? DisplayName = null, string? Error = null);

public sealed record SendResult(bool Success, string? RemoteId = null, string? Error = null);
