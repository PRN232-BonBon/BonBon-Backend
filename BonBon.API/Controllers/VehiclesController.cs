using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BonBon.API.Services;
using BonBon.Business.DTOs.Vehicles;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BonBon.API.Controllers;

[ApiController]
[Route("api/vehicles")]
public sealed class VehiclesController(
    IVehicleService vehicleService,
    IVehicleRepository vehicleRepository,
    VehicleImageProcessor imageProcessor) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IVehicleService _vehicleService = vehicleService;
    private readonly IVehicleRepository _vehicleRepository = vehicleRepository;
    private readonly VehicleImageProcessor _imageProcessor = imageProcessor;

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Catalog([FromServices] IAdminRepository catalog, CancellationToken ct)
    {
        var vehicles = await catalog.GetVehiclesAsync(true, ct);
        return Ok(vehicles.Select(v => new
        {
            v.Id, v.Brand, v.Model, v.Year, v.BodyType, v.Seats, v.Transmission, v.Fuel,
            v.PickupAddress, v.Description, v.Features, v.DayPrice, v.HourPrice, v.ImageUrls,
            ImageUrl = v.ImageUrls.FirstOrDefault(),
            v.WeeklyDiscountEnabled, v.WeeklyDiscountPercent, v.MileageLimitEnabled,
            v.DailyMileageLimitKm, v.OverageFeePerKm, v.RentalTerms,
            Rating = 0, CompletedTrips = 0, DepositFree = false, DeliveryAvailable = false, InstantBook = false,
        }));
    }

    [HttpPost("registrations")]
    [Authorize]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(90 * 1024 * 1024)]
    [ProducesResponseType(typeof(VehicleRegistrationResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> Register(CancellationToken ct)
    {
        if (!TryGetAccountId(out var accountId)) return Unauthorized();
        if (!Request.HasFormContentType)
            return ProblemResult("INVALID_METADATA", "Body phải là multipart/form-data.");

        var form = await Request.ReadFormAsync(ct);

        // metadata có thể được gửi dưới dạng field thường hoặc dạng file part (Blob).
        string? metadata = null;
        if (form.TryGetValue("metadata", out var metadataValues))
            metadata = metadataValues.ToString();
        else if (form.Files.Any(file => file.Name == "metadata"))
        {
            var metadataFile = form.Files.First(file => file.Name == "metadata");
            await using var metadataStream = metadataFile.OpenReadStream();
            using var buffer = new MemoryStream();
            await metadataStream.CopyToAsync(buffer, ct);
            metadata = Encoding.UTF8.GetString(buffer.ToArray());
        }

        if (string.IsNullOrWhiteSpace(metadata))
            return ProblemResult(
                "INVALID_METADATA",
                "Thiếu trường 'metadata' trong form. Gửi JSON thông tin đăng ký xe (tên field: metadata, "
                + "và ảnh dùng tên field: vehicleImages).");

        RegisterVehicleRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<RegisterVehicleRequest>(metadata, JsonOptions);
        }
        catch (JsonException)
        {
            return ProblemResult("INVALID_METADATA", "Thông tin đăng ký xe không đúng định dạng.");
        }
        if (request is null) return ProblemResult("INVALID_METADATA", "Thiếu thông tin đăng ký xe.");

        var vehicleImages = form.Files.GetFiles("vehicleImages");
        if (vehicleImages.Count is < 1 or > 8)
            return ProblemResult("INVALID_IMAGE_COUNT", "Vui lòng tải từ 1 đến 8 ảnh xe (field: vehicleImages).");

        try
        {
            var images = new List<StoredImage>(vehicleImages.Count);
            foreach (var image in vehicleImages)
                images.Add(await _imageProcessor.ProcessAsync(image, ct));

            var result = await _vehicleService.RegisterAsync(
                accountId,
                request,
                images,
                ct);
            if (!result.IsSuccess) return ProblemResult(result.ErrorCode!, result.ErrorMessage!);
            return Created($"/api/vehicles/{result.Data!.VehicleId}", result.Data);
        }
        catch (InvalidDataException exception)
        {
            return ProblemResult("INVALID_IMAGE", exception.Message);
        }
    }

    [HttpGet("mine")]
    [Authorize]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        if (!TryGetAccountId(out var accountId)) return Unauthorized();
        var result = await _vehicleService.GetMineAsync(accountId, ct);
        return Ok(result.Data);
    }

    [HttpGet("images/{imageId:guid}")]
    [AllowAnonymous]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Image(Guid imageId, CancellationToken ct)
    {
        var image = await _vehicleRepository.GetImageAsync(imageId, ct);
        return image is null ? NotFound() : File(image.Value.Data, image.Value.ContentType);
    }

    private bool TryGetAccountId(out Guid accountId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out accountId);

    private BadRequestObjectResult ProblemResult(string code, string detail) =>
        BadRequest(new ProblemDetails { Title = code, Detail = detail, Status = StatusCodes.Status400BadRequest });
}
