using System;
using System.Collections.Generic;

namespace BonBon.Entities.Models;

/// <summary>
/// At most one wallet per Account and rental role; Admin excluded.
/// </summary>
public partial class wallet
{
    public Guid wallet_id { get; set; }

    public Guid account_id { get; set; }

    public short role_id { get; set; }

    public string currency { get; set; }

    public string status { get; set; }

    public DateTime created_at { get; set; }

    public virtual account_role account_role { get; set; }

    public virtual ICollection<wallet_transaction> wallet_transactions { get; set; } = new List<wallet_transaction>();
}
