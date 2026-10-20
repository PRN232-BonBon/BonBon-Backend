using System.Net.WebSockets;
using System.Text.Json;
using BonBon.API.Hubs;
using BonBon.Business.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace BonBon.API.Services;

public sealed class TraccarTrackingWorker(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    IHubContext<TrackingHub> hub,
    IConfiguration configuration,
    ILogger<TraccarTrackingWorker> logger) : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IHubContext<TrackingHub> _hub = hub;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<TraccarTrackingWorker> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var baseUrl = _configuration["Traccar:BaseUrl"]?.TrimEnd('/');
                var token = _configuration["Traccar:ApiToken"];
                if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                    continue;
                }
                await ReceivePositionsAsync(baseUrl, token, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Traccar live socket disconnected; reconnecting.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ReceivePositionsAsync(string baseUrl, string token, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("Traccar");
        using var sessionResponse = await client.GetAsync($"{baseUrl}/api/session?token={Uri.EscapeDataString(token)}", ct);
        sessionResponse.EnsureSuccessStatusCode();
        if (!sessionResponse.Headers.TryGetValues("Set-Cookie", out var cookies))
            throw new InvalidOperationException("Traccar session did not return an authentication cookie.");
        var cookie = string.Join("; ", cookies.Select(value => value.Split(';', 2)[0]));

        var socketUri = new UriBuilder(baseUrl) { Scheme = baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws", Path = "/api/socket" }.Uri;
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Cookie", cookie);
        await socket.ConnectAsync(socketUri, ct);

        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var received = await socket.ReceiveAsync(buffer, ct);
            if (received.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, received.Count);
            if (!received.EndOfMessage) continue;
            await ProcessMessageAsync(message.ToArray(), ct);
            message.SetLength(0);
        }
    }

    private async Task ProcessMessageAsync(byte[] payload, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("positions", out var positions) || positions.ValueKind != JsonValueKind.Array)
            return;

        foreach (var position in positions.EnumerateArray())
        {
            if (!position.TryGetProperty("valid", out var valid) || !valid.GetBoolean()
                || !position.TryGetProperty("deviceId", out var deviceIdElement)
                || !position.TryGetProperty("latitude", out var latElement)
                || !position.TryGetProperty("longitude", out var lonElement)) continue;

            var deviceId = deviceIdElement.GetInt64();
            var latitude = latElement.GetDecimal();
            var longitude = lonElement.GetDecimal();
            var accuracy = position.TryGetProperty("accuracy", out var accuracyElement) && accuracyElement.TryGetDecimal(out var accuracyValue)
                ? accuracyValue : (decimal?)null;
            var time = position.TryGetProperty("fixTime", out var fixTime) && fixTime.ValueKind == JsonValueKind.String
                ? fixTime.GetDateTimeOffset() : DateTimeOffset.UtcNow;

            using var scope = _scopeFactory.CreateScope();
            var trackingService = scope.ServiceProvider.GetRequiredService<ITrackingService>();
            var updates = await trackingService.ProcessPositionAsync(deviceId, latitude, longitude, accuracy, time, ct);
            foreach (var update in updates)
                await _hub.Clients.Group(TrackingHub.GroupName(update.BookingId))
                    .SendAsync("PositionUpdated", update, ct);
        }
    }
}
