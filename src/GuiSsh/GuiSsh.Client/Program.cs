using GuiSsh.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// HttpClient for calling server SSH API endpoints
builder.Services.AddScoped(sp =>
    new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// MudBlazor
builder.Services.AddMudServices();

// ISshService: WASM-side implementation (calls server HTTP API)
builder.Services.AddScoped<ISshService, ClientSshService>();

// Browser storage services
builder.Services.AddScoped<ConnectionStore>();
builder.Services.AddScoped<CryptoService>();

// Shared services
builder.Services.AddSingleton<ShellCommandBuilder>();
builder.Services.AddSingleton<ShellOutputParser>();

await builder.Build().RunAsync();
