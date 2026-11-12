namespace BonBon.Business.Common;

public sealed record MileagePrice(decimal ActualDistanceKm, decimal ExcessKm, decimal Fee);

public static class MileagePricing
{
    public static MileagePrice Calculate(decimal pickupKm, decimal returnKm, int? includedKm, decimal? feePerKm)
    {
        var distance = returnKm - pickupKm;
        var excess = includedKm is > 0 ? Math.Max(0, distance - includedKm.Value) : 0;
        var fee = decimal.Round(excess * (feePerKm ?? 0), 0, MidpointRounding.AwayFromZero);
        return new(distance, excess, fee);
    }
}
