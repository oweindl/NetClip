using System;

namespace NetClip.ViewModels;

public abstract class FeedItemViewModel : ObservableObject
{
    public required string Sender { get; init; }
    public DateTime TimestampLocal { get; init; }
    public bool IsLocal { get; init; }
    public string HeaderText => $"{Sender}  ·  {TimestampLocal:HH:mm:ss}";
}

public class TextFeedItem : FeedItemViewModel
{
    public required string Text { get; init; }
}

public class FileFeedItem : FeedItemViewModel
{
    public required string FileId { get; init; }
    public required string FileName { get; init; }
    public long FileSize { get; init; }
    public string FileSizeText => FormatSize(FileSize);

    private double _progress;
    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    private bool _isTransferring = true;
    public bool IsTransferring
    {
        get => _isTransferring;
        set => SetProperty(ref _isTransferring, value);
    }

    private string _statusText = "Starting…";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private string? _localPath;
    public string? LocalPath
    {
        get => _localPath;
        set => SetProperty(ref _localPath, value);
    }

    private bool _canOpen;
    public bool CanOpen
    {
        get => _canOpen;
        set => SetProperty(ref _canOpen, value);
    }

    private static string FormatSize(long bytes)
    {
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.#} MB";
        return $"{mb / 1024.0:0.#} GB";
    }
}

public class DiscoveredHostViewModel
{
    public required string DisplayName { get; init; }
    public required string Address { get; init; }
    public required string PassphraseText { get; init; }
    public required string Key { get; init; }
}
