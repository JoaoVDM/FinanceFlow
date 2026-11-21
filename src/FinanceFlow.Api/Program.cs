using FinanceFlow.Api.Configuration;
using FinanceFlow.Api.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using FinanceFlow.Api.Authentication;
using FinanceFlow.Infrastructure;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Authorization;
using System.Text.Json.Serialization;
using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Common;

if (args.Length == 3 && args[0] == "--probe-readiness")
{
    Environment.ExitCode = await ReadinessProbe.RunAsync(args[1], args[2]);
    return;
}
var databaseMode = args.FirstOrDefault(argument => argument is "--provision-database" or "--migrate");
var builder = WebApplication.CreateBuilder(args.Where(argument => argument is not "--provision-database" and not "--migrate").ToArray());

if (databaseMode is not null)
{
    Environment.ExitCode = await DatabaseTasks.RunCommandAsync(databaseMode, builder.Configuration);
    return;
}

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
// Suppress provider error messages that could include SQL values or credentials.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Update", LogLevel.None);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16_384);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApiAuthentication();
builder.Services.AddApiRateLimits();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddControllers(options => options.Filters.Add(new AuthorizeFilter()))
    .AddJsonOptions(options => options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        ApiProblemDetails.Customize(context.ProblemDetails, context.HttpContext));
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var factory = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var problem = factory.CreateValidationProblemDetails(context.HttpContext, context.ModelState);
        ApiProblemDetails.Customize(problem, context.HttpContext);
        var result = new BadRequestObjectResult(problem);
        result.ContentTypes.Add("application/problem+json");
        return result;
    };
});

builder.Services.AddApiDocumentation();

var app = builder.Build();
// Resolve the financial calendar before serving requests; never silently use another zone.
_ = app.Services.GetRequiredService<FinancialCalendar>();

// Keep the public error format safe in every environment, including Development.
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseRouting();
app.Use(async (context, next) =>
{
    if ((HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsPatch(context.Request.Method))
        && (context.Request.Path.StartsWithSegments("/api/v1/accounts")
            || context.Request.Path.StartsWithSegments("/api/v1/categories")))
    {
        // Include validation, authentication and rate-limit errors, even before controller execution.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
    }

    if (context.Request.Path.StartsWithSegments("/api/v1/transactions")
        || context.Request.Path.StartsWithSegments("/api/v1/transfers")
        || (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.StartsWithSegments("/api/v1/accounts")
            && context.Request.Path.Value!.TrimEnd('/').EndsWith("/balance", StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
    }

    // Reject HTTP for credentials; do not redirect/replay a body containing a password.
    if (context.Request.Path.StartsWithSegments("/api") && !context.Request.IsHttps)
    {
        await ApiProblemDetails.WriteAsync(context, 400);
        return;
    }
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
        options.SwaggerEndpoint("/openapi/v1.json", "FinanceFlow API v1"));
}

app.MapControllers();
app.Run();

// Expose the entry point for WebApplicationFactory.
public partial class Program
{
}
