namespace BonBon.DataAccess.Repositories.Interfaces;

public interface ISePayPaymentRepository
{
    IPaymentRepository Payments { get; }
    Task RecordRejectedAsync(string reason, string fingerprint, DateTimeOffset now, CancellationToken ct);
    Task<bool> IsActiveAdminAsync(Guid account, CancellationToken ct);
}
