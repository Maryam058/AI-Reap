namespace AiReap.Api.Auth;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = "AiReap";
    public string Audience { get; set; } = "AiReapClient";
    public int ExpiryMinutes { get; set; } = 120;
}
