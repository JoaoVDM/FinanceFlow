using FinanceFlow.Api.Configuration;
using FinanceFlow.Api.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
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
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        document.Info.Title = context.ApplicationServices
            .GetRequiredService<IOptions<ApiDocumentationOptions>>().Value.Title;
        document.Info.Version = "v1";
        return Task.CompletedTask;
    }));

var app = builder.Build();

// Keep the public error format safe in every environment, including Development.
app.UseExceptionHandler();
app.UseStatusCodePages();

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
