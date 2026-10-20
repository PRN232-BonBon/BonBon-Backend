using System.Security.Claims;
using BonBon.Business.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BonBon.API.Hubs;

[Authorize]
public sealed class TrackingHub(ITrackingService trackingService) : Hub
{
    private readonly ITrackingService _trackingService = trackingService;

    public async Task JoinBooking(Guid bookingId)
    {
        var claim = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(claim, out var accountId)
            || !await _trackingService.CanAccessBookingAsync(accountId, bookingId, Context.ConnectionAborted))
            throw new HubException("Bạn không có quyền xem hành trình booking này.");
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(bookingId), Context.ConnectionAborted);
    }

    public Task LeaveBooking(Guid bookingId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(bookingId), Context.ConnectionAborted);

    public static string GroupName(Guid bookingId) => $"booking:{bookingId:N}";
}
