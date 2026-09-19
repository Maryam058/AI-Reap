using System.ComponentModel.DataAnnotations;

namespace AiReap.Api.Auth;

// Plain classes, not positional records: ASP.NET Core's model validator does not reliably
// bind DataAnnotations declared on record primary-constructor parameters.
public class RegisterRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required, MinLength(8)]
    public string Password { get; set; } = string.Empty;

    [Required]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    public string Role { get; set; } = string.Empty;
}

public class LoginRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

public record AuthResponse(string Token, string UserId, string Email, string DisplayName, IList<string> Roles);
