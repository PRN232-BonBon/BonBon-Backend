using System.ComponentModel.DataAnnotations;

namespace BonBon.Business.DTOs.Auth;

/// <summary>Request body cho POST /auth/refresh.</summary>
public sealed class RefreshTokenRequest
{
    [Required]
    public string RefreshToken { get; init; } = default!;
}
