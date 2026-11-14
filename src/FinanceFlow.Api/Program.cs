using FinanceFlow.Api.Configuration;
using FinanceFlow.Api.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;
using FinanceFlow.Api.Authentication;
using FinanceFlow.Infrastructure;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Authorization;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using FinanceFlow.Application.Accounts;
using FinanceFlow.Api.Contracts.Accounts;
using System.Text.Json.Nodes;

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

builder.Services.AddOptions<ApiDocumentationOptions>()
    .BindConfiguration(ApiDocumentationOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(options => !string.IsNullOrWhiteSpace(options.Title), "OpenApi:Title is required.")
    .ValidateOnStart();

builder.Services.AddOpenApi("v1", options =>
{
    options.AddSchemaTransformer((schema, context, cancellationToken) =>
    {
        if (context.JsonTypeInfo.Type == typeof(decimal) || context.JsonTypeInfo.Type == typeof(decimal?))
        {
            schema.Type = JsonSchemaType.Number;
            schema.Format = null;
            schema.Description = "Exact BRL decimal amount, cents only; range -999999999999.99 to 999999999999.99. No rounding.";
        }
        if (context.JsonTypeInfo.Type == typeof(CreateAccountRequest) && schema.Properties is not null)
        {
            schema.Properties["openingBalance"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Number, Minimum = "-999999999999.99", Maximum = "999999999999.99", MultipleOf = 0.01m,
                Description = "Required JSON number in BRL with exact cents. 10.000 is valid; 10.001 is invalid."
            };
            schema.Properties["type"] = new OpenApiSchema { Type = JsonSchemaType.String,
                Enum = [JsonValue.Create("checking"), JsonValue.Create("savings"), JsonValue.Create("cash"), JsonValue.Create("other")] };
            schema.Properties["currency"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("BRL")] };
        }
        return Task.CompletedTask;
    });
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        document.Info.Title = context.ApplicationServices
            .GetRequiredService<IOptions<ApiDocumentationOptions>>().Value.Title;
        document.Info.Version = "v1";
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
            Description = "Short-lived access token. No refresh token."
        };
        if (document.Paths is not null)
            foreach (var path in document.Paths.Where(x => x.Key.StartsWith("/api/v1/users", StringComparison.Ordinal)
                || x.Key.StartsWith("/api/v1/accounts", StringComparison.Ordinal) || x.Key.EndsWith("/auth/logout", StringComparison.Ordinal)))
                if (path.Value.Operations is not null)
                    foreach (var operation in path.Value.Operations.Values)
                        operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];
        if (document.Paths is not null)
            foreach (var path in document.Paths.Where(x => x.Key.StartsWith("/api/v1/accounts", StringComparison.Ordinal)))
                if (path.Value.Operations is not null)
                    foreach (var (method, operation) in path.Value.Operations)
                    {
                        if (method == HttpMethod.Post)
                        {
                            operation.Parameters ??= [];
                            operation.Parameters.Add(new OpenApiParameter
                            {
                                Name = "Idempotency-Key", In = ParameterLocation.Header, Required = true,
                                Description = "1–128 visible ASCII characters, no spaces; case-sensitive. Same command replays original 201/body/Location/ETag; different command returns 409.",
                                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 128, Pattern = "^[!-~]{1,128}$" }
                            });
                        }
                        if (operation.Responses is not null)
                            foreach (var (status, response) in operation.Responses)
                                if (response is OpenApiResponse writable && (status == "201" || (status == "200" && path.Key.Contains("{id}", StringComparison.Ordinal))))
                                {
                                    writable.Headers ??= new Dictionary<string, IOpenApiHeader>();
                                    writable.Headers["ETag"] = new OpenApiHeader { Description = "Strong opaque SQL rowversion validator.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
                                    if (status == "201") writable.Headers["Location"] = new OpenApiHeader { Description = "Relative URL of the created account, preserved on replay.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
                                }
                    }
        return Task.CompletedTask;
    });
});

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
