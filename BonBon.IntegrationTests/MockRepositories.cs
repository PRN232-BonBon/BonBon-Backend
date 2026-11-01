using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BonBon.DataAccess.Repositories.Interfaces;

namespace BonBon.IntegrationTests;

public class MockBookingRepository : IBookingRepository
{
    public readonly Guid VehicleId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    public readonly Guid OwnerId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    private readonly List<BookingDetailRecord> _bookings = new();
    private readonly List<InspectionRecord> _inspections = new();
    private readonly List<AdditionalChargeRecord> _charges = new();

    public Task<VehiclePricing?> GetVehiclePricingAsync(Guid vehicleId, CancellationToken ct = default)
    {
        if (vehicleId != VehicleId) return Task.FromResult<VehiclePricing?>(null);

        return Task.FromResult<VehiclePricing?>(new VehiclePricing(
            VehicleId: VehicleId,
            OwnerAccountId: OwnerId,
            IsOwnerActive: true,
            HourPrice: 100000m,
            DayPrice: 800000m,
            Status: "Available",
            WeeklyDiscountEnabled: true,
            WeeklyDiscountPercent: 10m,
            MileageLimitEnabled: true,
            DailyMileageLimitKm: 400,
            OverageFeePerKm: 3000m,
            RentalTerms: "Không hút thuốc, trả xe đúng giờ"
        ));
    }

    public Task<bool> IsVehicleAvailableAsync(Guid vehicleId, DateTimeOffset startAt, DateTimeOffset endAt, CancellationToken ct = default)
    {
        var hasOverlap = _bookings.Any(b =>
            b.VehicleId == vehicleId &&
            (b.Status == "AwaitingPayment" || b.Status == "Confirmed" || b.Status == "ReadyForPickup" || b.Status == "InProgress") &&
            startAt < b.EndAt && endAt > b.StartAt);

        return Task.FromResult(!hasOverlap);
    }

    public Task<bool> HasDuplicatePendingBookingAsync(Guid customerAccountId, Guid vehicleId, DateTimeOffset startAt, DateTimeOffset endAt, CancellationToken ct = default)
    {
        var exists = _bookings.Any(b =>
            b.CustomerAccountId == customerAccountId &&
            b.VehicleId == vehicleId &&
            (b.Status == "PendingApproval" || b.Status == "AwaitingPayment") &&
            b.StartAt == startAt &&
            b.EndAt == endAt);

        return Task.FromResult(exists);
    }

    public Task<CreatedBooking> CreateAsync(Guid customerAccountId, Guid vehicleId, string rentalType, DateTimeOffset startAt, DateTimeOffset endAt, CancellationToken ct = default)
    {
        var bookingId = Guid.NewGuid();
        var record = new BookingDetailRecord(
            BookingId: bookingId,
            Status: "PendingApproval",
            RentalType: rentalType,
            StartAt: startAt,
            EndAt: endAt,
            BillableUnits: 1,
            UnitPrice: 800000m,
            WeeklyDiscountPercent: 0m,
            DiscountAmount: 0m,
            QuotedRentalAmount: 800000m,
            IncludedMileageKm: 400,
            OverageFeePerKm: 3000m,
            RentalTerms: "Không hút thuốc, trả xe đúng giờ",
            Currency: "VND",
            CreatedAt: DateTimeOffset.UtcNow,
            VehicleId: vehicleId,
            Brand: "Hyundai",
            Model: "Accent",
            PlateNumber: "51K-12345",
            CoverImageUrl: "/api/vehicles/images/img-123",
            PickupAddress: "TP Thủ Đức, TP Hồ Chí Minh",
            Seats: 5,
            Transmission: "Automatic",
            FuelType: "Gasoline",
            CustomerAccountId: customerAccountId,
            CustomerName: "Test Customer",
            CustomerEmail: "testcustomer_prn@bonbon.com",
            CustomerPhone: "0901234567",
            OwnerAccountId: OwnerId,
            OwnerName: "Demo Owner",
            OwnerEmail: "demo-owner@bonbon.local",
            OwnerPhone: "0912345678",
            Inspections: new List<InspectionRecord>(),
            AdditionalCharges: new List<AdditionalChargeRecord>()
        );

        _bookings.Add(record);

        return Task.FromResult(new CreatedBooking(
            BookingId: bookingId,
            VehicleId: vehicleId,
            BillableUnits: 1,
            UnitPrice: 800000m,
            WeeklyDiscountPercent: 0m,
            DiscountAmount: 0m,
            QuotedRentalAmount: 800000m,
            IncludedMileageKm: 400,
            OverageFeePerKm: 3000m,
            RentalTerms: "Không hút thuốc",
            Currency: "VND",
            Status: "PendingApproval"
        ));
    }

    public Task<IReadOnlyList<CustomerBooking>> GetByCustomerAsync(Guid customerAccountId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CustomerBooking>>(_bookings.Where(b => b.CustomerAccountId == customerAccountId)
            .Select(b => new CustomerBooking(b.BookingId, b.Brand, b.Model, b.PlateNumber, b.CoverImageUrl, b.RentalType, b.StartAt, b.EndAt, b.QuotedRentalAmount, b.Currency, b.Status, b.CreatedAt))
            .ToList());

    public Task<BookingDetailRecord?> GetByIdAsync(Guid bookingId, CancellationToken ct = default)
    {
        var b = _bookings.FirstOrDefault(x => x.BookingId == bookingId);
        if (b == null) return Task.FromResult<BookingDetailRecord?>(null);

        var ins = _inspections.Where(i => i.BookingId == bookingId).ToList();
        var chg = _charges.Where(c => c.BookingId == bookingId).ToList();

        var updated = b with { Inspections = ins, AdditionalCharges = chg };
        return Task.FromResult<BookingDetailRecord?>(updated);
    }

    public Task<IReadOnlyList<OwnerBookingRecord>> GetByOwnerAsync(Guid ownerAccountId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OwnerBookingRecord>>(Array.Empty<OwnerBookingRecord>());

    public Task<bool> CancelAsync(Guid bookingId, CancellationToken ct = default) =>
        UpdateStatusAsync(bookingId, "Cancelled", new[] { "PendingApproval", "AwaitingPayment" }, ct);

    public Task<bool> ApproveAsync(Guid bookingId, CancellationToken ct = default) =>
        UpdateStatusAsync(bookingId, "AwaitingPayment", new[] { "PendingApproval" }, ct);

    public Task<bool> RejectAsync(Guid bookingId, CancellationToken ct = default) =>
        UpdateStatusAsync(bookingId, "Rejected", new[] { "PendingApproval" }, ct);

    public Task<bool> ConfirmPaymentAsync(Guid bookingId, CancellationToken ct = default) =>
        UpdateStatusAsync(bookingId, "Confirmed", new[] { "AwaitingPayment" }, ct);

    public Task<InspectionRecord?> GetInspectionAsync(Guid bookingId, string kind, CancellationToken ct = default) =>
        Task.FromResult(_inspections.FirstOrDefault(i => i.BookingId == bookingId && i.Kind == kind));

    public Task<IReadOnlyList<InspectionRecord>> GetInspectionsByBookingAsync(Guid bookingId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<InspectionRecord>>(_inspections.Where(i => i.BookingId == bookingId).ToList());

    public Task<IReadOnlyList<AdditionalChargeRecord>> GetChargesByBookingAsync(Guid bookingId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AdditionalChargeRecord>>(_charges.Where(c => c.BookingId == bookingId).ToList());

    public Task<InspectionRecord> CreateInspectionAsync(Guid bookingId, Guid vehicleId, string kind, decimal? odometerKm, decimal? fuelLevel, string? notes, DateTimeOffset? inspectedAt, CancellationToken ct = default)
    {
        var rec = new InspectionRecord(Guid.NewGuid(), bookingId, vehicleId, kind, odometerKm, fuelLevel, notes, inspectedAt ?? DateTimeOffset.UtcNow);
        _inspections.Add(rec);
        return Task.FromResult(rec);
    }

    public Task<AdditionalChargeRecord?> SaveExcessMileageChargeAsync(Guid bookingId, decimal excessKm, decimal feePerKm, decimal amount, CancellationToken ct = default)
    {
        var existing = _charges.FirstOrDefault(c => c.BookingId == bookingId && c.ReasonCode == "EXCESS_MILEAGE");
        if (existing != null)
        {
            var updated = existing with { Amount = amount, Description = $"Phí vượt quãng đường {excessKm} km ({feePerKm:N0} đ/km)" };
            _charges.Remove(existing);
            _charges.Add(updated);
            return Task.FromResult<AdditionalChargeRecord?>(updated);
        }

        var newRec = new AdditionalChargeRecord(
            Guid.NewGuid(),
            bookingId,
            "EXCESS_MILEAGE",
            $"Phí vượt quãng đường {excessKm} km ({feePerKm:N0} đ/km)",
            amount,
            "VND",
            "Pending",
            DateTimeOffset.UtcNow
        );
        _charges.Add(newRec);
        return Task.FromResult<AdditionalChargeRecord?>(newRec);
    }

    public Task<bool> UpdateStatusAsync(Guid bookingId, string newStatus, string[] allowedCurrentStatuses, CancellationToken ct = default)
    {
        var idx = _bookings.FindIndex(b => b.BookingId == bookingId);
        if (idx == -1) return Task.FromResult(false);

        var current = _bookings[idx];
        if (!allowedCurrentStatuses.Contains(current.Status)) return Task.FromResult(false);

        _bookings[idx] = current with { Status = newStatus };
        return Task.FromResult(true);
    }
}

public class MockVehicleRepository : IVehicleRepository
{
    private readonly MockBookingRepository _bookingRepo;

    public MockVehicleRepository(MockBookingRepository bookingRepo)
    {
        _bookingRepo = bookingRepo;
    }

    public Task<RegisteredVehicle> RegisterAsync(Guid ownerAccountId, VehicleRegistrationSubmission submission, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<OwnerVehicle>> GetMineAsync(Guid ownerAccountId, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<(byte[] Data, string ContentType)?> GetImageAsync(Guid imageId, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public async Task<IReadOnlyList<VehicleCatalogRecord>> SearchAsync(VehicleSearchFilter filter, CancellationToken ct = default)
    {
        var vehicles = new List<VehicleCatalogRecord>
        {
            new VehicleCatalogRecord(
                Id: _bookingRepo.VehicleId,
                Brand: "Hyundai",
                Model: "Accent",
                Year: 2022,
                PlateNumber: "51K-12345",
                BodyType: "Sedan",
                Seats: 5,
                Transmission: "Automatic",
                Fuel: "Gasoline",
                PickupAddress: "TP Thủ Đức, TP Hồ Chí Minh",
                Description: "Xe gia đình sạch sẽ, tiết kiệm xăng",
                Features: new[] { "GPS", "Bluetooth", "Camera lùi" },
                HourPrice: 100000m,
                DayPrice: 800000m,
                Status: "Available",
                OwnerId: _bookingRepo.OwnerId,
                OwnerName: "Demo Owner",
                OwnerEmail: "demo-owner@bonbon.local",
                OwnerPhone: "0912345678",
                ImageUrls: new[] { "/api/vehicles/images/img-123" },
                WeeklyDiscountEnabled: true,
                WeeklyDiscountPercent: 10m,
                MileageLimitEnabled: true,
                DailyMileageLimitKm: 400,
                OverageFeePerKm: 3000m,
                RentalTerms: "Không hút thuốc, trả xe đúng giờ"
            )
        };

        var query = vehicles.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(filter.Location))
        {
            query = query.Where(v => v.PickupAddress.Contains(filter.Location, StringComparison.OrdinalIgnoreCase));
        }

        if (filter.StartAt.HasValue && filter.EndAt.HasValue)
        {
            var availableVehicles = new List<VehicleCatalogRecord>();
            foreach (var v in query)
            {
                var isAvailable = await _bookingRepo.IsVehicleAvailableAsync(v.Id, filter.StartAt.Value, filter.EndAt.Value, ct);
                if (isAvailable)
                {
                    availableVehicles.Add(v);
                }
            }
            return availableVehicles;
        }

        return query.ToList();
    }
}
