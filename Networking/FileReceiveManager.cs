using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NetClip.Networking;

/// <summary>
/// Writes incoming files to disk as chunks arrive, keyed by file id.
/// Shared by HostEndpoint and ClientEndpoint since both act as file recipients.
/// </summary>
public class FileReceiveManager
{
    private readonly string _saveDir;
    private readonly Dictionary<string, ReceiveState> _states = new();
    private readonly object _lock = new();

    private sealed class ReceiveState
    {
        public required FileStream Stream;
        public required string Path;
        public long BytesReceived;
        public long TotalSize;
    }

    public FileReceiveManager(string saveDir)
    {
        _saveDir = saveDir;
        Directory.CreateDirectory(_saveDir);
    }

    public string BeginReceive(FileMetaPayload meta)
    {
        string safeSender = SanitizeFileName(meta.Sender);
        string safeName = SanitizeFileName(meta.FileName);
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(meta.TimestampUtc).LocalDateTime;
        string baseName = $"{safeSender}_{timestamp:yyyyMMdd_HHmmss}_{safeName}";
        string path = UniquePath(Path.Combine(_saveDir, baseName));

        var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        lock (_lock)
        {
            _states[meta.FileId] = new ReceiveState { Stream = fs, Path = path, TotalSize = meta.FileSize };
        }
        return path;
    }

    public double WriteChunk(string fileId, byte[] data)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(fileId, out var st)) return 0;
            st.Stream.Write(data, 0, data.Length);
            st.BytesReceived += data.Length;
            return st.TotalSize > 0 ? Math.Min(100.0, (double)st.BytesReceived / st.TotalSize * 100.0) : 100.0;
        }
    }

    public string? Complete(string fileId)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(fileId, out var st)) return null;
            st.Stream.Flush();
            st.Stream.Dispose();
            _states.Remove(fileId);
            return st.Path;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        string result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? "file" : result;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        int n = 2;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name} ({n}){ext}");
            n++;
        } while (File.Exists(candidate));
        return candidate;
    }
}
