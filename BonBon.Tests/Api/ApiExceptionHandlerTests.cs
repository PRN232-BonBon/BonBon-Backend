using System.Net;
using System.Text.Json;
using BonBon.API.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BonBon.Tests.Api;

public class ApiExceptionHandlerTests
{
    [Theory]
    [InlineData("https://bonbon.example.test", true)]
    [InlineData("https://untrusted.example.test", false)]
    public async Task Handled_error_is_safe_and_preserves_origin_policy(string origin, bool allowed)
    {
        using var server = new TestServer(new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddProblemDetails();
                services.AddExceptionHandler<ApiExceptionHandler>();
                services.AddCors(options => options.AddPolicy("Frontend", policy =>
                    policy.WithOrigins("https://bonbon.example.test").AllowAnyHeader().AllowAnyMethod()));
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseCors("Frontend");
                app.UseExceptionHandler();
                app.Run(_ => throw new InvalidOperationException("secret SQL connection detail"));
            }));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", origin);
        var response = await client.GetAsync("/api/vehicles");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
        if (allowed) Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        var content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret SQL", content);
        using var body = JsonDocument.Parse(content);
        Assert.Equal("INTERNAL_SERVER_ERROR", body.RootElement.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
    }
}
