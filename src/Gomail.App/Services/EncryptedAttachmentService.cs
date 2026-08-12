using System.Collections.Concurrent;
using System.Security.Cryptography;
using Gomail.Core;
using Windows.Storage;
using Windows.System;

namespace Gomail_App.Services;

public interface IAttachmentService
{
    Task OpenAsync(MailMessage message, MailAttachment attachment, CancellationToken cancellationToken = default);
    Task SaveAsAsync(MailMessage message, MailAttachment attachment, string destinationPath, CancellationToken cancellationToken = default);
    Task<string> ResolveInlineImagesAsync(MailMessage message, string html, CancellationToken cancellationToken = default);
    Task CleanTemporaryFilesAsync(CancellationToken cancellationToken = default);
    Task ClearEncryptedCacheAsync(CancellationToken cancellationToken = default);
}

public sealed class EncryptedAttachmentService : IAttachmentService
{
    private static readonly byte[] Magic = "GMA1"u8.ToArray();
    private const long MaximumCachedAttachmentBytes = 150L * 1024 * 1024;
    private readonly IMailStore store;
    private readonly IMailProviderRegistry providers;
    private readonly ISecretStore secrets;
    private readonly string cacheDirectory;
    private readonly string temporaryDirectory;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();

    public EncryptedAttachmentService(IMailStore store, IMailProviderRegistry providers, ISecretStore secrets)
    {
        this.store = store;
        this.providers = providers;
        this.secrets = secrets;
        cacheDirectory = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "attachments");
        temporaryDirectory = Path.Combine(ApplicationData.Current.TemporaryFolder.Path, "Gomail", "attachments");
        Directory.CreateDirectory(cacheDirectory);
        Directory.CreateDirectory(temporaryDirectory);
    }

    public async Task OpenAsync(MailMessage message, MailAttachment attachment, CancellationToken cancellationToken = default)
    {
        var plainBytes = await GetPlainBytesAsync(message, attachment, cancellationToken);
        var safeName = SanitizeFileName(attachment.FileName);
        var path = Path.Combine(temporaryDirectory, $"{attachment.Id:N}-{Guid.NewGuid():N}-{safeName}");
        await File.WriteAllBytesAsync(path, plainBytes, cancellationToken);
        var file = await StorageFile.GetFileFromPathAsync(path);
        if (!await Launcher.LaunchFileAsync(file))
        {
            throw new InvalidOperationException("Windows could not find an app that can open this file type.");
        }
    }

    public async Task SaveAsAsync(MailMessage message, MailAttachment attachment, string destinationPath, CancellationToken cancellationToken = default)
    {
        var plainBytes = await GetPlainBytesAsync(message, attachment, cancellationToken);
        await File.WriteAllBytesAsync(destinationPath, plainBytes, cancellationToken);
    }

    public async Task<string> ResolveInlineImagesAsync(MailMessage message, string html, CancellationToken cancellationToken = default)
    {
        var resolved = html;
        foreach (var attachment in message.Attachments.Where(static item => item.IsInline && !string.IsNullOrWhiteSpace(item.ContentId) && item.Size <= 10L * 1024 * 1024))
        {
            var marker = "cid:" + attachment.ContentId!.Trim('<', '>');
            if (!resolved.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;
            var bytes = await GetPlainBytesAsync(message, attachment, cancellationToken);
            var dataUri = $"data:{attachment.ContentType};base64,{Convert.ToBase64String(bytes)}";
            resolved = resolved.Replace(marker, dataUri, StringComparison.OrdinalIgnoreCase);
        }
        return resolved;
    }

    public Task CleanTemporaryFilesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(temporaryDirectory)) return Task.CompletedTask;
        foreach (var path in Directory.EnumerateFiles(temporaryDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddHours(-12)) File.Delete(path);
            }
            catch (IOException)
            {
                // Another process may still be viewing the file; try again on the next launch.
            }
            catch (UnauthorizedAccessException)
            {
                // The external viewer may still hold the file.
            }
        }
        return Task.CompletedTask;
    }

    public Task ClearEncryptedCacheAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(cacheDirectory)) return Task.CompletedTask;
        foreach (var path in Directory.EnumerateFiles(cacheDirectory, "*.gma", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return Task.CompletedTask;
    }

    private async Task<byte[]> GetPlainBytesAsync(MailMessage message, MailAttachment attachment, CancellationToken cancellationToken)
    {
        if (attachment.Size > MaximumCachedAttachmentBytes)
        {
            throw new InvalidOperationException("This attachment is larger than Inboxwell's 150 MB local safety limit.");
        }

        var gate = locks.GetOrAdd(attachment.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var encryptedPath = GetEncryptedPath(attachment);
            if (!File.Exists(encryptedPath))
            {
                await DownloadAndEncryptAsync(message, attachment, encryptedPath, cancellationToken);
            }

            try
            {
                return await DecryptAsync(encryptedPath, cancellationToken);
            }
            catch (CryptographicException)
            {
                File.Delete(encryptedPath);
                await store.SetAttachmentCachedPathAsync(attachment.Id, null, cancellationToken);
                await DownloadAndEncryptAsync(message, attachment, encryptedPath, cancellationToken);
                return await DecryptAsync(encryptedPath, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DownloadAndEncryptAsync(MailMessage message, MailAttachment attachment, string encryptedPath, CancellationToken cancellationToken)
    {
        var account = await store.GetAccountAsync(message.AccountId, cancellationToken)
            ?? throw new InvalidOperationException("The account for this message no longer exists.");
        await using var content = attachment.Size > 0 && attachment.Size <= int.MaxValue
            ? new MemoryStream((int)attachment.Size)
            : new MemoryStream();
        await providers.Get(account.Provider).DownloadAttachmentAsync(account, message, attachment, content, cancellationToken);
        if (content.Length > MaximumCachedAttachmentBytes)
        {
            throw new InvalidOperationException("This attachment is larger than Inboxwell's 150 MB local safety limit.");
        }

        var key = await GetKeyAsync(cancellationToken);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = content.ToArray();
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, tag.Length))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }
        CryptographicOperations.ZeroMemory(plain);

        var payload = new byte[Magic.Length + nonce.Length + tag.Length + cipher.Length];
        Magic.CopyTo(payload, 0);
        nonce.CopyTo(payload, Magic.Length);
        tag.CopyTo(payload, Magic.Length + nonce.Length);
        cipher.CopyTo(payload, Magic.Length + nonce.Length + tag.Length);
        var temporaryPath = encryptedPath + ".download";
        await File.WriteAllBytesAsync(temporaryPath, payload, cancellationToken);
        File.Move(temporaryPath, encryptedPath, true);
        await store.SetAttachmentCachedPathAsync(attachment.Id, encryptedPath, cancellationToken);
    }

    private async Task<byte[]> DecryptAsync(string encryptedPath, CancellationToken cancellationToken)
    {
        var payload = await File.ReadAllBytesAsync(encryptedPath, cancellationToken);
        if (payload.Length < 32 || !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new CryptographicException("The attachment cache is invalid.");
        }
        var key = await GetKeyAsync(cancellationToken);
        var nonce = payload.AsSpan(Magic.Length, 12);
        var tag = payload.AsSpan(Magic.Length + 12, 16);
        var cipher = payload.AsSpan(Magic.Length + 28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    private async Task<byte[]> GetKeyAsync(CancellationToken cancellationToken) =>
        Convert.FromBase64String(await secrets.GetOrCreateKeyAsync("gomail:attachment-cache-key", 32, cancellationToken));

    private string GetEncryptedPath(MailAttachment attachment)
    {
        var cacheRoot = Path.GetFullPath(cacheDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!string.IsNullOrWhiteSpace(attachment.CachedPath) &&
            Path.GetFullPath(attachment.CachedPath).StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
        {
            return attachment.CachedPath;
        }
        return Path.Combine(cacheDirectory, $"{attachment.Id:N}.gma");
    }

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "attachment" : name;
    }
}
