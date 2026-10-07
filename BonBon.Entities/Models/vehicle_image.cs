using System;
using System.Collections.Generic;

namespace BonBon.Entities.Models;

public partial class vehicle_image
{
    public Guid vehicle_image_id { get; set; }

    public Guid vehicle_id { get; set; }

    public string image_url { get; set; }

    public int sort_order { get; set; }

    public bool is_cover { get; set; }

    public DateTime created_at { get; set; }

    public virtual vehicle vehicle { get; set; }
}
