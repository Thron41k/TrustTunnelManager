using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using TrustTunnelManager.Application.Abstractions;
using TrustTunnelManager.Domain.Models;
using TrustTunnelManager.Infrastructure.Clients;
using TrustTunnelManager.Infrastructure.Mesh;
using TrustTunnelManager.Web.Components;

var builder = WebApplication.CreateBuilder(args);
var identity = new LocalNodeIdentity(builder.Configuration, builder.Environment);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 1_100_000;
    options.ConfigureHttpsDefaults(https =>
    {
        https.ServerCertificate = identity.Certificate;

        // Peer certificates are pinned by SHA-256 in PeerRequestAuthenticator.
        // The callback intentionally defers trust decisions to the endpoint:
        // browser UI requests may omit a certificate, while peer API routes require a pinned one.
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        https.ClientCertificateValidation = (_, _, _) => true;
    });
});

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "TrustTunnelManager.AntiForgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

var adminUsername = builder.Configuration["Admin:Username"];
var adminPassword = builder.Configuration["Admin:Password"];
if (string.IsNullOrWhiteSpace(adminUsername) || string.IsNullOrWhiteSpace(adminPassword) ||
    adminPassword.Length < 16)
{
    throw new InvalidOperationException(
        "Задайте Admin:Username и Admin:Password длиной не менее 16 символов через environment.");
}

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "TrustTunnelManager.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.IsEssential = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
    });

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }));
});

builder.Services.AddSingleton<ProcessCommandRunner>();
builder.Services.AddSingleton<IPeerRegistry, PeerRegistry>();
builder.Services.AddSingleton<IPairingInvitationStore, PairingInvitationStore>();
builder.Services.AddSingleton<IAuditLog, FileAuditLog>();
builder.Services.AddSingleton<PeerRequestAuthenticator>();
builder.Services.AddSingleton<ILocalTrustTunnelClientManager, SystemdTrustTunnelClientManager>();
builder.Services.AddSingleton<IPeerMeshService, PeerMeshService>();
builder.Services.AddSingleton(identity);

var app = builder.Build();
app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapGet("/login", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    var token = HtmlEncoder.Default.Encode(tokens.RequestToken ?? string.Empty);
    var error = context.Request.Query.ContainsKey("error")
        ? "<p>Неверное имя пользователя или пароль.</p>"
        : string.Empty;

    var html = $$"""
        <!doctype html>
        <html lang="ru">
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Вход — TrustTunnel Manager</title>
        <link rel="stylesheet" href="/app.css">
        <body>
          <main class="login-card">
            <h1>TrustTunnel Manager</h1>
            {{error}}
            <form method="post" action="/account/login">
              <input type="hidden" name="__RequestVerificationToken" value="{{token}}">
              <label>Пользователь</label>
              <input name="username" autocomplete="username" required>
              <label>Пароль</label>
              <input name="password" type="password" autocomplete="current-password" required>
              <button type="submit">Войти</button>
            </form>
          </main>
        </body>
        </html>
        """;

    return Results.Content(html, "text/html; charset=utf-8");
}).AllowAnonymous();

app.MapPost("/account/login", async Task<IResult> (HttpContext context, IAntiforgery antiforgery) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest();
    }

    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    var suppliedUsername = form["username"].ToString();
    var suppliedPassword = form["password"].ToString();
    var expectedPassword = Encoding.UTF8.GetBytes(adminPassword);
    var actualPassword = Encoding.UTF8.GetBytes(suppliedPassword);

    if (!string.Equals(suppliedUsername, adminUsername, StringComparison.Ordinal) ||
        !CryptographicOperations.FixedTimeEquals(expectedPassword, actualPassword))
    {
        return Results.Redirect("/login?error=1");
    }

    var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, adminUsername), new Claim(ClaimTypes.Role, "Administrator")],
        CookieAuthenticationDefaults.AuthenticationScheme));

    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    return Results.Redirect("/nodes");
})
.AllowAnonymous()
.RequireRateLimiting("login");

app.MapGet("/logout", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    var token = HtmlEncoder.Default.Encode(tokens.RequestToken ?? string.Empty);
    var html = $$"""
        <!doctype html>
        <html lang="ru">
        <meta charset="utf-8">
        <body>
          <form method="post" action="/account/logout">
            <input type="hidden" name="__RequestVerificationToken" value="{{token}}">
            <button type="submit">Подтвердить выход</button>
          </form>
        </body>
        </html>
        """;

    return Results.Content(html, "text/html; charset=utf-8");
}).RequireAuthorization();

app.MapPost("/account/logout", async Task<IResult> (HttpContext context, IAntiforgery antiforgery) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest();
    }

    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).RequireAuthorization();

app.MapPost("/api/pairing/invitations", async (
    HttpContext context, IPeerMeshService mesh, CancellationToken ct) =>
{
    var actor = context.User.Identity?.Name ?? "administrator";
    var token = await mesh.CreateInvitationAsync(actor, ct);
    return Results.Ok(new { token, expiresInSeconds = 300 });
}).RequireAuthorization();

app.MapPost("/internal/v1/pairing/accept", async Task<IResult> (
    HttpContext context,
    NodeJoinRequest request,
    LocalNodeIdentity local,
    IPairingInvitationStore invitations,
    IPeerRegistry peers,
    IAuditLog audit,
    CancellationToken ct) =>
{
    if (!context.Request.IsHttps || request is null || request.Node is null ||
        string.IsNullOrWhiteSpace(request.InviteToken))
    {
        return Results.BadRequest("Invalid pairing request.");
    }

    var certificate = await context.Connection.GetClientCertificateAsync(ct);
    if (certificate is null || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() ||
        DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
    {
        return Results.Unauthorized();
    }

    var fingerprint = LocalNodeIdentity.NormalizeThumbprint(
        certificate.GetCertHashString(HashAlgorithmName.SHA256));
    var claimedFingerprint = LocalNodeIdentity.NormalizeThumbprint(request.Node.CertificateThumbprint);

    if (fingerprint != claimedFingerprint || request.Node.NodeId == local.Info.NodeId)
        return Results.BadRequest("Invalid peer identity.");

    try
    {
        // AddOrUpdate performs strict NodeId, HTTPS URL and fingerprint validation.
        // Consume the invitation only after structural identity validation.
        await peers.AddOrUpdateAsync(request.Node, ct);
    }
    catch (InvalidDataException exception)
    {
        return Results.BadRequest(exception.Message);
    }

    if (!await invitations.TryConsumeAsync(request.InviteToken, ct))
    {
        await peers.RemoveAsync(request.Node.NodeId, ct);
        return Results.Unauthorized();
    }

    await audit.WriteAsync(new AuditEntry(
        DateTimeOffset.UtcNow,
        "peer:" + request.Node.Name,
        request.Node.NodeId,
        local.Info.NodeId,
        "peer.pair",
        true,
        "Одноразовое приглашение использовано; двустороннее сопряжение завершено."), ct);

    return Results.Ok(local.Info);
})
.AllowAnonymous()
.WithMetadata(new RequestSizeLimitAttribute(64_000));

app.MapGet("/internal/v1/status", async Task<IResult> (
    HttpContext context,
    PeerRequestAuthenticator authenticator,
    ILocalTrustTunnelClientManager local,
    CancellationToken ct) =>
{
    if (await authenticator.AuthenticateAsync(context, ct) is null)
        return Results.Unauthorized();

    return Results.Ok(await local.GetStatusAsync(ct));
});

app.MapPost("/internal/v1/service/{action}", async Task<IResult> (
    HttpContext context,
    string action,
    PeerRequestAuthenticator authenticator,
    ILocalTrustTunnelClientManager local,
    LocalNodeIdentity node,
    IAuditLog audit,
    CancellationToken ct) =>
{
    var peer = await authenticator.AuthenticateAsync(context, ct);
    if (peer is null)
        return Results.Unauthorized();

    var result = await local.ExecuteAsync(action, ct);
    await audit.WriteAsync(new AuditEntry(
        DateTimeOffset.UtcNow,
        "peer:" + peer.Node.Name,
        peer.Node.NodeId,
        node.Info.NodeId,
        "service." + action,
        result.Success,
        result.Message), ct);

    return Results.Ok(result);
});

app.MapGet("/internal/v1/logs", async Task<IResult> (
    HttpContext context,
    int? count,
    PeerRequestAuthenticator authenticator,
    ILocalTrustTunnelClientManager local,
    CancellationToken ct) =>
{
    if (await authenticator.AuthenticateAsync(context, ct) is null)
        return Results.Unauthorized();

    return Results.Ok(await local.GetLogsAsync(Math.Clamp(count ?? 100, 1, 500), ct));
});

app.MapGet("/internal/v1/configuration", async Task<IResult> (
    HttpContext context,
    PeerRequestAuthenticator authenticator,
    ILocalTrustTunnelClientManager local,
    CancellationToken ct) =>
{
    if (await authenticator.AuthenticateAsync(context, ct) is null)
        return Results.Unauthorized();

    return Results.Ok(await local.GetConfigurationAsync(ct));
});

app.MapPut("/internal/v1/configuration", async Task<IResult> (
    HttpContext context,
    ConfigurationUpdate update,
    PeerRequestAuthenticator authenticator,
    ILocalTrustTunnelClientManager local,
    LocalNodeIdentity node,
    IAuditLog audit,
    CancellationToken ct) =>
{
    var peer = await authenticator.AuthenticateAsync(context, ct);
    if (peer is null)
        return Results.Unauthorized();

    if (update is null || update.Content is null || update.Content.Length > 1_000_000)
        return Results.BadRequest("Configuration is missing or too large.");

    var result = await local.ApplyConfigurationAsync(update.Content, update.ExpectedRevision, ct);
    await audit.WriteAsync(new AuditEntry(
        DateTimeOffset.UtcNow,
        "peer:" + peer.Node.Name,
        peer.Node.NodeId,
        node.Info.NodeId,
        "configuration.apply",
        result.Success,
        result.Message), ct);

    return Results.Ok(result);
})
.WithMetadata(new RequestSizeLimitAttribute(1_100_000));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

app.Run();
