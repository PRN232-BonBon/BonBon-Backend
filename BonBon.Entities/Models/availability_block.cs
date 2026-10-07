using System;
using System.Collections.Generic;

namespace BonBon.Entities.Models;

public partial class availability_block
{
    public Guid availability_block_id { get; set; }

    public Guid vehicle_id { get; set; }

    public DateTime start_at { get; set; }

    public DateTime end_at { get; set; }

    public string reason { get; set; }

    public DateTime created_at { get; set; }

    public virtual vehicle vehicle { get; set; }
}
