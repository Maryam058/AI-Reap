using AiReap.Domain.Common;
using Microsoft.AspNetCore.Identity;

namespace AiReap.Api.Startup;

// §4 — the five fixed roles must exist before anyone can be assigned one.
public static class RoleSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();

        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }
    }
}
