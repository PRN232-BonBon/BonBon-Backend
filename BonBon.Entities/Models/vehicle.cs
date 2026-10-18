using System;
using System.Collections.Generic;

namespace BonBon.Entities.Models;

public partial class vehicle
{
    public Guid vehicle_id { get; set; }

    public Guid owner_account_id { get; set; }

    /// <summary>
    /// Generated constant 3; composite FK enforces VehicleOwner membership.
    /// </summary>
    public short? owner_role_id { get; set; }

    public string plate_number { get; set; }

    public string brand { get; set; }

    public string model { get; set; }

    public short seats { get; set; }

    public string transmission { get; set; }

    public string pickup_address { get; set; }

    /// <summary>
    /// VND per hour for rentals from 4 to 8 hours.
    /// </summary>
    public decimal hour_price { get; set; }

    /// <summary>
    /// VND per billable 24-hour period.
    /// </summary>
    public decimal day_price { get; set; }

    public string status { get; set; }

    public virtual account_role account_role { get; set; }

    public virtual ICollection<availability_block> availability_blocks { get; set; } = new List<availability_block>();

    public virtual ICollection<booking> bookings { get; set; } = new List<booking>();

    public virtual vehicle_image vehicle_image { get; set; }

    public virtual ICollection<vehicle_inspection> vehicle_inspections { get; set; } = new List<vehicle_inspection>();
}
