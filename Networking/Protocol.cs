using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace NetClip.Networking;

public enum MessageType : byte
{
    Hello = 1,
    HelloAck = 2,
    Text = 3,
    FileMeta = 4,
    FileChunk = 5,
    FileComplete = 6,
    ParticipantJoined = 7,
    ParticipantLeft = 8,
}

public record HelloPayload(string DisplayName, string Passphrase);
public record HelloAckPayload(bool Success, string? Reason, string? AssignedDisplayName, List<string> Participants);
public record TextPayload(string Sender, string Text, long TimestampUtc);
public record FileMetaPayload(string FileId, string Sender, string FileName, long FileSize, long TimestampUtc);
public record FileCompletePayload(string FileId);
public record ParticipantPayload(string DisplayName);

/// <summary>
/// Wire framing: [4-byte little-endian payload length][1-byte MessageType][payload].
/// FileChunk payloads are raw bytes (16-byte file id + chunk data), everything else is UTF-8 JSON.
/// </summary>
public static class FrameIO
{
    public static void WriteFrame(Stream stream, object writeLock, MessageType type, byte[] payload)
    {
        lock (writeLock)
        {
            Span<byte> header = stackalloc byte[5];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            header[4] = (byte)type;
            stream.Write(header);
            if (payload.Length > 0) stream.Write(payload);
            stream.Flush();
        }
    }

    public static (MessageType Type, byte[] Payload) ReadFrame(Stream stream)
    {
        byte[] header = ReadExact(stream, 5);
        int len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len < 0 || len > 32 * 1024 * 1024)
            throw new IOException($"Invalid frame length {len}");
        var type = (MessageType)header[4];
        byte[] payload = len > 0 ? ReadExact(stream, len) : [];
        return (type, payload);
    }

    private static byte[] ReadExact(Stream stream, int count)
    {
        byte[] buf = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buf, offset, count - offset);
            if (read == 0) throw new IOException("Connection closed");
            offset += read;
        }
        return buf;
    }
}

public static class FileSendHelper
{
    public const int ChunkSize = 64 * 1024;
    public const long WarnThresholdBytes = 150L * 1024 * 1024;

    public static IEnumerable<byte[]> ReadChunks(string path)
    {
        using var fs = File.OpenRead(path);
        byte[] buffer = new byte[ChunkSize];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (read == buffer.Length)
            {
                yield return buffer;
            }
            else
            {
                byte[] chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                yield return chunk;
            }
        }
    }
}
