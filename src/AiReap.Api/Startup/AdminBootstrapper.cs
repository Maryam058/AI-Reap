using AiReap.Domain.Common;
using AiReap.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace AiReap.Api.Startup;

// Creates the very first Administrator, since public sign-up can no longer grant any role.
//
// Safe by construction:
//  - does nothing if any Administrator already exists (so it can't be used later to add or reset one);
//  - needs Bootstrap:AdminEmail and Bootstrap:AdminPassword from configuration (user-secrets or
//    environment variables) — there is no built-in default credential;
//  - never logs the password and never touches an existing account with that email.
public static class AdminBootstrapper
{
    public const string SectionName = "Bootstrap";

    public static async Task EnsureAdministratorAsync(IServiceProvider services, IConfiguration configuration)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AdminBootstrapper");
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

        if ((await userManager.GetUsersInRoleAsync(Roles.Administrator)).Count > 0)
        {
            return;
        }

        var email = configuration[$"{SectionName}:AdminEmail"];
        var password = configuration[$"{SectionName}:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning(
                "No Administrator exists and no bootstrap credentials are configured. Set {Section}:AdminEmail and " +
                "{Section}:AdminPassword (user-secrets or environment variables) and restart to create the first administrator.",
                SectionName, SectionName);
            return;
        }

        if (await userManager.FindByEmailAsync(email) is not null)
        {
            logger.LogWarning(
                "Bootstrap administrator email {Email} already belongs to a non-administrator account; it was not modified. " +
                "Use a different {Section}:AdminEmail.", email, SectionName);
            return;
        }

        var admin = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = configuration[$"{SectionName}:AdminDisplayName"] is { Length: > 0 } name ? name : "Administrator"
        };

        var created = await userManager.CreateAsync(admin, password);
        if (!created.Succeeded)
        {
            // Fail loudly: a misconfigured bootstrap password should not silently leave the system with no admin.
            throw new InvalidOperationException(
                "Could not create the bootstrap administrator: " + string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        await userManager.AddToRoleAsync(admin, Roles.Administrator);
        logger.LogInformation("Created bootstrap administrator {Email}.", email);
    }
}
