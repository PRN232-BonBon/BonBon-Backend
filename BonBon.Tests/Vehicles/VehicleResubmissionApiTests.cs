using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BonBon.API.Controllers;
using BonBon.API.Infrastructure;
using BonBon.Business.Services.Implementations;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BonBon.Tests.Vehicles;

public class VehicleResubmissionApiTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Vehicle = Guid.NewGuid();
    private static string Metadata(object? features) => JsonSerializer.Serialize(new {
        plateNumber="TEST-001", brand="Toyota", model="Vios", productionYear=2022, seats=5,
        transmission="Automatic", fuelType="Gasoline", bodyType="Sedan", description="Xe sạch sẽ và được bảo dưỡng thường xuyên.",
        pickupAddress="123 Nguyễn Huệ, Quận 1", hourPrice=100000, dayPrice=700000,
        weeklyDiscountEnabled=false, mileageLimitEnabled=true, dailyMileageLimitKm=400, overageFeePerKm=3000, features
    });

    private static TestServer Host(IVehicleRepository repo) => new(new WebHostBuilder().ConfigureServices(services => {
        services.AddSingleton(repo); services.AddSingleton(new VehicleImageProcessor());
        services.AddSingleton<IBookingService>(new Mock<IBookingService>().Object);
        services.AddSingleton<IVehicleService, VehicleService>();
        services.AddControllers().AddApplicationPart(typeof(VehiclesController).Assembly);
        services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => {});
        services.AddAuthorization();
    }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));

    [Fact]
    public async Task Existing_images_can_be_kept_using_multipart_with_no_files()
    {
        var repo = new Mock<IVehicleRepository>(MockBehavior.Strict);
        repo.Setup(x => x.ResubmitAsync(Owner, Vehicle, It.Is<VehicleRegistrationSubmission>(s => s.VehicleImages.Count == 0), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegisteredVehicle(Vehicle, "PendingApproval", 2));
        using var host = Host(repo.Object); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Test-Account", Owner.ToString());
        using var form = new MultipartFormDataContent(); form.Add(new StringContent(Metadata(Array.Empty<string>())), "metadata");
        var response = await client.PostAsync($"/api/vehicles/registrations/{Vehicle}/resubmit", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("imageCount").GetInt32());
        repo.VerifyAll();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Null_or_empty_feature_is_rejected_before_writing(string? value)
    {
        var repo = new Mock<IVehicleRepository>(MockBehavior.Strict);
        using var host = Host(repo.Object); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Test-Account", Owner.ToString());
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Metadata(value is null ? null : new[] { value })), "metadata");
        var response = await client.PostAsync($"/api/vehicles/registrations/{Vehicle}/resubmit", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_FEATURES", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        repo.VerifyNoOtherCalls();
    }

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Test-Account", out var account)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, account.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
