using Microsoft.AspNetCore.Identity;

namespace AiReap.Infrastructure.Identity;

public class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;
}
