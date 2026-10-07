using System;
using System.Collections.Generic;

namespace BonBon.Entities.Models;

/// <summary>
/// SenderAccountId and SenderRoleId reference membership and identify booking participant context.
/// </summary>
public partial class message
{
    public Guid message_id { get; set; }

    public Guid booking_id { get; set; }

    public Guid sender_account_id { get; set; }

    public short sender_role_id { get; set; }

    public string body { get; set; }

    public DateTime sent_at { get; set; }

    public DateTime? read_at { get; set; }

    public virtual account_role account_role { get; set; }

    public virtual booking booking { get; set; }
}
