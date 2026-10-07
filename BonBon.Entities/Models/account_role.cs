using System;
using System.Collections.Generic;

namespace BonBon.Entities.Models;

/// <summary>
/// Per-role membership lifecycle; one Account may hold multiple roles.
/// </summary>
public partial class account_role
{
    public Guid account_id { get; set; }

    public short role_id { get; set; }

    public string status { get; set; }

    public DateTime assigned_at { get; set; }

    public virtual account account { get; set; }

    public virtual ICollection<booking> bookings { get; set; } = new List<booking>();

    public virtual ICollection<message> messages { get; set; } = new List<message>();

    public virtual role role { get; set; }

    public virtual ICollection<vehicle> vehicles { get; set; } = new List<vehicle>();

    public virtual wallet wallet { get; set; }
}
