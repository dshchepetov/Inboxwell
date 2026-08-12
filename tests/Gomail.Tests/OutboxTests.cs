using System.Text.Json;
using Gomail.Core;
using Gomail.Data;
using Gomail.Providers;

namespace Gomail.Tests;

public sealed class OutboxTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "gomail-outbox-tests", Guid.NewGuid().ToString("N"));
    private SqliteMailStore store = null!;
    private MailAccount account = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        store = new SqliteMailStore(Path.Combine(directory, "mail.db"));
        await store.InitializeAsync(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Provider = ProviderKind.Demo,
            Email = "sender@example.com",
            DisplayName = "Sender",
            IsDemo = true
        };
        await store.UpsertAccountAsync(account);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task OfflineSend_SurvivesAndFlushesAfterReconnect()
    {
        var connectivity = new TestConnectivity(false);
        var coordinator = new SyncCoordinator(
            store,
            new MailProviderRegistry(new IMailProvider[] { new DemoMailProvider() }),
            connectivity,
            new SystemClock());
        var draft = CreateDraft();
        await store.UpsertDraftAsync(draft with { DeliveryState = DraftDeliveryState.Queued });
        var outgoing = new OutgoingMessage
        {
            ClientMessageId = draft.Id,
            AccountId = account.Id,
            To = draft.To,
            Subject = draft.Subject,
            PlainTextBody = draft.PlainTextBody,
            HtmlBody = draft.HtmlBody
        };
        var operation = new PendingOperation
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = PendingOperationKind.Send,
            TargetRemoteId = draft.Id.ToString("N"),
            PayloadJson = JsonSerializer.Serialize(outgoing, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CreatedAt = DateTimeOffset.UtcNow
        };

        await coordinator.QueueAsync(operation);
        Assert.NotNull(await store.GetDraftAsync(draft.Id));

        connectivity.SetOnline(true);
        await coordinator.FlushOutboxAsync(account.Id);
        Assert.Null(await store.GetDraftAsync(draft.Id));
        Assert.Empty(await store.GetRunnableOperationsAsync(account.Id, DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public async Task SendLeftRunningByCrash_IsRecoveredOnNextFlush()
    {
        var connectivity = new TestConnectivity(true);
        var coordinator = new SyncCoordinator(
            store,
            new MailProviderRegistry(new IMailProvider[] { new DemoMailProvider() }),
            connectivity,
            new SystemClock());
        var draft = CreateDraft() with { DeliveryState = DraftDeliveryState.Sending };
        await store.UpsertDraftAsync(draft);
        await store.EnqueueAsync(new PendingOperation
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = PendingOperationKind.Send,
            TargetRemoteId = draft.Id.ToString("N"),
            PayloadJson = JsonSerializer.Serialize(new OutgoingMessage
            {
                ClientMessageId = draft.Id,
                AccountId = account.Id,
                To = draft.To,
                Subject = draft.Subject,
                PlainTextBody = draft.PlainTextBody,
                HtmlBody = draft.HtmlBody
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            State = PendingOperationState.Running,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await coordinator.FlushOutboxAsync(account.Id);

        Assert.Null(await store.GetDraftAsync(draft.Id));
        Assert.Empty(await store.GetRunnableOperationsAsync(account.Id, DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    private Draft CreateDraft() => new()
    {
        Id = Guid.NewGuid(),
        AccountId = account.Id,
        To = new[] { new MailAddress(string.Empty, "recipient@example.com") },
        Subject = "Queued message",
        PlainTextBody = "Send me later",
        HtmlBody = "<p>Send me later</p>",
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private sealed class TestConnectivity : IConnectivity
    {
        public TestConnectivity(bool isOnline) => IsOnline = isOnline;
        public bool IsOnline { get; private set; }
        public event EventHandler<bool>? Changed;
        public void SetOnline(bool value)
        {
            IsOnline = value;
            Changed?.Invoke(this, value);
        }
    }
}
