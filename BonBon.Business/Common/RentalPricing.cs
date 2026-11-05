namespace BonBon.Business.Common;

public sealed record RentalPrice(decimal UnitPrice, decimal BaseAmount, decimal DiscountPercent, decimal DiscountAmount)
{
    public decimal TotalAmount => BaseAmount - DiscountAmount;
}

// Shared by catalog estimates and booking quotes. Payments use the saved booking snapshot.
public static class RentalPricing
{
    public static RentalPrice Calculate(string rentalType, int units, decimal hourPrice, decimal dayPrice,
        bool weeklyDiscountEnabled, decimal? weeklyDiscountPercent)
    {
        var unitPrice = rentalType == "Hourly" ? hourPrice : dayPrice;
        var baseAmount = unitPrice * units;
        var discountPercent = rentalType == "Daily" && units >= 7 && weeklyDiscountEnabled
            ? weeklyDiscountPercent ?? 0 : 0;
        var discount = decimal.Round(baseAmount * discountPercent / 100, 0, MidpointRounding.AwayFromZero);
        return new(unitPrice, baseAmount, discountPercent, discount);
    }
}
