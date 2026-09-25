using System;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetClip.Networking;

public record HostAnnouncement(string App, string HostName, string DisplayName, int TcpPort, bool RequiresPassphrase);

/// <summary>Periodically broadcasts this host's presence on the LAN so joiners can auto-discover it.</summary>
public class HostAnnouncer
{
    public const int DiscoveryPort = 53536;

    private readonly UdpClient _udp = new(AddressFamily.InterNetwork) { EnableBroadcast = true };
    private readonly string _displayName;
    private readonly int _tcpPort;
    private readonly bool _requiresPassphrase;
    private CancellationTokenSource? _cts;

    public HostAnnouncer(string displayName, int tcpPort, bool requiresPassphrase)
    {
        _displayName = displayName;
        _tcpPort = tcpPort;
        _requiresPassphrase = requiresPassphrase;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken token)
    {
        var announcement = new HostAnnouncement("NetClip", Environment.MachineName, _displayName, _tcpPort, _requiresPassphrase);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(announcement);
        var endpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
        while (!token.IsCancellationRequested)
        {
            try { await _udp.SendAsync(data, endpoint, token); } catch { /* best-effort */ }
            try { await Task.Delay(2000, token); } catch (OperationCanceledException) { }
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp.Close(); } catch { }
    }
}

/// <summary>Listens for HostAnnouncer broadcasts so the Join screen can list hosts automatically.</summary>
public class HostBrowser : IDisposable
{
    public const int DiscoveryPort = HostAnnouncer.DiscoveryPort;

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public event EventHandler<(IPEndPoint Endpoint, HostAnnouncement Info)>? HostDiscovered;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        _ = Task.Run(ListenLoop);
    }

    private async Task ListenLoop()
    {
        var token = _cts!.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(token);
                var info = JsonSerializer.Deserialize<HostAnnouncement>(result.Buffer);
                if (info != null && info.App == "NetClip")
                    HostDiscovered?.Invoke(this, (result.RemoteEndPoint, info));
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { /* ignore malformed packets */ }
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Close(); } catch { }
    }

    public void Dispose() => Stop();
}
