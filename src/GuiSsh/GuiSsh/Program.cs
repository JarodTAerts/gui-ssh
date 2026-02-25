using GuiSsh;
using GuiSsh.Client.Services;
using GuiSsh.Components;
using GuiSsh.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Kestrel: allow large file transfers
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = null; // No upload size limit
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
builder.Services.AddSingleton<SshSessionManager>();
builder.Services.AddHostedService<SessionEvictionService>();

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

app.UseAntiforgery();

app.MapStaticAssets();
app.MapSshApi();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(GuiSsh.Client._Imports).Assembly);

app.Run();
