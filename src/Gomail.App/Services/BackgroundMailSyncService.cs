using Gomail.Core;

namespace Gomail_App.Services;

public sealed class BackgroundMailSyncService : IDisposable
{
    private readonly ISyncCoordinator sync;
    private readonly IConnectivity connectivity;
    private readonly SemaphoreSlim runLock = new(1, 1);
    private Timer? timer;
    private bool disposed;

    public BackgroundMailSyncService(ISyncCoordinator sync, IConnectivity connectivity)
    {
        this.sync = sync;
        this.connectivity = connectivity;
        connectivity.Changed += OnConnectivityChanged;
    }

    public void Start()
    {
        timer ??= new Timer(static state => ((BackgroundMailSyncService)state!).Trigger(), this, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(3));
    }

    private void OnConnectivityChanged(object? sender, bool online)
    {
        if (online) Trigger();
    }

    private void Trigger()
    {
        if (disposed || !connectivity.IsOnline) return;
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        if (!await runLock.WaitAsync(0)) return;
        try
        {
            await sync.SyncAllAsync();
        }
        catch
        {
            // Per-account errors are stored by the coordinator and shown in diagnostics.
        }
        finally
        {
            runLock.Release();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        connectivity.Changed -= OnConnectivityChanged;
        timer?.Dispose();
        runLock.Dispose();
    }
}
