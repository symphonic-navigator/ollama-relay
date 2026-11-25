using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OllamaRelay.Relay.Data;

namespace OllamaRelay.Relay.Security;

public sealed class JwtIssuer(RelayOptions options)
{
    public const string Issuer = "ollama-relay";
    public const string Audience = "ollama-relay-mgmt";
    public const string ClaimTokenVersion = "tv";
    public const string ClaimMustChangePassword = "pwc";
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(15);

    public SymmetricSecurityKey Key { get; } = new(Encoding.UTF8.GetBytes(options.JwtSigningKey));

    public string Issue(UserDoc user)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Name, user.Username),
            new(ClaimTypes.Role, user.IsAdmin ? "admin" : "user"),
            new(ClaimTokenVersion, user.TokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (user.MustChangePassword)
        {
            claims.Add(new Claim(ClaimMustChangePassword, "1"));
        }

        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.Add(AccessLifetime),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        });
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = Key,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = JwtRegisteredClaimNames.Name,
        RoleClaimType = ClaimTypes.Role,
    };
}
