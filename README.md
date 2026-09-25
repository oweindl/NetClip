# NetClip

A lightweight LAN clipboard and file-sharing tool for Windows. One machine hosts a session, others join, and everyone in the session can share text snippets and files in a simple chat-style feed.

## Features

- **Host / Join model** — one machine starts listening, others connect to it; everyone in the session sees the same feed.
- **Text sharing** — type or paste text, it's broadcast to everyone instantly. Select and copy any message directly, or use the per-message copy button. An optional toggle auto-copies newly received text straight to your clipboard.
- **File sharing** — drag and drop files anywhere into the feed. Recipients get them saved automatically to `Documents\NetClip\`, with a progress indicator and one-click "Open" / "Show in folder".
- **LAN auto-discovery** — joining machines see available hosts on the network automatically, or you can connect by IP:port manually.
- **Optional passphrase** — hosts can require a shared passphrase before anyone can join.
- **Light/dark theme** — modern Fluent (Windows 11-style) UI, built with WPF-UI.

## Requirements

- Windows 10/11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) to run, or the .NET 8 SDK to build from source

## Getting started

```bash
git clone https://github.com/oweindl/NetClip.git
cd NetClip
dotnet run
```

To build a single portable `.exe` (no .NET runtime required on the target machine):

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Usage

1. **Host a Session** — pick a display name, optional passphrase, and TCP port (default `53535`), then start hosting.
2. **Join a Session** — pick a discovered host from the list, or enter its `IP:port` manually, along with the passphrase if one is set.
3. Once connected, type text and press **Enter** to send, or **drag files** into the feed to share them with everyone in the session.

## How it works

NetClip uses a simple star topology: the host relays every message to all connected clients over TCP (custom length-prefixed framing). Host discovery uses periodic UDP broadcasts on the local network. There's no encryption — it's designed for trusted home/office LANs, with an optional passphrase as a basic gate against accidental joins.

## Security note

Traffic is unencrypted plaintext, intended for use on a trusted local network only. Don't use it over an untrusted or public network.

## License

MIT — see [LICENSE](LICENSE).
