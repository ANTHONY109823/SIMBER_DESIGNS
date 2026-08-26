using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SimberDesigns.Server.Models;
using SimberDesigns.Server.Options;

namespace SimberDesigns.Server.Services;

public interface IJwtTokenService
{
    string Create(User user, string? subscriptionTier);
}

public sealed class JwtTokenService(IOptions<JwtOptions> options) : IJwtTokenService
{
    public string Create(User user, string? subscriptionTier)
    {
        var jwt = options.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName),
            new(ClaimTypes.Role, user.Role),
            new("credits", user.CreditsBalance.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        if (!string.IsNullOrWhiteSpace(subscriptionTier))
        {
            claims.Add(new Claim("tier", subscriptionTier));
        }

        var token = new JwtSecurityToken(
            issuer: jwt.Issuer,
            audience: jwt.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(jwt.ExpiryHours),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
