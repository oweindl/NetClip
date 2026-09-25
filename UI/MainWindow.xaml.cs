using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NetClip.Networking;
using NetClip.ViewModels;
using Wpf.Ui.Appearance;

namespace NetClip.UI;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private const int DefaultPort = 53535;

    private INetClipEndpoint? _endpoint;
    private readonly ObservableCollection<FeedItemViewModel> _feed = new();
    private readonly Dictionary<string, FileFeedItem> _fileItems = new();
    private readonly List<string> _participants = new();

    private HostBrowser? _browser;
    private readonly DispatcherTimer _discoveryTimer;
    private readonly Dictionary<string, (HostAnnouncement Info, IPEndPoint Endpoint, DateTime LastSeen)> _discovered = new();
    private readonly ObservableCollection<DiscoveredHostViewModel> _discoveredVm = new();

    private bool _isDarkTheme;

    public MainWindow()
    {
        InitializeComponent();

        FeedItemsControl.ItemsSource = _feed;
        LvHosts.ItemsSource = _discoveredVm;

        _isDarkTheme = ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark;
        ApplicationThemeManager.Apply(_isDarkTheme ? ApplicationTheme.Dark : ApplicationTheme.Light);
        UpdateThemeIcon();

        _discoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _discoveryTimer.Tick += (_, _) => RefreshDiscoveredHosts();

        ShowPage(StartPage);
    }

    // ---------------------------------------------------------------- Theme

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        ApplicationThemeManager.Apply(_isDarkTheme ? ApplicationTheme.Dark : ApplicationTheme.Light);
        UpdateThemeIcon();
    }

    private void UpdateThemeIcon()
    {
        ThemeToggleButton.Icon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = _isDarkTheme ? Wpf.Ui.Controls.SymbolRegular.WeatherSunny24 : Wpf.Ui.Controls.SymbolRegular.WeatherMoon24
        };
        ThemeToggleButton.ToolTip = _isDarkTheme ? "Switch to light theme" : "Switch to dark theme";
    }

    // ---------------------------------------------------------------- Page switching

    private void ShowPage(UIElement page)
    {
        StartPage.Visibility = ReferenceEquals(page, StartPage) ? Visibility.Visible : Visibility.Collapsed;
        HostPage.Visibility = ReferenceEquals(page, HostPage) ? Visibility.Visible : Visibility.Collapsed;
        JoinPage.Visibility = ReferenceEquals(page, JoinPage) ? Visibility.Visible : Visibility.Collapsed;
        SessionPage.Visibility = ReferenceEquals(page, SessionPage) ? Visibility.Visible : Visibility.Collapsed;

        if (ReferenceEquals(page, JoinPage)) StartBrowsing();
        else StopBrowsing();
    }

    private void HostButton_Click(object sender, RoutedEventArgs e) => ShowPage(HostPage);
    private void JoinButton_Click(object sender, RoutedEventArgs e) => ShowPage(JoinPage);
    private void HostBack_Click(object sender, RoutedEventArgs e) => ShowPage(StartPage);
    private void JoinBack_Click(object sender, RoutedEventArgs e) => ShowPage(StartPage);

    // ---------------------------------------------------------------- Host setup

    private async void StartHosting_Click(object sender, RoutedEventArgs e)
    {
        int port = (int)(NumHostPort.Value ?? DefaultPort);
        string displayName = string.IsNullOrWhiteSpace(TxtHostName.Text) ? Environment.UserName : TxtHostName.Text.Trim();
        string passphrase = PwdHostPassphrase.Password;

        try
        {
            var host = new HostEndpoint(port, passphrase, displayName, GetSaveDir(), ToggleHostDiscovery.IsChecked == true);
            string passInfo = string.IsNullOrEmpty(passphrase) ? "no passphrase" : "passphrase set";
            AttachEndpoint(host, $"Hosting on port {port} as \"{host.LocalDisplayName}\" ({passInfo})");
        }
        catch (SocketException ex)
        {
            await ShowErrorAsync($"Could not start listening on port {port}:\n{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- Join setup

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        string addressPort = TxtManualAddress.Text.Trim();
        if (string.IsNullOrEmpty(addressPort))
        {
            await ShowErrorAsync("Enter a host address (IP:port) or pick one from the discovered list.");
            return;
        }

        string ip = addressPort;
        int port = DefaultPort;
        int colonIdx = addressPort.LastIndexOf(':');
        if (colonIdx > 0 && int.TryParse(addressPort[(colonIdx + 1)..], out var parsedPort))
        {
            ip = addressPort[..colonIdx];
            port = parsedPort;
        }

        string displayName = string.IsNullOrWhiteSpace(TxtJoinName.Text) ? Environment.UserName : TxtJoinName.Text.Trim();
        string passphrase = PwdJoinPassphrase.Password;
        string saveDir = GetSaveDir();

        BtnConnect.IsEnabled = false;
        TxtJoinStatus.Text = "Connecting…";

        try
        {
            var client = await Task.Run(() => ClientEndpoint.Connect(ip, port, displayName, passphrase, saveDir, TimeSpan.FromSeconds(6)));
            BtnConnect.IsEnabled = true;
            TxtJoinStatus.Text = "";
            AttachEndpoint(client, $"Connected to {ip}:{port} as \"{client.LocalDisplayName}\"");
        }
        catch (Exception ex)
        {
            BtnConnect.IsEnabled = true;
            TxtJoinStatus.Text = "";
            await ShowErrorAsync($"Could not connect:\n{ex.Message}");
        }
    }

    private void LvHosts_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LvHosts.SelectedItem is DiscoveredHostViewModel vm) TxtManualAddress.Text = vm.Address;
    }

    private void StartBrowsing()
    {
        _discovered.Clear();
        _discoveredVm.Clear();
        _browser = new HostBrowser();
        _browser.HostDiscovered += (_, tuple) =>
        {
            string key = $"{tuple.Endpoint.Address}:{tuple.Info.TcpPort}";
            Dispatcher.BeginInvoke(() => _discovered[key] = (tuple.Info, tuple.Endpoint, DateTime.Now));
        };
        _browser.Start();
        _discoveryTimer.Start();
    }

    private void StopBrowsing()
    {
        _discoveryTimer.Stop();
        _browser?.Stop();
        _browser = null;
    }

    private void RefreshDiscoveredHosts()
    {
        var cutoff = DateTime.Now.AddSeconds(-6);
        foreach (var key in _discovered.Where(kv => kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToList())
            _discovered.Remove(key);

        _discoveredVm.Clear();
        foreach (var (key, value) in _discovered)
        {
            _discoveredVm.Add(new DiscoveredHostViewModel
            {
                DisplayName = value.Info.DisplayName,
                Address = $"{value.Endpoint.Address}:{value.Info.TcpPort}",
                PassphraseText = value.Info.RequiresPassphrase ? "Required" : "None",
                Key = key
            });
        }
    }

    // ---------------------------------------------------------------- Session lifecycle

    private void AttachEndpoint(INetClipEndpoint endpoint, string roleDescription)
    {
        _endpoint = endpoint;
        _participants.Clear();
        _participants.AddRange(endpoint.InitialParticipants);
        _fileItems.Clear();
        _feed.Clear();

        endpoint.TextReceived += OnTextReceived;
        endpoint.FileEvent += OnFileEvent;
        endpoint.ParticipantJoined += OnParticipantJoined;
        endpoint.ParticipantLeft += OnParticipantLeft;
        endpoint.StatusChanged += OnStatusChanged;

        TxtRole.Text = roleDescription;
        UpdateParticipantsLabel();
        ShowPage(SessionPage);
        TxtInput.Focus();
    }

    private void DetachEndpoint()
    {
        if (_endpoint == null) return;
        _endpoint.TextReceived -= OnTextReceived;
        _endpoint.FileEvent -= OnFileEvent;
        _endpoint.ParticipantJoined -= OnParticipantJoined;
        _endpoint.ParticipantLeft -= OnParticipantLeft;
        _endpoint.StatusChanged -= OnStatusChanged;
        _endpoint.Stop();
        _endpoint = null;
    }

    private async void Leave_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Leave this session?", "Leave")) return;
        DetachEndpoint();
        ShowPage(StartPage);
    }

    private void UpdateParticipantsLabel()
    {
        string others = _participants.Count > 0 ? string.Join(", ", _participants) : "(no one else yet)";
        TxtParticipants.Text = $"You: {_endpoint?.LocalDisplayName}   ·   Others: {others}";
    }

    // ---------------------------------------------------------------- Networking event handlers

    private void OnTextReceived(object? sender, TextEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _feed.Add(new TextFeedItem { Sender = e.Sender, Text = e.Text, TimestampLocal = e.TimestampUtc.ToLocalTime(), IsLocal = e.IsLocal });
            ScrollFeedToEnd();

            if (!e.IsLocal && ToggleAutoCopy.IsChecked == true)
            {
                try { Clipboard.SetText(e.Text); } catch { /* clipboard may be locked by another app */ }
            }
        });
    }

    private void OnFileEvent(object? sender, FileEventInfo e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            switch (e.Kind)
            {
                case FileEventKind.Started:
                    var item = new FileFeedItem
                    {
                        FileId = e.FileId,
                        Sender = e.Sender,
                        FileName = e.FileName,
                        FileSize = e.FileSize,
                        TimestampLocal = DateTime.Now,
                        IsLocal = e.IsOutgoing
                    };
                    _fileItems[e.FileId] = item;
                    _feed.Add(item);
                    ScrollFeedToEnd();
                    break;

                case FileEventKind.Progress:
                    if (_fileItems.TryGetValue(e.FileId, out var pItem))
                    {
                        pItem.Progress = e.ProgressPercent;
                        pItem.StatusText = $"{e.ProgressPercent:0}% transferred";
                    }
                    break;

                case FileEventKind.Completed:
                    if (_fileItems.TryGetValue(e.FileId, out var cItem))
                    {
                        cItem.IsTransferring = false;
                        cItem.LocalPath = e.LocalPath;
                        cItem.StatusText = e.LocalPath != null ? (e.IsOutgoing ? "Sent" : $"Saved to {e.LocalPath}") : "Failed";
                        cItem.CanOpen = e.LocalPath != null && File.Exists(e.LocalPath);
                    }
                    break;
            }
        });
    }

    private void OnParticipantJoined(object? sender, string name)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_participants.Contains(name)) _participants.Add(name);
            UpdateParticipantsLabel();
            _feed.Add(new TextFeedItem { Sender = "System", Text = $"{name} joined the session", TimestampLocal = DateTime.Now, IsLocal = false });
            ScrollFeedToEnd();
        });
    }

    private void OnParticipantLeft(object? sender, string name)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _participants.Remove(name);
            UpdateParticipantsLabel();
            _feed.Add(new TextFeedItem { Sender = "System", Text = $"{name} left the session", TimestampLocal = DateTime.Now, IsLocal = false });
            ScrollFeedToEnd();
        });
    }

    private void OnStatusChanged(object? sender, string message)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            DetachEndpoint();
            ShowPage(StartPage);
            await ShowErrorAsync(message);
        });
    }

    private void ScrollFeedToEnd()
        => Dispatcher.InvokeAsync(() => FeedScrollViewer.ScrollToEnd(), DispatcherPriority.Background);

    // ---------------------------------------------------------------- Sending text

    private void TxtInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SendCurrentText();
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e) => SendCurrentText();

    private void SendCurrentText()
    {
        string text = TxtInput.Text.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(text) || _endpoint == null) return;
        TxtInput.Clear();
        var endpoint = _endpoint;
        Task.Run(() =>
        {
            try { endpoint.SendText(text); }
            catch (Exception ex) { Dispatcher.BeginInvoke(async () => await ShowErrorAsync($"Failed to send: {ex.Message}")); }
        });
    }

    // ---------------------------------------------------------------- Sending files (drag & drop)

    private void Feed_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Feed_Drop(object sender, DragEventArgs e)
    {
        if (_endpoint == null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                await ShowErrorAsync($"'{Path.GetFileName(path)}' is a folder. Drop individual files instead.");
                continue;
            }
            if (!File.Exists(path)) continue;

            var fi = new FileInfo(path);
            if (fi.Length > FileSendHelper.WarnThresholdBytes)
            {
                bool proceed = await ConfirmAsync(
                    $"'{fi.Name}' is {fi.Length / 1024.0 / 1024.0:0.#} MB, above the recommended 150 MB for LAN transfer.\nSend anyway?",
                    "Send anyway");
                if (!proceed) continue;
            }

            var endpoint = _endpoint;
            Task.Run(() =>
            {
                try { endpoint.SendFile(path); }
                catch (Exception ex) { Dispatcher.BeginInvoke(async () => await ShowErrorAsync($"Failed to send '{Path.GetFileName(path)}':\n{ex.Message}")); }
            });
        }
    }

    // ---------------------------------------------------------------- Feed item actions

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TextFeedItem item)
        {
            try { Clipboard.SetText(item.Text); } catch { /* clipboard may be locked by another app */ }
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FileFeedItem item && item.LocalPath != null && File.Exists(item.LocalPath))
        {
            try { Process.Start(new ProcessStartInfo(item.LocalPath) { UseShellExecute = true }); } catch { }
        }
    }

    private void ShowFolder_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FileFeedItem item && item.LocalPath != null && File.Exists(item.LocalPath))
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.LocalPath}\"")); } catch { }
        }
    }

    // ---------------------------------------------------------------- Dialog helpers

    private async Task<bool> ConfirmAsync(string message, string primaryText)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Owner = this,
            Title = "NetClip",
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            IsSecondaryButtonEnabled = false,
        };
        var result = await box.ShowDialogAsync();
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    private async Task ShowErrorAsync(string message)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Owner = this,
            Title = "NetClip",
            Content = message,
            CloseButtonText = "OK",
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false,
        };
        await box.ShowDialogAsync();
    }

    private static string GetSaveDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NetClip");

    protected override void OnClosed(EventArgs e)
    {
        DetachEndpoint();
        StopBrowsing();
        base.OnClosed(e);
    }
}
