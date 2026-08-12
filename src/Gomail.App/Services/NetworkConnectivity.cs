using Gomail.Core;
using Windows.Networking.Connectivity;

namespace Gomail_App.Services;

public sealed class NetworkConnectivity : IConnectivity, IDisposable
{
    private bool isOnline = ReadState();

    public NetworkConnectivity()
    {
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
    }

    public bool IsOnline => isOnline;

    public event EventHandler<bool>? Changed;

    private void OnNetworkStatusChanged(object sender)
    {
        var next = ReadState();
        if (next == isOnline)
        {
            return;
        }
        isOnline = next;
        Changed?.Invoke(this, next);
    }

    private static bool ReadState() =>
        NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;

    public void Dispose() => NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
}
