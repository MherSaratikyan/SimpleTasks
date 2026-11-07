using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SimpleTasks.Identity.Data;
using SimpleTasks.ServiceDefaults;

namespace SimpleTasks.Identity.Security;

/// <summary>
/// Signs short-lived service tokens. The key lives only in memory: a restart makes
/// the web app fetch a new token, and other services pick up the new key from JWKS.
/// </summary>
public sealed class TokenIssuer
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly JsonWebTokenHandler _handler = new();

    public RsaSecurityKey Key { get; } = new(RSA.Create(2048)) { KeyId = Guid.NewGuid().ToString("N") };

    public (string Token, DateTime ExpiresAt) Issue(User user)
    {
        var expiresAt = DateTime.UtcNow.Add(Lifetime);
        var claims = new List<Claim>
        {
            new("sub", user.Id.ToString()),
            new("email", user.Email),
            new("name", user.DisplayName)
        };
        if (user.IsSiteAdmin)
        {
            claims.Add(new Claim(Jwt.SiteAdminClaim, "true"));
        }

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Jwt.Issuer,
            Audience = Jwt.Audience,
            Subject = new ClaimsIdentity(claims),
            Expires = expiresAt,
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256)
        });
        return (token, expiresAt);
    }

    public object Jwks()
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(Key);
        return new { keys = new[] { new { kty = jwk.Kty, use = "sig", kid = Key.KeyId, alg = "RS256", n = jwk.N, e = jwk.E } } };
    }
}
