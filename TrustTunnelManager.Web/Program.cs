using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using TrustTunnelManager.Application.Abstractions;
using TrustTunnelManager.Domain.Models;
using TrustTunnelManager.Infrastructure.Clients;
using TrustTunnelManager.Infrastructure.Mesh;
using TrustTunnelManager.Web.Components;

var builder = WebApplication.CreateBuilder(args);
var identity = new LocalNodeIdentity(builder.Configuration, builder.Environment);
builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https =>
{
    https.ServerCertificate = identity.Certificate;
    https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
    https.ClientCertificateValidation = (_, _, _) => true;
}));

builder.Services.AddSingleton(identity);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "TrustTunnelManager.AntiForgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

var adminUsername = builder.Configuration["Admin:Username"];
var adminPassword = builder.Configuration["Admin:Password"];
if (string.IsNullOrWhiteSpace(adminUsername) || string.IsNullOrWhiteSpace(adminPassword) || adminPassword.Length < 16)
    throw new InvalidOperationException("Set Admin:Username and a minimum 16-character Admin:Password environment variable.");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "TrustTunnelManager.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.LoginPath = "/login";
    options.AccessDeniedPath = "/login";
    options.SlidingExpiration = true;
});
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddSingleton<ProcessCommandRunner>();
builder.Services.AddSingleton<IPeerRegistry, PeerRegistry>();
builder.Services.AddSingleton<IPairingInvitationStore, PairingInvitationStore>();
builder.Services.AddSingleton<IAuditLog, FileAuditLog>();
builder.Services.AddSingleton<PeerRequestAuthenticator>();
builder.Services.AddSingleton<ILocalTrustTunnelClientManager, SystemdTrustTunnelClientManager>();
builder.Services.AddSingleton<IPeerMeshService, PeerMeshService>();

var app = builder.Build();
app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapGet("/login", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    var token = HtmlEncoder.Default.Encode(tokens.RequestToken ?? string.Empty);
    var error = context.Request.Query.ContainsKey("error") ? "<p>Неверное имя пользователя или пароль.</p>" : "";
    var html = $$"""<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Вход</title><link rel="stylesheet" href="/app.css"><body><main class="login-card"><h1>TrustTunnel Manager</h1>{{error}}<form method="post" action="/account/login"><input type="hidden" name="__RequestVerificationToken" value="{{token}}"><label>Пользователь</label><input name="username" required><label>Пароль</label><input name="password" type="password" required><button type="submit">Войти</button></form></main></body></html>""";
    return Results.Content(html, "text/html; charset=utf-8");
}).AllowAnonymous();

app.MapPost("/account/login", async Task<IResult> (HttpContext context, IAntiforgery antiforgery) =>
{
    try { await antiforgery.ValidateRequestAsync(context); }
    catch (AntiforgeryValidationException) { return Results.BadRequest(); }
    var form = await context.Request.ReadFormAsync();
    var expected = Encoding.UTF8.GetBytes(adminPassword);
    var supplied = Encoding.UTF8.GetBytes(form["password"].ToString());
    if (!string.Equals(form["username"].ToString(), adminUsername, StringComparison.Ordinal) ||
        !CryptographicOperations.FixedTimeEquals(expected, supplied))
        return Results.Redirect("/login?error=1");
    var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, adminUsername), new Claim(ClaimTypes.Role, "Administrator")],
        CookieAuthenticationDefaults.AuthenticationScheme));
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    return Results.Redirect("/nodes");
}).AllowAnonymous();

app.MapGet("/logout", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    var token = HtmlEncoder.Default.Encode(tokens.RequestToken ?? string.Empty);
    var html = $$"""<!doctype html><html lang="ru"><meta charset="utf-8"><body><form method="post" action="/account/logout"><input type="hidden" name="__RequestVerificationToken" value="{{token}}"><button type="submit">Подтвердить выход</button></form></body></html>""";
    return Results.Content(html, "text/html; charset=utf-8");
}).RequireAuthorization();

app.MapPost("/account/logout", async Task<IResult> (HttpContext context, IAntiforgery antiforgery) =>
{
    try { await antiforgery.ValidateRequestAsync(context); }
    catch (AntiforgeryValidationException) { return Results.BadRequest(); }
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).RequireAuthorization();

app.MapPost("/api/pairing/invitations", async (IPeerMeshService mesh, CancellationToken ct) =>
    Results.Ok(new { token = await mesh.CreateInvitationAsync(ct), expiresInSeconds = 300 })).RequireAuthorization();

app.MapPost("/internal/v1/pairing/accept", async Task<IResult> (
    HttpContext context, NodeJoinRequest request, LocalNodeIdentity local,
    IPairingInvitationStore invitations, IPeerRegistry peers, IAuditLog audit, CancellationToken ct) =>
{
    if (!context.Request.IsHttps) return Results.BadRequest();
    var certificate = await context.Connection.GetClientCertificateAsync(ct);
    if (certificate is null || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime()
        || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime()) return Results.Unauthorized();
    var fingerprint = LocalNodeIdentity.NormalizeThumbprint(certificate.GetCertHashString(HashAlgorithmName.SHA256));
    if (fingerprint != LocalNodeIdentity.NormalizeThumbprint(request.Node.CertificateThumbprint) ||
        request.Node.NodeId == local.Info.NodeId ||
        !Uri.TryCreate(request.Node.BaseUrl, UriKind.Absolute, out var peerUri) || peerUri.Scheme != Uri.UriSchemeHttps)
        return Results.BadRequest("Invalid peer identity.");
    if (!await invitations.TryConsumeAsync(request.InviteToken, ct)) return Results.Unauthorized();

    await peers.AddOrUpdateAsync(request.Node, ct);
    await audit.WriteAsync(new AuditEntry(DateTimeOffset.UtcNow, "peer-admin", request.Node.NodeId,
        local.Info.NodeId, "peer.pair", true, "Single-use enrollment completed."), ct);
    return Results.Ok(local.Info);
}).AllowAnonymous();

app.MapGet("/internal/v1/status", async Task<IResult> (
    HttpContext context, PeerRequestAuthenticator auth, ILocalTrustTunnelClientManager local, CancellationToken ct) =>
{
    if (await auth.AuthenticateAsync(context, ct) is null) return Results.Unauthorized();
    return Results.Ok(await local.GetStatusAsync(ct));
});

app.MapPost("/internal/v1/service/{action}", async Task<IResult> (
    HttpContext context, string action, PeerRequestAuthenticator auth, ILocalTrustTunnelClientManager local,
    LocalNodeIdentity node, IAuditLog audit, CancellationToken ct) =>
{
    var peer = await auth.AuthenticateAsync(context, ct);
    if (peer is null) return Results.Unauthorized();
    var result = await local.ExecuteAsync(action, ct);
    var actor = context.Request.Headers["X-TTM-Actor"].ToString();
    await audit.WriteAsync(new AuditEntry(DateTimeOffset.UtcNow, string.IsNullOrWhiteSpace(actor) ? "peer-admin" : actor,
        peer.Node.NodeId, node.Info.NodeId, "service." + action, result.Success, result.Message), ct);
    return Results.Ok(result);
});

app.MapGet("/internal/v1/logs", async Task<IResult> (
    HttpContext context, int? count, PeerRequestAuthenticator auth, ILocalTrustTunnelClientManager local, CancellationToken ct) =>
{
    if (await auth.AuthenticateAsync(context, ct) is null) return Results.Unauthorized();
    return Results.Ok(await local.GetLogsAsync(count ?? 100, ct));
});

app.MapGet("/internal/v1/configuration", async Task<IResult> (
    HttpContext context, PeerRequestAuthenticator auth, ILocalTrustTunnelClientManager local, CancellationToken ct) =>
{
    if (await auth.AuthenticateAsync(context, ct) is null) return Results.Unauthorized();
    return Results.Ok(await local.GetConfigurationAsync(ct));
});

app.MapPut("/internal/v1/configuration", async Task<IResult> (
    HttpContext context, ConfigurationUpdate update, PeerRequestAuthenticator auth,
    ILocalTrustTunnelClientManager local, LocalNodeIdentity node, IAuditLog audit, CancellationToken ct) =>
{
    var peer = await auth.AuthenticateAsync(context, ct);
    if (peer is null) return Results.Unauthorized();
    var result = await local.ApplyConfigurationAsync(update.Content, update.ExpectedRevision, ct);
    var actor = context.Request.Headers["X-TTM-Actor"].ToString();
    await audit.WriteAsync(new AuditEntry(DateTimeOffset.UtcNow, string.IsNullOrWhiteSpace(actor) ? "peer-admin" : actor,
        peer.Node.NodeId, node.Info.NodeId, "configuration.apply", result.Success, result.Message), ct);
    return Results.Ok(result);
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization();
app.Run();
