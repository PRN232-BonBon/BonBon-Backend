using System;
using System.Collections.Generic;

namespace BonBon.Entities.Models;

public partial class vehicle_inspection
{
    public Guid vehicle_inspection_id { get; set; }

    public Guid booking_id { get; set; }

    public Guid vehicle_id { get; set; }

    public string kind { get; set; }

    /// <summary>
    /// Optional evidence only; no mileage-based billing in demo.
    /// </summary>
    public decimal? odometer_km { get; set; }

    /// <summary>
    /// Optional fuel percentage, 0..100.
    /// </summary>
    public decimal? fuel_level { get; set; }

    public string condition_notes { get; set; }

    public DateTime inspected_at { get; set; }

    public virtual booking booking { get; set; }

    public virtual vehicle vehicle { get; set; }
}
