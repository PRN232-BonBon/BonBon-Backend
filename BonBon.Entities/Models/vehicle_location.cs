using System;
using System.Collections.Generic;

namespace BonBon.Entities.Models;

public partial class vehicle_location
{
    public Guid vehicle_location_id { get; set; }

    public Guid booking_id { get; set; }

    public decimal latitude { get; set; }

    public decimal longitude { get; set; }

    public decimal? accuracy_meters { get; set; }

    public string source { get; set; }

    public DateTime recorded_at { get; set; }

    public virtual booking booking { get; set; }
}
