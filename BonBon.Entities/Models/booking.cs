using System;
using System.Collections.Generic;

namespace BonBon.Entities.Models;

/// <summary>
/// Hourly/Daily rental with flexible return time; billable units define hours/days.
/// </summary>
public partial class booking
{
    public Guid booking_id { get; set; }

    public Guid customer_account_id { get; set; }

    /// <summary>
    /// Generated constant 2; composite FK enforces Customer membership.
    /// </summary>
    public short? customer_role_id { get; set; }

    public Guid vehicle_id { get; set; }

    public string rental_type { get; set; }

    /// <summary>
    /// Hourly: exact hours from 4 to 8. Daily: elapsed 24-hour periods rounded up.
    /// </summary>
    public int billable_units { get; set; }

    public DateTime start_at { get; set; }

    /// <summary>
    /// Booked return deadline, not the actual return time.
    /// </summary>
    public DateTime end_at { get; set; }

    public decimal? pickup_meeting_latitude { get; set; }

    public decimal? pickup_meeting_longitude { get; set; }

    public decimal? return_meeting_latitude { get; set; }

    public decimal? return_meeting_longitude { get; set; }

    /// <summary>
    /// Price selected at booking; independent of later Vehicle price edits.
    /// </summary>
    public decimal unit_price_snapshot { get; set; }

    /// <summary>
    /// Generated: Quantity * UnitPriceSnapshot. Omit from INSERT/UPDATE.
    /// </summary>
    public decimal? quoted_rental_amount { get; set; }

    public string currency { get; set; }

    public string status { get; set; }

    public DateTime created_at { get; set; }

    public virtual account_role account_role { get; set; }

    public virtual ICollection<additional_charge> additional_charges { get; set; } = new List<additional_charge>();

    public virtual ICollection<message> messages { get; set; } = new List<message>();

    public virtual ICollection<payment> payments { get; set; } = new List<payment>();

    public virtual vehicle vehicle { get; set; }

    public virtual ICollection<vehicle_inspection> vehicle_inspections { get; set; } = new List<vehicle_inspection>();

    public virtual ICollection<vehicle_location> vehicle_locations { get; set; } = new List<vehicle_location>();

    public virtual ICollection<wallet_transaction> wallet_transactions { get; set; } = new List<wallet_transaction>();
}
