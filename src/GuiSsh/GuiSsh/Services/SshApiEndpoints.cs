using GuiSsh.Services;

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
