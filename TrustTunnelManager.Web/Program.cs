using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using Microsoft.EntityFrameworkCore;
using System.Text.Encodings.Web;
using TrustTunnelManager.Application.Abstractions;
using TrustTunnelManager.Infrastructure.Clients;
using TrustTunnelManager.Infrastructure.Configuration;
using TrustTunnelManager.Infrastructure.Processes;
using TrustTunnelManager.Infrastructure.Rules;
using TrustTunnelManager.Infrastructure.Systemd;
using TrustTunnelManager.Web.Components;
using TrustTunnelManager.Web.Data;
using TrustTunnelManager.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var databaseConnection = builder.Configuration.GetConnectionString("ApplicationDatabase")
    ?? "Data Source=App_Data/trusttunnelmanager.db";

if (!databaseConnection.Contains("Data Source=", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("ApplicationDatabase must use a SQLite Data Source.");
}

var databasePath = databaseConnection["Data Source=".Length..].Trim().Trim('"');

if (!Path.IsPathRooted(databasePath))
{
    databasePath = Path.Combine(builder.Environment.ContentRootPath, databasePath);
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
databaseConnection = $"Data Source={Path.GetFullPath(databasePath)}";

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlite(databaseConnection));

builder.Services
    .AddIdentityCore<IdentityUser>(options =>
    {
        options.Password.RequiredLength = 16;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.User.RequireUniqueEmail = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services
    .AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies();

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "TrustTunnelManager.AntiForgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "TrustTunnelManager.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.LoginPath = "/login";
    options.AccessDeniedPath = "/login";
    options.SlidingExpiration = true;
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    var knownProxies = builder.Configuration
        .GetSection("ForwardedHeaders:KnownProxies")
        .Get<string[]>() ?? [];

    foreach (var proxy in knownProxies)
    {
        if (IPAddress.TryParse(proxy, out var address))
        {
            options.KnownProxies.Add(address);
        }
    }
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<TrustTunnelManagerOptions>(
    builder.Configuration.GetSection(TrustTunnelManagerOptions.SectionName));

builder.Services.AddSingleton<TrustTunnelPathResolver>();
builder.Services.AddSingleton<ProcessCommandRunner>();
builder.Services.AddScoped<ConfigurationChangeCoordinator>();

builder.Services.AddScoped<ITrustTunnelClientManagementService, TrustTunnelClientManagementService>();
builder.Services.AddScoped<ITrustTunnelRulesManagementService, TrustTunnelRulesManagementService>();
builder.Services.AddScoped<ITrustTunnelServiceControl, LinuxSystemdServiceControl>();
builder.Services.AddScoped<ITrustTunnelClientConfigurationExporter, TrustTunnelClientConfigurationExporter>();
builder.Services.AddScoped<IAuditLog, DatabaseAuditLog>();

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

builder.Services.AddHostedService<DatabaseInitializationService>();

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/login", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    var token = HtmlEncoder.Default.Encode(tokens.RequestToken ?? string.Empty);
    var error = context.Request.Query["error"].ToString();
    var encodedError = string.IsNullOrWhiteSpace(error)
        ? string.Empty
        : $"<div class=\"login-error\">{HtmlEncoder.Default.Encode(error)}</div>";

    var html = $$"""
        <!DOCTYPE html>
        <html lang="ru">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Вход — TrustTunnel Manager</title>
            <link rel="stylesheet" href="/app.css">
            <link rel="stylesheet" href="/login.css">
        </head>
        <body class="login-body">
            <main class="login-card">
                <div class="login-mark">TT</div>
                <div class="login-eyebrow">TRUSTTUNNEL MANAGER</div>
                <h1>Вход в панель</h1>
                <p>Введите учётные данные администратора.</p>
                {{encodedError}}
                <form method="post" action="/account/login">
                    <input type="hidden" name="__RequestVerificationToken" value="{{token}}">
                    <label for="username">Имя пользователя</label>
                    <input id="username" name="username" autocomplete="username" required maxlength="256" autofocus>
                    <label for="password">Пароль</label>
                    <input id="password" name="password" type="password" autocomplete="current-password" required>
                    <label class="remember-row"><input name="rememberMe" type="checkbox" value="true"> Запомнить меня</label>
                    <button type="submit">Войти</button>
                </form>
                <div class="login-footer">Доступ разрешён только администраторам этого узла.</div>
            </main>
        </body>
        </html>
        """;

    return Results.Content(html, "text/html; charset=utf-8");
}).AllowAnonymous();

app.MapPost("/account/login", async (
    HttpContext context,
    IAntiforgery antiforgery,
    UserManager<IdentityUser> userManager,
    SignInManager<IdentityUser> signInManager) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest("Проверка безопасности формы не пройдена. Обновите страницу входа.");
    }

    var form = await context.Request.ReadFormAsync();
    var username = form["username"].ToString().Trim();
    var password = form["password"].ToString();
    var rememberMe = string.Equals(form["rememberMe"], "true", StringComparison.OrdinalIgnoreCase);

    var user = await userManager.FindByNameAsync(username);

    if (user is null)
    {
        return Results.Redirect("/login?error=Неверное%20имя%20пользователя%20или%20пароль.");
    }

    var result = await signInManager.PasswordSignInAsync(
        user,
        password,
        rememberMe,
        lockoutOnFailure: true);

    return result.Succeeded
        ? Results.Redirect("/")
        : Results.Redirect("/login?error=Неверное%20имя%20пользователя%20или%20пароль.");
}).AllowAnonymous();

app.MapGet("/account/antiforgery-token", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Ok(new { requestToken = tokens.RequestToken });
}).RequireAuthorization();

app.MapPost("/account/logout", async (
    HttpContext context,
    IAntiforgery antiforgery,
    SignInManager<IdentityUser> signInManager) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest("Проверка безопасности формы не пройдена.");
    }

    await signInManager.SignOutAsync();
    return Results.Redirect("/login");
}).AllowAnonymous();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

app.Run();
