using GuiSsh.Services;
using Microsoft.AspNetCore.Http.Features;

namespace GuiSsh;

/// <summary>
/// Registers SSH API endpoints used by WASM components (ClientSshService).
/// </summary>
public static class SshApiEndpoints
{
    public static void MapSshApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/ssh");

        group.MapPost("/connect", async (ConnectRequest req, SshSessionManager manager) =>
        {
            try
            {
                string sessionId;
                if (!string.IsNullOrEmpty(req.PrivateKey))
                {
                    sessionId = await manager.ConnectWithKeyAsync(req.Host, req.Port, req.Username, req.PrivateKey, req.Passphrase);
                }
                else
                {
                    sessionId = await manager.ConnectAsync(req.Host, req.Port, req.Username, req.Password);
                }
                return Results.Ok(new { SessionId = sessionId });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { Error = ex.Message });
            }
        });

        group.MapPost("/exec", async (ExecRequest req, SshSessionManager manager) =>
        {
            var result = await manager.ExecuteAsync(req.SessionId, req.Command);
            return Results.Ok(result);
        });

        group.MapPost("/disconnect", async (DisconnectRequest req, SshSessionManager manager) =>
        {
            await manager.DisconnectAsync(req.SessionId);
            return Results.Ok();
        });

        group.MapGet("/status/{sessionId}", (string sessionId, SshSessionManager manager) =>
        {
            var connected = manager.IsConnected(sessionId);
            return Results.Ok(new { Connected = connected });
        });

        group.MapPost("/download", async (DownloadRequest req, SshSessionManager manager) =>
        {
            try
            {
                var (data, fileName) = await manager.DownloadFileAsync(req.SessionId, req.RemotePath);
                return Results.File(data, "application/octet-stream", fileName);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { Error = ex.Message });
            }
        });

        // Streaming download — browser-native download with Content-Length for progress
        group.MapGet("/download/{sessionId}", async (string sessionId, string path, HttpContext ctx, SshSessionManager manager, ILogger<SshSessionManager> logger) =>
        {
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
                // Client disconnected — silently abort
                logger.LogInformation("Download cancelled by client: {Path}", path);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Download failed for {Path}", path);
                if (!ctx.Response.HasStarted)
                {
                    // Clear Content-Length to avoid mismatch with error JSON body
                    ctx.Response.ContentLength = null;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { Error = ex.Message });
                }
            }
        });

        group.MapPost("/upload", async (HttpRequest httpRequest, SshSessionManager manager) =>
        {
            try
            {
                var sessionId = httpRequest.Form["sessionId"].ToString();
                var remotePath = httpRequest.Form["remotePath"].ToString();
                var file = httpRequest.Form.Files.FirstOrDefault();

                if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(remotePath) || file == null)
                    return Results.BadRequest(new { Error = "Missing sessionId, remotePath, or file." });

                using var stream = file.OpenReadStream();
                await manager.UploadFileAsync(sessionId, remotePath, stream);
                return Results.Ok(new { Success = true, FileName = file.FileName });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { Error = ex.Message });
            }
        }).DisableAntiforgery();
    }
}

public record ConnectRequest(string Host, int Port, string Username, string Password, string? PrivateKey = null, string? Passphrase = null);
public record ExecRequest(string SessionId, string Command);
public record DisconnectRequest(string SessionId);
public record DownloadRequest(string SessionId, string RemotePath);
