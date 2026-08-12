using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Gomail.Core;
using Windows.Storage;

namespace Gomail_App.Services;

public sealed class LocalDiagnosticsService
{
    private readonly IMailStore store;
    private readonly object logLock = new();
    private readonly string logDirectory;
    private readonly string logPath;

    public LocalDiagnosticsService(IMailStore store)
    {
        this.store = store;
        logDirectory = Path.Combine(ApplicationData.Current.LocalFolder.Path, "Logs");
        logPath = Path.Combine(logDirectory, "inboxwell.log");
        Directory.CreateDirectory(logDirectory);
        var legacyLogPath = Path.Combine(logDirectory, "gomail.log");
        if (!File.Exists(logPath) && File.Exists(legacyLogPath))
        {
            File.Move(legacyLogPath, logPath);
        }
    }

    public string DataDirectory => ApplicationData.Current.LocalFolder.Path;

    public void LogException(string context, Exception exception)
    {
        try
        {
            lock (logLock)
            {
                RotateIfNeeded();
                File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} [{context}] {exception}\r\n\r\n", Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never create another application failure.
        }
    }

    public async Task<string> CreateReportAsync(CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        builder.AppendLine("Inboxwell diagnostics");
        builder.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        builder.AppendLine($"App version: {version}");
        builder.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        builder.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"Data directory: {DataDirectory}");
        builder.AppendLine();
        builder.AppendLine("Accounts:");
        foreach (var account in await store.GetAccountsAsync(cancellationToken))
        {
            builder.AppendLine($"- {account.Provider} · {account.Email} · {(account.IsEnabled ? "enabled" : "disabled")}");
            builder.AppendLine($"  Last successful sync: {account.LastSuccessfulSync?.ToLocalTime().ToString("O") ?? "never"}");
            if (!string.IsNullOrWhiteSpace(account.LastSyncError)) builder.AppendLine($"  Last error: {account.LastSyncError}");
        }
        var drafts = await store.GetDraftsAsync(cancellationToken: cancellationToken);
        builder.AppendLine();
        builder.AppendLine($"Local drafts/outbox: {drafts.Count}");
        builder.AppendLine($"Queued: {drafts.Count(static item => item.DeliveryState == DraftDeliveryState.Queued)}");
        builder.AppendLine($"Failed: {drafts.Count(static item => item.DeliveryState == DraftDeliveryState.Failed)}");

        if (File.Exists(logPath))
        {
            builder.AppendLine();
            builder.AppendLine("Recent application log:");
            string log;
            lock (logLock) log = File.ReadAllText(logPath, Encoding.UTF8);
            builder.Append(log.Length > 24_000 ? log[^24_000..] : log);
        }
        return builder.ToString();
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(logPath) || new FileInfo(logPath).Length < 1024 * 1024) return;
        var previous = Path.Combine(logDirectory, "inboxwell.previous.log");
        File.Move(logPath, previous, true);
    }
}
