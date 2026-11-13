using Microsoft.AspNetCore.Diagnostics;
using System.Data.Common;

namespace FinanceFlow.Api.Errors;

internal sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // Provider exceptions can contain SQL values; never log their raw messages.
        logger.LogError("Request failed with {ExceptionType}. TraceId: {TraceId}",
            exception.GetType().Name, httpContext.TraceIdentifier);

        var status = exception switch
        {
            BadHttpRequestException requestException => requestException.StatusCode,
            DbException or TimeoutException => 503,
            _ => 500
        };
        httpContext.Response.StatusCode = status;
        await ApiProblemDetails.WriteAsync(httpContext, status);
        return true;
    }
}
