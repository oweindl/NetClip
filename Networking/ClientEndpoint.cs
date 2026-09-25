using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace NetClip.Networking;

/// <summary>Connects to a HostEndpoint as one participant in the session.</summary>
public class ClientEndpoint : INetClipEndpoint
{
    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly object _writeLock = new();
    private readonly FileReceiveManager _fileReceiver;
    private volatile bool _stopped;

    public string LocalDisplayName { get; private set; }
    public List<string> InitialParticipants { get; private set; } = new();

    public event EventHandler<TextEventArgs>? TextReceived;
    public event EventHandler<FileEventInfo>? FileEvent;
    public event EventHandler<string>? ParticipantJoined;
    public event EventHandler<string>? ParticipantLeft;
    public event EventHandler<string>? StatusChanged;

    private ClientEndpoint(TcpClient tcpClient, string displayName, string saveDir)
    {
        _tcpClient = tcpClient;
        _stream = tcpClient.GetStream();
        LocalDisplayName = displayName;
        _fileReceiver = new FileReceiveManager(saveDir);
    }

    public static ClientEndpoint Connect(string hostAddress, int port, string displayName, string passphrase, string saveDir, TimeSpan timeout)
    {
        var tcpClient = new TcpClient();
        var connectTask = tcpClient.ConnectAsync(hostAddress, port);
        if (!connectTask.Wait(timeout))
        {
            tcpClient.Close();
            throw new TimeoutException($"Could not reach {hostAddress}:{port} within {timeout.TotalSeconds:0}s.");
        }

        var endpoint = new ClientEndpoint(tcpClient, displayName, saveDir);
        var hello = new HelloPayload(displayName, passphrase ?? "");
        FrameIO.WriteFrame(endpoint._stream, endpoint._writeLock, MessageType.Hello, JsonSerializer.SerializeToUtf8Bytes(hello));

        var (type, payload) = FrameIO.ReadFrame(endpoint._stream);
        if (type != MessageType.HelloAck)
            throw new IOException("Unexpected response from host.");
        var ack = JsonSerializer.Deserialize<HelloAckPayload>(payload) ?? throw new IOException("Bad response from host.");
        if (!ack.Success)
        {
            tcpClient.Close();
            throw new InvalidOperationException(ack.Reason ?? "Connection rejected by host.");
        }

        endpoint.LocalDisplayName = ack.AssignedDisplayName ?? displayName;
        endpoint.InitialParticipants = ack.Participants;
        _ = Task.Run(endpoint.ReadLoop);
        return endpoint;
    }

    private void ReadLoop()
    {
        try
        {
            while (true)
            {
                var (type, payload) = FrameIO.ReadFrame(_stream);
                HandleMessage(type, payload);
            }
        }
        catch (Exception ex)
        {
            if (!_stopped) StatusChanged?.Invoke(this, $"Disconnected: {ex.Message}");
        }
    }

    private void HandleMessage(MessageType type, byte[] payload)
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
                    break;
                }
            case MessageType.FileChunk:
                {
                    string fileId = new Guid(payload.AsSpan(0, 16)).ToString();
                    byte[] data = payload[16..];
                    double pct = _fileReceiver.WriteChunk(fileId, data);
                    FileEvent?.Invoke(this, new FileEventInfo { FileId = fileId, Kind = FileEventKind.Progress, ProgressPercent = pct, IsOutgoing = false });
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
                    break;
                }
            case MessageType.ParticipantJoined:
                {
                    var pj = JsonSerializer.Deserialize<ParticipantPayload>(payload)!;
                    ParticipantJoined?.Invoke(this, pj.DisplayName);
                    break;
                }
            case MessageType.ParticipantLeft:
                {
                    var pl = JsonSerializer.Deserialize<ParticipantPayload>(payload)!;
                    ParticipantLeft?.Invoke(this, pl.DisplayName);
                    break;
                }
        }
    }

    public void SendText(string text)
    {
        var payload = new TextPayload(LocalDisplayName, text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        FrameIO.WriteFrame(_stream, _writeLock, MessageType.Text, JsonSerializer.SerializeToUtf8Bytes(payload));
        TextReceived?.Invoke(this, new TextEventArgs { Sender = LocalDisplayName, Text = text, TimestampUtc = DateTime.UtcNow, IsLocal = true });
    }

    public void SendFile(string filePath)
    {
        var fileId = Guid.NewGuid().ToString();
        var guid = Guid.Parse(fileId);
        var fi = new FileInfo(filePath);
        var meta = new FileMetaPayload(fileId, LocalDisplayName, fi.Name, fi.Length, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        FrameIO.WriteFrame(_stream, _writeLock, MessageType.FileMeta, JsonSerializer.SerializeToUtf8Bytes(meta));
        FileEvent?.Invoke(this, new FileEventInfo { FileId = fileId, Sender = LocalDisplayName, FileName = fi.Name, FileSize = fi.Length, Kind = FileEventKind.Started, IsOutgoing = true, LocalPath = filePath });

        long sent = 0;
        foreach (var chunk in FileSendHelper.ReadChunks(filePath))
        {
            byte[] framePayload = new byte[16 + chunk.Length];
            guid.TryWriteBytes(framePayload);
            Buffer.BlockCopy(chunk, 0, framePayload, 16, chunk.Length);
            FrameIO.WriteFrame(_stream, _writeLock, MessageType.FileChunk, framePayload);
            sent += chunk.Length;
            double pct = fi.Length > 0 ? Math.Min(100.0, (double)sent / fi.Length * 100) : 100;
            FileEvent?.Invoke(this, new FileEventInfo { FileId = fileId, Kind = FileEventKind.Progress, ProgressPercent = pct, IsOutgoing = true, FileName = fi.Name });
        }

        FrameIO.WriteFrame(_stream, _writeLock, MessageType.FileComplete, JsonSerializer.SerializeToUtf8Bytes(new FileCompletePayload(fileId)));
        FileEvent?.Invoke(this, new FileEventInfo { FileId = fileId, Kind = FileEventKind.Completed, IsOutgoing = true, LocalPath = filePath, FileName = fi.Name });
    }

    public void Stop()
    {
        _stopped = true;
        try { _tcpClient.Close(); } catch { }
    }

    public void Dispose() => Stop();
}
