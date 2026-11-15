namespace BonBon.Business.DTOs.Payments;

public sealed record CreatePaymentRequest(Guid BookingId, string Provider = "VNPAY_SANDBOX");
