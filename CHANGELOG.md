# PAX Commander changelog

Internal log of what each version added. Not shown in the app yet.
Bump `<Version>` in `PaxCommander.csproj` for every release, and add an entry here.

## 1.3.8 (2026-11-05)
- Self-update: once a day, a few seconds after start-up, PAX Commander checks GitHub's latest release. If there's a
  newer version a card shows what's new with Update now / Later / Skip this version; nothing downloads until you
  click Update now. The installer is checked against the release's .sha256 file (a mismatch is thrown away), then
  an installed copy updates quietly in place, keeping your shortcut and folder-menu choices, and reopens. Portable
  copies open the normal setup screen. "Check for updates" is in the command palette and the ⋯ menu; Settings >
  Behaviour > "Check for updates" turns the daily check off. Copies before 1.3.8 can't update themselves.

## 1.3.7 (2026-11-05)
- Favorite hearts: a faint hollow heart sits left of each folder's icon. Click it to add the folder to favorites (it
  turns red and shows up in the sidebar); click a red heart to remove it. Folders that are already favorites show red
  wherever you browse. Settings > Look > "Favorite hearts" turns them off (on by default).

## 1.3.6 (2026-11-05)
- Mapped network drives get their own list/tiles button in the NETWORK header, separate from DRIVES, so each section
  can be rows or squares on its own. The choice is remembered.

## 1.3.5 (2026-11-05)
- Mapped network drives (the ones Windows/Explorer already has) are listed under NETWORK, below "Network", instead
  of among the local drives; DRIVES keeps local, USB and disc drives. Both follow the list/tile setting.

## 1.3.4 (2026-11-05)
- Slim scrollbars: a thin rounded thumb that stays the same width instead of widening over the list (and covering
  the Size column) when the pointer comes near. It brightens on hover or drag; clicking the track still pages.
  Colors follow the light and dark themes.

## 1.3.3 (2026-11-05)
- Multi-line commands in the terminal: pasted scripts keep their lines (the input grows to about ten lines, then
  scrolls), Shift+Enter starts another line, and Enter runs the whole thing as one piece. PowerShell gets it as a
  single script (loops and braces across lines work); over SSH the lines go to the shell as typed. The terminal
  shows the script's lines (PS> first, >> after). Up/Down move within a multi-line command instead of history.

## 1.3.2 (2026-11-05)
- Popped-out terminal windows are titled "PAX Terminal" (title bar and taskbar) instead of "PAX Commander".

## 1.3.1 (2026-11-05)
- Windows reopen where you left them: the main window and popped-out terminals remember their size, position and
  maximized state. It's saved as you move or resize (not only on a clean exit), and a position that's no longer on
  any screen (monitor unplugged) falls back to centred.
- Popped-out terminals use the app's own title bar: the PAX Commander mark, what the terminal is and its folder, and
  the same minimize / maximize / close buttons (drag empty space to move, double-click to maximize).

## 1.3.0 (2026-11-05)
- Pop-out terminal: the new button in the terminal header moves the same live session into its own window (a
  running command keeps going, history stays). Popped out, it is independent of the main window: browsing there
  neither moves it nor closes it, and its cd does not move the files. The button (or closing the window) docks it
  back into the tab; × in its header ends the session.
- App icon proportions match Explorer's folder: a little taller (100:88 instead of 93:73), so in the taskbar it's
  the same visual size as File Explorer's icon rather than looking short.

## 1.2.1 (2026-11-05)
- Bigger app icon: the Arctic folder now fills its frame edge to edge, like Explorer's own folder icon, so it is
  as large in the taskbar, Start menu and title bar.
- Add connection (the + beside CONNECTIONS) now says SSH outright: "SSH server (SFTP + terminal)…", "FTP / FTPS
  server…" and "Microsoft 365 PowerShell…". The connection type list reads "SSH (SFTP)".
- Map network drive moved into the NETWORK section's + menu; the sidebar list only shows Network itself.

## 1.2.0 (2026-11-02)
- Themes: Dark (the original), Light, or Match Windows (follows Windows' light/dark setting live). Switch in
  Settings ▸ Look, or with Ctrl+Alt+L / the "Light / dark theme" command. Everything recolors instantly: panes,
  lists, menus, tooltips, dialogs, previews, the terminal, and code colors (VS Code's light set in Light).
- New "Arctic" app icon: ice-white folder, blue rim and sheet, "PAX_" in bright-over-deep blue. The icon no
  longer follows the accent color.
- Terminal header: the Windows Terminal button is shorter and the title shortens itself in narrow panes, so nothing
  overlaps.

## 1.1.0 (2026-11-02)
- Terminal suggestions while typing: saved snippets first, then commands you've run before (most used and most
  recent first), then common admin commands for whatever you're connected to (Exchange Online, Graph, Teams,
  SharePoint, Security & Compliance, Azure, PowerShell, Linux). Arrow keys pick; Tab or Enter fills the line
  (it isn't run) and selects the first `<placeholder>`.
- Terminal **Commands** menu: browse the library by service, save the typed line as a snippet, remove snippets,
  clear remembered commands. Commands that look like they carry a password are never remembered.
- Full-size terminal: ⤢ in the terminal header or Ctrl+Shift+` gives it the whole tab, and the same again restores it.
- Auto-preview covers everything it can show: video, audio, PDF and Office documents, archives, and every kind of text
  (.txt, .log, .gitignore and other dot files, configs, code). Not folders, programs or unknown binaries.
- Colored previews for .gitignore, .gitattributes, .dockerignore, .npmrc, .gitmodules and similar dot files.
- Network: a NETWORK section in the sidebar. **Network** lists computers on the local network (same discovery as
  Explorer). Opening a computer lists its shared folders (admin shares hidden), with breadcrumbs Network › server ›
  share. Up goes share → computer → Network. You can type `network` or `\server`.
- Map network drive: pick a letter and a `\server\share`, choose whether to reconnect at sign-in, and optionally use
  a different account (optionally remembered in Windows Credential Manager). Mapped drives can be disconnected from
  the sidebar or This PC; shared folders get "Map network drive…" in their menu.
- Fix: non-English characters (ü, ✓, 日本) typed into the PowerShell terminal no longer get garbled.

## 1.0.0 (2026-11-02)
- Dual-pane file manager (side by side or stacked) with tabs, Details / Columns / Thumbnails views, natural sort,
  live folder watching, folder sizes, git status, labels, frecency (Frequent), favorites, batch rename, Quick Look,
  a command palette (Ctrl+K), and undo/redo for file operations.
- Background file-operation queue with conflict handling, Recycle Bin, and Windows shell context menus and icons.
- Mounted ISO/VHD detection and unmounting; drive list or compact drive tiles in the sidebar.
- Accent color picker (baby blue by default); custom title bar; the "PAX_" folder icon.
- FTP, FTPS and SFTP connections with saved logins in Windows Credential Manager, host key and certificate checks,
  and transfers between this PC and servers or between servers.
- Docked terminal per tab: SSH on servers, PowerShell on this PC. The terminal and the file view follow each
  other's folder. Windows commands work over SSH and Linux-style commands work in PowerShell.
- Microsoft 365 admin PowerShell connections (Exchange Online, Graph, Teams, SharePoint, Security & Compliance,
  Azure) with browser, device-code, certificate or saved-password sign-in, and a "Sign-in code" link button.
- Code previews colored by language with line numbers; minified JSON and XML are laid out. Auto-preview mode.
- Installer: paxcomm-setup.exe (self-contained, per-user, no admin needed), with silent install and uninstall.
