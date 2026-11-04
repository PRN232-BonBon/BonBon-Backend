namespace BonBon.Business.Configuration;

public sealed class PaymentOptions
{
    public bool Enabled { get; set; }
    public int CheckoutMinutes { get; set; } = 15;
}
