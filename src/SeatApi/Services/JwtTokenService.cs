using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace SeatApi.Services;

public class JwtTokenService : IJwtTokenService
{
    private readonly string _secret;
    private readonly int _expiryHours;

    public JwtTokenService(IConfiguration configuration)
    {
        _secret = configuration["Jwt:Secret"] ?? throw new InvalidOperationException("Missing Jwt:Secret");
        
        if (!int.TryParse(configuration["Jwt:ExpiryHours"], out _expiryHours))
        {
            _expiryHours = 24; // Default to 24 if missing or invalid
        }
    }

    public (string Token, int ExpiresInSeconds) Create(string userId, string role)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim("role", role)
        };

        var expiry = DateTime.UtcNow.AddHours(_expiryHours);

        var token = new JwtSecurityToken(
            claims: claims,
            expires: expiry,
            signingCredentials: creds
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);
        
        return (tokenString, _expiryHours * 3600);
    }
}
