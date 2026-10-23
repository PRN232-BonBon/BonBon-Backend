using BonBon.Business.Services.Implementations;
using BonBon.DataAccess.Repositories.Interfaces;
using Moq;
using Npgsql;

namespace BonBon.Tests.Bookings;

public class BookingDecisionServiceTests
{
    private readonly Mock<IBookingRepository> _repository = new(MockBehavior.Strict);
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _booking = Guid.NewGuid();

    [Theory]
    [InlineData(BookingDecision.Approve, BookingDecisionOutcome.Applied, "AwaitingPayment", false)]
    [InlineData(BookingDecision.Approve, BookingDecisionOutcome.AlreadyApplied, "AwaitingPayment", true)]
    [InlineData(BookingDecision.Reject, BookingDecisionOutcome.Applied, "Rejected", false)]
    [InlineData(BookingDecision.Reject, BookingDecisionOutcome.AlreadyApplied, "Rejected", true)]
    public async Task Maps_success_and_preserves_database_quote(
        BookingDecision decision, BookingDecisionOutcome outcome, string status, bool replay)
    {
        using var cancellation = new CancellationTokenSource();
        _repository.Setup(x => x.DecideAsync(_owner, _booking, decision, cancellation.Token))
            .ReturnsAsync(new BookingDecisionResult(outcome, new(_booking, status, 650000m, 1300000m, "VND")));
        var service = new BookingService(_repository.Object);
        var result = decision == BookingDecision.Approve
            ? await service.ApproveAsync(_owner, _booking, cancellation.Token)
            : await service.RejectAsync(_owner, _booking, cancellation.Token);
        Assert.True(result.IsSuccess);
        Assert.Equal(status, result.Data!.Status);
        Assert.Equal(650000m, result.Data.UnitPrice);
        Assert.Equal(1300000m, result.Data.QuotedRentalAmount);
        Assert.Equal(replay, result.Data.IsReplay);
        _repository.VerifyAll();
    }

    [Theory]
    [InlineData(BookingDecisionOutcome.NotFound, "BOOKING_NOT_FOUND")]
    [InlineData(BookingDecisionOutcome.NotOwner, "BOOKING_NOT_OWNER")]
    [InlineData(BookingDecisionOutcome.OwnerInactive, "OWNER_INACTIVE")]
    [InlineData(BookingDecisionOutcome.CustomerInactive, "CUSTOMER_INACTIVE")]
    [InlineData(BookingDecisionOutcome.VehicleUnavailable, "VEHICLE_UNAVAILABLE")]
    [InlineData(BookingDecisionOutcome.ScheduleConflict, "SCHEDULE_CONFLICT")]
    [InlineData(BookingDecisionOutcome.StateConflict, "BOOKING_STATE_CONFLICT")]
    [InlineData(BookingDecisionOutcome.ConcurrentChange, "BOOKING_DECISION_RETRY")]
    [InlineData(BookingDecisionOutcome.BookingWindowPassed, "BOOKING_WINDOW_PASSED")]
    public async Task Maps_transaction_outcomes(BookingDecisionOutcome outcome, string code)
    {
        _repository.Setup(x => x.DecideAsync(_owner, _booking, BookingDecision.Approve, default))
            .ReturnsAsync(new BookingDecisionResult(outcome));
        var result = await new BookingService(_repository.Object).ApproveAsync(_owner, _booking);
        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.ErrorCode);
    }

    [Theory]
    [InlineData("23P01", "SCHEDULE_CONFLICT")]
    [InlineData("40001", "BOOKING_DECISION_RETRY")]
    [InlineData("40P01", "BOOKING_DECISION_RETRY")]
    [InlineData("55P03", "BOOKING_DECISION_RETRY")]
    [InlineData("57014", "BOOKING_DECISION_RETRY")]
    [InlineData("23514", "BOOKING_DECISION_REJECTED")]
    public async Task Maps_database_conflicts_without_leaking_sql(string sqlState, string code)
    {
        _repository.Setup(x => x.DecideAsync(_owner, _booking, BookingDecision.Reject, default))
            .ThrowsAsync(new PostgresException("internal database detail", "ERROR", "ERROR", sqlState));
        var result = await new BookingService(_repository.Object).RejectAsync(_owner, _booking);
        Assert.Equal(code, result.ErrorCode);
        Assert.DoesNotContain("internal database detail", result.ErrorMessage!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invalid_ids_never_reach_database(bool missingOwner)
    {
        var result = await new BookingService(_repository.Object).ApproveAsync(
            missingOwner ? Guid.Empty : _owner, missingOwner ? _booking : Guid.Empty);
        Assert.Equal(missingOwner ? "OWNER_INACTIVE" : "BOOKING_ID_INVALID", result.ErrorCode);
        _repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Cancellation_is_not_converted_to_a_business_conflict()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        _repository.Setup(x => x.DecideAsync(_owner, _booking, BookingDecision.Approve, cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new BookingService(_repository.Object).ApproveAsync(_owner, _booking, cancellation.Token));
    }

    [Theory]
    [InlineData("Hourly", 4, 4)]
    [InlineData("Hourly", 8, 8)]
    [InlineData("Daily", 25, 2)]
    public void Existing_rental_window_validation_still_works(string rentalType, int hours, int units)
    {
        var start = new DateTimeOffset(2035, 1, 1, 9, 0, 0, TimeSpan.FromHours(7));
        var result = new BookingService(_repository.Object).ValidateWindow(new()
        {
            RentalType = rentalType, StartAt = start, EndAt = start.AddHours(hours),
        });
        Assert.True(result.IsSuccess);
        Assert.Equal(units, result.Data!.BillableUnits);
    }
}
