using TrustTunnelManager.Application.Abstractions;
using TrustTunnelManager.Infrastructure.Monitoring;
using TrustTunnelManager.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server keeps endpoint credentials and privileged operations on the server.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var metricsAddress = builder.Configuration["TrustTunnel:Metrics:BaseAddress"]
    ?? "http://127.0.0.1:1987/";

if (!Uri.TryCreate(metricsAddress, UriKind.Absolute, out var metricsUri) ||
    (metricsUri.Scheme != Uri.UriSchemeHttp && metricsUri.Scheme != Uri.UriSchemeHttps))
{
    throw new InvalidOperationException(
        "TrustTunnel:Metrics:BaseAddress must be an absolute HTTP or HTTPS URL.");
}

var timeoutSeconds = int.TryParse(
    builder.Configuration["TrustTunnel:Metrics:TimeoutSeconds"],
    out var configuredTimeout)
        ? Math.Clamp(configuredTimeout, 1, 30)
        : 3;

builder.Services.AddHttpClient<ITrustTunnelMonitoringService, TrustTunnelMonitoringService>(
    httpClient =>
    {
        httpClient.BaseAddress = metricsUri;
        httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
    });

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
