using System.Text.Json.Nodes;
using FinanceFlow.Api.Contracts.Accounts;
using FinanceFlow.Api.Contracts.Categories;
using FinanceFlow.Application.Categories;
using FinanceFlow.Api.Contracts.Transactions;
using FinanceFlow.Api.Contracts.Transfers;
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
            options.AddSchemaTransformer(ConfigureActivitySchemasAsync);
            options.AddSchemaTransformer(ConfigureTransactionSchemasAsync);
            options.AddSchemaTransformer(ConfigureTransferSchemasAsync);
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

        if (context.JsonTypeInfo.Type == typeof(UpdateAccountRequest) && schema.Properties is not null)
        {
            schema.Properties["type"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = [JsonValue.Create("checking"), JsonValue.Create("savings"), JsonValue.Create("cash"), JsonValue.Create("other")]
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
        ConfigureFinancialChanges(document);
        ConfigureTransactionOperations(document);
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
            || path.Key.StartsWith("/api/v1/transactions", StringComparison.Ordinal)
            || path.Key.StartsWith("/api/v1/transfers", StringComparison.Ordinal)
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
                        && (status == "201" || (status == "200" && path.Key.Contains("{id}", StringComparison.Ordinal)
                            && !path.Key.EndsWith("/balance", StringComparison.Ordinal))))
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
        if ((context.JsonTypeInfo.Type == typeof(CreateCategoryRequest) || context.JsonTypeInfo.Type == typeof(CategoryView)
            || context.JsonTypeInfo.Type == typeof(RenameCategoryRequest))
            && schema.Properties is not null)
        {
            if (context.JsonTypeInfo.Type != typeof(RenameCategoryRequest))
            {
                schema.Properties["kind"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Enum = [JsonValue.Create("income"), JsonValue.Create("expense")]
                };
            }
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

    private static Task ConfigureTransactionSchemasAsync(
        OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if ((context.JsonTypeInfo.Type == typeof(CreateTransactionRequest) || context.JsonTypeInfo.Type == typeof(UpdateTransactionRequest))
            && schema.Properties is not null)
        {
            schema.Properties["kind"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = [JsonValue.Create("income"), JsonValue.Create("expense")]
            };
            schema.Properties["amount"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Number,
                Minimum = "0.01",
                Maximum = "999999999999.99",
                MultipleOf = 0.01m,
                Description = "Positive BRL decimal with exact cents, validated before parsing; no silent rounding."
            };
        }

        return Task.CompletedTask;
    }

    private static Task ConfigureTransferSchemasAsync(
        OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type == typeof(CreateTransferRequest) && schema.Properties is not null)
        {
            schema.Properties["amount"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Number, Minimum = "0.01", Maximum = "999999999999.99", MultipleOf = 0.01m,
                Description = "Positive BRL decimal with exact cents; no silent rounding."
            };
        }
        return Task.CompletedTask;
    }

    private static void ConfigureTransactionOperations(OpenApiDocument document)
    {
        if (document.Paths is null)
        {
            return;
        }
        if (document.Paths.TryGetValue("/api/v1/accounts/{id}/balance", out var balancePath)
            && balancePath.Operations?.TryGetValue(HttpMethod.Get, out var balanceOperation) == true)
        {
            balanceOperation.Description = "BRL balance from opening balance, valid income/expense and noncanceled transfers in/out through inclusive asOf (yyyy-MM-dd). Default: today in America/Sao_Paulo; future rejected. Before opening: zero. Inactive accounts remain included. No persisted current balance.";
        }
        foreach (var (path, resource) in document.Paths.Where(path => path.Key.StartsWith("/api/v1/transactions", StringComparison.Ordinal)
            || path.Key.StartsWith("/api/v1/transfers", StringComparison.Ordinal)))
        {
            if (resource.Operations is null)
            {
                continue;
            }
            foreach (var (method, operation) in resource.Operations)
            {
                if (method == HttpMethod.Post)
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "Idempotency-Key",
                        In = ParameterLocation.Header,
                        Required = true,
                        Description = "1–128 visible ASCII characters, case-sensitive. Replays original 201/body/Location/ETag; changed command: 409.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 128, Pattern = "^[!-~]{1,128}$" }
                    });
                }

                foreach (var parameter in operation.Parameters ?? [])
                {
                    if (parameter is not OpenApiParameter writableParameter)
                    {
                        continue;
                    }
                    if (parameter.Name is "MinAmount" or "minAmount" or "MaxAmount" or "maxAmount")
                    {
                        writableParameter.Schema = new OpenApiSchema { Type = JsonSchemaType.Number, Minimum = "0", Maximum = "999999999999.99", MultipleOf = 0.01m };
                    }
                    if (parameter.Name is "kind" or "sort" or "direction")
                    {
                        var allowed = parameter.Name switch
                        {
                            "kind" => new[] { "income", "expense" },
                            "sort" => new[] { "occurredOn", "amount", "id" },
                            _ => new[] { "asc", "desc" }
                        };
                        writableParameter.Schema = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String,
                            Enum = allowed.Select(value => (JsonNode)JsonValue.Create(value)!).ToList()
                        };
                    }
                }

                if (operation.Responses is null)
                {
                    continue;
                }
                foreach (var (status, response) in operation.Responses)
                {
                    if (response is not OpenApiResponse writable)
                    {
                        continue;
                    }
                    writable.Headers ??= new Dictionary<string, IOpenApiHeader>();
                    writable.Headers["Cache-Control"] = new OpenApiHeader { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("no-store")] } };
                    if (status == "201" || (status == "200" && path.Contains("{id}", StringComparison.Ordinal)))
                    {
                        writable.Headers["ETag"] = new OpenApiHeader { Schema = new OpenApiSchema { Type = JsonSchemaType.String }, Description = "Strong opaque SQL rowversion validator." };
                    }
                    if (status == "201")
                    {
                        writable.Headers["Location"] = new OpenApiHeader { Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                            Description = path.StartsWith("/api/v1/transfers", StringComparison.Ordinal)
                                ? "Created transfer URL, preserved on replay." : "Created transaction URL, preserved on replay." };
                    }
                }
            }
        }
    }

    private static Task ConfigureActivitySchemasAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if ((context.JsonTypeInfo.Type == typeof(SetAccountActivityRequest)
            || context.JsonTypeInfo.Type == typeof(SetCategoryActivityRequest)) && schema.Properties is not null)
        {
            // Nullable DTOs distinguish a missing value; Required rejects null on the wire.
            schema.Properties["isActive"] = new OpenApiSchema { Type = JsonSchemaType.Boolean };
        }

        return Task.CompletedTask;
    }

    private static void ConfigureFinancialChanges(OpenApiDocument document)
    {
        foreach (var path in new[] { "/api/v1/accounts/{id}", "/api/v1/categories/{id}", "/api/v1/transactions/{id}", "/api/v1/transfers/{id}" })
        {
            if (document.Paths is null || !document.Paths.TryGetValue(path, out var resource) || resource.Operations is null)
            {
                continue;
            }

            foreach (var (method, operation) in resource.Operations)
            {
                if (method != HttpMethod.Put && method != HttpMethod.Patch && method != HttpMethod.Delete)
                {
                    continue;
                }

                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "If-Match",
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "One strong quoted canonical Base64 validator containing 8 rowversion bytes. Missing: 428; malformed: 400; stale: 412. No wildcard, weak tag or list. Every accepted write, including no-op, returns a new ETag.",
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        MinLength = 14,
                        MaxLength = 14,
                        Pattern = "^\"[A-Za-z0-9+/]{10}[AEIMQUYcgkosw048]=\"$"
                    }
                });
                if (operation.Responses?.TryGetValue("204", out var response) == true && response is OpenApiResponse writable)
                {
                    writable.Headers ??= new Dictionary<string, IOpenApiHeader>();
                    writable.Headers["ETag"] = new OpenApiHeader
                    {
                        Description = "New strong rowversion validator, including when the requested values were unchanged.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    };
                    writable.Headers["Cache-Control"] = new OpenApiHeader
                    {
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("no-store")] }
                    };
                }
            }
        }
    }
}
