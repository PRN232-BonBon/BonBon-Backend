using System;
using System.Collections.Generic;

namespace BonBon.Entities.Models;

public partial class additional_charge
{
    public Guid additional_charge_id { get; set; }

    public Guid booking_id { get; set; }

    public string reason_code { get; set; }

    public string description { get; set; }

    public decimal amount { get; set; }

    public string currency { get; set; }

    public string evidence_url { get; set; }

    public string status { get; set; }

    public DateTime proposed_at { get; set; }

    public virtual booking booking { get; set; }
}
