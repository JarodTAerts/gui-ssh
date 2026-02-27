using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace GuiSsh.Services;

/// <summary>
/// Reads Azure Container Apps Easy Auth headers and populates HttpContext.User.
/// In development without Easy Auth, falls back to a dev identity so the app
/// remains functional during local development.
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

            // Parse the full X-MS-CLIENT-PRINCIPAL for additional claims
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
                            if (type != null && val != null
                                && type != ClaimTypes.NameIdentifier
                                && type != ClaimTypes.Name)
                            {
                                claims.Add(new Claim(type, val));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse X-MS-CLIENT-PRINCIPAL header");
                }
            }

            var identity = new ClaimsIdentity(claims, "EasyAuth");
            context.User = new ClaimsPrincipal(identity);
        }
        else if (_env.IsDevelopment())
        {
            // Dev fallback — simulate an authenticated user for local development
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
