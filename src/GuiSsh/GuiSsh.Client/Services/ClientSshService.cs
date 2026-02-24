using System.Net.Http.Json;
using GuiSsh.Client.Models;

namespace GuiSsh.Client.Services;

/// <summary>
/// WASM-side ISshService implementation that calls the server's HTTP API endpoints.
/// Used when components are running in WebAssembly render mode.
/// </summary>
public class ClientSshService : ISshService
{
    private readonly HttpClient _http;

    public ClientSshService(HttpClient http)
    {
        _http = http;
    }

    public async Task<string> ConnectAsync(string host, int port, string username, string password)
    {
        var response = await _http.PostAsJsonAsync("/api/ssh/connect", new
        {
            Host = host,
            Port = port,
            Username = username,
            Password = password
        });

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ConnectResponse>();
        return result?.SessionId ?? throw new InvalidOperationException("No session ID returned.");
    }

    public async Task DisconnectAsync(string sessionId)
    {
        var response = await _http.PostAsJsonAsync("/api/ssh/disconnect", new { SessionId = sessionId });
        response.EnsureSuccessStatusCode();
    }

    public async Task<CommandResult> ExecuteAsync(string sessionId, string command)
    {
        var response = await _http.PostAsJsonAsync("/api/ssh/exec", new
        {
            SessionId = sessionId,
            Command = command
        });

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<CommandResult>()
               ?? new CommandResult { ExitCode = -1, StdErr = "Failed to deserialize response." };
    }

    public async Task<bool> IsConnectedAsync(string sessionId)
    {
        try
        {
            var response = await _http.GetAsync($"/api/ssh/status/{sessionId}");
            if (!response.IsSuccessStatusCode)
                return false;

            var result = await response.Content.ReadFromJsonAsync<StatusResponse>();
            return result?.Connected ?? false;
        }
        catch
        {
            return false;
        }
    }

    private record ConnectResponse(string SessionId);
    private record StatusResponse(bool Connected);
}
