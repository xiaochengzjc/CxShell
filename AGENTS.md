# Repository Guidelines

## Project Structure & Module Organization

ChiXueSsh is a single-project Avalonia desktop SSH client targeting .NET 10. The application entry points are `Program.cs`, `App.axaml`, and `App.axaml.cs`.

- `Views/`: Avalonia `.axaml` views and their code-behind files.
- `ViewModels/`: MVVM presentation logic, commands, and observable state.
- `Models/`: session, SFTP, and monitoring data objects.
- `Services/`: SSH/SFTP connections, persistence, monitoring, and Linux parsing.
- `Terminal/`: terminal buffer, cells, ANSI parsing, and color handling.
- `Controls/` and `Converters/`: reusable UI controls and binding converters.
- `Assets/`: resources embedded through `ChiXueSsh.csproj`.

`AtomUI/` is ignored reference source and explicitly excluded from compilation. Do not treat `bin/` or `obj/` as source.

## Build, Test, and Development Commands

Run commands from the repository root:

```powershell
dotnet restore
dotnet build ChiXueSsh.csproj
dotnet run --project ChiXueSsh.csproj
dotnet format ChiXueSsh.csproj
```

`restore` downloads NuGet dependencies, `build` compiles the app, `run` launches the desktop client, and `format` applies standard .NET formatting.

## Coding Style & Naming Conventions

Use four-space indentation in C# and follow existing file-scoped namespace style. Keep nullable reference types enabled. Use PascalCase for types, public members, views, and view models; use camelCase for parameters and `_camelCase` for private fields. Pair each view with matching names such as `TerminalView.axaml` and `TerminalView.axaml.cs`. Prefer CommunityToolkit.Mvvm attributes (`[ObservableProperty]`, `[RelayCommand]`) over repetitive boilerplate.

## Testing Guidelines

No automated test project currently exists. Before submitting changes, run `dotnet build ChiXueSsh.csproj` and manually exercise affected SSH, SFTP, terminal, or monitoring workflows. New test projects should use names such as `ChiXueSsh.Tests`, with test files named `<ClassName>Tests.cs`; run them with `dotnet test`.

## Terminal Interaction Requirements

`Controls/TerminalControl.cs` owns terminal-grid interaction. Preserve left-button forward, reverse, and multi-line highlighted selection. Right-click opens the `Popup` in `Views/TerminalView.axaml`, containing Copy and Paste; disable Copy without a selection. Copy writes selected text to the system clipboard. Paste normalizes line endings and sends text through `InputReceived` to the SSH shell at its current cursor. Use Avalonia 12 clipboard extensions from `Avalonia.Input.Platform` (`SetTextAsync`, `TryGetTextAsync`) and catch platform failures. Follow the AtomUI 6.0 manual and local `AtomUI/` reference source for menu styling.

Terminal scrollback is stored by `TerminalBuffer`; `TerminalControl` renders a scroll-offset viewport over that history. Mouse wheel up/down must move through scrollback without sending data to the SSH shell. Hide the cursor while viewing history, preserve selection/copy against the visible viewport, and return to the bottom when the user types, pastes, or sends a key command. If output arrives while the user is viewing history, keep the historical viewport stable instead of forcing it to the bottom.

Unexpected SSH or remote-shell closure must trigger automatic terminal reconnection every five seconds. Successful reconnection stops retries; explicitly closing a tab or disconnecting must cancel them. Treat a zero-byte blocking shell read as remote EOF.

## Protocol Implementation Notes

Implement protocols incrementally in the agreed order: SSH, SFTP, FTP, TELNET, RLOGIN, SERIAL, LOCAL, then RDP. `Services/IFileTransferService.cs` is the shared backend contract for file browsers; SFTP uses `SftpService`, and plain FTP uses `FtpService` with FluentFTP. `SftpViewModel` selects the service from `SessionInfo.Protocol`, so keep uploads, downloads, rename, delete, and mkdir behavior protocol-neutral. SFTP startup options on `SessionInfo` include local start folder, remote start folder, and custom server command; local/remote folders are active, while SSH.NET still opens the standard SFTP subsystem unless a lower-level custom server implementation is added.

Terminal protocols use `Services/ITerminalConnectionService.cs`. SSH uses `SshConnectionService`; TELNET uses `TelnetConnectionService` with TCP plus TELNET IAC negotiation filtering; RLOGIN uses `RloginConnectionService` with the standard null-delimited startup handshake and terminal window-size message; SERIAL uses `SerialConnectionService` with `System.IO.Ports`; LOCAL uses `LocalTerminalConnectionService` to run the Windows shell through redirected stdio. TELNET and RLOGIN password-auth sessions prompt once for a password, then use the configured login prompt strings on `SessionInfo` to auto-send username/password when remote text matches. TELNET session options include XDISPLOC (`$PCADDR` substitution), passive/active option negotiation, and forced character-at-a-time mode via LINEMODE rejection. RLOGIN uses the session username for both local and remote login names. SERIAL stores port name, baud rate, data bits, stop bits, parity, and flow control on `SessionInfo`. LOCAL is a basic command shell, not a full ConPTY implementation; keep ordinary command execution working and document any PTY upgrade separately. RDP is not a terminal protocol; `RdpLaunchService` writes a temporary `.rdp` file and starts Windows `mstsc.exe`, while RDP display, device, and audio options are stored on `SessionInfo`. Only SSH tabs should auto-start monitor or SFTP side panels.

SSH X11 forwarding currently uses `ForwardedPortRemote` plus an automatic `DISPLAY=localhost:10.0`-style export to reach a local Windows X server such as Xmanager, VcXsrv, or Xming. This is not a full OpenSSH `x11-req` channel implementation; it depends on remote TCP forwarding being allowed and the local X server listening on the configured display port.

## ZMODEM File Transfer

`Services/ZmodemTransfer.cs` implements `rz`/`sz` in pure C#; do not reintroduce Node, `zmodem.js`, or `zmodem4dotnet` runtime dependencies. `SshConnectionService` exposes raw binary shell data via `BinaryDataReceived`; `TerminalViewModel` detects ZMODEM startup headers and hands the byte stream to `ZmodemTransfer`.

Remote `sz <file>` must prompt for a local folder, download the offered file, swallow the ZMODEM `rz` preamble, high-bit header terminators (`0x8a`, `0x8d`) and XON/XOFF padding, then wait for and consume the final remote `OO`. Remote `rz` must prompt for one or more local files and upload them at the current remote shell location. Keep ZMODEM status messages plain text to avoid ANSI artifacts during binary transfers.

## Commit & Pull Request Guidelines

History currently contains only `first commit`, so no established commit convention exists. Use short imperative subjects, for example `Fix SFTP directory refresh`. Keep commits focused. Pull requests should describe behavior changes, list verification steps, link relevant issues, and include screenshots for visual Avalonia changes. Never commit credentials, private keys, session data, or generated `bin/` and `obj/` output.
