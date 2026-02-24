# GUI-SSH: Blazor Hybrid SSH & Visual Desktop Client

## Design Document

---

## 1. Project Overview

GUI-SSH is a **.NET 10 Blazor Web App** using **Interactive Auto** rendering (server + WASM hybrid) that provides two modes of interacting with remote servers over SSH:

1. **Terminal Mode** — A full interactive SSH terminal in the browser (xterm.js).
2. **Desktop/GUI Mode** — A mini desktop environment with a Windows Explorer-like file manager, where **all operations generate and execute shell commands** through the underlying SSH terminal. Users can visually browse, copy, delete, edit files — and see exactly what commands are being run.

Run locally with `dotnet run`. Deployment target is flexible (Docker, App Service, container, etc.) — decided later.

**Key principles:**
- **Hybrid rendering** — The app starts server-rendered for instant first paint. WASM downloads in the background. Once ready, interactive UI components transition to running in the browser. SSH-bound components (terminal) stay server-rendered.
- **Two projects, one app** — A server host project (SSH.NET, API endpoints, server-rendered components) and a client project (WASM components for desktop UI). `dotnet run` on the server project starts everything.
- **SSH.NET runs on the server** — No external proxy. The server C# code directly opens SSH connections. WASM components call server API endpoints for SSH operations.
- **All user state in the browser** — Saved connections, encrypted credentials, and preferences stored in localStorage/IndexedDB.
- **Backend stores nothing** — No database, no config files, no credential storage. Stateless beyond in-memory SSH sessions.
- **Remote server handles auth** — The app has no login. The SSH server decides who gets in.

---

## 2. Architecture

### 2.1 High-Level Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                          Browser                             │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐  │
│  │                                                        │  │
│  │  ┌─ Server-Rendered (SignalR) ──────────────────────┐  │  │
│  │  │  TerminalView (xterm.js)                         │  │  │
│  │  │  • Persistent ShellStream I/O                    │  │  │
│  │  │  • Runs on server, pushes to browser via SignalR │  │  │
│  │  └─────────────────────────────────────────────────-┘  │  │
│  │                                                        │  │
│  │  ┌─ WASM-Rendered (after initial server paint) ─────┐  │  │
│  │  │  Sidebar, Desktop, FileManager, Editor, Taskbar  │  │  │
│  │  │  • C# runs in browser (fast, no round-trip)      │  │  │
│  │  │  • Calls server API for SSH operations           │  │  │
│  │  └─────────────────────────────────────────────────-┘  │  │
│  │                                                        │  │
│  │  localStorage / IndexedDB                              │  │
│  │  • Saved connections (encrypted)  • Preferences        │  │
│  └────────────────────────────────────────────────────────┘  │
│        │ SignalR circuit          │ HTTP API calls           │
└────────┼──────────────────────────┼──────────────────────────┘
         │                          │
┌────────┼──────────────────────────┼──────────────────────────┐
│        ▼                          ▼                          │
│   ASP.NET Core Host (.NET 10)                                │
│                                                              │
│  ┌─────────────────┐    ┌──────────────────────────────┐     │
│  │ Blazor Server   │    │ Minimal API endpoints        │     │
│  │ circuit handler │    │ POST /api/ssh/connect         │     │
│  │ (TerminalView)  │    │ POST /api/ssh/exec            │     │
│  └────────┬────────┘    │ POST /api/ssh/disconnect      │     │
│           │             └──────────────┬───────────────┘     │
│           │                            │                     │
│  ┌────────┴────────────────────────────┴───────────────┐     │
│  │           SshSessionManager (Singleton)             │     │
│  │                                                     │     │
│  │  ┌─────────────────┐  ┌────────────────────────┐    │     │
│  │  │  Shell Channel  │  │  Exec Channel          │    │     │
│  │  │  (Terminal I/O) │  │  (GUI file operations) │    │     │
│  │  └────────┬────────┘  └───────────┬────────────┘    │     │
│  │           └───────────┬───────────┘                 │     │
│  │                SSH.NET (Renci.SshNet)                │     │
│  └───────────────────────┬─────────────────────────────┘     │
│                          │                                   │
└──────────────────────────┼───────────────────────────────────┘
                           │ TCP port 22
                   ┌───────┴────────┐
                   │  Remote Server │
                   │  (auth is here)│
                   └────────────────┘
```

### 2.2 Render Mode Strategy

.NET 10 Blazor's **Interactive Auto** mode starts each component server-rendered (instant first paint via SignalR), then seamlessly transitions to WASM once the runtime has downloaded in the background. We use this globally, with one exception:

| Component | Render Mode | Why |
|-----------|-------------|-----|
| **TerminalView** | `@rendermode InteractiveServer` | Needs persistent `ShellStream` connection on the server. High-frequency bidirectional I/O flows directly through SignalR — no benefit to running in WASM since every byte must reach the server anyway. |
| **Everything else** | `@rendermode InteractiveAuto` | Desktop chrome (windows, drag/resize, taskbar), file manager, editor, sidebar — all benefit from running in the browser. No round-trip latency for UI interactions. SSH data fetched via HTTP API calls to the server. |

```razor
@* Home.razor — page-level default *@
@page "/"
@rendermode InteractiveAuto

@* TerminalView.razor — forced server *@
@rendermode InteractiveServer
```

**Why this split matters:**
- **Terminal** is latency-insensitive to render location (keystrokes go to server regardless), but latency-sensitive to data push (server → browser). Server rendering lets `ShellStream.DataReceived` push directly via SignalR without serializing through an extra API.
- **Desktop UI** is latency-sensitive to interactions (dragging windows, clicking menus, typing in editor). Running in WASM means these are instant — no server round-trip for pure UI work.

### 2.3 How WASM Components Call SSH Operations

WASM components can't access SSH.NET directly (it runs on the server). They use an `ISshService` abstraction with two implementations:

```csharp
// In GuiSsh.Client (shared interface — available to both projects)
public interface ISshService
{
    Task<string> ConnectAsync(string sessionId, string host, int port, string user, string password);
    Task DisconnectAsync(string sessionId);
    Task<CommandResult> ExecuteAsync(string sessionId, string command);
    Task<bool> IsConnectedAsync(string sessionId);
}

// In GuiSsh (server project) — registered when running as Server
public class ServerSshService : ISshService
{
    private readonly SshSessionManager _manager;
    // Calls SSH.NET directly
}

// In GuiSsh.Client (client project) — registered when running as WASM
public class ClientSshService : ISshService
{
    private readonly HttpClient _http;
    // Calls server API: POST /api/ssh/exec, etc.
}
```

DI registration picks the right implementation based on render context:
```csharp
// Server Program.cs
builder.Services.AddScoped<ISshService, ServerSshService>();

// Client Program.cs  
builder.Services.AddScoped<ISshService, ClientSshService>();
```

The server exposes matching API endpoints:
```
POST /api/ssh/connect       { host, port, user, password } → sessionId
POST /api/ssh/exec          { sessionId, command }         → CommandResult
POST /api/ssh/disconnect    { sessionId }                  → ok
GET  /api/ssh/status/{id}                                  → connected/disconnected
```

### 2.4 Why Hybrid Simplifies This App

| Pure Server (previous design) | Hybrid Server + WASM (current) |
|-------------------------------|--------------------------------|
| All UI interactions round-trip through SignalR | UI-heavy components (desktop, editor) run in browser — **instant interaction** |
| Window dragging, menu clicks add server load | Pure UI work offloaded to WASM — **server only handles SSH** |
| Every user holds a full SignalR circuit for all components | SignalR circuit only needed for terminal — **reduced server memory** |
| Large Blazor component trees on server | Most components run client-side — **better scalability** |
| `dotnet run` to start | Still `dotnet run` to start — **no extra complexity** |

### 2.5 Project Structure

```
gui-ssh/
├── DESIGN.md
├── GuiSsh.sln
│
├── src/
│   ├── GuiSsh/                              # Server host project
│   │   ├── GuiSsh.csproj                     # .NET 10, references GuiSsh.Client
│   │   ├── Program.cs                        # Host builder, DI, API endpoints, middleware
│   │   ├── appsettings.json
│   │   │
│   │   ├── Components/
│   │   │   ├── App.razor                     # Root component (sets global rendermode)
│   │   │   ├── Routes.razor                  # Router
│   │   │   │
│   │   │   ├── Layout/
│   │   │   │   └── MainLayout.razor          # Shell: sidebar + main body
│   │   │   │
│   │   │   ├── Pages/
│   │   │   │   └── Home.razor                # Main app page (@rendermode InteractiveAuto)
│   │   │   │
│   │   │   └── Terminal/
│   │   │       └── TerminalView.razor        # xterm.js wrapper (@rendermode InteractiveServer)
│   │   │
│   │   ├── Services/
│   │   │   ├── SshSessionManager.cs          # Singleton — manages all SSH connections
│   │   │   ├── SshConnectionFactory.cs       # Creates SSH.NET clients
│   │   │   ├── ServerSshService.cs           # ISshService impl — direct SSH.NET calls
│   │   │   └── SshApiEndpoints.cs            # Minimal API endpoints for WASM clients
│   │   │
│   │   └── wwwroot/                          # Static files served by host
│   │
│   └── GuiSsh.Client/                       # Client (WASM) project
│       ├── GuiSsh.Client.csproj              # .NET 10, Blazor WASM
│       ├── Program.cs                        # WASM service registration
│       ├── _Imports.razor
│       │
│       ├── Components/
│       │   ├── Layout/
│       │   │   ├── Sidebar.razor             # Saved connections list
│       │   │   ├── ConnectionForm.razor      # Add/edit connection dialog
│       │   │   └── SessionTabs.razor         # Terminal | Desktop tab bar
│       │   │
│       │   ├── Desktop/
│       │   │   ├── DesktopView.razor         # Desktop environment container
│       │   │   ├── Taskbar.razor             # Bottom taskbar (open windows)
│       │   │   ├── FileManager.razor         # Explorer-like file browser
│       │   │   ├── FileList.razor            # File/folder grid or list
│       │   │   ├── FileContextMenu.razor     # Right-click menu
│       │   │   ├── BreadcrumbNav.razor       # Path breadcrumb
│       │   │   └── DesktopWindow.razor       # Draggable/resizable window
│       │   │
│       │   └── Editor/
│       │       ├── ITextEditor.cs            # Editor abstraction interface
│       │       ├── SimpleTextEditor.razor    # Basic <textarea> editor (MVP)
│       │       └── EditorWindow.razor        # Editor inside a desktop window
│       │
│       ├── Services/
│       │   ├── ISshService.cs                # Shared interface (used by both projects)
│       │   ├── ClientSshService.cs           # ISshService impl — calls server HTTP API
│       │   ├── ShellCommandBuilder.cs        # Generates shell commands for GUI ops
│       │   ├── ShellOutputParser.cs          # Parses ls, stat, etc. output
│       │   ├── ConnectionStore.cs            # localStorage/IndexedDB CRUD
│       │   └── CryptoService.cs              # Web Crypto API for credential encryption
│       │
│       ├── Models/
│       │   ├── SavedConnection.cs            # Host, port, user (creds separate)
│       │   ├── FileEntry.cs                  # Parsed file/dir info
│       │   ├── CommandResult.cs              # stdout, stderr, exit code
│       │   └── DesktopWindowState.cs         # Window position, size, z-order
│       │
│       └── wwwroot/
│           ├── js/
│           │   └── terminal-interop.js       # xterm.js JS interop
│           └── css/
│               ├── app.css                   # Global styles
│               └── desktop.css               # Desktop environment styles
│
└── tests/
    ├── GuiSsh.Tests/                         # Server-side tests
    └── GuiSsh.Client.Tests/                  # Client-side / shared tests
```

**Why two projects?** .NET 10 Blazor Web App with Interactive Auto requires this split:
- **Server project** (`GuiSsh`) — Hosts the app, runs server-rendered components, holds SSH.NET, exposes API endpoints. References the client project.
- **Client project** (`GuiSsh.Client`) — Contains components and services that can run in WASM. Also referenced by the server project for server-side prerendering.

`dotnet run` on the server project starts everything — no separate steps needed.

### 2.6 Dependency Injection & Lifetime

```csharp
// ─── Server: Program.cs ───
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddSingleton<SshSessionManager>();        // Manages all SSH sessions
builder.Services.AddTransient<SshConnectionFactory>();
builder.Services.AddScoped<ISshService, ServerSshService>(); // Server-side: direct SSH.NET

// ─── Client: Program.cs ───
builder.Services.AddScoped<ISshService, ClientSshService>(); // WASM-side: HTTP API calls
builder.Services.AddScoped<ConnectionStore>();               // Browser storage via JS interop
builder.Services.AddScoped<CryptoService>();                 // Web Crypto API via JS interop
builder.Services.AddSingleton<ShellCommandBuilder>();
builder.Services.AddSingleton<ShellOutputParser>();
```

**Key lifetime rules:**
- `SshSessionManager` is a **singleton** on the server — it survives across circuits and API calls, holding SSH connections keyed by session ID.
- `ISshService` is **scoped** — the correct implementation is injected based on whether the component is running on Server or WASM.
- `ConnectionStore` and `CryptoService` need JS interop → always run in the browser context (scoped per circuit or WASM instance).

---

## 3. UI Design — Mini Desktop Environment

### 3.1 Overall Layout

```
┌─────────────────────────────────────────────────────────────┐
│  GUI-SSH                                              ─ □ x │
├────────┬────────────────────────────────────────────────────┤
│        │  [Terminal]  [Desktop]                             │
│ SAVED  ├────────────────────────────────────────────────────┤
│ CONNS  │                                                    │
│        │   (Active mode content fills this area)            │
│ ┌────┐ │                                                    │
│ │🟢  │ │   Terminal Mode:  xterm.js full-size terminal      │
│ │srv1│ │                                                    │
│ └────┘ │   — OR —                                           │
│        │                                                    │
│ ┌────┐ │   Desktop Mode:  Mini desktop environment          │
│ │⚪  │ │   ┌──────────────────────────────────────────┐     │
│ │srv2│ │   │ 📁 File Manager              ─ □ x      │     │
│ └────┘ │   │ / > home > user                          │     │
│        │   │ ┌─────┐ ┌─────┐ ┌─────┐ ┌─────┐        │     │
│ ┌────┐ │   │ │ 📁  │ │ 📄  │ │ 📄  │ │ 📁  │        │     │
│ │    │ │   │ │.ssh │ │.bash│ │ doc │ │ src │        │     │
│ │[+] │ │   │ └─────┘ └─────┘ └─────┘ └─────┘        │     │
│ │Add │ │   └──────────────────────────────────────────┘     │
│ └────┘ │                                                    │
│        │   ┌──────────────────────────────────────────┐     │
│        │   │ 📝 Editor: config.yml         ─ □ x      │     │
│        │   │ server:                                   │     │
│        │   │   port: 8080                              │     │
│        │   │   host: 0.0.0.0                           │     │
│        │   │                     [Save] [Close]        │     │
│        │   └──────────────────────────────────────────┘     │
│        ├────────────────────────────────────────────────────┤
│        │ ▶ [File Manager]  [Editor: config.yml]    (taskbar)│
└────────┴────────────────────────────────────────────────────┘
```

### 3.2 Sidebar — Saved Connections

- Persistent list of saved SSH connections (stored in browser IndexedDB via JS interop)
- Each entry shows: name/alias, host, connection status indicator (🟢 connected, ⚪ disconnected, 🔴 error)
- Click a connection → connects if not already, switches to that session
- **[+] Add** button opens connection form dialog
- Right-click / kebab menu: Edit, Duplicate, Delete, Connect/Disconnect
- Drag to reorder (stretch goal)

### 3.3 Session Tabs — Terminal | Desktop

Once a connection is selected, the main body shows two tabs:

| Tab | Content |
|-----|---------|
| **Terminal** | Full xterm.js interactive SSH terminal |
| **Desktop** | Mini desktop environment with file manager windows |

Both tabs share the same SSH connection. Switching is instant — the terminal stays alive when viewing Desktop mode and vice versa.

### 3.4 Desktop Mode — Mini Desktop Environment

The desktop mode renders a **windowed environment** reminiscent of a simple OS desktop:

- **Desktop surface** — dark/themed background area where windows float
- **Windows** — draggable, resizable containers (File Manager, Editor, etc.)
  - Title bar with window name, minimize/maximize/close buttons
  - Each window has a type: FileManager, TextEditor, ImageViewer, etc.
- **Taskbar** — bottom bar showing open windows, click to focus/minimize
- **Context menu** — right-click on desktop to: Open File Manager, Open Terminal, Refresh

#### 3.4.1 File Manager Window

The file manager is the primary desktop window:

- **Breadcrumb navigation** — `/` > `home` > `user` > `projects`
- **Address bar** — editable path input
- **File grid** — icon + name for each file/directory
  - Folders: 📁 icon, double-click to navigate
  - Files: icon by extension (📄 text, 🖼️ image, ⚙️ config, etc.)
- **Detail view toggle** — switch between icon grid and table (name, size, date, permissions)
- **Context menu** (right-click on file):
  - Open / Edit (text files open in Editor window)
  - Copy, Cut, Paste
  - Rename
  - Delete (with confirmation)
  - Properties (permissions, size, dates)
- **Toolbar** — New File, New Folder, Upload, Download, Refresh, View toggle, Show hidden files

#### 3.4.2 Editor Window

- Opens as a separate floating window on the desktop
- **MVP**: Simple `<textarea>` with monospace font, basic syntax highlighting via CSS
- **Componentized** via `ITextEditor` interface to allow swapping in CodeMirror later
- Title bar shows filename
- Footer: [Save] [Save As] [Close] buttons
- Save runs `cat > filename << 'GUISSH_EOF' ... GUISSH_EOF` or `echo "..." > file` via SSH

#### 3.4.3 Window Management

```csharp
class DesktopWindowState {
    string Id;
    string Title;
    WindowType Type;          // FileManager, Editor, ImageViewer
    double X, Y;              // Position
    double Width, Height;     // Size
    int ZIndex;               // Stacking order
    bool IsMinimized;
    bool IsMaximized;
    Dictionary<string, object> Data;  // Window-specific state
}
```

---

## 4. Core Design Principle: GUI Generates Shell Commands

> **All file operations in Desktop mode generate and execute real shell commands through the SSH connection.**

This is the defining architectural principle. The GUI is a visual layer on top of the terminal — not a separate channel.

### 4.1 Command Generation

| GUI Action | Generated Shell Command |
|------------|------------------------|
| List directory | `ls -la --color=never --time-style=long-iso /path` |
| Navigate to folder | `cd /path && ls -la --color=never --time-style=long-iso` |
| View file content | `cat /path/to/file` |
| Create file | `touch /path/to/newfile` |
| Create directory | `mkdir -p /path/to/newdir` |
| Delete file | `rm /path/to/file` |
| Delete directory | `rm -rf /path/to/dir` (with confirmation) |
| Rename/Move | `mv /path/old /path/new` |
| Copy | `cp -r /path/src /path/dst` |
| Change permissions | `chmod 755 /path/to/file` |
| File info | `stat /path/to/file` |
| Read for editor | `cat /path/to/file` |
| Save from editor | `cat > /path/to/file << 'GUISSH_EOF'\n...\nGUISH_EOF` |
| Search files | `find /path -name "pattern"` |
| Disk usage | `du -sh /path/*` |

### 4.2 Command Execution Strategy

The SSH.NET `SshClient` supports multiple channels on a single connection. We use:

- **Channel 1 — Interactive Shell (`ShellStream`)**: The terminal tab (server-rendered). User types commands, sees output. Data flows from xterm.js via JS interop → `TerminalView` component (on server) → `ShellStream.Write()`, and `ShellStream.DataReceived` → server → JS interop via SignalR → xterm.js display.
- **Channel 2+ — Exec Channels (`SshClient.RunCommand()`)**: GUI file operations. Called from `SshSessionManager` on the server — either directly (server render mode) or via API endpoint (WASM render mode). Each operation opens a short-lived exec channel, runs the command, returns stdout/stderr. Does **not** interfere with the interactive shell.

```
┌────────────────────────────────────────────────┐
│         SSH.NET SshClient (one per connection)  │
│                                                 │
│  ShellStream (persistent)                       │
│    └─ Terminal tab: interactive I/O             │
│                                                 │
│  RunCommand() (on-demand exec channels)         │
│    └─ GUI ops: ls, cat, rm, mv, chmod, etc.    │
│    └─ Output parsed by ShellOutputParser        │
│    └─ Optionally echoed to terminal display     │
│                                                 │
│  (Both share the same TCP connection/session)   │
└────────────────────────────────────────────────┘
```

### 4.3 Output Parsing

The `ShellOutputParser` service parses structured command output:

```csharp
public class ShellOutputParser
{
    // Parse `ls -la --time-style=long-iso` output into FileEntry list
    public List<FileEntry> ParseLsOutput(string output);
    
    // Parse `stat` output into file metadata
    public FileMetadata ParseStatOutput(string output);
    
    // Parse `du -sh` output into size info
    public DiskUsage ParseDuOutput(string output);
}
```

**Example `ls -la` parsing:**
```
total 16
drwxr-xr-x  4 user group 4096 2026-02-24 10:30 .
drwxr-xr-x 10 user group 4096 2026-02-23 09:15 ..
-rw-r--r--  1 user group  220 2026-02-20 14:00 .bashrc
drwxr-xr-x  2 user group 4096 2026-02-24 10:30 projects
```
→ Parsed into `List<FileEntry>` with permissions, owner, group, size, date, name, type.

### 4.4 Challenges with Command-Based File Operations

| Challenge | Mitigation |
|-----------|------------|
| **Parsing fragility** — `ls` output format varies across distros | Use `--time-style=long-iso` and `--color=never` flags; fall back to `stat --format=...` for structured output |
| **Filenames with spaces/special chars** | Always shell-escape filenames: `printf '%q' "$name"` or use single-quote wrapping |
| **Large file reads** — `cat` on a 100MB file floods the channel | Cap preview size (first 1MB); warn user; use `head -c 1048576 file` |
| **Binary files** — `cat` on binary corrupts terminal | Detect via `file --mime-type`; show preview only for text types |
| **No SFTP upload/download** | Upload: base64-encode in browser → `base64 -d > file` via exec channel. Download: `base64 file` via exec → decode in browser. Size-limited. |
| **Concurrent operations** | `SshClient.RunCommand()` is thread-safe; SSH.NET handles channel multiplexing |

---

## 5. SSH Session Management

### 5.1 SshSessionManager (Singleton — Server-Side)

Unlike the previous scoped-per-circuit design, the session manager is now a **singleton** on the server. This is because WASM components communicate with it via HTTP API — they don't share a DI scope with the server. Sessions are keyed by a client-generated session ID.

```csharp
public class SshSessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ActiveSession> _sessions = new();
    
    public async Task<string> ConnectAsync(string sessionId, string host, int port,
        string username, string password);
    
    public ActiveSession? GetSession(string sessionId);
    
    public async Task DisconnectAsync(string sessionId);
    
    // Runs a command on the exec channel (for GUI operations)
    public async Task<CommandResult> ExecuteAsync(string sessionId, string command);
    
    // Returns the ShellStream for terminal components (server-rendered only)
    public ShellStream? GetShellStream(string sessionId);
    
    // Cleanup idle sessions (background hosted service)
    public void EvictExpired(TimeSpan maxIdle);
}

public class ActiveSession
{
    public string SessionId { get; set; }
    public SshClient SshClient { get; set; }
    public ShellStream ShellStream { get; set; }
    public DateTime LastActivity { get; set; }
    public bool IsConnected => SshClient?.IsConnected ?? false;
}
```

`CommandResult` lives in the shared client project (used by both `ISshService` implementations):

```csharp
// In GuiSsh.Client/Models/CommandResult.cs
public class CommandResult
{
    public string StdOut { get; set; }
    public string StdErr { get; set; }
    public int ExitCode { get; set; }
    public bool Success => ExitCode == 0;
}
```

### 5.2 Lifecycle

1. **User clicks connection** → WASM component decrypts credentials from IndexedDB → calls `ISshService.ConnectAsync()` → (WASM impl) HTTP POST to `/api/ssh/connect` → `SshSessionManager.ConnectAsync()` → SSH.NET connects, ShellStream opened → returns session ID
2. **User opens terminal** → `TerminalView` (server-rendered) gets `ShellStream` from `SshSessionManager` via session ID → wires up bidirectional I/O
3. **User runs GUI file operation** → WASM `FileManager` calls `ISshService.ExecuteAsync(sessionId, cmd)` → HTTP POST → `SshSessionManager.ExecuteAsync()` → SSH.NET `RunCommand()` → returns `CommandResult`
4. **User switches connections** → Different session ID; all sessions remain alive
5. **User closes tab / navigates away** → WASM calls `ISshService.DisconnectAsync()` (best-effort) + background `HostedService` evicts idle sessions after timeout (default 10 min)

### 5.3 Session ID Strategy

The session ID ties the WASM client to a server-side SSH connection. It is:
- A GUID generated client-side on connect
- Sent as a header or parameter with every API call and used by `TerminalView` to locate its `ShellStream`
- Not a secret (the SSH credentials are not recoverable from it) — but should be treated as an opaque token

### 5.4 Terminal Data Flow

`TerminalView` is the one component forced to `@rendermode InteractiveServer`. It runs on the server and accesses `ShellStream` directly:

```
User keystroke
  → xterm.js onData callback
  → JS interop: dotnetRef.invokeMethodAsync('OnTerminalInput', data)
  → TerminalView.razor [JSInvokable] (runs on server, via SignalR)
  → ShellStream.Write(Encoding.UTF8.GetBytes(data))
  → Remote SSH server

Remote SSH server sends output
  → ShellStream.DataReceived event (fires on server)
  → TerminalView batches data (16ms buffer)
  → JS interop: JS.InvokeVoidAsync('terminalInterop.write', encodedData)
  → SignalR → browser → xterm.js terminal.write()
  → Screen
```

The SignalR circuit for `TerminalView` is the only persistent server connection. All other components have transitioned to WASM and communicate via stateless HTTP API calls.

---

## 6. Connection & Credential Management

### 6.1 Storage Architecture (All Client-Side)

```
┌─────────────────────────────────────────────┐
│              Browser Storage                 │
│                                              │
│  localStorage:                               │
│    • Connection list (metadata only)         │
│    • UI preferences (theme, layout)          │
│    • Last-used connection ID                 │
│                                              │
│  IndexedDB (encrypted):                      │
│    • Passwords (AES-GCM encrypted)           │
│    • Private keys (AES-GCM encrypted)        │
│    • Encryption key derived from master       │
│      password via PBKDF2                     │
│                                              │
│  Server stores: NOTHING                      │
└─────────────────────────────────────────────┘
```

### 6.2 Credential Encryption

Sensitive credentials encrypted in the browser using the **Web Crypto API** (via JS interop):

1. User sets a **master password** on first use
2. Master password → **PBKDF2** (100k iterations, SHA-256) → encryption key
3. Credentials encrypted with **AES-GCM** (256-bit) → stored in IndexedDB
4. Decrypted on-demand when connecting → sent to server via HTTP API call → used by SSH.NET → immediately discarded

```csharp
public class CryptoService
{
    private readonly IJSRuntime _js;
    
    // Calls Web Crypto API via JS interop
    public Task<string> EncryptAsync(string plaintext, string masterKey);
    public Task<string> DecryptAsync(string ciphertext, string masterKey);
}
```

### 6.3 Saved Connection Model

```csharp
public class SavedConnection
{
    public string Id { get; set; }           // GUID
    public string Name { get; set; }         // User-friendly alias
    public string Host { get; set; }         // hostname or IP
    public int Port { get; set; } = 22;
    public string Username { get; set; }
    public AuthMethod AuthMethod { get; set; }  // Password, PrivateKey, KeyAndPassphrase
    
    // These are stored encrypted in IndexedDB, not in this object
    // Retrieved on-demand via CryptoService
    
    public int SortOrder { get; set; }
    public DateTime LastConnected { get; set; }
    public string DefaultPath { get; set; }  // Initial directory on connect
}
```

---

## 7. Technology Stack

| Layer | Technology | Justification |
|-------|-----------|---------------|
| **App model** | Blazor Web App (.NET 10) — Interactive Auto | Hybrid rendering: server-rendered first paint, WASM for interactive UI; `dotnet run` to start |
| **Render modes** | InteractiveServer (terminal) + InteractiveAuto (everything else) | Terminal needs persistent ShellStream on server; desktop UI benefits from browser execution |
| **UI framework** | MudBlazor | Rich Blazor component library (drawers, tabs, dialogs, menus, icons). Material Design. |
| **Terminal emulator** | xterm.js + xterm-addon-fit + xterm-addon-webgl | Industry standard; used by VS Code |
| **Text editor (MVP)** | `<textarea>` wrapper | Componentized behind `ITextEditor` interface for future CodeMirror/Monaco swap |
| **SSH library** | SSH.NET (Renci.SshNet) | Mature, pure C#, shell + exec channels, runs on server |
| **Server ↔ WASM comms** | Minimal API (HTTP) + SignalR (terminal only) | WASM components call `/api/ssh/*` endpoints; terminal uses SignalR circuit |
| **Client crypto** | Web Crypto API (via JS interop) | AES-GCM encryption for credentials in browser IndexedDB |
| **Client storage** | localStorage + IndexedDB (via JS interop) | Connections and encrypted secrets stored in browser |
| **Icons** | MudBlazor built-in icons | Material icon set + custom file-type mapping |

---

## 8. Key Technical Decisions

### 8.1 Hybrid Render Mode (Interactive Auto)

.NET 10 Blazor's Interactive Auto mode gives us the best of both worlds:

- **Instant first paint** — Server-side rendering means the user sees the UI immediately, no waiting for WASM download.
- **Fast interactions** — Once WASM loads, desktop UI components run in the browser. Dragging windows, clicking menus, typing in the editor — all instant, no server round-trip.
- **Server SSH access** — SSH.NET runs on the server. WASM components call it via HTTP API. Terminal component stays server-rendered for direct ShellStream access.
- **Single `dotnet run`** — Despite two projects, the server project hosts everything.

The WASM runtime (~2-5MB) downloads in the background during the first server-rendered session. Subsequent visits load WASM immediately from cache.

### 8.2 ISshService Abstraction

The shared `ISshService` interface is the key architectural seam:

- **Server render mode**: Injected as `ServerSshService` → calls `SshSessionManager` directly (in-process)
- **WASM render mode**: Injected as `ClientSshService` → calls `/api/ssh/*` endpoints via `HttpClient`

Components use `ISshService` without knowing which render mode they're in. This makes the render mode split transparent to component code.

### 8.3 SshSessionManager as Singleton

The previous design used scoped (per-circuit) SSH session management. The hybrid model requires a **singleton** because:
- WASM components call via HTTP API — there is no shared DI scope between API calls
- A singleton `SshSessionManager` holds all SSH connections, keyed by session ID
- A background `IHostedService` evicts idle sessions after a configurable timeout
- The terminal component (server-rendered) accesses the same singleton to get its `ShellStream`

### 8.4 GUI via Shell Commands (Not SFTP)

All Desktop mode file operations generate shell commands executed via SSH `RunCommand()`. Benefits:
- Doesn't require SFTP subsystem on the remote server
- Transparent — users can see/learn the commands being generated
- Simpler — no SFTP state management or separate channel protocol
- Native copy (`cp`) instead of download-then-reupload

**Tradeoff**: Shell output parsing is fragile. Mitigated by consistent flags, OS detection, and robust parsing.

### 8.5 xterm.js Integration (Server-Rendered)

xterm.js requires JS interop regardless of render mode. `TerminalView` uses `InteractiveServer` so that `ShellStream.DataReceived` can push data directly via SignalR:

```javascript
// terminal-interop.js
export function createTerminal(elementId, dotnetRef) {
    const term = new Terminal({ cursorBlink: true, fontSize: 14 });
    const fitAddon = new FitAddon();
    term.loadAddon(fitAddon);
    term.open(document.getElementById(elementId));
    fitAddon.fit();
    
    term.onData(data => dotnetRef.invokeMethodAsync('OnTerminalInput', data));
    term.onResize(size => dotnetRef.invokeMethodAsync('OnTerminalResize', size.cols, size.rows));
    
    return term;
}

export function writeToTerminal(term, data) {
    term.write(data);
}
```

**Performance**: Terminal output batched (16ms buffer) before pushing to the browser as a single JS interop call.

### 8.6 Editor Component Abstraction

```csharp
public interface ITextEditor
{
    string Content { get; set; }
    string FileName { get; set; }
    bool IsReadOnly { get; set; }
    bool IsDirty { get; }
    event EventHandler<string> OnSave;
}
```

MVP: `SimpleTextEditor` wraps a `<textarea>` with monospace font (runs in WASM — editing is instant). Future: swap in CodeMirror or Monaco implementing the same interface.

---

## 9. Data Flow Examples

### 9.1 Connecting to a Server

```
1. User clicks saved connection in sidebar (WASM component)
2. ConnectionStore reads encrypted password from IndexedDB (JS interop, in-browser)
3. CryptoService decrypts password (Web Crypto API, in-browser)
4. ISshService.ConnectAsync() called → ClientSshService → HTTP POST /api/ssh/connect
5. Server: SshSessionManager creates SshClient via SshConnectionFactory, calls .Connect()
6. Server: ShellStream opened, session stored with session ID
7. Session ID returned to WASM client
8. Sidebar indicator turns 🟢
9. Credentials discarded from server memory immediately
```

### 9.2 Listing Files in Desktop Mode

```
1. User switches to Desktop tab (WASM component)
2. DesktopView opens FileManager window (WASM — instant, no server call)
3. FileManager builds command: ShellCommandBuilder.ListDirectory("/home/user")
4. ISshService.ExecuteAsync(sessionId, cmd) → ClientSshService → HTTP POST /api/ssh/exec
5. Server: SshSessionManager.ExecuteAsync() → SSH.NET RunCommand()
6. CommandResult { stdout, stderr, exitCode } returned via HTTP response
7. ShellOutputParser.ParseLsOutput(stdout) → List<FileEntry> (parsed in WASM)
8. FileList.razor re-renders in browser — instant
```

### 9.3 Editing a File

```
1. User right-clicks file → "Edit" (WASM context menu)
2. ISshService.ExecuteAsync(sessionId, "cat /home/user/config.yml")
3. Content returned via HTTP, displayed in EditorWindow (WASM — editing is local)
4. User edits text — all keystrokes handled in browser, no server calls
5. User clicks [Save]
6. ShellCommandBuilder.WriteFile(path, newContent) → heredoc command
7. ISshService.ExecuteAsync(sessionId, heredocCommand) → HTTP POST
8. Success → MudBlazor snackbar notification
```

---

## 10. Challenges & Risks

### 🔴 Critical

| # | Challenge | Impact | Mitigation |
|---|-----------|--------|------------|
| 1 | **Shell output parsing reliability** | Different distros have different `ls`/`stat` formats | Detect OS via `uname`; use POSIX-compatible flags; `stat --format=...` fallback; extensive parser tests |
| 2 | **Large file handling** | `cat` on large files floods the SSH channel and server memory | Limit to 1MB with `head -c`; warn before opening large files; stream where possible |
| 3 | **Session lifecycle management** | Singleton session manager must handle orphaned SSH connections when browser tabs close without explicit disconnect | Background `IHostedService` evicts idle sessions (default 10 min timeout); `beforeunload` event triggers best-effort disconnect call |
| 4 | **Credentials in transit** | Decrypted credentials travel from WASM → server API over HTTP | HTTPS in production; `localhost` OK over HTTP for local dev; credentials never logged or persisted server-side |

### 🟡 Significant

| # | Challenge | Impact | Mitigation |
|---|-----------|--------|------------|
| 5 | **xterm.js JS interop throughput** | Terminal output must flow via SignalR (server-rendered component) | Buffer `ShellStream.DataReceived` events; flush every 16ms; use `byte[]` not strings |
| 6 | **File upload without SFTP** | Base64 encoding inflates size by 33% and is slow | OK for files <5MB; SFTP channel as future enhancement |
| 7 | **Window management in Blazor** | Draggable/resizable windows need custom implementation | MudBlazor `MudDialog` initially; evolve to `position: absolute` + JS drag handlers (runs in WASM — snappy) |
| 8 | **IndexedDB encryption UX** | Master password friction on every session | Offer "stay unlocked on this device" via non-extractable Web Crypto keys |
| 9 | **Render mode boundary** | Server-rendered `TerminalView` and WASM desktop components must coexist on the same page | Use `@rendermode` per-component; share session ID via cascading parameter or URL; no direct component references across mode boundaries |
| 10 | **WASM initial download** | First visit downloads ~2-5MB WASM runtime | Server-rendered first paint is instant; WASM loads in background; cached for subsequent visits |

### 🟢 Minor

| # | Challenge | Notes |
|---|-----------|-------|
| 11 | Filenames with special characters | Shell-escape all names in `ShellCommandBuilder` |
| 12 | Mobile responsiveness | Desktop mode impractical on mobile; terminal mode fullscreen only |
| 13 | Browser refresh loses SSH connections | Sessions survive on server (singleton); WASM reconnects using stored session ID |

---

## 11. MVP Scope

### MVP (v1.0)
- [ ] .NET 10 Blazor Web App with Interactive Auto — `dotnet run` to start
- [ ] Server + Client project structure with ISshService abstraction
- [ ] Saved connections sidebar (stored encrypted in browser IndexedDB)
- [ ] Connect to SSH server (password auth)
- [ ] Interactive terminal with xterm.js (server-rendered)
- [ ] Tab switching: Terminal ↔ Desktop
- [ ] Desktop mode: File Manager window with icon grid view (WASM)
- [ ] File operations via shell commands: navigate, create, delete, rename
- [ ] Text file editor in a desktop window (basic `<textarea>`, WASM)
- [ ] Download files (base64-over-SSH for small files)
- [ ] Multiple simultaneous connections in sidebar
- [ ] Connection status indicators
- [ ] MudBlazor-based UI

### Future (v2.0+)
- [ ] Private key authentication
- [ ] Upload files
- [ ] CodeMirror/Monaco editor upgrade (swap via `ITextEditor`)
- [ ] Drag-and-drop file operations
- [ ] Image preview window
- [ ] Split view (terminal + desktop side-by-side)
- [ ] File search (`find` / `grep`)
- [ ] Detail/table view for files
- [ ] Customizable themes (dark/light/wallpaper)
- [ ] Keyboard shortcuts
- [ ] "Show command" toggle — echo GUI commands to terminal
- [ ] Optional SFTP channel for bulk transfers
- [ ] Git status overlay on files
- [ ] Docker deployment option
- [ ] Azure App Service / Container App deployment

---

## 12. Development Phases

### Phase 1: Scaffolding & Connection (Week 1)
- Create .NET 10 Blazor Web App (Server + Client projects) with MudBlazor
- SSH.NET integration: `SshSessionManager`, `SshConnectionFactory`
- `ISshService` interface + server/client implementations
- Minimal API endpoints (`/api/ssh/connect`, `/api/ssh/exec`, `/api/ssh/disconnect`)
- Connection form dialog (MudBlazor)
- Saved connections in browser IndexedDB with encryption (JS interop)
- Sidebar with connection list
- Basic connect/disconnect flow

### Phase 2: Terminal Mode (Week 2)
- xterm.js integration with JS interop
- `TerminalView` component with `@rendermode InteractiveServer`
- Wire ShellStream ↔ xterm.js bidirectional data flow via SignalR
- Terminal resize handling
- Output batching for performance
- Connection status indicators in sidebar
- Multi-session support (switch between connections)

### Phase 3: Desktop Shell & File Manager (Week 3)
- Desktop surface with window management (DesktopWindow component, WASM)
- Taskbar component
- File Manager window: listing via `ISshService.ExecuteAsync` + `ShellOutputParser`
- Breadcrumb navigation
- File/folder icon rendering
- Right-click context menu

### Phase 4: File Operations & Editor (Week 4)
- Create, delete, rename operations via shell commands (WASM → server API)
- Text editor window (`SimpleTextEditor` behind `ITextEditor`, WASM)
- Save file via heredoc command
- File download (base64 encoding)
- Properties dialog (permissions, size)
- Error handling for failed commands

### Phase 5: Polish (Week 5)
- Session idle eviction (background HostedService)
- Reconnection handling (WASM reconnects to existing server sessions)
- Loading states & MudBlazor snackbar notifications
- Tab switching UX
- Edge cases (special chars in filenames, permission errors, lost connections)
- README & developer documentation

---

## 13. Dependencies

### NuGet — Server Project (`GuiSsh`)
```
Microsoft.AspNetCore.App             — ASP.NET Core + Blazor Server (SDK, included)
SSH.NET (Renci.SshNet)               — SSH connections (shell + exec)
MudBlazor                            — UI component library
```

### NuGet — Client Project (`GuiSsh.Client`)
```
Microsoft.AspNetCore.Components.WebAssembly  — Blazor WASM (SDK)
MudBlazor                                    — UI component library (shared)
```

### npm / CDN (loaded in App.razor)
```
xterm                                — Terminal emulator
xterm-addon-fit                      — Auto-fit terminal to container
xterm-addon-webgl                    — GPU-accelerated rendering (optional)
```

---

## 14. Security Model

| Concern | Approach |
|---------|----------|
| **App authentication** | None. The app is an open tool. The remote SSH server handles auth. |
| **Credential storage** | Encrypted in browser IndexedDB (AES-GCM + PBKDF2-derived key). Decrypted client-side before sending to server API. |
| **Credentials on server** | Exist in memory only during SSH connect. Never logged, serialized, or persisted. |
| **Data in transit** | HTTPS in production (configurable). Localhost use is fine over HTTP. |
| **Server data retention** | Zero. No database, no files, no logs containing credentials. SSH sessions held in memory only while active. |
| **API security** | Session IDs are opaque GUIDs. For multi-user deployment, add auth middleware to API endpoints. For personal use, not needed. |
| **Input sanitization** | `ShellCommandBuilder` is the single point of command construction with proper escaping. |
| **Trust model** | Whoever runs the server can theoretically intercept credentials. For personal use (`dotnet run` on your machine) this is a non-issue. |

---

## 15. Running the App

```bash
# Prerequisites: .NET 10 SDK

# Clone and run
cd gui-ssh/src/GuiSsh
dotnet run

# App available at https://localhost:5001 (or http://localhost:5000)
# First visit: server-rendered (instant). WASM downloads in background.
# Subsequent visits: WASM cached — interactive UI runs in browser.
```

That's it. No Node.js, no Docker, no Azure account needed for development.

---

*This document is a living design spec. Updated to .NET 10 Blazor hybrid (Interactive Auto) architecture.*
