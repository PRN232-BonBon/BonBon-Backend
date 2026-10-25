using BonBon.Business.Common;
using BonBon.Business.DTOs.Vehicles;
using BonBon.DataAccess.Repositories.Interfaces;

namespace BonBon.Business.Services.Interfaces;

public interface IVehicleService
{
    Task<ServiceResult<VehicleRegistrationResponse>> RegisterAsync(
        Guid ownerAccountId,
        RegisterVehicleRequest request,
        IReadOnlyList<StoredImage> vehicleImages,
        StoredImage registrationDocument,
        CancellationToken ct = default);

    Task<ServiceResult<IReadOnlyList<OwnerVehicleResponse>>> GetMineAsync(Guid ownerAccountId, CancellationToken ct = default);
}
