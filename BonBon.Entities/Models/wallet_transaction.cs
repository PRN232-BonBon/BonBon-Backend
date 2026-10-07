using System;
using System.Collections.Generic;

namespace BonBon.Entities.Models;

public partial class wallet_transaction
{
    public Guid wallet_transaction_id { get; set; }

    public Guid wallet_id { get; set; }

    public Guid? booking_id { get; set; }

    public string idempotency_key { get; set; }

    public string type { get; set; }

    public string direction { get; set; }

    public decimal amount { get; set; }

    public string status { get; set; }

    public DateTime occurred_at { get; set; }

    public virtual booking booking { get; set; }

    public virtual wallet wallet { get; set; }
}
