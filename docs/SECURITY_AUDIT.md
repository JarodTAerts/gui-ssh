# GUI-SSH Security Audit

**Date:** 2026-02-24  
**Scope:** Full codebase review of the GUI-SSH web-based SSH client  
**Severity Levels:** Critical / High / Medium / Low / Informational

---

## Executive Summary

GUI-SSH is a Blazor-based web SSH client with both Interactive Server and WebAssembly render modes. The server manages SSH sessions via SSH.NET, exposes HTTP API endpoints for the WASM client, and directly connects Blazor Server components to shell streams. The application has several significant security concerns, the most critical being the complete absence of authentication and authorization on all API endpoints, and weak client-side credential encryption that relies on publicly derivable keys.

---

## Findings

### 1. CRITICAL — No Authentication or Authorization on API Endpoints

**Location:** `SshApiEndpoints.cs`, `Program.cs`

All `/api/ssh/*` endpoints are completely unauthenticated:

- `POST /api/ssh/connect` — Create SSH connections to arbitrary hosts
- `POST /api/ssh/exec` — Execute arbitrary commands on active sessions
- `POST /api/ssh/disconnect` — Disconnect sessions
- `GET /api/ssh/status/{sessionId}` — Probe session status
- `POST /api/ssh/download` — Download files from remote servers
- `GET /api/ssh/download/{sessionId}` — Stream file downloads
- `POST /api/ssh/upload` — Upload files to remote servers

Any network client that can reach the server can invoke these endpoints without any credentials. If the server is exposed beyond localhost, it becomes an open proxy for SSH connections.

**Impact:** An attacker can use the server as a pivot to connect to any SSH-accessible host, execute arbitrary commands, and exfiltrate data from remote systems.

**Mitigation:**
- Implement authentication (e.g., ASP.NET Core Identity, cookie auth, or JWT bearer tokens) and require it on all `/api/ssh/*` endpoints.
- Bind sessions to the authenticated user so that users can only access their own sessions.
- For single-user local deployments, at minimum restrict listening to `localhost` only and add a shared secret or local auth mechanism.

---

### 2. CRITICAL — Insecure Credential Encryption (Deterministic, No User Secret)

**Location:** `crypto-interop.js`, `CryptoService.cs`

The credential encryption scheme uses AES-GCM with a PBKDF2-derived key, but the passphrase is entirely deterministic and derived from public information:

```javascript
function makePassphrase(connectionId) {
    return `GuiSsh.${connectionId}.${APP_SALT}`;
}
```

The `connectionId` is a GUID stored in localStorage, and `APP_SALT` is the hardcoded string `'GuiSsh.Credential.Store.v1'`. Anyone with access to the browser's localStorage (XSS, browser extensions, shared machines, developer tools) can trivially reconstruct the passphrase and decrypt all stored credentials — passwords and private keys alike.

**Impact:** Stored SSH credentials (passwords and private keys) are effectively obfuscated rather than truly encrypted. Any localStorage access compromises all saved credentials.

**Mitigation:**
- Require a user-supplied master password that is never stored and is used as part of the PBKDF2 derivation.
- Consider using the Web Authentication API (passkeys) or browser-native credential management.
- Display a clear warning to users that saving credentials relies on browser security.
- Alternatively, move credential storage server-side with proper encryption at rest using a hardware-backed key or user-authenticated vault.

---

### 3. HIGH — No SSH Host Key Verification (MITM Vulnerability)

**Location:** `SshConnectionFactory.cs`

The `SshClient` is created without configuring a `HostKeyReceived` event handler. SSH.NET's default behavior is to accept all host keys without verification, making every connection vulnerable to man-in-the-middle attacks.

```csharp
// No HostKeyReceived handler configured
return new SshClient(connectionInfo);
```

**Impact:** An attacker performing a MITM attack on the network path between the GUI-SSH server and the target SSH host can intercept all traffic, including commands, file transfers, and credentials.

**Mitigation:**
- Implement host key verification with a known-hosts store.
- On first connection, present the host key fingerprint to the user for trust-on-first-use (TOFU) confirmation.
- Store accepted host keys and reject connections where the key has changed (with user override option).
- Consider integrating with the system's `~/.ssh/known_hosts` file.

---

### 4. HIGH — Session Hijacking via Insecure Direct Object Reference (IDOR)

**Location:** `SshSessionManager.cs`, `SshApiEndpoints.cs`

Sessions are keyed by a GUID (`Guid.NewGuid().ToString()`). Any client that knows or guesses a session ID can execute commands, download/upload files, and disconnect that session. There is no ownership verification — the session ID is the only authorization token.

```csharp
group.MapPost("/exec", async (ExecRequest req, SshSessionManager manager) =>
{
    var result = await manager.ExecuteAsync(req.SessionId, req.Command);
    return Results.Ok(result);
});
```

**Impact:** If session IDs are leaked (via logs, URL parameters, browser history), any party can hijack the SSH session.

**Mitigation:**
- Bind sessions to authenticated users and verify ownership on every API call.
- Use cryptographically random session tokens with sufficient entropy (GUIDs are already 128-bit, but should be paired with user binding).
- Avoid exposing session IDs in URLs (the streaming download endpoint puts the session ID in the URL path).
- Add session-scoped HMAC tokens for additional verification.

---

### 5. HIGH — Server-Side Request Forgery (SSRF) via SSH Connection

**Location:** `SshApiEndpoints.cs` (`/connect` endpoint), `SshConnectionFactory.cs`

The connect endpoint accepts any `host` and `port` from the client with no validation or allowlisting. An attacker can use this to:

- Port-scan internal networks from the server's vantage point.
- Connect to internal services not exposed to the internet.
- Probe infrastructure behind firewalls.

```csharp
group.MapPost("/connect", async (ConnectRequest req, SshSessionManager manager) =>
{
    // No host/port validation
    sessionId = await manager.ConnectAsync(req.Host, req.Port, req.Username, req.Password);
});
```

**Impact:** The server becomes an open proxy for SSH connections to any network-accessible host.

**Mitigation:**
- Implement host/port allowlists or blocklists (e.g., block RFC 1918 private ranges, link-local, loopback).
- Add configurable connection policies (allowed hosts, CIDR ranges, port ranges).
- Rate-limit connection attempts per source IP.
- Log all connection attempts with source IP for audit purposes.

---

### 6. HIGH — Anti-Forgery Disabled on Upload Endpoint

**Location:** `SshApiEndpoints.cs`

```csharp
}).DisableAntiforgery();
```

The file upload endpoint explicitly disables anti-forgery protection, making it vulnerable to cross-site request forgery attacks. A malicious website visited by an authenticated user could trigger file uploads to remote SSH servers.

**Impact:** An attacker can upload arbitrary files to remote servers through a user's active SSH session via CSRF.

**Mitigation:**
- Re-enable anti-forgery and send the antiforgery token from WASM clients via a custom header.
- Alternatively, use bearer token authentication which is inherently CSRF-resistant.
- If disabling antiforgery is necessary for multipart uploads, implement alternative CSRF protection (e.g., custom header requirement, `SameSite=Strict` cookies).

---

### 7. HIGH — Credentials Transmitted in Request Bodies

**Location:** `ClientSshService.cs`, `SshApiEndpoints.cs`

SSH passwords and private keys are sent as JSON in HTTP POST request bodies:

```csharp
var response = await _http.PostAsJsonAsync("/api/ssh/connect", new
{
    Host = host, Port = port, Username = username, Password = password
});
```

While HTTPS encrypts the transport, these credentials can be:
- Logged by middleware, reverse proxies, or WAFs that inspect request bodies.
- Captured in memory dumps of the server process.
- Visible in browser dev tools network tab and saved in browser extension intercepts.

**Impact:** Credential exposure through logging, debugging, or proxy inspection.

**Mitigation:**
- Ensure HTTPS is strictly enforced (HSTS is configured but only in production).
- Use `[SensitiveData]` attributes or custom model binders that redact credentials from logs.
- Consider using ephemeral key exchange (e.g., the server generates a one-time token, client sends encrypted credentials using that token).
- Implement ASP.NET Core's `IHttpLogging` exclusions to prevent credential logging.
- Clear credential strings from memory after use.

---

### 8. MEDIUM — No Rate Limiting or Brute Force Protection

**Location:** Application-wide

There is no rate limiting on any endpoint. Critical concerns:

- **`/api/ssh/connect`**: Unlimited connection attempts enable brute-force password attacks against SSH servers.
- **`/api/ssh/exec`**: Unlimited command execution.
- **All endpoints**: No protection against denial-of-service through request flooding.

**Mitigation:**
- Add rate limiting middleware (`Microsoft.AspNetCore.RateLimiting`).
- Apply strict limits to the connect endpoint (e.g., 5 failed attempts per minute per target host).
- Implement exponential backoff on failed connection attempts.
- Add per-IP request limits across all endpoints.

---

### 9. MEDIUM — Unlimited Upload Size (Denial of Service)

**Location:** `Program.cs`

```csharp
options.Limits.MaxRequestBodySize = null; // No upload size limit
```

The server accepts request bodies of unlimited size. An attacker can exhaust server memory and disk by uploading extremely large files.

**Impact:** Server-side denial of service through memory/disk exhaustion.

**Mitigation:**
- Set a reasonable `MaxRequestBodySize` (e.g., 500MB or 1GB).
- Implement streaming upload handling to avoid buffering entire files in memory.
- Add per-session upload quotas.
- Monitor and alert on unusually large transfers.

---

### 10. MEDIUM — CDN-Loaded JavaScript Dependencies (Supply Chain Risk)

**Location:** `terminal-interop.js`

```javascript
const xtermModule = await import('https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/+esm');
const fitModule = await import('https://cdn.jsdelivr.net/npm/@xterm/addon-fit@0.10.0/+esm');
```

xterm.js is loaded from a public CDN at runtime. If the CDN is compromised, an attacker could inject malicious code that captures all terminal input/output, including credentials typed into SSH sessions.

**Impact:** Supply chain attack could capture all SSH session data including passwords typed in terminal.

**Mitigation:**
- Bundle xterm.js locally as a static asset using npm/yarn and include it in the build.
- If CDN usage is desired, add Subresource Integrity (SRI) hashes to verify the loaded scripts.
- Implement a Content Security Policy that restricts script sources.

---

### 11. MEDIUM — Use of `eval()` and Inline Script Execution

**Location:** `AppShell.razor`, `FileManager.razor`

```csharp
await JS.InvokeVoidAsync("eval",
    $"document.documentElement.setAttribute('data-theme', '{(_isDarkMode ? "dark" : "light")}')");
```

```csharp
await JS.InvokeVoidAsync("eval", $"document.getElementById('{_fileInputId}')?.click()");
```

While the current values interpolated into `eval()` are internally controlled, using `eval()` establishes a dangerous pattern. If any of these interpolated values ever incorporate user input, it becomes an XSS vector.

**Impact:** Potential XSS if user-controlled data enters the eval path; also prevents deployment of strict CSP.

**Mitigation:**
- Replace all `eval()` calls with dedicated JS interop functions.
- For theme switching: create a `setTheme(themeName)` JS function.
- For file picker: create a `triggerFileInput(elementId)` JS function.
- Add a strict Content Security Policy header that disallows `unsafe-eval`.

---

### 12. MEDIUM — WriteFile Heredoc Delimiter Collision

**Location:** `ShellCommandBuilder.cs`

```csharp
public string WriteFile(string path, string content)
{
    var delimiter = "GUISSH_EOF";
    return $"cat > {escaped} << '{delimiter}'\n{content}\n{delimiter}";
}
```

The heredoc delimiter `GUISSH_EOF` is static. If a user saves a file containing the literal text `GUISSH_EOF` on a line by itself, the heredoc will terminate early, and subsequent text will be interpreted as shell commands.

**Impact:** Arbitrary command execution on the remote server if file content contains the delimiter string.

**Mitigation:**
- Generate a random delimiter per write operation (e.g., `GUISSH_EOF_<random>`).
- Alternatively, use base64-encoded content and decode on the remote side: `echo '<base64>' | base64 -d > file`.
- Or use SFTP for file writes instead of shell commands.

---

### 13. MEDIUM — No Content Security Policy (CSP)

**Location:** `Program.cs`

No CSP headers are configured. This leaves the application vulnerable to:
- Cross-site scripting (XSS) via injected scripts.
- Data exfiltration via unauthorized fetch/XHR requests.
- Clickjacking.

**Mitigation:**
- Add CSP headers via middleware:
  ```
  Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self' wss:; img-src 'self' data:;
  ```
- Add `X-Frame-Options: DENY` or `frame-ancestors 'none'`.
- Add `X-Content-Type-Options: nosniff`.

---

### 14. MEDIUM — Permissive AllowedHosts Configuration

**Location:** `appsettings.json`

```json
"AllowedHosts": "*"
```

This accepts requests with any `Host` header, which can facilitate:
- Host header injection attacks.
- DNS rebinding attacks (attacker's domain resolves to the server's IP).
- Cache poisoning if fronted by a reverse proxy.

**Mitigation:**
- Set `AllowedHosts` to the specific hostname(s) the application should respond to.
- For development: `"AllowedHosts": "localhost"`.
- For production: `"AllowedHosts": "gui-ssh.yourdomain.com"`.

---

### 15. MEDIUM — Session ID Exposed in URL Path (Streaming Download)

**Location:** `SshApiEndpoints.cs`

```csharp
group.MapGet("/download/{sessionId}", async (string sessionId, string path, ...) =>
```

The session ID is included in the URL for the streaming download endpoint. URLs are:
- Logged in server access logs.
- Stored in browser history.
- Visible to browser extensions.
- Captured by proxy servers.
- Sent in `Referer` headers when navigating away.

**Impact:** Session ID leakage through URL logging enables session hijacking.

**Mitigation:**
- Move the session ID to a request header or POST body.
- Use one-time download tokens: generate a short-lived token for each download request and use that in the URL instead of the session ID.
- Strip `Referer` headers via `Referrer-Policy: no-referrer`.

---

### 16. LOW — No Terminal Resize Handling (Incomplete Implementation)

**Location:** `TerminalView.razor`

```csharp
[JSInvokable]
public void OnTerminalResize(int cols, int rows)
{
    // TODO: Send terminal resize to SSH channel
}
```

While not directly a security issue, the terminal resize is not forwarded to the SSH channel. This could cause display issues that might lead users to miss security-relevant output (e.g., prompts being hidden or wrapped).

**Mitigation:**
- Implement `SendWindowChangeRequest` on the SSH shell channel when the terminal resizes.

---

### 17. LOW — Private Keys Held as Managed Strings

**Location:** `SshConnectionFactory.cs`, `ClientSshService.cs`, `CryptoService.cs`

Private keys and passwords are stored as regular .NET `string` objects, which:
- Are immutable and cannot be zeroed after use.
- May persist in managed heap memory for an indeterminate period.
- Could appear in memory dumps.

**Mitigation:**
- Use `SecureString` or `byte[]` for sensitive data with explicit zeroing after use.
- In the `SshConnectionFactory`, ensure the `MemoryStream` containing the key is zeroed before disposal.
- Minimize the lifetime of credential references.

---

### 18. LOW — Error Messages May Leak Internal Information

**Location:** `SshApiEndpoints.cs`

```csharp
catch (Exception ex)
{
    return Results.BadRequest(new { Error = ex.Message });
}
```

Raw exception messages are returned to the client. These may contain:
- Internal server paths.
- SSH library version information.
- Network topology details.
- Stack traces (especially in development mode).

**Mitigation:**
- Return generic error messages to clients.
- Log detailed errors server-side only.
- Use ASP.NET Core's Problem Details pattern with appropriate detail levels per environment.

---

### 19. INFORMATIONAL — Development Profile Exposes HTTP Endpoint

**Location:** `launchSettings.json`

The `http` launch profile binds to `http://localhost:5240` without TLS. If used beyond development, all credentials and session data travel in plaintext.

**Mitigation:**
- Ensure only the `https` profile is used in any non-development context.
- Consider removing the HTTP-only profile or adding a redirect.
- The existing `UseHttpsRedirection()` middleware helps but only when both ports are active.

---

### 20. INFORMATIONAL — No Audit Logging

**Location:** Application-wide

There is no audit trail for security-relevant events:
- Who connected where and when.
- What commands were executed.
- What files were uploaded/downloaded.
- Failed connection attempts.

Basic `ILogger.LogInformation` calls exist for some operations but are insufficient for security auditing.

**Mitigation:**
- Implement structured audit logging for all security-relevant events.
- Include: timestamp, source IP, user identity, target host, action, outcome.
- Store audit logs separately from application logs with tamper protection.
- Consider integrating with a SIEM system for production deployments.

---

## Summary Matrix

| # | Severity | Finding | Status |
|---|----------|---------|--------|
| 1 | **Critical** | No authentication on API endpoints | **Resolved** |
| 2 | **Critical** | Credential encryption uses publicly derivable keys | **Resolved** |
| 3 | **High** | No SSH host key verification (MITM) | **Resolved** |
| 4 | **High** | Session IDOR — no ownership verification | **Resolved** |
| 5 | **High** | SSRF via unrestricted SSH connection targets | **Resolved** |
| 6 | **High** | Anti-forgery disabled on upload endpoint | **Mitigated** |
| 7 | **High** | Credentials in HTTP request bodies | **Resolved** |
| 8 | **Medium** | No rate limiting or brute force protection | **Resolved** |
| 9 | **Medium** | Unlimited upload size (DoS) | **Resolved** |
| 10 | **Medium** | CDN-loaded JS dependencies (supply chain) | Open |
| 11 | **Medium** | eval() usage prevents strict CSP | Open |
| 12 | **Medium** | Heredoc delimiter collision in WriteFile | Open |
| 13 | **Medium** | No Content Security Policy headers | Open |
| 14 | **Medium** | Permissive AllowedHosts configuration | Open |
| 15 | **Medium** | Session ID in download URL path | Open |
| 16 | **Low** | Terminal resize not forwarded to SSH | Open |
| 17 | **Low** | Private keys in managed strings (no secure wipe) | **Resolved** |
| 18 | **Low** | Error messages may leak internals | **Partially resolved** |
| 19 | **Info** | HTTP profile in development | Open |
| 20 | **Info** | No audit logging | Open |

---

## Recommended Priority Order

1. ~~**Add authentication and authorization** (#1, #4)~~ — **Resolved.** Easy Auth middleware + session ownership.
2. ~~**Fix credential encryption** (#2)~~ — **Resolved.** Non-extractable CryptoKey in IndexedDB.
3. ~~**Add SSH host key verification** (#3)~~ — **Resolved.** TOFU with HostKeyStore + approval dialog.
4. ~~**Restrict connection targets** (#5)~~ — **Resolved.** ConnectionPolicy with DNS resolution check.
5. **Fix heredoc delimiter** (#12) — Use randomized delimiters or switch to SFTP for file writes.
6. ~~**Re-enable anti-forgery or add alternative CSRF protection** (#6)~~ — **Mitigated.** X-Requested-With header check.
7. ~~**Add rate limiting** (#8) and **upload size limits** (#9)~~ — **Resolved.** Rate limiter + 500 MB cap.
8. **Bundle JS dependencies locally** (#10) and **remove eval()** (#11).
9. **Add security headers** (#13, #14) — CSP, X-Frame-Options, AllowedHosts.
10. **Add audit logging** (#20).
