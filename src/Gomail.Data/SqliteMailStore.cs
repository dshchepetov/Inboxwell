using System.Globalization;
using System.Text.Json;
using Gomail.Core;
using Microsoft.Data.Sqlite;

namespace Gomail.Data;

public sealed class SqliteMailStore : IMailStore
{
    private const int CurrentSchemaVersion = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string databasePath;
    private string? encryptionKeyHex;

    public SqliteMailStore(string databasePath)
    {
        this.databasePath = databasePath;
    }

    public async Task InitializeAsync(string encryptionKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptionKey);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? AppContext.BaseDirectory);
        SQLitePCL.Batteries_V2.Init();
        encryptionKeyHex = Convert.ToHexString(Convert.FromBase64String(encryptionKey));

        await using var connection = await OpenAsync(cancellationToken);
        await MigrateAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<MailAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, provider, email, display_name, color, is_enabled, is_demo, last_sync, last_error, settings_json FROM accounts ORDER BY display_name, email";
        var result = new List<MailAccount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadAccount(reader));
        }
        return result;
    }

    public async Task<MailAccount?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, provider, email, display_name, color, is_enabled, is_demo, last_sync, last_error, settings_json FROM accounts WHERE id = $id";
        command.Parameters.AddWithValue("$id", accountId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAccount(reader) : null;
    }

    public async Task UpsertAccountAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accounts (id, provider, email, display_name, color, is_enabled, is_demo, last_sync, last_error, settings_json)
            VALUES ($id, $provider, $email, $displayName, $color, $enabled, $demo, $lastSync, $lastError, $settings)
            ON CONFLICT(id) DO UPDATE SET
                provider=excluded.provider, email=excluded.email, display_name=excluded.display_name,
                color=excluded.color, is_enabled=excluded.is_enabled, is_demo=excluded.is_demo,
                last_sync=excluded.last_sync, last_error=excluded.last_error, settings_json=excluded.settings_json;
            """;
        command.Parameters.AddWithValue("$id", account.Id.ToString("N"));
        command.Parameters.AddWithValue("$provider", (int)account.Provider);
        command.Parameters.AddWithValue("$email", account.Email);
        command.Parameters.AddWithValue("$displayName", account.DisplayName);
        command.Parameters.AddWithValue("$color", account.Color);
        command.Parameters.AddWithValue("$enabled", account.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$demo", account.IsDemo ? 1 : 0);
        command.Parameters.AddWithValue("$lastSync", DbValue(account.LastSuccessfulSync));
        command.Parameters.AddWithValue("$lastError", DbValue(account.LastSyncError));
        command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(account.Settings, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteWithParameterAsync(connection, "DELETE FROM messages_fts WHERE rowid IN (SELECT row_id FROM messages_fts_map WHERE account_id = $id)", "$id", accountId.ToString("N"), cancellationToken);
        await ExecuteWithParameterAsync(connection, "DELETE FROM accounts WHERE id = $id", "$id", accountId.ToString("N"), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(Guid? accountId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, account_id, remote_id, name, special_kind, unread_count, total_count, parent_remote_id FROM folders" +
                              (accountId.HasValue ? " WHERE account_id = $accountId" : string.Empty) +
                              " ORDER BY special_kind, name COLLATE NOCASE";
        if (accountId.HasValue)
        {
            command.Parameters.AddWithValue("$accountId", accountId.Value.ToString("N"));
        }

        var result = new List<MailFolder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new MailFolder
            {
                Id = ParseGuid(reader.GetString(0)),
                AccountId = ParseGuid(reader.GetString(1)),
                RemoteId = reader.GetString(2),
                Name = reader.GetString(3),
                SpecialKind = (SpecialFolderKind)reader.GetInt32(4),
                UnreadCount = reader.GetInt32(5),
                TotalCount = reader.GetInt32(6),
                ParentRemoteId = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        }
        return result;
    }

    public async Task<IReadOnlyList<MailConversation>> GetConversationsAsync(
        Guid? accountId = null,
        Guid? folderId = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var filters = new List<string> { "EXISTS (SELECT 1 FROM messages visible WHERE visible.conversation_id = c.id AND (visible.flags & $deleted) = 0)" };
        command.Parameters.AddWithValue("$deleted", (int)MailFlags.Deleted);
        if (accountId.HasValue)
        {
            filters.Add("c.account_id = $accountId");
            command.Parameters.AddWithValue("$accountId", accountId.Value.ToString("N"));
        }
        if (folderId.HasValue)
        {
            filters.Add("EXISTS (SELECT 1 FROM messages m WHERE m.conversation_id = c.id AND m.folder_id = $folderId AND (m.flags & $deleted) = 0)");
            command.Parameters.AddWithValue("$folderId", folderId.Value.ToString("N"));
        }

        command.CommandText = "SELECT c.id, c.account_id, c.thread_key, c.provider_thread_id, c.subject, c.snippet, c.participants_json, c.last_message_at, c.message_count, c.unread_count, c.is_starred, c.has_attachments, c.labels_json FROM conversations c" +
                              (filters.Count > 0 ? " WHERE " + string.Join(" AND ", filters) : string.Empty) +
                              " ORDER BY c.last_message_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 12_000));
        return await ReadConversationsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<MailMessage>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var attachments = await LoadAttachmentsAsync(connection, conversationId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, account_id, folder_id, conversation_id, remote_id, provider_thread_id, internet_message_id,
                   in_reply_to, references_json, from_json, to_json, cc_json, bcc_json, subject, snippet,
                   text_body, html_body, sent_at, received_at, flags, labels_json
            FROM messages WHERE conversation_id = $conversationId AND (flags & $deleted) = 0 ORDER BY received_at, sent_at;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("N"));
        command.Parameters.AddWithValue("$deleted", (int)MailFlags.Deleted);
        var result = new List<MailMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var messageId = ParseGuid(reader.GetString(0));
            result.Add(new MailMessage
            {
                Id = messageId,
                AccountId = ParseGuid(reader.GetString(1)),
                FolderId = ParseGuid(reader.GetString(2)),
                ConversationId = ParseGuid(reader.GetString(3)),
                RemoteId = reader.GetString(4),
                ProviderThreadId = NullableString(reader, 5),
                InternetMessageId = NullableString(reader, 6),
                InReplyTo = NullableString(reader, 7),
                References = Deserialize<string[]>(reader.GetString(8)),
                From = Deserialize<MailAddress>(reader.GetString(9)) ?? new MailAddress(string.Empty, string.Empty),
                To = Deserialize<MailAddress[]>(reader.GetString(10)),
                Cc = Deserialize<MailAddress[]>(reader.GetString(11)),
                Bcc = Deserialize<MailAddress[]>(reader.GetString(12)),
                Subject = reader.GetString(13),
                Snippet = reader.GetString(14),
                TextBody = NullableString(reader, 15),
                HtmlBody = NullableString(reader, 16),
                SentAt = ParseDate(reader.GetString(17)),
                ReceivedAt = ParseDate(reader.GetString(18)),
                Flags = (MailFlags)reader.GetInt32(19),
                Labels = Deserialize<string[]>(reader.GetString(20)),
                Attachments = attachments.TryGetValue(messageId, out var messageAttachments) ? messageAttachments : Array.Empty<MailAttachment>()
            });
        }
        return result;
    }

    public async Task UpsertBatchAsync(SyncBatch batch, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var folder in batch.Folders)
        {
            await UpsertFolderAsync(connection, folder, cancellationToken);
        }
        foreach (var conversation in batch.Conversations)
        {
            await UpsertConversationAsync(connection, conversation, cancellationToken);
        }
        foreach (var message in batch.Messages)
        {
            await UpsertMessageAsync(connection, message, cancellationToken);
        }
        var suppliedConversationIds = batch.Conversations.Select(static conversation => conversation.Id).ToHashSet();
        foreach (var conversationId in batch.Messages.Select(static message => message.ConversationId).Distinct().Where(id => !suppliedConversationIds.Contains(id)))
        {
            await RecomputeConversationAsync(connection, conversationId, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteRemoteMessagesAsync(Guid accountId, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken = default)
    {
        if (remoteIds.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var affectedConversations = new HashSet<Guid>();
        foreach (var remoteId in remoteIds)
        {
            await using (var lookup = connection.CreateCommand())
            {
                lookup.CommandText = "SELECT conversation_id FROM messages WHERE account_id=$accountId AND remote_id=$remoteId";
                lookup.Parameters.AddWithValue("$accountId", accountId.ToString("N"));
                lookup.Parameters.AddWithValue("$remoteId", remoteId);
                var value = await lookup.ExecuteScalarAsync(cancellationToken);
                if (value is string id && Guid.TryParseExact(id, "N", out var parsed)) affectedConversations.Add(parsed);
            }
            await DeleteFtsEntryAsync(connection, accountId, remoteId, cancellationToken);

            await using var message = connection.CreateCommand();
            message.CommandText = "DELETE FROM messages WHERE account_id = $accountId AND remote_id = $remoteId";
            message.Parameters.AddWithValue("$accountId", accountId.ToString("N"));
            message.Parameters.AddWithValue("$remoteId", remoteId);
            await message.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var conversationId in affectedConversations) await RecomputeConversationAsync(connection, conversationId, cancellationToken);
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.CommandText = "DELETE FROM conversations WHERE account_id=$accountId AND NOT EXISTS (SELECT 1 FROM messages m WHERE m.conversation_id=conversations.id)";
            cleanup.Parameters.AddWithValue("$accountId", accountId.ToString("N"));
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetAttachmentCachedPathAsync(Guid attachmentId, string? cachedPath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE attachments SET cached_path=$path WHERE id=$id";
        command.Parameters.AddWithValue("$id", attachmentId.ToString("N"));
        command.Parameters.AddWithValue("$path", (object?)cachedPath ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Signature>> GetSignaturesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, account_id, name, html, plain_text, default_new, default_reply, rtf FROM signatures WHERE account_id = $accountId ORDER BY name";
        command.Parameters.AddWithValue("$accountId", accountId.ToString("N"));
        var result = new List<Signature>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new Signature
            {
                Id = ParseGuid(reader.GetString(0)),
                AccountId = ParseGuid(reader.GetString(1)),
                Name = reader.GetString(2),
                Html = reader.GetString(3),
                PlainText = reader.GetString(4),
                IsDefaultForNew = reader.GetBoolean(5),
                IsDefaultForReplies = reader.GetBoolean(6),
                Rtf = reader.GetString(7)
            });
        }
        return result;
    }

    public async Task UpsertSignatureAsync(Signature signature, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (signature.IsDefaultForNew)
        {
            await ExecuteWithParameterAsync(connection, "UPDATE signatures SET default_new = 0 WHERE account_id = $id", "$id", signature.AccountId.ToString("N"), cancellationToken);
        }
        if (signature.IsDefaultForReplies)
        {
            await ExecuteWithParameterAsync(connection, "UPDATE signatures SET default_reply = 0 WHERE account_id = $id", "$id", signature.AccountId.ToString("N"), cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO signatures (id, account_id, name, html, plain_text, default_new, default_reply, rtf)
            VALUES ($id, $accountId, $name, $html, $plain, $new, $reply, $rtf)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name, html=excluded.html, plain_text=excluded.plain_text,
                default_new=excluded.default_new, default_reply=excluded.default_reply, rtf=excluded.rtf;
            """;
        command.Parameters.AddWithValue("$id", signature.Id.ToString("N"));
        command.Parameters.AddWithValue("$accountId", signature.AccountId.ToString("N"));
        command.Parameters.AddWithValue("$name", signature.Name);
        command.Parameters.AddWithValue("$html", signature.Html);
        command.Parameters.AddWithValue("$plain", signature.PlainText);
        command.Parameters.AddWithValue("$rtf", signature.Rtf);
        command.Parameters.AddWithValue("$new", signature.IsDefaultForNew ? 1 : 0);
        command.Parameters.AddWithValue("$reply", signature.IsDefaultForReplies ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteSignatureAsync(Guid signatureId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteWithParameterAsync(connection, "DELETE FROM signatures WHERE id = $id", "$id", signatureId.ToString("N"), cancellationToken);
    }

    public async Task<IReadOnlyList<Draft>> GetDraftsAsync(Guid? accountId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, account_id, remote_id, to_json, cc_json, bcc_json, subject, html_body, plain_text_body,
                   attachments_json, reply_to_remote_id, provider_thread_id, updated_at, delivery_state, last_error, is_important, rtf_body
            FROM drafts
            """ + (accountId.HasValue ? " WHERE account_id=$accountId" : string.Empty) + " ORDER BY updated_at DESC";
        if (accountId.HasValue)
        {
            command.Parameters.AddWithValue("$accountId", accountId.Value.ToString("N"));
        }

        var result = new List<Draft>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadDraft(reader));
        }
        return result;
    }

    public async Task<Draft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, account_id, remote_id, to_json, cc_json, bcc_json, subject, html_body, plain_text_body,
                   attachments_json, reply_to_remote_id, provider_thread_id, updated_at, delivery_state, last_error, is_important, rtf_body
            FROM drafts WHERE id=$id
            """;
        command.Parameters.AddWithValue("$id", draftId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDraft(reader) : null;
    }

    public async Task UpsertDraftAsync(Draft draft, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO drafts (
                id, account_id, remote_id, to_json, cc_json, bcc_json, subject, html_body, plain_text_body,
                attachments_json, reply_to_remote_id, provider_thread_id, updated_at, delivery_state, last_error, is_important, rtf_body)
            VALUES (
                $id, $accountId, $remoteId, $to, $cc, $bcc, $subject, $html, $plain,
                $attachments, $replyTo, $providerThreadId, $updatedAt, $state, $lastError, $important, $rtf)
            ON CONFLICT(id) DO UPDATE SET
                account_id=excluded.account_id, remote_id=excluded.remote_id, to_json=excluded.to_json,
                cc_json=excluded.cc_json, bcc_json=excluded.bcc_json, subject=excluded.subject,
                html_body=excluded.html_body, plain_text_body=excluded.plain_text_body,
                attachments_json=excluded.attachments_json, reply_to_remote_id=excluded.reply_to_remote_id,
                provider_thread_id=excluded.provider_thread_id, updated_at=excluded.updated_at,
                delivery_state=excluded.delivery_state, last_error=excluded.last_error, is_important=excluded.is_important,
                rtf_body=excluded.rtf_body;
            """;
        command.Parameters.AddWithValue("$id", draft.Id.ToString("N"));
        command.Parameters.AddWithValue("$accountId", draft.AccountId.ToString("N"));
        command.Parameters.AddWithValue("$remoteId", DbValue(draft.RemoteId));
        command.Parameters.AddWithValue("$to", JsonSerializer.Serialize(draft.To, JsonOptions));
        command.Parameters.AddWithValue("$cc", JsonSerializer.Serialize(draft.Cc, JsonOptions));
        command.Parameters.AddWithValue("$bcc", JsonSerializer.Serialize(draft.Bcc, JsonOptions));
        command.Parameters.AddWithValue("$subject", draft.Subject);
        command.Parameters.AddWithValue("$html", draft.HtmlBody);
        command.Parameters.AddWithValue("$plain", draft.PlainTextBody);
        command.Parameters.AddWithValue("$attachments", JsonSerializer.Serialize(draft.Attachments, JsonOptions));
        command.Parameters.AddWithValue("$replyTo", DbValue(draft.ReplyToRemoteId));
        command.Parameters.AddWithValue("$providerThreadId", DbValue(draft.ProviderThreadId));
        command.Parameters.AddWithValue("$updatedAt", FormatDate(draft.UpdatedAt));
        command.Parameters.AddWithValue("$state", (int)draft.DeliveryState);
        command.Parameters.AddWithValue("$lastError", DbValue(draft.LastError));
        command.Parameters.AddWithValue("$important", draft.IsImportant ? 1 : 0);
        command.Parameters.AddWithValue("$rtf", draft.RtfBody);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteWithParameterAsync(connection, "DELETE FROM drafts WHERE id=$id", "$id", draftId.ToString("N"), cancellationToken);
    }

    public async Task<SyncCursor?> GetCursorAsync(Guid accountId, string scope, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value, updated_at FROM sync_cursors WHERE account_id = $accountId AND scope = $scope";
        command.Parameters.AddWithValue("$accountId", accountId.ToString("N"));
        command.Parameters.AddWithValue("$scope", scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SyncCursor(accountId, scope, reader.GetString(0), ParseDate(reader.GetString(1)))
            : null;
    }

    public async Task SetCursorAsync(SyncCursor cursor, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_cursors (account_id, scope, value, updated_at) VALUES ($accountId, $scope, $value, $updatedAt)
            ON CONFLICT(account_id, scope) DO UPDATE SET value=excluded.value, updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$accountId", cursor.AccountId.ToString("N"));
        command.Parameters.AddWithValue("$scope", cursor.Scope);
        command.Parameters.AddWithValue("$value", cursor.Value);
        command.Parameters.AddWithValue("$updatedAt", FormatDate(cursor.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnqueueAsync(PendingOperation operation, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pending_operations (id, account_id, kind, target_remote_id, payload_json, state, attempt_count, created_at, next_attempt_at, last_error)
            VALUES ($id, $accountId, $kind, $target, $payload, $state, $attempts, $created, $next, $error)
            ON CONFLICT(id) DO NOTHING;
            """;
        BindOperation(command, operation);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PendingOperation>> GetRunnableOperationsAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, account_id, kind, target_remote_id, payload_json, state, attempt_count, created_at, next_attempt_at, last_error
            FROM pending_operations
            WHERE account_id = $accountId AND state IN ($queued, $retry, $running) AND (next_attempt_at IS NULL OR next_attempt_at <= $now)
            ORDER BY created_at;
            """;
        command.Parameters.AddWithValue("$accountId", accountId.ToString("N"));
        command.Parameters.AddWithValue("$queued", (int)PendingOperationState.Queued);
        command.Parameters.AddWithValue("$retry", (int)PendingOperationState.WaitingForRetry);
        command.Parameters.AddWithValue("$running", (int)PendingOperationState.Running);
        command.Parameters.AddWithValue("$now", FormatDate(now));
        var result = new List<PendingOperation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadOperation(reader));
        }
        return result;
    }

    public async Task UpdateOperationAsync(PendingOperation operation, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE pending_operations SET state=$state, attempt_count=$attempts, next_attempt_at=$next, last_error=$error
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", operation.Id.ToString("N"));
        command.Parameters.AddWithValue("$state", (int)operation.State);
        command.Parameters.AddWithValue("$attempts", operation.AttemptCount);
        command.Parameters.AddWithValue("$next", DbValue(operation.NextAttemptAt));
        command.Parameters.AddWithValue("$error", DbValue(operation.LastError));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ApplyOptimisticOperationAsync(PendingOperation operation, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var (setMask, clearMask) = operation.Kind switch
        {
            PendingOperationKind.MarkRead => ((int)MailFlags.Read, 0),
            PendingOperationKind.MarkUnread => (0, (int)MailFlags.Read),
            PendingOperationKind.Star => ((int)MailFlags.Starred, 0),
            PendingOperationKind.Unstar => (0, (int)MailFlags.Starred),
            PendingOperationKind.Delete => ((int)MailFlags.Deleted, 0),
            _ => (0, 0)
        };

        if (setMask != 0 || clearMask != 0)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE messages SET flags = (flags | $setMask) & ~$clearMask WHERE account_id=$accountId AND remote_id=$remoteId";
            command.Parameters.AddWithValue("$setMask", setMask);
            command.Parameters.AddWithValue("$clearMask", clearMask);
            command.Parameters.AddWithValue("$accountId", operation.AccountId.ToString("N"));
            command.Parameters.AddWithValue("$remoteId", operation.TargetRemoteId);
            await command.ExecuteNonQueryAsync(cancellationToken);

            await using var aggregate = connection.CreateCommand();
            aggregate.CommandText = """
                UPDATE conversations SET
                    message_count = (SELECT COUNT(*) FROM messages m WHERE m.conversation_id=conversations.id AND (m.flags & $deleted)=0),
                    unread_count = (SELECT COUNT(*) FROM messages m WHERE m.conversation_id=conversations.id AND (m.flags & $read)=0 AND (m.flags & $deleted)=0),
                    is_starred = CASE WHEN EXISTS (SELECT 1 FROM messages m WHERE m.conversation_id=conversations.id AND (m.flags & $starred)!=0 AND (m.flags & $deleted)=0) THEN 1 ELSE 0 END,
                    has_attachments = CASE WHEN EXISTS (
                        SELECT 1 FROM messages m JOIN attachments a ON a.message_id=m.id
                        WHERE m.conversation_id=conversations.id AND (m.flags & $deleted)=0
                    ) THEN 1 ELSE 0 END
                WHERE account_id=$accountId AND id IN (
                    SELECT conversation_id FROM messages WHERE account_id=$accountId AND remote_id=$remoteId
                );
                """;
            aggregate.Parameters.AddWithValue("$read", (int)MailFlags.Read);
            aggregate.Parameters.AddWithValue("$deleted", (int)MailFlags.Deleted);
            aggregate.Parameters.AddWithValue("$starred", (int)MailFlags.Starred);
            aggregate.Parameters.AddWithValue("$accountId", operation.AccountId.ToString("N"));
            aggregate.Parameters.AddWithValue("$remoteId", operation.TargetRemoteId);
            await aggregate.ExecuteNonQueryAsync(cancellationToken);
        }

        if (operation.Kind is PendingOperationKind.Move or PendingOperationKind.Archive)
        {
            using var payload = JsonDocument.Parse(operation.PayloadJson);
            if (payload.RootElement.TryGetProperty("folderId", out var folderId) &&
                folderId.ValueKind == JsonValueKind.String &&
                Guid.TryParseExact(folderId.GetString(), "N", out _))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE messages SET folder_id=$folderId WHERE account_id=$accountId AND remote_id=$remoteId";
                command.Parameters.AddWithValue("$folderId", folderId.GetString() ?? string.Empty);
                command.Parameters.AddWithValue("$accountId", operation.AccountId.ToString("N"));
                command.Parameters.AddWithValue("$remoteId", operation.TargetRemoteId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    public async Task PurgeCompletedOperationsAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM pending_operations WHERE state=$completed AND created_at < $olderThan";
        command.Parameters.AddWithValue("$completed", (int)PendingOperationState.Completed);
        command.Parameters.AddWithValue("$olderThan", FormatDate(olderThan));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CancelPendingOperationsAsync(Guid accountId, PendingOperationKind kind, string targetRemoteId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM pending_operations
            WHERE account_id=$accountId AND kind=$kind AND target_remote_id=$target
              AND state IN ($queued, $retry, $failed);
            """;
        command.Parameters.AddWithValue("$accountId", accountId.ToString("N"));
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$target", targetRemoteId);
        command.Parameters.AddWithValue("$queued", (int)PendingOperationState.Queued);
        command.Parameters.AddWithValue("$retry", (int)PendingOperationState.WaitingForRetry);
        command.Parameters.AddWithValue("$failed", (int)PendingOperationState.Failed);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MailConversation>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return await GetConversationsAsync(request.AccountId, request.FolderId, request.Limit, cancellationToken);
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var filters = new List<string> { "messages_fts MATCH $query", "(matched.flags & $deleted) = 0" };
        command.Parameters.AddWithValue("$deleted", (int)MailFlags.Deleted);
        if (request.AccountId.HasValue)
        {
            filters.Add("c.account_id = $accountId");
            command.Parameters.AddWithValue("$accountId", request.AccountId.Value.ToString("N"));
        }
        if (request.FolderId.HasValue)
        {
            filters.Add("EXISTS (SELECT 1 FROM messages m WHERE m.conversation_id=c.id AND m.folder_id=$folderId)");
            command.Parameters.AddWithValue("$folderId", request.FolderId.Value.ToString("N"));
        }
        command.CommandText = $"""
            SELECT DISTINCT c.id, c.account_id, c.thread_key, c.provider_thread_id, c.subject, c.snippet,
                   c.participants_json, c.last_message_at, c.message_count, c.unread_count, c.is_starred,
                   c.has_attachments, c.labels_json
            FROM messages_fts
            JOIN conversations c ON c.id = messages_fts.conversation_id
            JOIN messages matched ON matched.account_id = messages_fts.account_id AND matched.remote_id = messages_fts.remote_id
            WHERE {string.Join(" AND ", filters)}
            ORDER BY c.last_message_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", ToFtsQuery(request.Text));
        command.Parameters.AddWithValue("$limit", Math.Clamp(request.Limit, 1, 500));
        return await ReadConversationsAsync(command, cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (encryptionKeyHex is null)
        {
            throw new InvalidOperationException("The mail store must be initialized before use.");
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, $"PRAGMA key = \"x'{encryptionKeyHex}'\"; PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;", cancellationToken);
        return connection;
    }

    private static async Task UpsertFolderAsync(SqliteConnection connection, MailFolder folder, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO folders (id, account_id, remote_id, name, special_kind, unread_count, total_count, parent_remote_id)
            VALUES ($id, $accountId, $remoteId, $name, $kind, $unread, $total, $parent)
            ON CONFLICT(account_id, remote_id) DO UPDATE SET name=excluded.name, special_kind=excluded.special_kind,
                unread_count=excluded.unread_count, total_count=excluded.total_count, parent_remote_id=excluded.parent_remote_id;
            """;
        command.Parameters.AddWithValue("$id", folder.Id.ToString("N"));
        command.Parameters.AddWithValue("$accountId", folder.AccountId.ToString("N"));
        command.Parameters.AddWithValue("$remoteId", folder.RemoteId);
        command.Parameters.AddWithValue("$name", folder.Name);
        command.Parameters.AddWithValue("$kind", (int)folder.SpecialKind);
        command.Parameters.AddWithValue("$unread", folder.UnreadCount);
        command.Parameters.AddWithValue("$total", folder.TotalCount);
        command.Parameters.AddWithValue("$parent", DbValue(folder.ParentRemoteId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertConversationAsync(SqliteConnection connection, MailConversation conversation, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversations (id, account_id, thread_key, provider_thread_id, subject, snippet, participants_json,
                last_message_at, message_count, unread_count, is_starred, has_attachments, labels_json)
            VALUES ($id, $accountId, $threadKey, $providerThreadId, $subject, $snippet, $participants, $lastMessageAt,
                $messageCount, $unreadCount, $starred, $attachments, $labels)
            ON CONFLICT(id) DO UPDATE SET provider_thread_id=excluded.provider_thread_id,
                subject=excluded.subject, snippet=excluded.snippet, participants_json=excluded.participants_json,
                last_message_at=excluded.last_message_at, message_count=excluded.message_count, unread_count=excluded.unread_count,
                is_starred=excluded.is_starred, has_attachments=excluded.has_attachments, labels_json=excluded.labels_json;
            """;
        command.Parameters.AddWithValue("$id", conversation.Id.ToString("N"));
        command.Parameters.AddWithValue("$accountId", conversation.AccountId.ToString("N"));
        command.Parameters.AddWithValue("$threadKey", conversation.ThreadKey);
        command.Parameters.AddWithValue("$providerThreadId", DbValue(conversation.ProviderThreadId));
        command.Parameters.AddWithValue("$subject", conversation.Subject);
        command.Parameters.AddWithValue("$snippet", conversation.Snippet);
        command.Parameters.AddWithValue("$participants", JsonSerializer.Serialize(conversation.Participants, JsonOptions));
        command.Parameters.AddWithValue("$lastMessageAt", FormatDate(conversation.LastMessageAt));
        command.Parameters.AddWithValue("$messageCount", conversation.MessageCount);
        command.Parameters.AddWithValue("$unreadCount", conversation.UnreadCount);
        command.Parameters.AddWithValue("$starred", conversation.IsStarred ? 1 : 0);
        command.Parameters.AddWithValue("$attachments", conversation.HasAttachments ? 1 : 0);
        command.Parameters.AddWithValue("$labels", JsonSerializer.Serialize(conversation.Labels, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertMessageAsync(SqliteConnection connection, MailMessage message, CancellationToken cancellationToken)
    {
        var existed = false;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = "SELECT EXISTS(SELECT 1 FROM messages WHERE account_id=$accountId AND remote_id=$remoteId)";
            lookup.Parameters.AddWithValue("$accountId", message.AccountId.ToString("N"));
            lookup.Parameters.AddWithValue("$remoteId", message.RemoteId);
            existed = Convert.ToInt32(await lookup.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO messages (id, account_id, folder_id, conversation_id, remote_id, provider_thread_id, internet_message_id,
                in_reply_to, references_json, from_json, to_json, cc_json, bcc_json, subject, snippet, text_body, html_body,
                sent_at, received_at, flags, labels_json)
            VALUES ($id, $accountId, $folderId, $conversationId, $remoteId, $providerThreadId, $internetMessageId, $inReplyTo,
                $references, $from, $to, $cc, $bcc, $subject, $snippet, $textBody, $htmlBody, $sentAt, $receivedAt, $flags, $labels)
            ON CONFLICT(account_id, remote_id) DO UPDATE SET folder_id=excluded.folder_id, conversation_id=excluded.conversation_id,
                provider_thread_id=excluded.provider_thread_id, internet_message_id=excluded.internet_message_id,
                in_reply_to=excluded.in_reply_to, references_json=excluded.references_json, from_json=excluded.from_json,
                to_json=excluded.to_json, cc_json=excluded.cc_json, bcc_json=excluded.bcc_json, subject=excluded.subject,
                snippet=excluded.snippet, text_body=COALESCE(excluded.text_body, messages.text_body),
                html_body=COALESCE(excluded.html_body, messages.html_body), sent_at=excluded.sent_at,
                received_at=excluded.received_at, flags=excluded.flags, labels_json=excluded.labels_json;
            """;
        command.Parameters.AddWithValue("$id", message.Id.ToString("N"));
        command.Parameters.AddWithValue("$accountId", message.AccountId.ToString("N"));
        command.Parameters.AddWithValue("$folderId", message.FolderId.ToString("N"));
        command.Parameters.AddWithValue("$conversationId", message.ConversationId.ToString("N"));
        command.Parameters.AddWithValue("$remoteId", message.RemoteId);
        command.Parameters.AddWithValue("$providerThreadId", DbValue(message.ProviderThreadId));
        command.Parameters.AddWithValue("$internetMessageId", DbValue(message.InternetMessageId));
        command.Parameters.AddWithValue("$inReplyTo", DbValue(message.InReplyTo));
        command.Parameters.AddWithValue("$references", JsonSerializer.Serialize(message.References, JsonOptions));
        command.Parameters.AddWithValue("$from", JsonSerializer.Serialize(message.From, JsonOptions));
        command.Parameters.AddWithValue("$to", JsonSerializer.Serialize(message.To, JsonOptions));
        command.Parameters.AddWithValue("$cc", JsonSerializer.Serialize(message.Cc, JsonOptions));
        command.Parameters.AddWithValue("$bcc", JsonSerializer.Serialize(message.Bcc, JsonOptions));
        command.Parameters.AddWithValue("$subject", message.Subject);
        command.Parameters.AddWithValue("$snippet", message.Snippet);
        command.Parameters.AddWithValue("$textBody", DbValue(message.TextBody));
        command.Parameters.AddWithValue("$htmlBody", DbValue(message.HtmlBody));
        command.Parameters.AddWithValue("$sentAt", FormatDate(message.SentAt));
        command.Parameters.AddWithValue("$receivedAt", FormatDate(message.ReceivedAt));
        command.Parameters.AddWithValue("$flags", (int)message.Flags);
        command.Parameters.AddWithValue("$labels", JsonSerializer.Serialize(message.Labels, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);

        await DeleteFtsEntryAsync(connection, message.AccountId, message.RemoteId, cancellationToken);

        var indexBody = message.TextBody ?? message.HtmlBody;
        if (indexBody is null)
        {
            await using var bodyLookup = connection.CreateCommand();
            bodyLookup.CommandText = "SELECT COALESCE(text_body, html_body, '') FROM messages WHERE account_id=$accountId AND remote_id=$remoteId";
            bodyLookup.Parameters.AddWithValue("$accountId", message.AccountId.ToString("N"));
            bodyLookup.Parameters.AddWithValue("$remoteId", message.RemoteId);
            indexBody = await bodyLookup.ExecuteScalarAsync(cancellationToken) as string;
        }
        await using var insertFts = connection.CreateCommand();
        insertFts.CommandText = "INSERT INTO messages_fts (account_id, conversation_id, remote_id, subject, sender, recipients, body) VALUES ($accountId, $conversationId, $remoteId, $subject, $sender, $recipients, $body)";
        insertFts.Parameters.AddWithValue("$accountId", message.AccountId.ToString("N"));
        insertFts.Parameters.AddWithValue("$conversationId", message.ConversationId.ToString("N"));
        insertFts.Parameters.AddWithValue("$remoteId", message.RemoteId);
        insertFts.Parameters.AddWithValue("$subject", message.Subject);
        insertFts.Parameters.AddWithValue("$sender", $"{message.From.Name} {message.From.Address}");
        insertFts.Parameters.AddWithValue("$recipients", string.Join(' ', message.To.Concat(message.Cc).Select(static x => $"{x.Name} {x.Address}")));
        insertFts.Parameters.AddWithValue("$body", $"{indexBody} {message.Snippet}");
        await insertFts.ExecuteNonQueryAsync(cancellationToken);
        await using (var rowIdCommand = connection.CreateCommand())
        {
            rowIdCommand.CommandText = "INSERT OR REPLACE INTO messages_fts_map (account_id, remote_id, row_id) VALUES ($accountId, $remoteId, last_insert_rowid())";
            rowIdCommand.Parameters.AddWithValue("$accountId", message.AccountId.ToString("N"));
            rowIdCommand.Parameters.AddWithValue("$remoteId", message.RemoteId);
            await rowIdCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (message.Attachments.Count > 0 || message.TextBody is not null || message.HtmlBody is not null)
        {
            var cachedPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            if (existed)
            {
                await using (var cachedLookup = connection.CreateCommand())
                {
                    cachedLookup.CommandText = "SELECT remote_id, cached_path FROM attachments WHERE message_id=$id AND cached_path IS NOT NULL";
                    cachedLookup.Parameters.AddWithValue("$id", message.Id.ToString("N"));
                    await using var cachedReader = await cachedLookup.ExecuteReaderAsync(cancellationToken);
                    while (await cachedReader.ReadAsync(cancellationToken)) cachedPaths[cachedReader.GetString(0)] = cachedReader.GetString(1);
                }
                await ExecuteWithParameterAsync(connection, "DELETE FROM attachments WHERE message_id=$id", "$id", message.Id.ToString("N"), cancellationToken);
            }
            foreach (var attachment in message.Attachments)
            {
                await using var attachmentCommand = connection.CreateCommand();
                attachmentCommand.CommandText = "INSERT INTO attachments (id, message_id, remote_id, file_name, content_type, size, is_inline, content_id, cached_path) VALUES ($id, $messageId, $remoteId, $fileName, $contentType, $size, $inline, $contentId, $cachedPath)";
                attachmentCommand.Parameters.AddWithValue("$id", attachment.Id.ToString("N"));
                attachmentCommand.Parameters.AddWithValue("$messageId", attachment.MessageId.ToString("N"));
                attachmentCommand.Parameters.AddWithValue("$remoteId", attachment.RemoteId);
                attachmentCommand.Parameters.AddWithValue("$fileName", attachment.FileName);
                attachmentCommand.Parameters.AddWithValue("$contentType", attachment.ContentType);
                attachmentCommand.Parameters.AddWithValue("$size", attachment.Size);
                attachmentCommand.Parameters.AddWithValue("$inline", attachment.IsInline ? 1 : 0);
                attachmentCommand.Parameters.AddWithValue("$contentId", DbValue(attachment.ContentId));
                attachmentCommand.Parameters.AddWithValue("$cachedPath", DbValue(attachment.CachedPath ?? cachedPaths.GetValueOrDefault(attachment.RemoteId)));
                await attachmentCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task RecomputeConversationAsync(SqliteConnection connection, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE conversations SET
                message_count = (SELECT COUNT(*) FROM messages m WHERE m.conversation_id=$id AND (m.flags & $deleted)=0),
                unread_count = (SELECT COUNT(*) FROM messages m WHERE m.conversation_id=$id AND (m.flags & $deleted)=0 AND (m.flags & $read)=0),
                is_starred = CASE WHEN EXISTS (SELECT 1 FROM messages m WHERE m.conversation_id=$id AND (m.flags & $deleted)=0 AND (m.flags & $starred)!=0) THEN 1 ELSE 0 END,
                has_attachments = CASE WHEN EXISTS (
                    SELECT 1 FROM messages m JOIN attachments a ON a.message_id=m.id
                    WHERE m.conversation_id=$id AND (m.flags & $deleted)=0
                ) THEN 1 ELSE 0 END,
                last_message_at = COALESCE((SELECT MAX(m.received_at) FROM messages m WHERE m.conversation_id=$id AND (m.flags & $deleted)=0), last_message_at),
                subject = COALESCE((SELECT m.subject FROM messages m WHERE m.conversation_id=$id AND (m.flags & $deleted)=0 ORDER BY m.received_at DESC LIMIT 1), subject),
                snippet = COALESCE((SELECT m.snippet FROM messages m WHERE m.conversation_id=$id AND (m.flags & $deleted)=0 ORDER BY m.received_at DESC LIMIT 1), snippet)
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", conversationId.ToString("N"));
        command.Parameters.AddWithValue("$deleted", (int)MailFlags.Deleted);
        command.Parameters.AddWithValue("$read", (int)MailFlags.Read);
        command.Parameters.AddWithValue("$starred", (int)MailFlags.Starred);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteFtsEntryAsync(SqliteConnection connection, Guid accountId, string remoteId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM messages_fts WHERE rowid = (
                SELECT row_id FROM messages_fts_map WHERE account_id=$accountId AND remote_id=$remoteId
            );
            DELETE FROM messages_fts_map WHERE account_id=$accountId AND remote_id=$remoteId;
            """;
        command.Parameters.AddWithValue("$accountId", accountId.ToString("N"));
        command.Parameters.AddWithValue("$remoteId", remoteId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<MailAttachment>>> LoadAttachmentsAsync(SqliteConnection connection, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, a.message_id, a.remote_id, a.file_name, a.content_type, a.size, a.is_inline, a.content_id, a.cached_path
            FROM attachments a JOIN messages m ON m.id=a.message_id WHERE m.conversation_id=$conversationId;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("N"));
        var grouped = new Dictionary<Guid, List<MailAttachment>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var messageId = ParseGuid(reader.GetString(1));
            if (!grouped.TryGetValue(messageId, out var list))
            {
                list = new List<MailAttachment>();
                grouped[messageId] = list;
            }
            list.Add(new MailAttachment
            {
                Id = ParseGuid(reader.GetString(0)),
                MessageId = messageId,
                RemoteId = reader.GetString(2),
                FileName = reader.GetString(3),
                ContentType = reader.GetString(4),
                Size = reader.GetInt64(5),
                IsInline = reader.GetBoolean(6),
                ContentId = NullableString(reader, 7),
                CachedPath = NullableString(reader, 8)
            });
        }
        return grouped.ToDictionary(static pair => pair.Key, static pair => (IReadOnlyList<MailAttachment>)pair.Value);
    }

    private static async Task<IReadOnlyList<MailConversation>> ReadConversationsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var result = new List<MailConversation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new MailConversation
            {
                Id = ParseGuid(reader.GetString(0)),
                AccountId = ParseGuid(reader.GetString(1)),
                ThreadKey = reader.GetString(2),
                ProviderThreadId = NullableString(reader, 3),
                Subject = reader.GetString(4),
                Snippet = reader.GetString(5),
                Participants = Deserialize<MailAddress[]>(reader.GetString(6)),
                LastMessageAt = ParseDate(reader.GetString(7)),
                MessageCount = reader.GetInt32(8),
                UnreadCount = reader.GetInt32(9),
                IsStarred = reader.GetBoolean(10),
                HasAttachments = reader.GetBoolean(11),
                Labels = Deserialize<string[]>(reader.GetString(12))
            });
        }
        return result;
    }

    private static MailAccount ReadAccount(SqliteDataReader reader) => new()
    {
        Id = ParseGuid(reader.GetString(0)),
        Provider = (ProviderKind)reader.GetInt32(1),
        Email = reader.GetString(2),
        DisplayName = reader.GetString(3),
        Color = reader.GetString(4),
        IsEnabled = reader.GetBoolean(5),
        IsDemo = reader.GetBoolean(6),
        LastSuccessfulSync = reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)),
        LastSyncError = NullableString(reader, 8),
        Settings = Deserialize<Dictionary<string, string>>(reader.GetString(9))
    };

    private static PendingOperation ReadOperation(SqliteDataReader reader) => new()
    {
        Id = ParseGuid(reader.GetString(0)),
        AccountId = ParseGuid(reader.GetString(1)),
        Kind = (PendingOperationKind)reader.GetInt32(2),
        TargetRemoteId = reader.GetString(3),
        PayloadJson = reader.GetString(4),
        State = (PendingOperationState)reader.GetInt32(5),
        AttemptCount = reader.GetInt32(6),
        CreatedAt = ParseDate(reader.GetString(7)),
        NextAttemptAt = reader.IsDBNull(8) ? null : ParseDate(reader.GetString(8)),
        LastError = NullableString(reader, 9)
    };

    private static void BindOperation(SqliteCommand command, PendingOperation operation)
    {
        command.Parameters.AddWithValue("$id", operation.Id.ToString("N"));
        command.Parameters.AddWithValue("$accountId", operation.AccountId.ToString("N"));
        command.Parameters.AddWithValue("$kind", (int)operation.Kind);
        command.Parameters.AddWithValue("$target", operation.TargetRemoteId);
        command.Parameters.AddWithValue("$payload", operation.PayloadJson);
        command.Parameters.AddWithValue("$state", (int)operation.State);
        command.Parameters.AddWithValue("$attempts", operation.AttemptCount);
        command.Parameters.AddWithValue("$created", FormatDate(operation.CreatedAt));
        command.Parameters.AddWithValue("$next", DbValue(operation.NextAttemptAt));
        command.Parameters.AddWithValue("$error", DbValue(operation.LastError));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> GetUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var version = await GetUserVersionAsync(connection, cancellationToken);
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"This mailbox database was created by a newer Inboxwell version ({version}).");
        }

        if (version == 0)
        {
            await ExecuteAsync(connection, Schema, cancellationToken);
            version = 1;
        }

        if (version < 2)
        {
            await ExecuteAsync(connection, MigrationV2, cancellationToken);
            version = 2;
        }

        if (version < 3)
        {
            await ExecuteAsync(connection, MigrationV3, cancellationToken);
            version = 3;
        }

        if (version < 4)
        {
            await ExecuteAsync(connection, MigrationV4, cancellationToken);
            version = 4;
        }

        if (version < 5)
        {
            await ExecuteAsync(connection, MigrationV5, cancellationToken);
            version = 5;
        }

        if (version < 6)
        {
            await ExecuteAsync(connection, MigrationV6, cancellationToken);
            version = 6;
        }

        if (version < 7)
        {
            await ExecuteAsync(connection, MigrationV7, cancellationToken);
            version = 7;
        }

        if (version < 8)
        {
            await ExecuteAsync(connection, MigrationV8, cancellationToken);
        }
    }

    private static Draft ReadDraft(SqliteDataReader reader) => new()
    {
        Id = ParseGuid(reader.GetString(0)),
        AccountId = ParseGuid(reader.GetString(1)),
        RemoteId = NullableString(reader, 2),
        To = Deserialize<MailAddress[]>(reader.GetString(3)),
        Cc = Deserialize<MailAddress[]>(reader.GetString(4)),
        Bcc = Deserialize<MailAddress[]>(reader.GetString(5)),
        Subject = reader.GetString(6),
        HtmlBody = reader.GetString(7),
        PlainTextBody = reader.GetString(8),
        Attachments = Deserialize<OutgoingAttachment[]>(reader.GetString(9)),
        ReplyToRemoteId = NullableString(reader, 10),
        ProviderThreadId = NullableString(reader, 11),
        UpdatedAt = ParseDate(reader.GetString(12)),
        DeliveryState = (DraftDeliveryState)reader.GetInt32(13),
        LastError = NullableString(reader, 14),
        IsImportant = reader.GetBoolean(15),
        RtfBody = reader.GetString(16)
    };

    private static async Task ExecuteWithParameterAsync(SqliteConnection connection, string sql, string parameterName, object value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(parameterName, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions) ??
        (typeof(T).IsArray ? (T)(object)Array.CreateInstance(typeof(T).GetElementType()!, 0) : Activator.CreateInstance<T>());

    private static Guid ParseGuid(string value) => Guid.ParseExact(value, "N");
    private static string FormatDate(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static object DbValue(string? value) => value is null ? DBNull.Value : value;
    private static object DbValue(DateTimeOffset? value) => value.HasValue ? FormatDate(value.Value) : DBNull.Value;

    private static string ToFtsQuery(string text)
    {
        var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static term => $"\"{term.Replace("\"", "\"\"")}\"*");
        return string.Join(" AND ", terms);
    }

    private const string Schema = """
        PRAGMA user_version = 1;

        CREATE TABLE IF NOT EXISTS accounts (
            id TEXT PRIMARY KEY,
            provider INTEGER NOT NULL,
            email TEXT NOT NULL,
            display_name TEXT NOT NULL,
            color TEXT NOT NULL,
            is_enabled INTEGER NOT NULL,
            is_demo INTEGER NOT NULL DEFAULT 0,
            last_sync TEXT,
            last_error TEXT,
            settings_json TEXT NOT NULL DEFAULT '{}'
        );

        CREATE TABLE IF NOT EXISTS folders (
            id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            remote_id TEXT NOT NULL,
            name TEXT NOT NULL,
            special_kind INTEGER NOT NULL,
            unread_count INTEGER NOT NULL DEFAULT 0,
            total_count INTEGER NOT NULL DEFAULT 0,
            parent_remote_id TEXT,
            UNIQUE(account_id, remote_id)
        );

        CREATE TABLE IF NOT EXISTS conversations (
            id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            thread_key TEXT NOT NULL,
            provider_thread_id TEXT,
            subject TEXT NOT NULL,
            snippet TEXT NOT NULL,
            participants_json TEXT NOT NULL,
            last_message_at TEXT NOT NULL,
            message_count INTEGER NOT NULL,
            unread_count INTEGER NOT NULL,
            is_starred INTEGER NOT NULL,
            has_attachments INTEGER NOT NULL,
            labels_json TEXT NOT NULL,
            UNIQUE(account_id, thread_key)
        );

        CREATE TABLE IF NOT EXISTS messages (
            id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            folder_id TEXT NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
            conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
            remote_id TEXT NOT NULL,
            provider_thread_id TEXT,
            internet_message_id TEXT,
            in_reply_to TEXT,
            references_json TEXT NOT NULL,
            from_json TEXT NOT NULL,
            to_json TEXT NOT NULL,
            cc_json TEXT NOT NULL,
            bcc_json TEXT NOT NULL,
            subject TEXT NOT NULL,
            snippet TEXT NOT NULL,
            text_body TEXT,
            html_body TEXT,
            sent_at TEXT NOT NULL,
            received_at TEXT NOT NULL,
            flags INTEGER NOT NULL,
            labels_json TEXT NOT NULL,
            UNIQUE(account_id, remote_id)
        );

        CREATE TABLE IF NOT EXISTS attachments (
            id TEXT PRIMARY KEY,
            message_id TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
            remote_id TEXT NOT NULL,
            file_name TEXT NOT NULL,
            content_type TEXT NOT NULL,
            size INTEGER NOT NULL,
            is_inline INTEGER NOT NULL,
            content_id TEXT,
            cached_path TEXT
        );

        CREATE TABLE IF NOT EXISTS signatures (
            id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            name TEXT NOT NULL,
            html TEXT NOT NULL,
            plain_text TEXT NOT NULL,
            default_new INTEGER NOT NULL,
            default_reply INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS sync_cursors (
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            scope TEXT NOT NULL,
            value TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY(account_id, scope)
        );

        CREATE TABLE IF NOT EXISTS pending_operations (
            id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            kind INTEGER NOT NULL,
            target_remote_id TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            state INTEGER NOT NULL,
            attempt_count INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            next_attempt_at TEXT,
            last_error TEXT
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
            account_id UNINDEXED,
            conversation_id UNINDEXED,
            remote_id UNINDEXED,
            subject,
            sender,
            recipients,
            body,
            tokenize='unicode61 remove_diacritics 2'
        );

        CREATE INDEX IF NOT EXISTS ix_folders_account ON folders(account_id);
        CREATE INDEX IF NOT EXISTS ix_conversations_account_time ON conversations(account_id, last_message_at DESC);
        CREATE INDEX IF NOT EXISTS ix_messages_conversation ON messages(conversation_id, received_at);
        CREATE INDEX IF NOT EXISTS ix_messages_folder ON messages(folder_id, received_at DESC);
        CREATE INDEX IF NOT EXISTS ix_pending_account_state ON pending_operations(account_id, state, next_attempt_at);
        """;

    private const string MigrationV2 = """
        CREATE TABLE IF NOT EXISTS drafts (
            id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            remote_id TEXT,
            to_json TEXT NOT NULL,
            cc_json TEXT NOT NULL,
            bcc_json TEXT NOT NULL,
            subject TEXT NOT NULL,
            html_body TEXT NOT NULL,
            plain_text_body TEXT NOT NULL,
            attachments_json TEXT NOT NULL,
            reply_to_remote_id TEXT,
            provider_thread_id TEXT,
            updated_at TEXT NOT NULL,
            delivery_state INTEGER NOT NULL DEFAULT 0,
            last_error TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_drafts_account_updated ON drafts(account_id, updated_at DESC);
        PRAGMA user_version = 2;
        """;

    // SQLite needs an index on every child foreign-key column for fast cascades.
    // Without these, removing a mailbox repeatedly scans the full message and
    // attachment tables and becomes progressively slower as mail is downloaded.
    private const string MigrationV3 = """
        CREATE INDEX IF NOT EXISTS ix_messages_account ON messages(account_id);
        CREATE INDEX IF NOT EXISTS ix_attachments_message ON attachments(message_id);
        CREATE INDEX IF NOT EXISTS ix_signatures_account ON signatures(account_id);
        PRAGMA user_version = 3;
        """;

    private const string MigrationV4 = """
        ALTER TABLE drafts ADD COLUMN is_important INTEGER NOT NULL DEFAULT 0;
        PRAGMA user_version = 4;
        """;

    private const string MigrationV5 = """
        ALTER TABLE drafts ADD COLUMN rtf_body TEXT NOT NULL DEFAULT '';
        PRAGMA user_version = 5;
        """;

    private const string MigrationV6 = """
        CREATE TABLE IF NOT EXISTS messages_fts_map (
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            remote_id TEXT NOT NULL,
            row_id INTEGER NOT NULL UNIQUE,
            PRIMARY KEY(account_id, remote_id)
        );
        INSERT OR REPLACE INTO messages_fts_map (account_id, remote_id, row_id)
            SELECT account_id, remote_id, rowid FROM messages_fts;
        PRAGMA user_version = 6;
        """;

    // Unified folders filter conversations through their messages. The earlier
    // folder/date index could not answer that membership test and became
    // quadratic on a large Inbox.
    private const string MigrationV7 = """
        CREATE INDEX IF NOT EXISTS ix_messages_folder_conversation ON messages(folder_id, conversation_id);
        CREATE INDEX IF NOT EXISTS ix_conversations_time ON conversations(last_message_at DESC);
        PRAGMA user_version = 7;
        """;

    private const string MigrationV8 = """
        ALTER TABLE signatures ADD COLUMN rtf TEXT NOT NULL DEFAULT '';
        PRAGMA user_version = 8;
        """;
}
