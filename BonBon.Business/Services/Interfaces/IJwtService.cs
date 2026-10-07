using BonBon.Entities;

namespace BonBon.Business.Services.Interfaces;

public interface IJwtService
{
    /// <summary>Tạo JWT access token cho account.</summary>
    /// <returns>(token string, expiry unix timestamp)</returns>
    (string Token, long ExpiresAt) GenerateAccessToken(Account account, IEnumerable<string> roles);

    /// <summary>
    /// Tạo cặp (raw refresh token, hash để lưu DB).
    /// Client nhận raw token; DB lưu hash.
    /// </summary>
    (string RawToken, string TokenHash) GenerateRefreshToken();
}
