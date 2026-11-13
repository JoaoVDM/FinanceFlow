using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

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
}
