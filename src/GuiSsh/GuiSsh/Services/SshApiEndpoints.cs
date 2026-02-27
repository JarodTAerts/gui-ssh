using System.Security.Claims;
using GuiSsh.Services;
using Microsoft.AspNetCore.Http.Features;

namespace GuiSsh;

/// <summary>
/// Registers SSH API endpoints used by WASM components (ClientSshService).
/// All endpoints require authentication and verify session ownership.
/// </summary>
public static class SshApiEndpoints
{
    /// <summary>
    /// Extracts the authenticated user's ID from the Easy Auth claims.
    /// Returns null if the user is not authenticated.
    /// </summary>
    private static string? GetUserId(HttpContext ctx)
        => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);

    public static void MapSshApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/ssh")
            .RequireAuthorization();

        group.MapPost("/connect", async (ConnectRequest req, SshSessionManager manager,
            ConnectionPolicy policy, HttpContext ctx, ILogger<SshSessionManager> logger) =>
        {
            var userId = GetUserId(ctx);
            if (string.IsNullOrEmpty(userId))
                return Results.Unauthorized();

            var (allowed, reason) = await policy.EvaluateAsync(req.Host, req.Port);
            if (!allowed)
                return Results.Ok(new Client.Models.ConnectResult { Error = reason });

            try
            {
                Client.Models.ConnectResult result;
                if (!string.IsNullOrEmpty(req.PrivateKey))
                {
                    result = await manager.ConnectWithKeyAsync(req.Host, req.Port, req.Username,
                        req.PrivateKey, req.Passphrase, userId);
                }
                else
                {
                    result = await manager.ConnectAsync(req.Host, req.Port, req.Username,
                        req.Password, userId);
                }
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Connection failed for {Host}:{Port} by user {UserId}",
                    req.Host, req.Port, userId);
                return Results.Ok(new Client.Models.ConnectResult
                {
                    Error = "Connection failed. Check host and credentials."
                });
            }
        }).RequireRateLimiting("connect");

        group.MapPost("/connect/trust", async (TrustHostKeyRequest req, SshSessionManager manager,
            ConnectionPolicy policy, HttpContext ctx, ILogger<SshSessionManager> logger) =>
        {
            var userId = GetUserId(ctx);
            if (string.IsNullOrEmpty(userId))
                return Results.Unauthorized();

            var (allowed, reason) = await policy.EvaluateAsync(req.Host, req.Port);
            if (!allowed)
                return Results.Ok(new Client.Models.ConnectResult { Error = reason });

            try
            {
                // Trust the host key, then reconnect
                await manager.TrustAndReconnectAsync(req.Host, req.Port, req.Fingerprint, req.Algorithm);

                Client.Models.ConnectResult result;
                if (!string.IsNullOrEmpty(req.PrivateKey))
                {
                    result = await manager.ConnectWithKeyAsync(req.Host, req.Port, req.Username,
                        req.PrivateKey, req.Passphrase, userId);
                }
                else
                {
                    result = await manager.ConnectAsync(req.Host, req.Port, req.Username,
                        req.Password, userId);
                }
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Trust-and-reconnect failed for {Host}:{Port} by user {UserId}",
                    req.Host, req.Port, userId);
                return Results.Ok(new Client.Models.ConnectResult
                {
                    Error = "Connection failed after trusting host key."
                });
            }
        }).RequireRateLimiting("connect");

        group.MapPost("/exec", async (ExecRequest req, SshSessionManager manager, HttpContext ctx) =>
        {
            var userId = GetUserId(ctx);
            if (string.IsNullOrEmpty(userId))
                return Results.Unauthorized();

            var session = manager.GetSessionForOwner(req.SessionId, userId);
            if (session == null)
                return Results.NotFound(new { Error = "Session not found." });

            var result = await manager.ExecuteAsync(req.SessionId, req.Command);
            return Results.Ok(result);
        });

        group.MapPost("/disconnect", async (DisconnectRequest req, SshSessionManager manager, HttpContext ctx) =>
        {
            var userId = GetUserId(ctx);
            if (string.IsNullOrEmpty(userId))
                return Results.Unauthorized();

            var session = manager.GetSessionForOwner(req.SessionId, userId);
            if (session == null)
                return Results.NotFound(new { Error = "Session not found." });

            await manager.DisconnectAsync(req.SessionId);
            return Results.Ok();
        });

        group.MapGet("/status/{sessionId}", (string sessionId, SshSessionManager manager, HttpContext ctx) =>
        {
            var userId = GetUserId(ctx);
            if (string.IsNullOrEmpty(userId))
                return Results.Unauthorized();

            var session = manager.GetSessionForOwner(sessionId, userId);
            var connected = session?.IsConnected ?? false;
            return Results.Ok(new { Connected = connected });
        });

        group.MapPost("/download", async (DownloadRequest req, SshSessionManager manager, HttpContext ctx,
            ILogger<SshSessionManager> logger) =>
        {
            var userId = GetUserId(ctx);
            if (string.IsNullOrEmpty(userId))
                return Results.Unauthorized();

            var session = manager.GetSessionForOwner(req.SessionId, userId);
            if (session == null)
                return Results.NotFound(new { Error = "Session not found." });

            try
            {
                var (data, fileName) = await manager.DownloadFileAsync(req.SessionId, req.RemotePath);
                return Results.File(data, "application/octet-stream", fileName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Download failed for {Path} on session {SessionId}", req.RemotePath, req.SessionId);
                return Results.BadRequest(new { Error = "Download failed." });
            }
        });

        // Streaming download — browser-native download with Content-Length for progress
        group.MapGet("/download/{sessionId}", async (string sessionId, string path, HttpContext ctx,
            SshSessionManager manager, ILogger<SshSessionManager> logger) =>
        {
            var userId = GetUserId(ctx);
            if (string.IsNullOrEmpty(userId))
            {
                ctx.Response.StatusCode = 401;
                return;
            }

            var session = manager.GetSessionForOwner(sessionId, userId);
            if (session == null)
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            try
            {
                // SSH.NET's DownloadFile uses synchronous Stream.Write — allow it for this endpoint
                var syncIOFeature = ctx.Features.Get<IHttpBodyControlFeature>();
                if (syncIOFeature != null)
                    syncIOFeature.AllowSynchronousIO = true;

                // Disable response buffering for true streaming
                var bufferingFeature = ctx.Features.Get<IHttpResponseBodyFeature>();
                bufferingFeature?.DisableBuffering();

                // Get file info for Content-Length and filename
                var (size, isDirectory, fileName) = await manager.GetFileInfoAsync(sessionId, path);
                if (isDirectory)
                    fileName += ".tar.gz";

                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
                if (!isDirectory && size > 0)
                    ctx.Response.ContentLength = size;

                logger.LogInformation("Starting streaming download: {Path} ({Size} bytes)", path, size);

                // Pass cancellation token so browser cancel/close aborts the SFTP transfer
                var ct = ctx.RequestAborted;
                await manager.StreamDownloadAsync(sessionId, path, ctx.Response.Body, cancellationToken: ct);

                logger.LogInformation("Completed streaming download: {Path}", path);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Download cancelled by client: {Path}", path);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Download failed for {Path}", path);
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.ContentLength = null;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { Error = "Download failed." });
                }
            }
        });

        group.MapPost("/upload", async (HttpRequest httpRequest, SshSessionManager manager,
            ILogger<SshSessionManager> logger) =>
        {
            var userId = GetUserId(httpRequest.HttpContext);
            if (string.IsNullOrEmpty(userId))
                return Results.Unauthorized();

            // Require custom header as CSRF protection (browsers won't add this cross-origin)
            if (!httpRequest.Headers.ContainsKey("X-Requested-With"))
                return Results.StatusCode(403);

            try
            {
                var sessionId = httpRequest.Form["sessionId"].ToString();
                var remotePath = httpRequest.Form["remotePath"].ToString();
                var file = httpRequest.Form.Files.FirstOrDefault();

                if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(remotePath) || file == null)
                    return Results.BadRequest(new { Error = "Missing sessionId, remotePath, or file." });

                var session = manager.GetSessionForOwner(sessionId, userId);
                if (session == null)
                    return Results.NotFound(new { Error = "Session not found." });

                using var stream = file.OpenReadStream();
                await manager.UploadFileAsync(sessionId, remotePath, stream);
                return Results.Ok(new { Success = true, FileName = file.FileName });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Upload failed");
                return Results.BadRequest(new { Error = "Upload failed." });
            }
        }).DisableAntiforgery(); // Multipart uploads require this; X-Requested-With header mitigates CSRF
    }
}

public record ConnectRequest(string Host, int Port, string Username, string Password, string? PrivateKey = null, string? Passphrase = null);
public record TrustHostKeyRequest(string Host, int Port, string Username, string Password, string Fingerprint, string Algorithm, string? PrivateKey = null, string? Passphrase = null);
public record ExecRequest(string SessionId, string Command);
public record DisconnectRequest(string SessionId);
public record DownloadRequest(string SessionId, string RemotePath);
