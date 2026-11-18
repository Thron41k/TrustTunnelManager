using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TrustTunnelManager.Web.Data;

namespace TrustTunnelManager.Web.Services;

/// <summary>
/// Creates the local database and provisions the first administrator only when
/// the database contains no users. There is intentionally no public registration.
/// </summary>
public sealed class DatabaseInitializationService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<DatabaseInitializationService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await database.Database.EnsureCreatedAsync(cancellationToken);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        if (await userManager.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        var username = configuration["TrustTunnelManager:BootstrapAdmin:Username"];
        var password = configuration["TrustTunnelManager:BootstrapAdmin:Password"];

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogCritical(
                "No TrustTunnelManager administrator exists. Configure TrustTunnelManager__BootstrapAdmin__Username and TrustTunnelManager__BootstrapAdmin__Password, start once, then remove these environment variables.");
            return;
        }

        var user = new IdentityUser
        {
            UserName = username.Trim(),
            Email = null,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, password);

        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(error => error.Description));
            throw new InvalidOperationException(
                $"Не удалось создать первого администратора TrustTunnelManager: {errors}");
        }

        logger.LogWarning(
            "The initial TrustTunnelManager administrator {AdminUser} was created. Remove the bootstrap password from the service environment after setup.",
            user.UserName);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
