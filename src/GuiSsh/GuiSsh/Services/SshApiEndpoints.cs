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
                var sessionId = await manager.ConnectAsync(req.Host, req.Port, req.Username, req.Password);
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
    }
}

public record ConnectRequest(string Host, int Port, string Username, string Password);
public record ExecRequest(string SessionId, string Command);
public record DisconnectRequest(string SessionId);
