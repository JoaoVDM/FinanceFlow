using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace FinanceFlow.Api.Errors;

internal static class ApiProblemDetails
{
    public static void Customize(ProblemDetails problem, HttpContext context)
    {
        problem.Instance ??= context.Request.Path;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        problem.Extensions["code"] = problem.Status switch
        {
            400 => "validation_error",
            401 => "unauthorized",
            403 => "forbidden",
            409 => "conflict",
            412 => "precondition_failed",
            428 => "precondition_required",
            413 => "payload_too_large",
            429 => "rate_limit_exceeded",
            503 => "service_unavailable",
            404 => "not_found",
            405 => "method_not_allowed",
            500 => "internal_error",
            _ => "http_error"
        };

        if (problem.Status == StatusCodes.Status500InternalServerError)
        {
            problem.Title = "An unexpected error occurred.";
            problem.Detail = "Contact support with the trace identifier.";
        }
    }

    public static async Task WriteAsync(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        var service = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        var problem = new ProblemDetails { Status = status, Title = ReasonPhrases.GetReasonPhrase(status), Type = "about:blank" };
        Customize(problem, context);
        if (await service.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem
        })) return;
        // An incompatible Accept header must not fall through to a developer exception page.
        await context.Response.WriteAsJsonAsync(problem, options: (JsonSerializerOptions?)null,
            contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}
