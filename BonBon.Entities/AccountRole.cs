namespace BonBon.Entities;

/// <summary>
/// Junction M:N giữa Account và Role.
/// Một account có thể giữ nhiều role độc lập.
/// </summary>
public class AccountRole
{
    public Guid AccountId { get; set; }
    public short RoleId { get; set; }

    /// <summary>Pending | Active | Rejected | Suspended</summary>
    public string Status { get; set; } = "Pending";

    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public Account Account { get; set; } = default!;
    public Role Role { get; set; } = default!;
}
