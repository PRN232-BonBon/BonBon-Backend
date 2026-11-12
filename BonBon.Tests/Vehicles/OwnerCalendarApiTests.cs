using System.Security.Claims;
using BonBon.API.Controllers;
using BonBon.API.Services;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BonBon.Tests.Vehicles;

public class OwnerCalendarApiTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("2026-11-05T00:00:00Z", "2026-11-04T00:00:00Z")]
    [InlineData("2026-11-05T00:00:00Z", "2026-11-05T00:00:00Z")]
    [InlineData("2026-11-05T00:00:00Z", "2027-01-01T00:00:00Z")]
    public async Task Invalid_ranges_never_reach_database(string? start, string? end)
    {
        var repository = new Mock<IVehicleRepository>(MockBehavior.Strict);
        var controller = Create(repository.Object, Guid.NewGuid());
        Assert.IsType<BadRequestObjectResult>(await controller.OwnerBlocks(start is null ? null : DateTimeOffset.Parse(start), end is null ? null : DateTimeOffset.Parse(end), default));
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Uses_authenticated_identity_and_rejects_missing_identity()
    {
        var owner = Guid.NewGuid(); var start = DateTimeOffset.UtcNow; var end = start.AddDays(14);
        var repository = new Mock<IVehicleRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetOwnerBlocksAsync(owner, start, end, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<OwnerAvailabilityBlock>());
        Assert.IsType<OkObjectResult>(await Create(repository.Object, owner).OwnerBlocks(start, end, default));
        Assert.IsType<UnauthorizedResult>(await Create(repository.Object, null).OwnerBlocks(start, end, default));
        Assert.NotEmpty(typeof(VehiclesController).GetMethod(nameof(VehiclesController.OwnerBlocks))!.GetCustomAttributes(typeof(AuthorizeAttribute), true));
        repository.VerifyAll();
    }

    private static VehiclesController Create(IVehicleRepository repository, Guid? account)
    {
        var controller = new VehiclesController(new Mock<IVehicleService>().Object, repository, new VehicleImageProcessor());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(account.HasValue ? [new Claim(ClaimTypes.NameIdentifier, account.Value.ToString())] : [], "Test")) } };
        return controller;
    }
}
