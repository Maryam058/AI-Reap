namespace AiReap.Application.Common;

// Implemented in AiReap.Api (reads the authenticated ClaimsPrincipal) so Application
// services can know "who" without depending on ASP.NET Core HTTP types.
public interface ICurrentUser
{
    string UserId { get; }
    bool IsInRole(string role);
}
