# PAX Commander

A fast file manager for Windows, built to replace File Explorer: two panes, tabs, a real search, remote servers and a terminal in one window.

## Features

### Browsing
- **Two panes with tabs.** The session (open tabs and folders) comes back when you restart.
- **Views:** details list, tiles with thumbnails, and Miller columns.
- **Preview pane and Quick Look** for pictures, text and code.
- **Address bar** you can type in, sidebar with pinned folders and drive tiles, and frecency ("most used recently") jumps.
- **Labels and Git status** on files and folders, and folder sizes.
- **Command palette** (fuzzy search over every command) and keyboard shortcuts throughout.
- **Windows shell built in:** real icons and thumbnails, the native right-click menu, Explorer-compatible copy and paste, and the Recycle Bin, with restore.
- **Network:** browse computers and shares, and map network drives.
- **Disk images:** shows which ISO/VHD a drive letter is mounted from; mount and unmount.

### File operations
- **Job queue** for copy, move, delete and zip, with conflict handling, pause, and **undo**.
- **Batch rename.**

### Search
Searches run in parallel and understand filters:

```text
report ext:pdf size:>2mb modified:thisweek
type:image size:1mb..5mb -ext:gif
content:"TODO" modified:<30d
```

Filters: `ext:`, `type:` (image, video, audio, doc, archive, code, folder, file), `size:`, `modified:`, `created:` and `content:`. A `-` in front excludes, and `*` and `?` match whole names.

### Remote and terminal
- **FTP, FTPS and SFTP** connections browse like local folders. Passwords are kept in Windows Credential Manager, and host keys are trusted on first use.
- **Docked terminal** (Ctrl+`): local PowerShell, or SSH to a remote server. The file pane follows the terminal's folder. Common Windows commands (`dir`, `cls`, `copy`…) also work on Linux servers.
- **Microsoft 365 connections** that open in the docked PowerShell. Saved passwords stay in Credential Manager.

### Look
- Dark and light themes (or follow Windows), with an accent colour you choose.
- Its own title bar, in the same style as the rest of the window.

## Build

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). Windows is the main target.

```bash
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:DebugType=none -o publish
```

**Installer:** `installer\build-setup.ps1` builds `paxcomm-setup.exe`. It installs for the current user, with a Start menu shortcut, an optional desktop shortcut and "Open in PAX Commander" on folders' right-click menu.

```bash
paxcomm-setup.exe --quiet --no-desktop
PaxCommander.exe --uninstall --quiet
```

Only one copy runs at a time: opening it again hands the folder to the window that's already open.

Changes between versions are in [CHANGELOG.md](CHANGELOG.md).

## Code

| Folder | What's there |
| --- | --- |
| `Core/` | Files and folders, settings, frecency, labels, undo, the shells, Git status |
| `Core/Remote/` | FTP/SFTP file systems, transfers, the SSH shell |
| `Ops/` | The copy/move/delete/zip engines and the job queue |
| `Search/` | Query parser, parallel tree walker, content scanner, natural sort |
| `Platform/Win32/` | Shell icons, thumbnails, context menu, clipboard, Recycle Bin, network, disk images |
| `Views/` | Main window, panes, list/tile/columns views, preview, terminal, dialogs, themes |

Built with [Avalonia](https://avaloniaui.net/) 12 on .NET 10, with [FluentFTP](https://github.com/robinrodricks/FluentFTP) and [SSH.NET](https://github.com/sshnet/SSH.NET).
