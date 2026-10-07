using System;
using System.Collections.Generic;

namespace BonBon.Entities.Models;

public partial class payment
{
    public Guid payment_id { get; set; }

    public Guid booking_id { get; set; }

    public string provider_reference { get; set; }

    public string provider { get; set; }

    public decimal amount { get; set; }

    public string currency { get; set; }

    public string status { get; set; }

    public DateTime? paid_at { get; set; }

    public DateTime created_at { get; set; }

    public virtual booking booking { get; set; }
}
