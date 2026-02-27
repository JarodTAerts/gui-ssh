# GUI-SSH Security Implementation Plan

**Date:** 2026-02-24  
**Auth Strategy:** Azure Container Apps Easy Auth with Microsoft Entra ID  
**Reference:** [SECURITY_AUDIT.md](SECURITY_AUDIT.md)

---

## Table of Contents

1. [Phase 1 — Authentication & Session Ownership](#phase-1--authentication--session-ownership) (Findings #1, #4, #6)
2. [Phase 2 — Credential Storage Rework](#phase-2--credential-storage-rework) (Finding #2)
3. [Phase 3 — SSH Host Key Verification](#phase-3--ssh-host-key-verification) (Finding #3)
4. [Phase 4 — Connection Policy & SSRF Protection](#phase-4--connection-policy--ssrf-protection) (Finding #5)
5. [Phase 5 — Credential Handling Hardening](#phase-5--credential-handling-hardening) (Findings #7, #17)
6. [Phase 6 — Rate Limiting & Upload Limits](#phase-6--rate-limiting--upload-limits) (Findings #8, #9)
7. [Phase 7 — Frontend Security Hardening](#phase-7--frontend-security-hardening) (Findings #10, #11, #13)
8. [Phase 8 — Miscellaneous Fixes](#phase-8--miscellaneous-fixes) (Findings #12, #14, #15, #16, #18)
9. [Phase 9 — Audit Logging](#phase-9--audit-logging) (Finding #20)
10. [Infrastructure — Container Apps & Bicep](#infrastructure--container-apps--bicep)

---

## Phase 1 — Authentication & Session Ownership

**Addresses:** #1 (Critical), #4 (High), #6 (High)  
**Effort:** ~2–3 hours  
**Dependencies:** Azure Container Apps deployment with Easy Auth configured

### 1a. How Easy Auth Works with This App

Azure Container Apps Easy Auth sits as a reverse proxy in front of the app. Unauthenticated requests are redirected to the Entra ID login page. After login, the proxy injects identity headers into every request:

| Header | Content |
|--------|---------|
| `X-MS-CLIENT-PRINCIPAL` | Base64-encoded JSON with claims |
| `X-MS-CLIENT-PRINCIPAL-ID` | User's Entra Object ID (GUID) |
| `X-MS-CLIENT-PRINCIPAL-NAME` | User's email/UPN |

The app itself does **not** need `Microsoft.Identity.Web` or token validation — Easy Auth handles it. The app only needs to **read these headers** and **enforce session ownership**.

### 1b. Create `EasyAuthMiddleware.cs`

A middleware that extracts the authenticated user identity from Easy Auth headers and makes it available via `HttpContext.User`. In local development (where Easy Auth is not present), it falls back to a configurable dev identity.

**New file:** `Services/EasyAuthMiddleware.cs`

```csharp
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace GuiSsh.Services;

/// <summary>
/// Reads Azure Container Apps Easy Auth headers and populates HttpContext.User.
/// In development, falls back to a configurable dev identity.
/// </summary>
public class EasyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHostEnvironment _env;
    private readonly ILogger<EasyAuthMiddleware> _logger;

    public EasyAuthMiddleware(RequestDelegate next, IHostEnvironment env,
        ILogger<EasyAuthMiddleware> logger)
    {
        _next = next;
        _env = env;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var principalHeader = context.Request.Headers["X-MS-CLIENT-PRINCIPAL"].FirstOrDefault();
        var principalId = context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"].FirstOrDefault();
        var principalName = context.Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault();

        if (!string.IsNullOrEmpty(principalId))
        {
            // Easy Auth provided identity — build ClaimsPrincipal
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, principalId),
                new(ClaimTypes.Name, principalName ?? "unknown"),
                new("auth_provider", "easyauth")
            };

            // Optionally parse the full X-MS-CLIENT-PRINCIPAL for additional claims
            if (!string.IsNullOrEmpty(principalHeader))
            {
                try
                {
                    var json = Encoding.UTF8.GetString(Convert.FromBase64String(principalHeader));
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("claims", out var claimsArray))
                    {
                        foreach (var c in claimsArray.EnumerateArray())
                        {
                            var type = c.GetProperty("typ").GetString();
                            var val = c.GetProperty("val").GetString();
                            if (type != null && val != null)
                                claims.Add(new Claim(type, val));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse X-MS-CLIENT-PRINCIPAL");
                }
            }

            var identity = new ClaimsIdentity(claims, "EasyAuth");
            context.User = new ClaimsPrincipal(identity);
        }
        else if (_env.IsDevelopment())
        {
            // Dev fallback — simulate an authenticated user
            var devClaims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "dev-user-00000000"),
                new Claim(ClaimTypes.Name, "developer@localhost"),
                new Claim("auth_provider", "dev-fallback")
            };
            context.User = new ClaimsPrincipal(
                new ClaimsIdentity(devClaims, "DevFallback"));
        }
        // In production with no Easy Auth headers → User remains unauthenticated

        await _next(context);
    }
}
```

### 1c. Add User ID to `ActiveSession`

Add an `OwnerId` property to `ActiveSession` so sessions are bound to the user who created them.

**File:** `Services/SshSessionManager.cs` — modify `ActiveSession` class:

```csharp
public class ActiveSession
{
    public required string SessionId { get; set; }
    public required string OwnerId { get; set; }          // ← NEW: Entra Object ID
    public required SshClient SshClient { get; set; }
    public required ShellStream ShellStream { get; set; }
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public bool IsConnected => SshClient?.IsConnected ?? false;
}
```

### 1d. Pass User Identity Through Session Manager

Modify `ConnectClientAsync` to accept and store the owner ID:

```csharp
private async Task<string> ConnectClientAsync(SshClient client, string host, int port, string ownerId)
{
    var sessionId = Guid.NewGuid().ToString();
    await Task.Run(() => client.Connect());
    var shellStream = _factory.CreateShellStream(client);

    var session = new ActiveSession
    {
        SessionId = sessionId,
        OwnerId = ownerId,              // ← Bind to user
        SshClient = client,
        ShellStream = shellStream,
        LastActivity = DateTime.UtcNow
    };
    // ... (rest unchanged)
}
```

Update `ConnectAsync` and `ConnectWithKeyAsync` signatures to include `string ownerId` and pass it through.

### 1e. Add Ownership Verification Helper

Add a method to `SshSessionManager` that verifies ownership:

```csharp
public ActiveSession? GetSessionForOwner(string sessionId, string ownerId)
{
    var session = GetSession(sessionId);
    if (session == null)
        return null;

    if (session.OwnerId != ownerId)
    {
        _logger.LogWarning("Session {SessionId} access denied for user {UserId} (owner: {OwnerId})",
            sessionId, ownerId, session.OwnerId);
        return null;
    }

    return session;
}
```

### 1f. Secure All API Endpoints

Modify `SshApiEndpoints.cs` to:
1. Require authentication on the group.
2. Extract user ID from `HttpContext.User`.
3. Use ownership-verified session access.

```csharp
public static void MapSshApi(this WebApplication app)
{
    var group = app.MapGroup("/api/ssh")
        .RequireAuthorization();    // ← All endpoints require auth

    group.MapPost("/connect", async (ConnectRequest req, SshSessionManager manager, HttpContext ctx) =>
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new UnauthorizedAccessException();

        try
        {
            string sessionId;
            if (!string.IsNullOrEmpty(req.PrivateKey))
                sessionId = await manager.ConnectWithKeyAsync(
                    req.Host, req.Port, req.Username, req.PrivateKey, req.Passphrase, userId);
            else
                sessionId = await manager.ConnectAsync(
                    req.Host, req.Port, req.Username, req.Password, userId);

            return Results.Ok(new { SessionId = sessionId });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { Error = "Connection failed." }); // Generic error
        }
    });

    group.MapPost("/exec", async (ExecRequest req, SshSessionManager manager, HttpContext ctx) =>
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var session = manager.GetSessionForOwner(req.SessionId, userId);
        if (session == null)
            return Results.NotFound(new { Error = "Session not found." });

        var result = await manager.ExecuteAsync(req.SessionId, req.Command);
        return Results.Ok(result);
    });

    // ... Repeat pattern for disconnect, status, download, upload
}
```

### 1g. Fix Anti-Forgery on Upload (Finding #6)

With Easy Auth, tokens are passed via cookies set by the Container Apps proxy. Since the auth is cookie-based (not bearer), CSRF is still relevant. The fix is to add a **custom header requirement** instead of disabling antiforgery entirely:

```csharp
group.MapPost("/upload", async (HttpRequest httpRequest, SshSessionManager manager) =>
{
    // Require a custom header as CSRF protection (browsers won't add this cross-origin)
    if (!httpRequest.Headers.ContainsKey("X-Requested-With"))
        return Results.StatusCode(403);

    var userId = httpRequest.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // ... rest of handler (with ownership verification)
}).DisableAntiforgery(); // Still needed for multipart, but custom header mitigates CSRF
```

Update `ClientSshService.cs` to send the header on uploads:

```csharp
public async Task UploadFileAsync(string sessionId, string remotePath, Stream fileStream, string fileName)
{
    using var content = new MultipartFormDataContent();
    content.Add(new StringContent(sessionId), "sessionId");
    content.Add(new StringContent(remotePath), "remotePath");
    content.Add(new StreamContent(fileStream), "file", fileName);

    var request = new HttpRequestMessage(HttpMethod.Post, "/api/ssh/upload") { Content = content };
    request.Headers.Add("X-Requested-With", "GuiSsh");  // ← CSRF protection

    var response = await _http.SendAsync(request);
    response.EnsureSuccessStatusCode();
}
```

### 1h. Register Middleware in `Program.cs`

```csharp
// After builder.Build()
var app = builder.Build();

app.UseMiddleware<EasyAuthMiddleware>();   // ← Add before UseAntiforgery

// Add authorization services in builder
builder.Services.AddAuthorization();      // ← Add to service registration

// After UseHttpsRedirection, before UseAntiforgery
app.UseAuthorization();
```

### 1i. Server-Side (Blazor Server) Session Ownership

For `TerminalView.razor` and `ServerSshService` which bypass the HTTP API, inject user identity via `AuthenticationStateProvider`:

```csharp
// In ServerSshService, inject the user identity provider
public class ServerSshService : ISshService
{
    private readonly SshSessionManager _manager;
    private readonly AuthenticationStateProvider _authProvider;

    public ServerSshService(SshSessionManager manager, AuthenticationStateProvider authProvider)
    {
        _manager = manager;
        _authProvider = authProvider;
    }

    private async Task<string> GetUserIdAsync()
    {
        var state = await _authProvider.GetAuthenticationStateAsync();
        return state.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new UnauthorizedAccessException("Not authenticated.");
    }

    public async Task<string> ConnectAsync(string host, int port, string username, string password)
    {
        var userId = await GetUserIdAsync();
        return await _manager.ConnectAsync(host, port, username, password, userId);
    }
    // ... repeat pattern for all methods
}
```

---

## Phase 2 — Credential Storage Rework

**Addresses:** #2 (Critical)  
**Effort:** ~1 hour

### Problem

The encryption passphrase is `GuiSsh.{connectionId}.GuiSsh.Credential.Store.v1` — entirely derivable from public data in localStorage. Anyone with access to localStorage (XSS, browser extensions, shared machines) can reconstruct the key and decrypt all stored credentials.

### Proposed Implementation: Non-Extractable Browser CryptoKey in IndexedDB

Replace the PBKDF2-from-deterministic-passphrase scheme with a proper `CryptoKey` generated via the Web Crypto API. The key is created with `extractable: false`, meaning the raw key material **can never be read out** — not by JavaScript, not by extensions, not by dev tools. The `CryptoKey` object is stored in IndexedDB (which supports structured-cloneable objects like `CryptoKey`), while the encrypted ciphertext remains in localStorage.

**Security properties:**
- **Non-extractable:** Even if an attacker has XSS, they cannot export the key bytes. They could call encrypt/decrypt while on-page, but cannot steal the key for offline attacks.
- **Origin-bound:** The key is tied to the app's origin — a different site cannot access it.
- **No user interaction required:** Completely transparent. No prompts, no master passwords.

**Tradeoffs:**
- Per-browser/per-device — credentials don't roam across browsers or machines. Users must re-enter credentials on a new device.
- Clearing browser data (IndexedDB) destroys the key. Saved credentials become undecryptable and must be re-entered.

#### 2a. Rewrite `crypto-interop.js`

```javascript
// Browser-side credential encryption using Web Crypto API.
// Uses a non-extractable AES-GCM key stored in IndexedDB.
// Key material can never be read by JavaScript — only used for encrypt/decrypt.

window.cryptoInterop = (() => {
    const DB_NAME = 'guissh-keystore';
    const DB_VERSION = 1;
    const STORE_NAME = 'keys';
    const KEY_ID = 'credential-master-key';

    // --- IndexedDB helpers ---

    function openDb() {
        return new Promise((resolve, reject) => {
            const req = indexedDB.open(DB_NAME, DB_VERSION);
            req.onupgradeneeded = () => {
                const db = req.result;
                if (!db.objectStoreNames.contains(STORE_NAME))
                    db.createObjectStore(STORE_NAME);
            };
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
    }

    async function getStoredKey() {
        const db = await openDb();
        return new Promise((resolve, reject) => {
            const tx = db.transaction(STORE_NAME, 'readonly');
            const store = tx.objectStore(STORE_NAME);
            const req = store.get(KEY_ID);
            req.onsuccess = () => resolve(req.result ?? null);
            req.onerror = () => reject(req.error);
        });
    }

    async function storeKey(key) {
        const db = await openDb();
        return new Promise((resolve, reject) => {
            const tx = db.transaction(STORE_NAME, 'readwrite');
            const store = tx.objectStore(STORE_NAME);
            const req = store.put(key, KEY_ID);
            req.onsuccess = () => resolve();
            req.onerror = () => reject(req.error);
        });
    }

    // --- Key management ---

    async function getOrCreateKey() {
        let key = await getStoredKey();
        if (key) return key;

        // Generate a new non-extractable AES-GCM 256-bit key
        key = await crypto.subtle.generateKey(
            { name: 'AES-GCM', length: 256 },
            false,    // ← NON-EXTRACTABLE: key material can never be read
            ['encrypt', 'decrypt']
        );

        await storeKey(key);
        return key;
    }

    // --- Encrypt / Decrypt ---

    async function encrypt(plaintext) {
        const key = await getOrCreateKey();
        const enc = new TextEncoder();
        const iv = crypto.getRandomValues(new Uint8Array(12));

        const ciphertext = await crypto.subtle.encrypt(
            { name: 'AES-GCM', iv },
            key,
            enc.encode(plaintext)
        );

        // Prepend IV to ciphertext
        const result = new Uint8Array(iv.length + ciphertext.byteLength);
        result.set(iv, 0);
        result.set(new Uint8Array(ciphertext), iv.length);

        // Encode as base64
        let binary = '';
        for (let i = 0; i < result.length; i++)
            binary += String.fromCharCode(result[i]);
        return btoa(binary);
    }

    async function decrypt(base64Data) {
        try {
            const key = await getOrCreateKey();
            const binary = atob(base64Data);
            const data = new Uint8Array(binary.length);
            for (let i = 0; i < binary.length; i++)
                data[i] = binary.charCodeAt(i);

            const iv = data.slice(0, 12);
            const encrypted = data.slice(12);

            const decrypted = await crypto.subtle.decrypt(
                { name: 'AES-GCM', iv },
                key,
                encrypted
            );

            return new TextDecoder().decode(decrypted);
        } catch (e) {
            console.warn('cryptoInterop.decrypt failed:', e);
            return null;
        }
    }

    // --- Public API (unchanged interface for Blazor interop) ---

    return {
        encryptCredential: async function (connectionId, username, password) {
            const payload = JSON.stringify({ u: username, p: password });
            const encrypted = await encrypt(payload);
            localStorage.setItem(`guissh_cred_${connectionId}`, encrypted);
            return encrypted;
        },

        decryptCredential: async function (connectionId, encryptedData) {
            const json = await decrypt(encryptedData);
            if (!json) return null;
            try {
                const obj = JSON.parse(json);
                return { username: obj.u, password: obj.p };
            } catch (e) {
                console.warn('cryptoInterop.decryptCredential JSON parse failed:', e);
                return null;
            }
        }
    };
})();
```

#### 2b. C# `CryptoService` Changes

The `CryptoService.cs` interface **does not change** — `EncryptCredentialAsync`, `DecryptCredentialAsync`, `EncryptKeyCredentialAsync`, and `DecryptKeyCredentialAsync` all call the same `cryptoInterop.encryptCredential` / `cryptoInterop.decryptCredential` JS functions, which now use the IndexedDB-backed key internally. No C# code changes required.

#### 2c. Migration Path (Old → New Scheme)

Credentials encrypted with the old PBKDF2-from-public-data scheme cannot be decrypted by the new key. Migration strategy:

1. On first load after the update, the new `decryptCredential` will fail for old entries (AES-GCM tag mismatch).
2. `Sidebar.razor` already handles this — when decryption returns `null`, it shows "Stored credentials could not be decrypted. Please re-enter." and prompts the user.
3. When the user re-enters credentials and saves, they are re-encrypted with the new non-extractable key.
4. **No additional migration code needed.** The existing error-handling flow serves as the migration path.

#### 2d. Limitations & User Communication

Add a note to the connection form when "Save credentials (encrypted)" is checked:

> Credentials are encrypted with a browser-generated key that cannot be exported.  
> If you clear browser data or switch browsers, you will need to re-enter saved credentials.

This sets expectations and avoids confusion when credentials are "lost" after a browser data clear.

---

## Phase 3 — SSH Host Key Verification

**Addresses:** #3 (High)  
**Effort:** ~3 hours

### Proposed Implementation: Trust-on-First-Use (TOFU) with Known-Hosts Store

#### 3a. Create `HostKeyStore.cs`

A server-side service that persists known host key fingerprints. Uses a JSON file on the container's persistent volume.

```csharp
namespace GuiSsh.Services;

public class HostKeyStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, string> _knownHosts = new(); // "host:port" → "SHA256:fingerprint"

    public HostKeyStore(IConfiguration config)
    {
        _filePath = config.GetValue<string>("KnownHostsPath")
            ?? Path.Combine(AppContext.BaseDirectory, "known_hosts.json");
        Load();
    }

    public HostKeyStatus Check(string host, int port, string fingerprint)
    {
        var key = $"{host}:{port}";
        if (!_knownHosts.TryGetValue(key, out var stored))
            return HostKeyStatus.Unknown;

        return stored == fingerprint ? HostKeyStatus.Trusted : HostKeyStatus.Changed;
    }

    public async Task TrustAsync(string host, int port, string fingerprint)
    {
        await _lock.WaitAsync();
        try
        {
            _knownHosts[$"{host}:{port}"] = fingerprint;
            await SaveAsync();
        }
        finally { _lock.Release(); }
    }

    // Load/Save from JSON file ...
}

public enum HostKeyStatus { Trusted, Unknown, Changed }
```

#### 3b. Add Host Key Verification to `SshConnectionFactory`

```csharp
public SshClient CreateClient(string host, int port, string username, string password,
    HostKeyStore hostKeyStore, out string? fingerprint)
{
    var connectionInfo = new ConnectionInfo(host, port, username,
        new PasswordAuthenticationMethod(username, password))
    {
        Timeout = TimeSpan.FromSeconds(15),
        ChannelCloseTimeout = TimeSpan.FromSeconds(5)
    };

    string? capturedFingerprint = null;
    var client = new SshClient(connectionInfo);

    client.HostKeyReceived += (sender, e) =>
    {
        capturedFingerprint = Convert.ToHexString(e.HostKeyFingerPrint);
        var status = hostKeyStore.Check(host, port, capturedFingerprint);

        switch (status)
        {
            case HostKeyStatus.Trusted:
                e.CanTrust = true;
                break;
            case HostKeyStatus.Changed:
                e.CanTrust = false;  // Reject — key changed (possible MITM)
                break;
            case HostKeyStatus.Unknown:
                e.CanTrust = true;   // Accept for TOFU, caller will persist
                break;
        }
    };

    fingerprint = capturedFingerprint;
    return client;
}
```

#### 3c. Update Connect Flow

In `SshSessionManager.ConnectAsync`:

1. Create client with host key handler.
2. If status is `Unknown`, return the fingerprint to the UI and ask the user to confirm.
3. If confirmed, call `HostKeyStore.TrustAsync()`.
4. If status is `Changed`, return an error: "Host key has changed — possible MITM attack."

This requires a two-step connect flow:
- Step 1: `POST /api/ssh/connect` → returns `{ NeedsHostKeyApproval: true, Fingerprint: "SHA256:...", Host: "..." }`.
- Step 2: `POST /api/ssh/connect/confirm` → with approval flag, proceeds to connect.

#### 3d. UI Component

Add a `HostKeyApprovalDialog` that shows:

```
The authenticity of host 'server.example.com (192.168.1.10)' can't be established.
ED25519 key fingerprint is SHA256:abc123...

Are you sure you want to continue connecting?
[Yes, trust this host]  [Cancel]
```

---

## Phase 4 — Connection Policy & SSRF Protection

**Addresses:** #5 (High)  
**Effort:** ~1 hour

### Proposed Implementation: Configurable Connection Policy

#### 4a. Create `ConnectionPolicy.cs`

```csharp
using System.Net;

namespace GuiSsh.Services;

public class ConnectionPolicy
{
    private readonly HashSet<string> _blockedCidrs = new();
    private readonly HashSet<string> _allowedHosts = new();
    private readonly bool _blockPrivateRanges;
    private readonly int _minPort;
    private readonly int _maxPort;

    public ConnectionPolicy(IConfiguration config)
    {
        var section = config.GetSection("ConnectionPolicy");
        _blockPrivateRanges = section.GetValue("BlockPrivateRanges", true);
        _minPort = section.GetValue("MinPort", 22);
        _maxPort = section.GetValue("MaxPort", 22);  // Default: SSH only

        var allowed = section.GetSection("AllowedHosts").Get<string[]>();
        if (allowed != null)
            _allowedHosts = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
    }

    public (bool Allowed, string? Reason) Evaluate(string host, int port)
    {
        // Port range check
        if (port < _minPort || port > _maxPort)
            return (false, $"Port {port} is outside the allowed range ({_minPort}–{_maxPort}).");

        // Allowlist check (if configured, only these hosts are permitted)
        if (_allowedHosts.Count > 0 && !_allowedHosts.Contains(host))
            return (false, $"Host '{host}' is not in the allowed hosts list.");

        // Block private/internal ranges
        if (_blockPrivateRanges && IPAddress.TryParse(host, out var ip))
        {
            if (IsPrivateOrReserved(ip))
                return (false, "Connections to private/internal IP ranges are blocked.");
        }

        return (true, null);
    }

    private static bool IsPrivateOrReserved(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] == 10                                        // 10.0.0.0/8
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) // 172.16.0.0/12
                || (bytes[0] == 192 && bytes[1] == 168)                  // 192.168.0.0/16
                || bytes[0] == 127                                       // 127.0.0.0/8
                || (bytes[0] == 169 && bytes[1] == 254);                 // 169.254.0.0/16
        }
        return IPAddress.IsLoopback(ip);
    }
}
```

#### 4b. Configuration in `appsettings.json`

```json
{
  "ConnectionPolicy": {
    "BlockPrivateRanges": true,
    "MinPort": 22,
    "MaxPort": 2222,
    "AllowedHosts": []
  }
}
```

When `AllowedHosts` is empty, all non-blocked hosts are allowed. When populated, it acts as a strict allowlist.

#### 4c. Integrate into Connect Endpoint

```csharp
group.MapPost("/connect", async (ConnectRequest req, SshSessionManager manager,
    ConnectionPolicy policy, HttpContext ctx) =>
{
    var (allowed, reason) = policy.Evaluate(req.Host, req.Port);
    if (!allowed)
        return Results.BadRequest(new { Error = reason });

    // ... proceed with connection
});
```

Also add DNS resolution check: resolve the hostname *before* connecting and evaluate the resolved IP against the policy, to prevent DNS rebinding to private ranges.

---

## Phase 5 — Credential Handling Hardening

**Addresses:** #7 (High), #17 (Low)  
**Effort:** ~1 hour

### 5a. Suppress Credential Logging

Add a custom `IHttpLoggingInterceptor` or configure request logging to exclude the `/api/ssh/connect` body:

```csharp
// In Program.cs
builder.Services.AddHttpLogging(options =>
{
    options.LoggingFields = HttpLoggingFields.RequestPath
        | HttpLoggingFields.RequestMethod
        | HttpLoggingFields.ResponseStatusCode;
    // Explicitly do NOT include RequestBody
});
```

### 5b. Redact Sensitive Parameters in Structured Logging

In `SshSessionManager`, never log credentials. The current code is fine here (it only logs `host:port` and `sessionId`), but add a defensive rule:

```csharp
_logger.LogInformation("SSH session {SessionId} connected to {Host}:{Port} by {UserId}",
    sessionId, host, port, ownerId);
// NEVER log: password, privateKey, passphrase
```

### 5c. Zero Key Material After Use

In `SshConnectionFactory.CreateClientWithKey`:

```csharp
public SshClient CreateClientWithKey(string host, int port, string username,
    string privateKeyPem, string? passphrase = null, HostKeyStore? hostKeyStore = null)
{
    var keyBytes = System.Text.Encoding.UTF8.GetBytes(privateKeyPem);
    try
    {
        using var keyStream = new MemoryStream(keyBytes);
        var keyFile = passphrase != null
            ? new PrivateKeyFile(keyStream, passphrase)
            : new PrivateKeyFile(keyStream);

        // ... create client
    }
    finally
    {
        // Zero the byte array (string can't be zeroed, but array can)
        Array.Clear(keyBytes, 0, keyBytes.Length);
    }
}
```

---

## Phase 6 — Rate Limiting & Upload Limits

**Addresses:** #8 (Medium), #9 (Medium)  
**Effort:** ~30 minutes

### 6a. Add Rate Limiting

```csharp
// Program.cs — add to service registration
using System.Threading.RateLimiting;

builder.Services.AddRateLimiter(options =>
{
    // Global per-IP limiter
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1)
            }));

    // Strict limiter for connect endpoint
    options.AddPolicy("connect", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1)
            }));

    options.RejectionStatusCode = 429;
});

// In the pipeline, before UseAuthorization:
app.UseRateLimiter();
```

Apply the strict policy to the connect endpoint:

```csharp
group.MapPost("/connect", ...).RequireRateLimiting("connect");
```

### 6b. Set Upload Size Limit

```csharp
// Program.cs — Kestrel config
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 500 * 1024 * 1024; // 500 MB max
    // ... rest unchanged
});
```

---

## Phase 7 — Frontend Security Hardening

**Addresses:** #10 (Medium), #11 (Medium), #13 (Medium)  
**Effort:** ~1–2 hours

### 7a. Bundle xterm.js Locally (Finding #10)

Remove CDN loading and install xterm.js as a local dependency:

```bash
cd src/GuiSsh/GuiSsh/wwwroot
npm init -y
npm install @xterm/xterm@5.5.0 @xterm/addon-fit@0.10.0
```

Update `terminal-interop.js` to use local paths:

```javascript
// Before (CDN):
const xtermModule = await import('https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/+esm');

// After (local):
const xtermModule = await import('/lib/xterm/xterm.mjs');
const fitModule = await import('/lib/xterm/addon-fit.mjs');
```

Copy the ESM bundles into `wwwroot/lib/xterm/` in a build step, or reference from `node_modules` via a static file mapping.

### 7b. Remove All `eval()` Calls (Finding #11)

**Create new file:** `wwwroot/js/ui-interop.js`

```javascript
export function setTheme(theme) {
    document.documentElement.setAttribute('data-theme', theme);
}

export function triggerFileInput(elementId) {
    document.getElementById(elementId)?.click();
}
```

**Update `AppShell.razor`:**

```csharp
// Before:
await JS.InvokeVoidAsync("eval",
    $"document.documentElement.setAttribute('data-theme', '{(...)}')");

// After:
private IJSObjectReference? _uiModule;

protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (firstRender)
        _uiModule = await JS.InvokeAsync<IJSObjectReference>("import", "/js/ui-interop.js");
}

private async Task ApplyTheme()
{
    if (_uiModule != null)
        await _uiModule.InvokeVoidAsync("setTheme", _isDarkMode ? "dark" : "light");
}
```

**Update `FileManager.razor`:**

```csharp
// Before:
await JS.InvokeVoidAsync("eval", $"document.getElementById('{_fileInputId}')?.click()");

// After:
await _uiModule.InvokeVoidAsync("triggerFileInput", _fileInputId);
```

### 7c. Add Security Headers (Finding #13)

**Add middleware in `Program.cs`:**

```csharp
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' 'wasm-unsafe-eval'; " +      // Blazor WASM needs wasm-unsafe-eval
        "style-src 'self' 'unsafe-inline'; " +           // MudBlazor uses inline styles
        "connect-src 'self' wss:; " +
        "img-src 'self' data:; " +
        "frame-ancestors 'none';";
    await next();
});
```

Place this before `app.UseStaticFiles()` / `app.MapStaticAssets()`.

---

## Phase 8 — Miscellaneous Fixes

### 8a. Randomize Heredoc Delimiter (Finding #12)

**File:** `ShellCommandBuilder.cs`

```csharp
public string WriteFile(string path, string content)
{
    var escaped = ShellEscape(path);
    var delimiter = $"GUISSH_EOF_{Guid.NewGuid():N}";  // Random delimiter
    return $"cat > {escaped} << '{delimiter}'\n{content}\n{delimiter}";
}
```

Alternative (more robust — bypass shell entirely):

```csharp
public string WriteFileBase64(string path, string content)
{
    var escaped = ShellEscape(path);
    var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
    return $"echo '{b64}' | base64 -d > {escaped}";
}
```

The base64 approach is recommended because it entirely eliminates content-to-command injection.

### 8b. Restrict AllowedHosts (Finding #14)

**File:** `appsettings.json`

```json
{
  "AllowedHosts": "localhost"
}
```

Add a production override in `appsettings.Production.json`:

```json
{
  "AllowedHosts": "gui-ssh.<your-container-app>.azurecontainerapps.io"
}
```

### 8c. Remove Session ID from Download URL (Finding #15)

Replace the `GET /download/{sessionId}?path=...` endpoint with a one-time download token system:

```csharp
// New endpoint: request a download token (POST, session ID in body)
group.MapPost("/download/token", async (DownloadTokenRequest req, SshSessionManager manager,
    HttpContext ctx, DownloadTokenService tokens) =>
{
    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var session = manager.GetSessionForOwner(req.SessionId, userId);
    if (session == null)
        return Results.NotFound();

    var token = tokens.CreateToken(req.SessionId, req.RemotePath, TimeSpan.FromMinutes(5));
    return Results.Ok(new { Token = token });
});

// Download by token (no session ID in URL)
group.MapGet("/download/stream/{token}", async (string token, HttpContext ctx,
    SshSessionManager manager, DownloadTokenService tokens, ILogger<SshSessionManager> logger) =>
{
    var info = tokens.RedeemToken(token);  // One-time use
    if (info == null)
        return Results.NotFound();

    // ... proceed with streaming download using info.SessionId, info.RemotePath
});
```

**`DownloadTokenService`** is a simple in-memory singleton with a `ConcurrentDictionary<string, DownloadTokenInfo>` and expiry enforcement.

### 8d. Implement Terminal Resize (Finding #16)

**File:** `TerminalView.razor`

```csharp
[JSInvokable]
public void OnTerminalResize(int cols, int rows)
{
    if (_shellStream != null)
    {
        try
        {
            _shellStream.SendWindowChangeRequest((uint)cols, (uint)rows, 0, 0);
        }
        catch (Exception ex)
        {
            _ = WriteToTerminalAsync($"\r\n*** Resize failed: {ex.Message} ***\r\n");
        }
    }
}
```

### 8e. Sanitize Error Messages (Finding #18)

**File:** `SshApiEndpoints.cs` — replace all raw exception returns:

```csharp
// Before:
catch (Exception ex)
{
    return Results.BadRequest(new { Error = ex.Message });
}

// After:
catch (Exception ex)
{
    logger.LogError(ex, "Connection failed for {Host}:{Port}", req.Host, req.Port);
    return Results.BadRequest(new { Error = "Connection failed. Check host and credentials." });
}
```

Apply this pattern to every `catch` block in the API endpoints. Only log the real exception server-side.

---

## Phase 9 — Audit Logging

**Addresses:** #20 (Informational)  
**Effort:** ~1 hour

### 9a. Create `AuditLogger.cs`

```csharp
namespace GuiSsh.Services;

public class AuditLogger
{
    private readonly ILogger<AuditLogger> _logger;

    public AuditLogger(ILogger<AuditLogger> logger) => _logger = logger;

    public void LogConnect(string userId, string host, int port, bool success, string? error = null)
    {
        _logger.LogInformation(
            "AUDIT | Action=Connect | User={UserId} | Host={Host}:{Port} | Success={Success} | Error={Error}",
            userId, host, port, success, error ?? "none");
    }

    public void LogCommand(string userId, string sessionId, string commandPreview)
    {
        // Log first 100 chars of command to avoid logging sensitive data
        var preview = commandPreview.Length > 100 ? commandPreview[..100] + "..." : commandPreview;
        _logger.LogInformation(
            "AUDIT | Action=Execute | User={UserId} | Session={SessionId} | Command={Command}",
            userId, sessionId, preview);
    }

    public void LogFileTransfer(string userId, string sessionId, string direction, string path)
    {
        _logger.LogInformation(
            "AUDIT | Action={Direction} | User={UserId} | Session={SessionId} | Path={Path}",
            direction, userId, sessionId, path);
    }

    public void LogDisconnect(string userId, string sessionId)
    {
        _logger.LogInformation(
            "AUDIT | Action=Disconnect | User={UserId} | Session={SessionId}",
            userId, sessionId);
    }
}
```

Register as singleton and inject into `SshApiEndpoints`. In production, configure the logger category `GuiSsh.Services.AuditLogger` to ship to Azure Monitor / Log Analytics via the Container Apps built-in log driver.

---

## Infrastructure — Container Apps & Bicep

### Easy Auth Bicep Configuration

```bicep
resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'gui-ssh'
  location: location
  properties: {
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      // Easy Auth configuration
      authConfigs: [
        {
          name: 'current'
          properties: {
            platform: {
              enabled: true
            }
            globalValidation: {
              unauthenticatedClientAction: 'RedirectToLoginPage'
              redirectToProvider: 'azureactivedirectory'
            }
            identityProviders: {
              azureActiveDirectory: {
                enabled: true
                registration: {
                  clientId: '<YOUR-APP-REGISTRATION-CLIENT-ID>'
                  openIdIssuer: 'https://login.microsoftonline.com/<YOUR-TENANT-ID>/v2.0'
                }
                validation: {
                  allowedAudiences: [
                    'api://<YOUR-APP-REGISTRATION-CLIENT-ID>'
                  ]
                }
              }
            }
          }
        }
      ]
    }
  }
}
```

### Entra ID App Registration Setup

1. Go to **Entra ID** → **App registrations** → **New registration**
2. Name: `GUI-SSH`
3. Redirect URI: `https://gui-ssh.<unique>.azurecontainerapps.io/.auth/login/aad/callback`
4. Under **Authentication**: Enable **ID tokens**
5. Under **API permissions**: `User.Read` (default)
6. Copy the **Application (client) ID** and **Directory (tenant) ID** for the Bicep config above

**Cost:** $0 — Entra ID free tier, Container Apps Easy Auth is included at no extra charge.

---

## Implementation Order & Effort Summary

| Phase | Findings | Severity Addressed | Status |
|-------|----------|-------------------|--------|
| 1 | #1, #4, #6 | 2× Critical, 1× High | **Completed** |
| 2 | #2 | 1× Critical | **Completed** |
| 3 | #3 | 1× High | **Completed** |
| 4 | #5 | 1× High | **Completed** |
| 5 | #7, #17 | 1× High, 1× Low | **Completed** |
| 6 | #8, #9 | 2× Medium | **Completed** |
| 7 | #10, #11, #13 | 3× Medium | Skipped |
| 8 | #12, #14, #15, #16, #18 | 3× Medium, 1× Low, 1× Low | Skipped |
| 9 | #20 | 1× Info | Skipped |
| Infra | — | Setup | Not started |

### Phase 1 — Completed (2026-02-25)

Files changed:
- **Created** `Services/EasyAuthMiddleware.cs` — reads Azure Container Apps Easy Auth headers, populates `HttpContext.User`, dev fallback identity
- **Modified** `Services/SshSessionManager.cs` — added `OwnerId` to `ActiveSession`, added `GetSessionForOwner()`, updated `ConnectAsync`/`ConnectWithKeyAsync` signatures to accept `ownerId`
- **Modified** `Services/SshApiEndpoints.cs` — all endpoints require authorization, extract user ID from claims, verify session ownership, sanitized error messages, added `X-Requested-With` CSRF check on upload
- **Modified** `Services/ServerSshService.cs` — injects `AuthenticationStateProvider`, extracts user ID for session ownership on Blazor Server connect calls
- **Modified** `Program.cs` — added `AddAuthorization()`, `UseMiddleware<EasyAuthMiddleware>()`, `UseAuthorization()`
- **Modified** `ClientSshService.cs` — upload sends `X-Requested-With: GuiSsh` header for CSRF protection

### Phase 2 — Completed (2026-02-25)

Files changed:
- **Rewritten** `wwwroot/js/crypto-interop.js` — replaced PBKDF2-from-deterministic-passphrase with non-extractable AES-GCM CryptoKey stored in IndexedDB. Key material can never be exported. Public API (`encryptCredential`, `decryptCredential`) unchanged — no C# changes needed.

Migration: Existing saved credentials encrypted with the old scheme will fail to decrypt. The existing UI error handling prompts users to re-enter credentials, serving as the natural migration path.

Phases 3–9 and infrastructure remain for future implementation.

### Phase 3 — Completed (2026-02-25)

Files changed:
- **Created** `Client/Models/ConnectResult.cs` — new model with `SessionId`, `NeedsHostKeyApproval`, `HostKeyChanged`, `HostKeyFingerprint`, `HostKeyAlgorithm`, `Error` to support the two-step TOFU flow
- **Created** `Services/HostKeyStore.cs` — server-side singleton persisting known host fingerprints (SHA256) to JSON file; supports `Check()` (Trusted/Unknown/Changed), `TrustAsync()`, `RemoveAsync()`
- **Created** `Client/Components/Layout/HostKeyApprovalDialog.razor` — MudBlazor dialog showing fingerprint for unknown keys (accept/cancel) or a warning for changed keys (close only)
- **Modified** `Services/SshConnectionFactory.cs` — added `CreateClientWithHostKeyCheck()` and `CreateClientWithKeyAndHostKeyCheck()` methods that wire up SSH.NET's `HostKeyReceived` event to verify against `HostKeyStore`
- **Modified** `Services/SshSessionManager.cs` — connect methods now return `ConnectResult` instead of `string`; two-step flow: unknown keys disconnect and return fingerprint, changed keys reject immediately; `TrustAndReconnectAsync()` for post-approval reconnect
- **Modified** `Services/SshApiEndpoints.cs` — `/connect` returns `ConnectResult` object; added `/connect/trust` endpoint for trust-and-reconnect after user approval; added `TrustHostKeyRequest` record
- **Modified** `Client/Services/ISshService.cs` — `ConnectAsync` and `ConnectWithKeyAsync` now return `ConnectResult`; added `TrustHostKeyAndConnectAsync()`
- **Modified** `Services/ServerSshService.cs` — updated to match new interface, delegates trust-and-reconnect to manager
- **Modified** `Client/Services/ClientSshService.cs` — updated connect methods to deserialize `ConnectResult`; added `TrustHostKeyAndConnectAsync()` calling `/connect/trust`
- **Modified** `Client/Components/Layout/Sidebar.razor` — `ConnectToServer()` handles `ConnectResult`: shows `HostKeyApprovalDialog` for unknown keys, error for changed keys, auto-trusts and reconnects on approval
- **Modified** `Program.cs` — registered `HostKeyStore` as singleton

Phases 4–9 and infrastructure remain for future implementation.

### Phase 4 — Completed (2026-02-26)

Files changed:
- **Created** `Services/ConnectionPolicy.cs` — config-driven SSRF protection: port range restriction, private/reserved IP blocking (IPv4 + IPv6 + IPv4-mapped-IPv6), optional host allowlist, async DNS resolution to prevent DNS rebinding
- **Modified** `appsettings.json` — added `ConnectionPolicy` section with `BlockPrivateRanges`, `MinPort`, `MaxPort`, `AllowedHosts`
- **Modified** `Services/SshApiEndpoints.cs` — `/connect` and `/connect/trust` evaluate connection policy before attempting SSH connection
- **Modified** `Services/ServerSshService.cs` — injects `ConnectionPolicy`, calls `EvaluateAsync()` before all connect methods
- **Modified** `Program.cs` — registered `ConnectionPolicy` as singleton

Phases 5–9 and infrastructure remain for future implementation.

### Phase 5 — Completed (2026-02-26)

Files changed:
- **Modified** `Services/SshConnectionFactory.cs` — both `CreateClientWithKey()` and `CreateClientWithKeyAndHostKeyCheck()` now zero the private key byte array in a `finally` block with `Array.Clear()` after SSH.NET ingests the key material
- **Modified** `Program.cs` — added `AddHttpLogging()` configured to log only RequestPath, RequestMethod, and ResponseStatusCode (never request bodies, preventing credential leakage into logs)

### Phase 6 — Completed (2026-02-26)

Files changed:
- **Modified** `Program.cs` — added `AddRateLimiter()` with global 100 req/min per-IP limiter and strict 10 req/min `"connect"` policy; `UseRateLimiter()` in pipeline; set `MaxRequestBodySize` to 500 MB (was unlimited)
- **Modified** `Services/SshApiEndpoints.cs` — `.RequireRateLimiting("connect")` applied to `/connect` and `/connect/trust` endpoints

Phases 7–9 and infrastructure remain for future implementation.
