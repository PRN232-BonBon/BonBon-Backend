namespace BonBon.DataAccess.Repositories.Interfaces;

public enum BookingDecision { Approve, Reject }

public enum BookingDecisionOutcome
{
    Applied, AlreadyApplied, NotFound, NotOwner, OwnerInactive, CustomerInactive,
    VehicleUnavailable, ScheduleConflict, StateConflict, ConcurrentChange, BookingWindowPassed,
}

public sealed record BookingDecisionSnapshot(
    Guid BookingId, string Status, decimal UnitPrice, decimal QuotedRentalAmount, string Currency);

public sealed record BookingDecisionResult(BookingDecisionOutcome Outcome, BookingDecisionSnapshot? Booking = null);
