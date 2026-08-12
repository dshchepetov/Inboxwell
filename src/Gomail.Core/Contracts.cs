namespace Gomail.Core;

public interface IMailProvider
{
    ProviderKind Kind { get; }
    ProviderCapabilities Capabilities { get; }

    Task<ConnectionTestResult> TestConnectionAsync(MailAccount account, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SyncBatch> InitialSyncAsync(MailAccount account, DateTimeOffset bodyCutoff, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SyncBatch> IncrementalSyncAsync(MailAccount account, SyncCursor cursor, CancellationToken cancellationToken = default);
    Task ExecuteAsync(MailAccount account, PendingOperation operation, CancellationToken cancellationToken = default);
    Task<SendResult> SendAsync(MailAccount account, OutgoingMessage message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailMessage>> SearchAsync(MailAccount account, SearchRequest request, CancellationToken cancellationToken = default);
    Task<MailMessage> HydrateMessageAsync(MailAccount account, MailMessage message, CancellationToken cancellationToken = default);
    Task DownloadAttachmentAsync(MailAccount account, MailMessage message, MailAttachment attachment, Stream destination, CancellationToken cancellationToken = default);
}

public interface IMailProviderRegistry
{
    IMailProvider Get(ProviderKind kind);
    IReadOnlyCollection<IMailProvider> All { get; }
}

public interface IMailStore
{
    Task InitializeAsync(string encryptionKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailAccount>> GetAccountsAsync(CancellationToken cancellationToken = default);
    Task<MailAccount?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task UpsertAccountAsync(MailAccount account, CancellationToken cancellationToken = default);
    Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MailFolder>> GetFoldersAsync(Guid? accountId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailConversation>> GetConversationsAsync(Guid? accountId = null, Guid? folderId = null, int limit = 200, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailMessage>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task UpsertBatchAsync(SyncBatch batch, CancellationToken cancellationToken = default);
    Task DeleteRemoteMessagesAsync(Guid accountId, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken = default);
    Task SetAttachmentCachedPathAsync(Guid attachmentId, string? cachedPath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Signature>> GetSignaturesAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task UpsertSignatureAsync(Signature signature, CancellationToken cancellationToken = default);
    Task DeleteSignatureAsync(Guid signatureId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Draft>> GetDraftsAsync(Guid? accountId = null, CancellationToken cancellationToken = default);
    Task<Draft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default);
    Task UpsertDraftAsync(Draft draft, CancellationToken cancellationToken = default);
    Task DeleteDraftAsync(Guid draftId, CancellationToken cancellationToken = default);

    Task<SyncCursor?> GetCursorAsync(Guid accountId, string scope, CancellationToken cancellationToken = default);
    Task SetCursorAsync(SyncCursor cursor, CancellationToken cancellationToken = default);

    Task EnqueueAsync(PendingOperation operation, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingOperation>> GetRunnableOperationsAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task UpdateOperationAsync(PendingOperation operation, CancellationToken cancellationToken = default);
    Task ApplyOptimisticOperationAsync(PendingOperation operation, CancellationToken cancellationToken = default);
    Task CancelPendingOperationsAsync(Guid accountId, PendingOperationKind kind, string targetRemoteId, CancellationToken cancellationToken = default);
    Task PurgeCompletedOperationsAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MailConversation>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);
}

public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
    Task<string> GetOrCreateKeyAsync(string key, int bytes = 32, CancellationToken cancellationToken = default);
}

public interface IHtmlSanitizer
{
    string Sanitize(string html, bool allowExternalImages = false);
}

public interface IAppNotifier
{
    Task NotifyNewMailAsync(MailConversation conversation, CancellationToken cancellationToken = default);
}

public interface IConnectivity
{
    bool IsOnline { get; }
    event EventHandler<bool>? Changed;
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface ISyncCoordinator
{
    Task SyncAllAsync(bool forceFull = false, CancellationToken cancellationToken = default);
    Task SyncAccountAsync(Guid accountId, bool forceFull = false, CancellationToken cancellationToken = default);
    Task FlushOutboxAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task QueueAsync(PendingOperation operation, CancellationToken cancellationToken = default);
}

public class MailProviderException : Exception
{
    public MailProviderException(string message, bool isTransient = false, Exception? innerException = null)
        : base(message, innerException)
    {
        IsTransient = isTransient;
    }

    public bool IsTransient { get; }
}
