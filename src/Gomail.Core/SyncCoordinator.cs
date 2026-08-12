using System.Collections.Concurrent;
using System.Text.Json;

namespace Gomail.Core;

public sealed class SyncCoordinator : ISyncCoordinator
{
    private static readonly TimeSpan DefaultBodyWindow = TimeSpan.FromDays(90);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IMailStore store;
    private readonly IMailProviderRegistry providers;
    private readonly IConnectivity connectivity;
    private readonly IClock clock;
    private readonly IAppNotifier? notifier;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> accountLocks = new();

    public SyncCoordinator(IMailStore store, IMailProviderRegistry providers, IConnectivity connectivity, IClock clock, IAppNotifier? notifier = null)
    {
        this.store = store;
        this.providers = providers;
        this.connectivity = connectivity;
        this.clock = clock;
        this.notifier = notifier;
    }

    public async Task SyncAllAsync(bool forceFull = false, CancellationToken cancellationToken = default)
    {
        if (!connectivity.IsOnline)
        {
            return;
        }

        var accounts = await store.GetAccountsAsync(cancellationToken);
        await Parallel.ForEachAsync(
            accounts.Where(static account => account.IsEnabled && !account.IsDemo),
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 3 },
            async (account, token) => await SyncAccountAsync(account.Id, forceFull, token));
    }

    public async Task SyncAccountAsync(Guid accountId, bool forceFull = false, CancellationToken cancellationToken = default)
    {
        if (!connectivity.IsOnline)
        {
            return;
        }

        var accountLock = accountLocks.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
        await accountLock.WaitAsync(cancellationToken);
        try
        {
            var account = await store.GetAccountAsync(accountId, cancellationToken)
                ?? throw new InvalidOperationException($"Account {accountId} was not found.");
            var provider = providers.Get(account.Provider);
            if (NeedsProviderProfile(account))
            {
                var profile = await provider.TestConnectionAsync(account, cancellationToken);
                if (profile.Success)
                {
                    account = account with
                    {
                        Email = string.IsNullOrWhiteSpace(profile.Email) ? account.Email : profile.Email,
                        DisplayName = string.IsNullOrWhiteSpace(profile.DisplayName) ? account.DisplayName : profile.DisplayName
                    };
                    await store.UpsertAccountAsync(account, cancellationToken);
                }
            }
            var cursor = forceFull ? null : await store.GetCursorAsync(accountId, "mail", cancellationToken);
            var stream = cursor is null
                ? provider.InitialSyncAsync(account, clock.UtcNow - DefaultBodyWindow, cancellationToken)
                : provider.IncrementalSyncAsync(account, cursor, cancellationToken);
            string? discoveredDisplayName = null;

            await foreach (var batch in stream.WithCancellation(cancellationToken))
            {
                discoveredDisplayName ??= batch.Messages
                    .Where(message => message.From.Address.Equals(account.Email, StringComparison.OrdinalIgnoreCase))
                    .Select(static message => message.From.Name)
                    .FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name));
                await store.UpsertBatchAsync(batch, cancellationToken);
                if (batch.DeletedRemoteMessageIds.Count > 0)
                {
                    await store.DeleteRemoteMessagesAsync(accountId, batch.DeletedRemoteMessageIds, cancellationToken);
                }

                if (batch.NextCursor is not null)
                {
                    await store.SetCursorAsync(batch.NextCursor, cancellationToken);
                }
            }

            await FlushOutboxCoreAsync(account, provider, cancellationToken);
            var displayName = ShouldAdoptDiscoveredName(account, discoveredDisplayName)
                ? discoveredDisplayName!
                : account.DisplayName;
            await store.UpsertAccountAsync(account with { DisplayName = displayName, LastSuccessfulSync = clock.UtcNow, LastSyncError = null }, cancellationToken);
            if (account.LastSuccessfulSync is { } previousSync && notifier is not null)
            {
                var conversations = await store.GetConversationsAsync(account.Id, limit: 100, cancellationToken: cancellationToken);
                foreach (var conversation in conversations.Where(item => item.UnreadCount > 0 && item.LastMessageAt > previousSync).Take(5))
                {
                    try
                    {
                        await notifier.NotifyNewMailAsync(conversation, cancellationToken);
                    }
                    catch
                    {
                        // A disabled or unavailable Windows notification service must not fail mail sync.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var account = await store.GetAccountAsync(accountId, cancellationToken);
            if (account is not null)
            {
                await store.UpsertAccountAsync(account with { LastSyncError = exception.Message }, cancellationToken);
                try
                {
                    await FlushOutboxCoreAsync(account, providers.Get(account.Provider), cancellationToken);
                }
                catch
                {
                    // The original sync error remains the most useful account-level status.
                }
            }
        }
        finally
        {
            accountLock.Release();
        }
    }

    private static bool ShouldAdoptDiscoveredName(MailAccount account, string? discoveredName)
    {
        if (string.IsNullOrWhiteSpace(discoveredName) || account.Provider is ProviderKind.Imap or ProviderKind.Demo)
        {
            return false;
        }

        var localPart = account.Email.Split('@')[0];
        return account.DisplayName.Equals(account.Email, StringComparison.OrdinalIgnoreCase) ||
               account.DisplayName.Equals(localPart, StringComparison.OrdinalIgnoreCase) ||
               account.DisplayName.Equals("Gmail", StringComparison.OrdinalIgnoreCase) ||
               account.DisplayName.StartsWith("pending-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NeedsProviderProfile(MailAccount account)
    {
        if (account.Provider is not (ProviderKind.Gmail or ProviderKind.Microsoft365)) return false;
        var localPart = account.Email.Split('@')[0];
        return account.DisplayName.Equals(account.Email, StringComparison.OrdinalIgnoreCase) ||
               account.DisplayName.Equals(localPart, StringComparison.OrdinalIgnoreCase) ||
               account.DisplayName.Equals("Gmail", StringComparison.OrdinalIgnoreCase) ||
               account.DisplayName.StartsWith("pending-", StringComparison.OrdinalIgnoreCase);
    }

    public async Task FlushOutboxAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!connectivity.IsOnline)
        {
            return;
        }

        var account = await store.GetAccountAsync(accountId, cancellationToken)
            ?? throw new InvalidOperationException($"Account {accountId} was not found.");
        await FlushOutboxCoreAsync(account, providers.Get(account.Provider), cancellationToken);
    }

    public async Task QueueAsync(PendingOperation operation, CancellationToken cancellationToken = default)
    {
        await store.ApplyOptimisticOperationAsync(operation, cancellationToken);
        await store.EnqueueAsync(operation, cancellationToken);
        if (connectivity.IsOnline)
        {
            await FlushOutboxAsync(operation.AccountId, cancellationToken);
        }
    }

    private async Task FlushOutboxCoreAsync(MailAccount account, IMailProvider provider, CancellationToken cancellationToken)
    {
        var operations = await store.GetRunnableOperationsAsync(account.Id, clock.UtcNow, cancellationToken);
        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var running = operation with { State = PendingOperationState.Running, AttemptCount = operation.AttemptCount + 1 };
            await store.UpdateOperationAsync(running, cancellationToken);
            try
            {
                if (running.Kind == PendingOperationKind.Send)
                {
                    await UpdateDraftStateAsync(running, DraftDeliveryState.Sending, null, cancellationToken);
                    var outgoing = JsonSerializer.Deserialize<OutgoingMessage>(running.PayloadJson, JsonOptions)
                        ?? throw new MailProviderException("The queued message is malformed.");
                    var result = await provider.SendAsync(account, outgoing, cancellationToken);
                    if (!result.Success)
                    {
                        throw new MailProviderException(result.Error ?? "The server rejected the message.", true);
                    }
                }
                else
                {
                    await provider.ExecuteAsync(account, running, cancellationToken);
                }

                await store.UpdateOperationAsync(running with { State = PendingOperationState.Completed, LastError = null }, cancellationToken);
                if (running.Kind == PendingOperationKind.Send && Guid.TryParseExact(running.TargetRemoteId, "N", out var sentDraftId))
                {
                    await store.DeleteDraftAsync(sentDraftId, cancellationToken);
                }
            }
            catch (MailProviderException exception) when (exception.IsTransient && running.AttemptCount < 6)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, running.AttemptCount) * 2));
                await store.UpdateOperationAsync(running with
                {
                    State = PendingOperationState.WaitingForRetry,
                    NextAttemptAt = clock.UtcNow + delay,
                    LastError = exception.Message
                }, cancellationToken);
                await UpdateDraftStateAsync(running, DraftDeliveryState.Queued, exception.Message, cancellationToken);
            }
            catch (Exception exception)
            {
                await store.UpdateOperationAsync(running with { State = PendingOperationState.Failed, LastError = exception.Message }, cancellationToken);
                await UpdateDraftStateAsync(running, DraftDeliveryState.Failed, exception.Message, cancellationToken);
            }
        }

        await store.PurgeCompletedOperationsAsync(clock.UtcNow.AddDays(-7), cancellationToken);
    }

    private async Task UpdateDraftStateAsync(PendingOperation operation, DraftDeliveryState state, string? error, CancellationToken cancellationToken)
    {
        if (operation.Kind != PendingOperationKind.Send || !Guid.TryParseExact(operation.TargetRemoteId, "N", out var draftId))
        {
            return;
        }

        var draft = await store.GetDraftAsync(draftId, cancellationToken);
        if (draft is not null)
        {
            await store.UpsertDraftAsync(draft with
            {
                DeliveryState = state,
                LastError = error,
                UpdatedAt = clock.UtcNow
            }, cancellationToken);
        }
    }
}
