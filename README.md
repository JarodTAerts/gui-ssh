# GUI-SSH

A web-based SSH client with a full desktop-like GUI experience. Connect to remote Linux servers over SSH and interact with them through a terminal emulator **and** a visual desktop environment — complete with a file manager, text editor, task manager, and more — all running in your browser.

Built with .NET 10, Blazor (Interactive Auto), MudBlazor, SSH.NET, and xterm.js.

![.NET 10](https://img.shields.io/badge/.NET-10-purple)
![Blazor](https://img.shields.io/badge/Blazor-Auto-blue)
![License](https://img.shields.io/badge/license-MIT-green)

---

## Features

- **Terminal** — Full xterm.js terminal emulator with 256-color support, rendered server-side for real-time shell interaction
- **Desktop Environment** — A windowed GUI workspace (rendered via WebAssembly) with:
  - **File Manager** — Browse, navigate, create, rename, move, copy, delete files and folders; upload and download files
  - **Text Editor** — Open remote files, create new files, Save / Save As with remote path dialogs
  - **Task Manager** — Live view of running processes, CPU/memory/swap/uptime stats, kill processes, auto-refresh every 5 seconds
  - **Info Panel** — View detailed file/folder metadata and permissions
- **Start Menu** — Launch new File Manager, Text Editor, or Task Manager windows from the taskbar
- **Multiple Connections** — Save and manage multiple SSH connections in the sidebar; switch between sessions while preserving state
- **Authentication** — Password and private key (PEM) authentication
- **Encrypted Credential Storage** — Saved credentials are encrypted client-side using AES-GCM + PBKDF2 via the Web Crypto API and stored in the browser's IndexedDB — never on the server
- **Light/Dark Theme** — Catppuccin-inspired color scheme with persistent preference (saved to localStorage)
- **File Transfer** — Upload files from your machine and download files/folders (directories are archived as tar.gz)
- **Session Management** — Idle sessions are automatically evicted after 10 minutes of inactivity

---

## Architecture

```
┌─────────────────────────────────────────────────────┐
│                    Browser                          │
│  ┌──────────────┐  ┌────────────────────────────┐   │
│  │  Terminal     │  │  Desktop (WASM)            │   │
│  │  (Server-    │  │  File Manager, Editor,     │   │
│  │   rendered)  │  │  Task Manager, Taskbar     │   │
│  └──────┬───────┘  └────────────┬───────────────┘   │
│         │ SignalR                │ HTTP API          │
└─────────┼───────────────────────┼───────────────────┘
          │                       │
┌─────────┼───────────────────────┼───────────────────┐
│         ▼                       ▼    ASP.NET Core   │
│  ┌─────────────┐  ┌──────────────────────────┐      │
│  │ ShellStream │  │ /api/ssh/* endpoints     │      │
│  │ (real-time) │  │ (exec, upload, download) │      │
│  └──────┬──────┘  └────────────┬─────────────┘      │
│         │                      │                    │
│         ▼                      ▼                    │
│  ┌──────────────────────────────────────────┐       │
│  │         SshSessionManager (singleton)    │       │
│  │         SSH.NET connections + SFTP       │       │
│  └──────────────────┬───────────────────────┘       │
└─────────────────────┼───────────────────────────────┘
                      │ SSH
                      ▼
               Remote Linux Server
```

**Rendering modes:**
- **Terminal** uses Interactive Server (SignalR) for low-latency, real-time shell I/O
- **Desktop** uses Interactive WebAssembly — the file manager, editor, and other tools run in the browser and call back to the server via HTTP API

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A remote Linux server with SSH access (password or key-based)

---

## Getting Started

### 1. Clone the repository

```bash
git clone https://github.com/JarodTAerts/gui-ssh.git
cd gui-ssh
```

### 2. Build

```bash
dotnet build GuiSsh.slnx
```

### 3. Run

```bash
cd src/GuiSsh/GuiSsh
dotnet run
```

The app will start on:
- **HTTP**: http://localhost:5240
- **HTTPS**: https://localhost:7107

### 4. Connect

1. Open the app in your browser
2. Click the **+** button in the sidebar to add a new SSH connection
3. Enter host, port (default 22), username, and password or private key
4. Click **Connect**
5. Use the **Terminal** tab for a full shell, or the **Desktop** tab for the GUI environment

---

## Project Structure

```
gui-ssh/
├── GuiSsh.slnx                          # Solution file
├── DESIGN.md                             # Detailed architecture & design document
├── src/GuiSsh/
│   ├── GuiSsh/                           # Server project (ASP.NET Core host)
│   │   ├── Components/
│   │   │   ├── App.razor                 # Root component
│   │   │   ├── Layout/
│   │   │   │   ├── AppShell.razor        # Main layout (app bar, sidebar, theme)
│   │   │   │   └── SessionTabs.razor     # Terminal/Desktop tab switcher
│   │   │   └── Terminal/
│   │   │       └── TerminalView.razor    # xterm.js terminal (server-rendered)
│   │   ├── Services/
│   │   │   ├── SshSessionManager.cs      # Core SSH session management
│   │   │   ├── SshConnectionFactory.cs   # SSH client/stream creation
│   │   │   ├── ServerSshService.cs       # ISshService implementation (server)
│   │   │   ├── SshApiEndpoints.cs        # Minimal API endpoints
│   │   │   └── SessionEvictionService.cs # Background idle session cleanup
│   │   ├── wwwroot/
│   │   │   ├── app.css                   # Global styles (Catppuccin theme)
│   │   │   └── js/                       # JS interop modules
│   │   └── Program.cs                    # App startup & DI registration
│   │
│   └── GuiSsh.Client/                    # Client project (Blazor WASM)
│       ├── Components/
│       │   ├── Desktop/
│       │   │   ├── DesktopView.razor      # Desktop window manager
│       │   │   ├── DesktopWindow.razor    # Draggable/resizable window
│       │   │   ├── FileManager.razor      # File browser
│       │   │   ├── TaskManager.razor      # Process viewer / system monitor
│       │   │   ├── Taskbar.razor          # Start menu & window buttons
│       │   │   ├── InfoPanel.razor        # File info viewer
│       │   │   └── InputDialog.razor      # Text input dialog
│       │   ├── Editor/
│       │   │   └── EditorWindow.razor     # Text file editor
│       │   └── Layout/
│       │       ├── Sidebar.razor          # Connection list
│       │       └── ConnectionForm.razor   # Add/edit connection dialog
│       ├── Models/                        # Shared data models
│       ├── Services/
│       │   ├── ISshService.cs             # Service interface
│       │   ├── ClientSshService.cs        # ISshService via HTTP (WASM)
│       │   ├── ShellCommandBuilder.cs     # Shell command generation
│       │   └── ShellOutputParser.cs       # ls output parser
│       └── Program.cs                     # WASM startup & DI registration
└── tests/                                # (placeholder)
```

---

## Configuration

### App Settings

Default configuration is in `src/GuiSsh/GuiSsh/appsettings.json`. No special configuration is required for local development.

### Launch URLs

Configured in `src/GuiSsh/GuiSsh/Properties/launchSettings.json`:

| Profile | URL |
|---------|-----|
| http | `http://localhost:5240` |
| https | `https://localhost:7107` |

---

## Security Notes

- **Credentials are never stored on the server.** Saved connections are encrypted client-side using AES-GCM with a key derived via PBKDF2, and stored in the browser's IndexedDB.
- SSH connections are established server-side using SSH.NET. The server holds active SSH sessions in memory and evicts idle ones after 10 minutes.
- All shell commands executed through the desktop GUI are constructed with proper POSIX shell escaping to prevent injection.

---

## Tech Stack

| Component | Technology |
|-----------|-----------|
| Framework | .NET 10, ASP.NET Core |
| UI | Blazor (Interactive Auto), MudBlazor 9 |
| Terminal | xterm.js 5.5 |
| SSH | SSH.NET 2025.1.0 |
| Crypto | Web Crypto API (AES-GCM, PBKDF2) |
| Styling | Catppuccin color palette, CSS custom properties |

---

## License

MIT
