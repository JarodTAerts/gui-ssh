using GuiSsh;
using GuiSsh.Client.Services;
using GuiSsh.Components;
using GuiSsh.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using MudBlazor.Services;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Kestrel: set upload size limit (500 MB) and keep-alive settings
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 500 * 1024 * 1024; // 500 MB max upload
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(30);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
    options.Limits.MinResponseDataRate = new MinDataRate(
        bytesPerSecond: 100, gracePeriod: TimeSpan.FromSeconds(30)); // Allow slow connections
});

// Add Blazor with both render modes
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// MudBlazor
builder.Services.AddMudServices();

// SSH services (server-side)
builder.Services.AddSingleton<SshConnectionFactory>();
builder.Services.AddSingleton<HostKeyStore>();
builder.Services.AddSingleton<ConnectionPolicy>();
builder.Services.AddSingleton<SshSessionManager>();
builder.Services.AddHostedService<SessionEvictionService>();

// Authentication & Authorization
builder.Services.AddAuthorization();

// Rate limiting
builder.Services.AddRateLimiter(options =>
{
    // Global per-IP limiter: 100 requests per minute
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1)
            }));

    // Strict limiter for connect endpoints: 10 attempts per minute per IP
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

// HTTP logging: exclude request bodies to avoid logging credentials
builder.Services.AddHttpLogging(options =>
{
    options.LoggingFields = Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestPath
        | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestMethod
        | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.ResponseStatusCode;
});

// ISshService: server-side implementation (direct SSH.NET calls)
builder.Services.AddScoped<ISshService, ServerSshService>();

// Shared services (available to both render modes)
builder.Services.AddSingleton<ShellCommandBuilder>();
builder.Services.AddSingleton<ShellOutputParser>();

// Browser storage services (needed server-side for prerendering of Interactive Auto components)
builder.Services.AddScoped<ConnectionStore>();
builder.Services.AddScoped<CryptoService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// Easy Auth: populate HttpContext.User from Azure Container Apps headers (dev fallback in Development)
app.UseMiddleware<EasyAuthMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapSshApi();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(GuiSsh.Client._Imports).Assembly);

app.Run();
