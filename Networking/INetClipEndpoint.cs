using System;
using System.Collections.Generic;

namespace NetClip.Networking;

public enum FileEventKind { Started, Progress, Completed, Failed }

public class TextEventArgs : EventArgs
{
    public required string Sender { get; init; }
    public required string Text { get; init; }
    public DateTime TimestampUtc { get; init; }
    public bool IsLocal { get; init; }
}

public class FileEventInfo : EventArgs
{
    public required string FileId { get; init; }
    public string Sender { get; init; } = "";
    public string FileName { get; init; } = "";
    public long FileSize { get; init; }
    public FileEventKind Kind { get; init; }
    public double ProgressPercent { get; init; }
    public string? LocalPath { get; init; }
    public bool IsOutgoing { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Common surface for both the hosting side (relay) and the joining side (single connection),
/// so the UI can talk to either without knowing which role it is.
/// </summary>
public interface INetClipEndpoint : IDisposable
{
    string LocalDisplayName { get; }
    List<string> InitialParticipants { get; }

    event EventHandler<TextEventArgs>? TextReceived;
    event EventHandler<FileEventInfo>? FileEvent;
    event EventHandler<string>? ParticipantJoined;
    event EventHandler<string>? ParticipantLeft;
    event EventHandler<string>? StatusChanged;

    void SendText(string text);
    void SendFile(string filePath);
    void Stop();
}
