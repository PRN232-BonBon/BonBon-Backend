using System;
using System.Collections.Generic;

namespace BonBon.Entities.Models;

/// <summary>
/// Fixed roles: 1 Admin, 2 Customer, 3 VehicleOwner.
/// </summary>
public partial class role
{
    public short role_id { get; set; }

    public string code { get; set; }

    public virtual ICollection<account_role> account_roles { get; set; } = new List<account_role>();
}
