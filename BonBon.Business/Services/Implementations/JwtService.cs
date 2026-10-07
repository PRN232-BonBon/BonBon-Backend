using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BonBon.Business.Services.Interfaces;
using BonBon.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace BonBon.Business.Services.Implementations;

public class JwtService(IConfiguration config) : IJwtService
{
    private readonly IConfiguration _config = config;

    public (string Token, long ExpiresAt) GenerateAccessToken(Account account, IEnumerable<string> roles)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_config["Jwt:Key"]
                ?? throw new InvalidOperationException("Jwt:Key is not configured.")));

        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub,   account.AccountId.ToString()),
            new(JwtRegisteredClaimNames.Email, account.Email),
            new(JwtRegisteredClaimNames.Name,  account.FullName),
            new(JwtRegisteredClaimNames.Jti,   Guid.NewGuid().ToString()),
        };

        // Thêm từng role thành một claim riêng để ClaimsPrincipal.IsInRole() hoạt động
        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var expiryMinutes = int.Parse(_config["Jwt:ExpiryMinutes"] ?? "60");
        var expires = DateTime.UtcNow.AddMinutes(expiryMinutes);

        var token = new JwtSecurityToken(
            issuer:             _config["Jwt:Issuer"],
            audience:           _config["Jwt:Audience"],
            claims:             claims,
            expires:            expires,
            signingCredentials: credentials);

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);
        var expiresAt    = new DateTimeOffset(expires).ToUnixTimeSeconds();

        return (tokenString, expiresAt);
    }

    public (string RawToken, string TokenHash) GenerateRefreshToken()
    {
        // 64 bytes ngẫu nhiên → base64 → SHA-256 hash để lưu DB
        var randomBytes = RandomNumberGenerator.GetBytes(64);
        var rawToken    = Convert.ToBase64String(randomBytes);
        var tokenHash   = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

        return (rawToken, tokenHash);
    }
}
