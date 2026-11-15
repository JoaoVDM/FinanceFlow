using FinanceFlow.Api.Contracts.Categories;
using FinanceFlow.Api.Concurrency;
using FinanceFlow.Application.Categories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinanceFlow.Api.Controllers;

[ApiController, Authorize, Route("api/v1/categories"), RequestSizeLimit(16_384)]
public sealed class CategoriesController(CategoriesService categories) : ControllerBase
{
    private static readonly HashSet<string> ListParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "page", "pageSize", "kind", "isActive", "sort", "direction"
    };

    [HttpPost]
    [ProducesResponseType<CategoryView>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Create(CreateCategoryRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await categories.CreateAsync(new(request.Name, request.Kind), cancellationToken);
        if (result.Error is { } error)
        {
            return Failure(error);
        }

        var category = result.Category!;
        Response.Headers.ETag = '"' + category.Version + '"';
        return Created("/api/v1/categories/" + category.Id.ToString("D"), category);
    }

    [HttpGet]
    [ProducesResponseType<CategoryPage>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> List([FromQuery] CategoryListRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (Request.Query.Any(parameter => !ListParameters.Contains(parameter.Key)
            || parameter.Value.Count != 1 || string.IsNullOrEmpty(parameter.Value[0])))
        {
            return Failure(CategoryFailure.InvalidInput);
        }

        var result = await categories.ListAsync(
            new(request.Page, request.PageSize, request.Kind, request.IsActive, request.Sort, request.Direction),
            cancellationToken);
        return result.Error is { } error ? Failure(error) : Ok(result.Page);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<CategoryView>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await categories.GetAsync(id, cancellationToken);
        if (result.Error is { } error)
        {
            return Failure(error);
        }

        Response.Headers.ETag = '"' + result.Category!.Version + '"';
        return Ok(result.Category);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Rename(Guid id, RenameCategoryRequest request, CancellationToken cancellationToken)
    {
        var headerError = IfMatchHeader.Read(Request.Headers, out var version);
        if (headerError is { } status)
        {
            return Problem(statusCode: status);
        }

        var result = await categories.RenameAsync(id, new(request.Name), version!, cancellationToken);
        return ChangeResponse(result);
    }

    [HttpPatch("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> SetActivity(Guid id, SetCategoryActivityRequest request, CancellationToken cancellationToken)
    {
        var headerError = IfMatchHeader.Read(Request.Headers, out var version);
        if (headerError is { } status)
        {
            return Problem(statusCode: status);
        }

        var result = await categories.SetActivityAsync(id, request.IsActive!.Value, version!, cancellationToken);
        return ChangeResponse(result);
    }

    private IActionResult ChangeResponse(CategoryChangeResult result)
    {
        if (result.Error is { } error)
        {
            return Failure(error);
        }

        Response.Headers.ETag = '"' + result.Version + '"';
        return NoContent();
    }

    private IActionResult Failure(CategoryFailure error)
    {
        var status = error switch
        {
            CategoryFailure.InvalidInput => 400,
            CategoryFailure.Unauthenticated => 401,
            CategoryFailure.NotFound => 404,
            CategoryFailure.Duplicate => 409,
            CategoryFailure.VersionConflict => 412,
            _ => 503
        };
        if (status == 401)
        {
            Response.Headers.WWWAuthenticate = "Bearer";
        }

        return Problem(statusCode: status, title: status switch
        {
            400 => "Invalid category request.",
            401 => "Authentication required.",
            404 => "Resource not found.",
            409 => "A category with this name and kind already exists.",
            412 => "The resource version has changed.",
            _ => "Service unavailable."
        });
    }
}
