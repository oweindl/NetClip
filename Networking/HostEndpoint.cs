using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetClip.Networking;

internal sealed class PeerConnection
{
    public TcpClient TcpClient { get; }
    public NetworkStream Stream { get; }
    public string DisplayName { get; }
    private readonly object _writeLock = new();

    public PeerConnection(TcpClient tcpClient, NetworkStream stream, string displayName)
    {
        TcpClient = tcpClient;
        Stream = stream;
        DisplayName = displayName;
    }

    public void SendFrame(MessageType type, byte[] payload) => FrameIO.WriteFrame(Stream, _writeLock, type, payload);

    public void Close()
    {
        try { TcpClient.Close(); } catch { }
    }
}

/// <summary>
/// Hosts a session: accepts client connections and relays every message to all other clients (star topology).
/// The host is itself a full participant — it can send/receive text and files like any client.
/// </summary>
public class HostEndpoint : INetClipEndpoint
{
    private readonly TcpListener _listener;
    private readonly string _passphrase;
    private readonly List<PeerConnection> _peers = new();
    private readonly object _peersLock = new();
    private readonly FileReceiveManager _fileReceiver;
    private readonly HostAnnouncer? _announcer;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _stopped;

    public string LocalDisplayName { get; }
    public int Port { get; }
    public List<string> InitialParticipants { get; } = new();

    public event EventHandler<TextEventArgs>? TextReceived;
    public event EventHandler<FileEventInfo>? FileEvent;
    public event EventHandler<string>? ParticipantJoined;
    public event EventHandler<string>? ParticipantLeft;
    public event EventHandler<string>? StatusChanged;

    public HostEndpoint(int port, string passphrase, string displayName, string saveDir, bool enableDiscovery)
    {
        Port = port;
        _passphrase = passphrase ?? "";
        LocalDisplayName = displayName;
        _fileReceiver = new FileReceiveManager(saveDir);
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);

        if (enableDiscovery)
        {
            _announcer = new HostAnnouncer(displayName, port, !string.IsNullOrEmpty(_passphrase));
            _announcer.Start();
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var tcpClient = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandlePeer(tcpClient));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            if (!_stopped) StatusChanged?.Invoke(this, $"Accept loop error: {ex.Message}");
        }
    }

    private void HandlePeer(TcpClient tcpClient)
    {
        var stream = tcpClient.GetStream();
        PeerConnection? peer = null;
        try
        {
            var (type, payload) = FrameIO.ReadFrame(stream);
            if (type != MessageType.Hello) { tcpClient.Close(); return; }
            var hello = JsonSerializer.Deserialize<HelloPayload>(payload) ?? throw new IOException("Bad hello");

            if (!string.IsNullOrEmpty(_passphrase) && hello.Passphrase != _passphrase)
            {
                var fail = new HelloAckPayload(false, "Incorrect passphrase", null, new List<string>());
                FrameIO.WriteFrame(stream, new object(), MessageType.HelloAck, JsonSerializer.SerializeToUtf8Bytes(fail));
                tcpClient.Close();
                return;
            }

            string assignedName = DedupeName(hello.DisplayName);
            peer = new PeerConnection(tcpClient, stream, assignedName);

            List<string> participantsForNewPeer;
            lock (_peersLock)
            {
                participantsForNewPeer = _peers.Select(p => p.DisplayName).ToList();
                participantsForNewPeer.Add(LocalDisplayName);
                _peers.Add(peer);
            }

            var ack = new HelloAckPayload(true, null, assignedName, participantsForNewPeer);
            peer.SendFrame(MessageType.HelloAck, JsonSerializer.SerializeToUtf8Bytes(ack));

            BroadcastExcept(peer, MessageType.ParticipantJoined, JsonSerializer.SerializeToUtf8Bytes(new ParticipantPayload(assignedName)));
            ParticipantJoined?.Invoke(this, assignedName);

            while (true)
            {
                var (msgType, msgPayload) = FrameIO.ReadFrame(stream);
                HandlePeerMessage(peer, msgType, msgPayload);
            }
        }
        catch { /* peer disconnected or protocol error */ }
        finally
        {
            if (peer != null)
            {
                lock (_peersLock) { _peers.Remove(peer); }
                peer.Close();
                BroadcastExcept(null, MessageType.ParticipantLeft, JsonSerializer.SerializeToUtf8Bytes(new ParticipantPayload(peer.DisplayName)));
                ParticipantLeft?.Invoke(this, peer.DisplayName);
            }
            else
            {
                tcpClient.Close();
            }
        }
    }

    private void HandlePeerMessage(PeerConnection sender, MessageType type, byte[] payload)
    {
        switch (type)
        {
            case MessageType.Text:
                {
                    var text = JsonSerializer.Deserialize<TextPayload>(payload)!;
                    TextReceived?.Invoke(this, new TextEventArgs
                    {
                        Sender = text.Sender,
                        Text = text.Text,
                        TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(text.TimestampUtc).UtcDateTime,
                        IsLocal = false
                    });
                    BroadcastExcept(sender, type, payload);
                    break;
                }
            case MessageType.FileMeta:
                {
                    var meta = JsonSerializer.Deserialize<FileMetaPayload>(payload)!;
                    _fileReceiver.BeginReceive(meta);
                    FileEvent?.Invoke(this, new FileEventInfo
                    {
                        FileId = meta.FileId,
                        Sender = meta.Sender,
                        FileName = meta.FileName,
                        FileSize = meta.FileSize,
                        Kind = FileEventKind.Started,
                        IsOutgoing = false
                    });
                    BroadcastExcept(sender, type, payload);
                    break;
                }
            case MessageType.FileChunk:
                {
                    string fileId = new Guid(payload.AsSpan(0, 16)).ToString();
                    byte[] data = payload[16..];
                    double pct = _fileReceiver.WriteChunk(fileId, data);
                    FileEvent?.Invoke(this, new FileEventInfo { FileId = fileId, Kind = FileEventKind.Progress, ProgressPercent = pct, IsOutgoing = false });
                    BroadcastExcept(sender, type, payload);
                    break;
                }
            case MessageType.FileComplete:
                {
                    var comp = JsonSerializer.Deserialize<FileCompletePayload>(payload)!;
                    string? path = _fileReceiver.Complete(comp.FileId);
                    FileEvent?.Invoke(this, new FileEventInfo
                    {
                        FileId = comp.FileId,
                        Kind = FileEventKind.Completed,
                        LocalPath = path,
                        FileName = path != null ? Path.GetFileName(path) : "",
                        IsOutgoing = false
                    });
                    BroadcastExcept(sender, type, payload);
                    break;
                }
        }
    }

    private void BroadcastExcept(PeerConnection? exclude, MessageType type, byte[] payload)
    {
        List<PeerConnection> targets;
        lock (_peersLock) { targets = _peers.Where(p => !ReferenceEquals(p, exclude)).ToList(); }
        foreach (var p in targets)
        {
            try { p.SendFrame(type, payload); } catch { /* its own read loop will notice and clean up */ }
        }
    }

    private string DedupeName(string requested)
    {
        lock (_peersLock)
        {
            var existing = new HashSet<string>(_peers.Select(p => p.DisplayName)) { LocalDisplayName };
            if (!existing.Contains(requested)) return requested;
            int n = 2;
            while (existing.Contains($"{requested} ({n})")) n++;
            return $"{requested} ({n})";
        }
    }

    public void SendText(string text)
    {
        var payload = new TextPayload(LocalDisplayName, text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        BroadcastExcept(null, MessageType.Text, JsonSerializer.SerializeToUtf8Bytes(payload));
        TextReceived?.Invoke(this, new TextEventArgs { Sender = LocalDisplayName, Text = text, TimestampUtc = DateTime.UtcNow, IsLocal = true });
    }

    public void SendFile(string filePath)
    {
        var fileId = Guid.NewGuid().ToString();
        var guid = Guid.Parse(fileId);
        var fi = new FileInfo(filePath);
        var meta = new FileMetaPayload(fileId, LocalDisplayName, fi.Name, fi.Length, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        BroadcastExcept(null, MessageType.FileMeta, JsonSerializer.SerializeToUtf8Bytes(meta));
        FileEvent?.Invoke(this, new FileEventInfo { FileId = fileId, Sender = LocalDisplayName, FileName = fi.Name, FileSize = fi.Length, Kind = FileEventKind.Started, IsOutgoing = true, LocalPath = filePath });

        long sent = 0;
        foreach (var chunk in FileSendHelper.ReadChunks(filePath))
        {
            byte[] framePayload = new byte[16 + chunk.Length];
            guid.TryWriteBytes(framePayload);
            Buffer.BlockCopy(chunk, 0, framePayload, 16, chunk.Length);
            BroadcastExcept(null, MessageType.FileChunk, framePayload);
            sent += chunk.Length;
            double pct = fi.Length > 0 ? Math.Min(100.0, (double)sent / fi.Length * 100) : 100;
            FileEvent?.Invoke(this, new FileEventInfo { FileId = fileId, Kind = FileEventKind.Progress, ProgressPercent = pct, IsOutgoing = true, FileName = fi.Name });
        }

        BroadcastExcept(null, MessageType.FileComplete, JsonSerializer.SerializeToUtf8Bytes(new FileCompletePayload(fileId)));
        FileEvent?.Invoke(this, new FileEventInfo { FileId = fileId, Kind = FileEventKind.Completed, IsOutgoing = true, LocalPath = filePath, FileName = fi.Name });
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        try { _cts.Cancel(); } catch { }
        _announcer?.Stop();
        try { _listener.Stop(); } catch { }
        lock (_peersLock)
        {
            foreach (var p in _peers) p.Close();
            _peers.Clear();
        }
    }

    public void Dispose() => Stop();
}
