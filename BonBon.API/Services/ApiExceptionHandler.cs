using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;

namespace BonBon.API.Services;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        logger.LogError(exception, "Unhandled API error. TraceId: {TraceId}", traceId);
        await Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
            title: "INTERNAL_SERVER_ERROR",
            detail: "Hệ thống tạm thời gặp lỗi. Vui lòng thử lại sau.",
            extensions: new Dictionary<string, object?> { ["traceId"] = traceId }).ExecuteAsync(context);
        return true;
    }
}
