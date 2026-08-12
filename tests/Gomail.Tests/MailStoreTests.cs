using Gomail.Core;
using Gomail.Data;
using Gomail.Providers;

namespace Gomail.Tests;

public sealed class MailStoreTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "gomail-tests", Guid.NewGuid().ToString("N"));
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
            Email = "test@example.com",
            DisplayName = "Test Mailbox",
            IsDemo = true
        };
        await store.UpsertAccountAsync(account);
        await store.UpsertBatchAsync(DemoMailProvider.CreateDemoBatch(account));
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RoundTrip_PreservesMailboxAndThreads()
    {
        var accounts = await store.GetAccountsAsync();
        var folders = await store.GetFoldersAsync(account.Id);
        var conversations = await store.GetConversationsAsync(account.Id);
        var messages = await store.GetMessagesAsync(conversations[0].Id);

        Assert.Single(accounts);
        Assert.Contains(folders, folder => folder.SpecialKind == SpecialFolderKind.Inbox);
        Assert.Equal(6, conversations.Count);
        Assert.NotEmpty(messages);
    }

    [Fact]
    public async Task Search_UsesEncryptedLocalIndex()
    {
        var results = await store.SearchAsync(new SearchRequest("invoice", account.Id, Limit: 20));
        Assert.Single(results);
        Assert.Contains("Invoice", results[0].Subject, StringComparison.OrdinalIgnoreCase);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var rawDatabase = await File.ReadAllBytesAsync(Path.Combine(directory, "mail.db"));
        Assert.False(rawDatabase.AsSpan().IndexOf("Invoice and delivery window"u8) >= 0);
    }

    [Fact]
    public async Task OfflineOperation_IsQueuedAndAppliedOptimistically()
    {
        var conversation = (await store.GetConversationsAsync(account.Id)).First(item => item.UnreadCount > 0);
        var message = (await store.GetMessagesAsync(conversation.Id))[0];
        var operation = new PendingOperation
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = PendingOperationKind.MarkRead,
            TargetRemoteId = message.RemoteId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await store.ApplyOptimisticOperationAsync(operation);
        await store.EnqueueAsync(operation);

        var updated = (await store.GetMessagesAsync(conversation.Id))[0];
        var updatedConversation = (await store.GetConversationsAsync(account.Id)).Single(item => item.Id == conversation.Id);
        var queued = await store.GetRunnableOperationsAsync(account.Id, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.True(updated.Flags.HasFlag(MailFlags.Read));
        Assert.Equal(0, updatedConversation.UnreadCount);
        Assert.Contains(queued, item => item.Id == operation.Id);
    }

    [Fact]
    public async Task Draft_RoundTripsAndCanBeDeleted()
    {
        var draft = new Draft
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            To = new[] { new MailAddress("Recipient", "recipient@example.com") },
            Subject = "A durable draft",
            PlainTextBody = "This must survive a restart.",
            HtmlBody = "<p>This must survive a restart.</p>",
            IsImportant = true,
            Attachments = new[] { new OutgoingAttachment("notes.txt", "text/plain", @"C:\Temp\notes.txt") },
            UpdatedAt = DateTimeOffset.UtcNow,
            DeliveryState = DraftDeliveryState.Draft
        };

        await store.UpsertDraftAsync(draft);
        var restored = await store.GetDraftAsync(draft.Id);
        Assert.NotNull(restored);
        Assert.Equal(draft.Subject, restored.Subject);
        Assert.Equal("recipient@example.com", restored.To[0].Address);
        Assert.Single(restored.Attachments);
        Assert.True(restored.IsImportant);

        await store.DeleteDraftAsync(draft.Id);
        Assert.Null(await store.GetDraftAsync(draft.Id));
    }

    [Fact]
    public async Task UpdatingMessage_ReplacesItsSearchEntryInsteadOfDuplicatingIt()
    {
        var conversation = (await store.GetConversationsAsync(account.Id)).First();
        var message = (await store.GetMessagesAsync(conversation.Id)).First();
        await store.UpsertBatchAsync(new SyncBatch { Messages = new[] { message with { Snippet = "unique replacement marker" } } });
        await store.UpsertBatchAsync(new SyncBatch { Messages = new[] { message with { Snippet = "unique replacement marker" } } });

        var results = await store.SearchAsync(new SearchRequest("unique replacement marker", account.Id, null, false, 50));

        Assert.Single(results, result => result.Id == conversation.Id);
    }

    [Fact]
    public async Task DeletedMessages_DisappearFromAllMailAndSearch()
    {
        var conversation = (await store.GetConversationsAsync(account.Id)).Single(item => item.Subject.Contains("Invoice", StringComparison.OrdinalIgnoreCase));
        var message = (await store.GetMessagesAsync(conversation.Id)).Single();
        await store.ApplyOptimisticOperationAsync(new PendingOperation
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = PendingOperationKind.Delete,
            TargetRemoteId = message.RemoteId,
            CreatedAt = DateTimeOffset.UtcNow
        });

        Assert.DoesNotContain(await store.GetConversationsAsync(account.Id), item => item.Id == conversation.Id);
        Assert.Empty(await store.GetMessagesAsync(conversation.Id));
        Assert.Empty(await store.SearchAsync(new SearchRequest("invoice", account.Id)));
    }

    [Fact]
    public async Task ArchiveWithoutLocalDestination_DoesNotCorruptFolderReference()
    {
        var conversation = (await store.GetConversationsAsync(account.Id)).First();
        var message = (await store.GetMessagesAsync(conversation.Id)).First();
        await store.ApplyOptimisticOperationAsync(new PendingOperation
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = PendingOperationKind.Archive,
            TargetRemoteId = message.RemoteId,
            PayloadJson = "{\"folderId\":null}",
            CreatedAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(message.FolderId, (await store.GetMessagesAsync(conversation.Id)).First().FolderId);
    }

    [Fact]
    public async Task SignatureDefaults_AreUniquePerAccount()
    {
        var first = new Signature { Id = Guid.NewGuid(), AccountId = account.Id, Name = "First", PlainText = "One", IsDefaultForNew = true };
        var second = new Signature { Id = Guid.NewGuid(), AccountId = account.Id, Name = "Second", PlainText = "Two", IsDefaultForNew = true };
        await store.UpsertSignatureAsync(first);
        await store.UpsertSignatureAsync(second);

        var signatures = await store.GetSignaturesAsync(account.Id);
        Assert.Single(signatures, static item => item.IsDefaultForNew);
        Assert.True(signatures.Single(item => item.Id == second.Id).IsDefaultForNew);
    }

    [Fact]
    public async Task MetadataOnlyRefresh_PreservesCachedBodyAttachmentsAndSearchText()
    {
        var conversation = (await store.GetConversationsAsync(account.Id)).Single(item => item.Subject.Contains("Invoice", StringComparison.OrdinalIgnoreCase));
        var original = (await store.GetMessagesAsync(conversation.Id)).Single();
        Assert.NotEmpty(original.Attachments);
        var hydrated = original with { TextBody = "uniquepreservedbodyterm" };
        await store.UpsertBatchAsync(new SyncBatch { Messages = new[] { hydrated } });

        await store.UpsertBatchAsync(new SyncBatch
        {
            Messages = new[] { hydrated with { TextBody = null, HtmlBody = null, Attachments = Array.Empty<MailAttachment>(), Flags = original.Flags | MailFlags.Read } }
        });

        var restored = (await store.GetMessagesAsync(conversation.Id)).Single();
        Assert.False(string.IsNullOrWhiteSpace(restored.TextBody));
        Assert.NotEmpty(restored.Attachments);
        Assert.Single(await store.SearchAsync(new SearchRequest("uniquepreservedbodyterm", account.Id)));
    }

    [Fact]
    public async Task SearchMetadataRefresh_ToleratesThreadKeyDriftForAnExistingConversation()
    {
        var conversation = (await store.GetConversationsAsync(account.Id)).First();
        var message = (await store.GetMessagesAsync(conversation.Id)).First();
        var refreshed = conversation with
        {
            ThreadKey = $"server-search:{conversation.ThreadKey}",
            Subject = "Updated by server search"
        };

        await store.UpsertBatchAsync(new SyncBatch
        {
            Conversations = new[] { refreshed },
            Messages = new[] { message with { Subject = refreshed.Subject } }
        });

        var stored = (await store.GetConversationsAsync(account.Id)).Single(item => item.Id == conversation.Id);
        Assert.Equal("Updated by server search", stored.Subject);
        Assert.Equal(conversation.ThreadKey, stored.ThreadKey);
    }
}
