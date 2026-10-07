namespace BonBon.Entities;

/// <summary>
/// Bảng tham chiếu cố định: 1=Admin, 2=Customer, 3=VehicleOwner.
/// </summary>
public class Role
{
    public short RoleId { get; set; }

    /// <summary>Admin | Customer | VehicleOwner</summary>
    public string Code { get; set; } = default!;

    // Navigation
    public ICollection<AccountRole> AccountRoles { get; set; } = [];
}
