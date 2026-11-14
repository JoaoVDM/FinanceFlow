using System.Text.Json.Nodes;
using FinanceFlow.Api.Contracts.Accounts;
using FinanceFlow.Api.Contracts.Categories;
using FinanceFlow.Application.Categories;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace FinanceFlow.Api.Configuration;

internal static class ApiDocumentationConfiguration
{
    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddOptions<ApiDocumentationOptions>()
            .BindConfiguration(ApiDocumentationOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(options => !string.IsNullOrWhiteSpace(options.Title), "OpenApi:Title is required.")
            .ValidateOnStart();

        services.AddOpenApi("v1", options =>
        {
            options.AddSchemaTransformer(ConfigureAccountSchemasAsync);
            options.AddSchemaTransformer(ConfigureCategorySchemasAsync);
            options.AddDocumentTransformer(ConfigureDocumentAsync);
        });

        return services;
    }

    private static Task ConfigureAccountSchemasAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
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
                Type = JsonSchemaType.Number,
                Minimum = "-999999999999.99",
                Maximum = "999999999999.99",
                MultipleOf = 0.01m,
                Description = "Required JSON number in BRL with exact cents. 10.000 is valid; 10.001 is invalid."
            };
            schema.Properties["type"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = [JsonValue.Create("checking"), JsonValue.Create("savings"), JsonValue.Create("cash"), JsonValue.Create("other")]
            };
            schema.Properties["currency"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = [JsonValue.Create("BRL")]
            };
        }

        return Task.CompletedTask;
    }

    private static Task ConfigureDocumentAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info.Title = context.ApplicationServices
            .GetRequiredService<IOptions<ApiDocumentationOptions>>().Value.Title;
        document.Info.Version = "v1";
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Short-lived access token. No refresh token."
        };

        ConfigureBearerRequirements(document);
        ConfigureAccountOperations(document);
        ConfigureCategoryOperations(document);
        return Task.CompletedTask;
    }

    private static void ConfigureBearerRequirements(OpenApiDocument document)
    {
        if (document.Paths is null)
        {
            return;
        }

        var protectedPaths = document.Paths.Where(path =>
            path.Key.StartsWith("/api/v1/users", StringComparison.Ordinal)
            || path.Key.StartsWith("/api/v1/accounts", StringComparison.Ordinal)
            || path.Key.StartsWith("/api/v1/categories", StringComparison.Ordinal)
            || path.Key.EndsWith("/auth/logout", StringComparison.Ordinal));

        foreach (var path in protectedPaths)
        {
            if (path.Value.Operations is null)
            {
                continue;
            }

            foreach (var operation in path.Value.Operations.Values)
            {
                operation.Security =
                [
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference("Bearer", document)] = []
                    }
                ];
            }
        }
    }

    private static void ConfigureAccountOperations(OpenApiDocument document)
    {
        if (document.Paths is null)
        {
            return;
        }

        var accountPaths = document.Paths.Where(path => path.Key.StartsWith("/api/v1/accounts", StringComparison.Ordinal));
        foreach (var path in accountPaths)
        {
            if (path.Value.Operations is null)
            {
                continue;
            }

            foreach (var (method, operation) in path.Value.Operations)
            {
                if (method == HttpMethod.Post)
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "Idempotency-Key",
                        In = ParameterLocation.Header,
                        Required = true,
                        Description = "1–128 visible ASCII characters, no spaces; case-sensitive. Same command replays original 201/body/Location/ETag; different command returns 409.",
                        Schema = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String,
                            MinLength = 1,
                            MaxLength = 128,
                            Pattern = "^[!-~]{1,128}$"
                        }
                    });
                }

                if (operation.Responses is null)
                {
                    continue;
                }

                foreach (var (status, response) in operation.Responses)
                {
                    if (response is OpenApiResponse writable
                        && (status == "201" || (status == "200" && path.Key.Contains("{id}", StringComparison.Ordinal))))
                    {
                        writable.Headers ??= new Dictionary<string, IOpenApiHeader>();
                        writable.Headers["ETag"] = new OpenApiHeader
                        {
                            Description = "Strong opaque SQL rowversion validator.",
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                        };
                        if (status == "201")
                        {
                            writable.Headers["Location"] = new OpenApiHeader
                            {
                                Description = "Relative URL of the created account, preserved on replay.",
                                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                            };
                        }
                    }
                }
            }
        }
    }

    private static Task ConfigureCategorySchemasAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if ((context.JsonTypeInfo.Type == typeof(CreateCategoryRequest) || context.JsonTypeInfo.Type == typeof(CategoryView))
            && schema.Properties is not null)
        {
            schema.Properties["kind"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = [JsonValue.Create("income"), JsonValue.Create("expense")]
            };
            if (schema.Properties["name"] is OpenApiSchema name)
            {
                name.Description = "Required, 1–100 UTF-16 code units after Trim and NFC. Normalized uppercase NFC name must also fit 100; accents are significant.";
            }
        }

        return Task.CompletedTask;
    }

    private static void ConfigureCategoryOperations(OpenApiDocument document)
    {
        if (document.Paths is null)
        {
            return;
        }

        var categoryPaths = document.Paths.Where(path => path.Key.StartsWith("/api/v1/categories", StringComparison.Ordinal));
        foreach (var path in categoryPaths)
        {
            if (path.Value.Operations is null)
            {
                continue;
            }

            foreach (var (method, operation) in path.Value.Operations)
            {
                if (method == HttpMethod.Get && operation.Parameters is not null)
                {
                    foreach (var parameter in operation.Parameters.OfType<OpenApiParameter>())
                    {
                        parameter.Schema = parameter.Name switch
                        {
                            "kind" => new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                Enum = [JsonValue.Create("income"), JsonValue.Create("expense")]
                            },
                            "sort" => new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                Enum = [JsonValue.Create("name"), JsonValue.Create("id")],
                                Default = JsonValue.Create("name")
                            },
                            "direction" => new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                Enum = [JsonValue.Create("asc"), JsonValue.Create("desc")],
                                Default = JsonValue.Create("asc")
                            },
                            "page" => new OpenApiSchema
                            {
                                Type = JsonSchemaType.Integer,
                                Minimum = "1",
                                Default = JsonValue.Create(1)
                            },
                            "pageSize" => new OpenApiSchema
                            {
                                Type = JsonSchemaType.Integer,
                                Minimum = "1",
                                Maximum = "100",
                                Default = JsonValue.Create(20)
                            },
                            _ => parameter.Schema
                        };
                    }
                }

                if (operation.Responses is null)
                {
                    continue;
                }

                foreach (var (status, response) in operation.Responses)
                {
                    if (response is not OpenApiResponse writable
                        || (status != "201" && !(status == "200" && path.Key.Contains("{id}", StringComparison.Ordinal))))
                    {
                        continue;
                    }

                    writable.Headers ??= new Dictionary<string, IOpenApiHeader>();
                    writable.Headers["ETag"] = new OpenApiHeader
                    {
                        Description = "Strong opaque SQL rowversion validator.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    };
                    if (status == "201")
                    {
                        writable.Headers["Location"] = new OpenApiHeader
                        {
                            Description = "Relative URL of the created category.",
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                        };
                    }
                }
            }
        }
    }
}
