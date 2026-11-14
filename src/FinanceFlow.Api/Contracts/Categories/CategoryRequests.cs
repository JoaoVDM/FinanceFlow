using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace FinanceFlow.Api.Contracts.Categories;

public sealed class CreateCategoryRequest
{
    // Length is checked after Trim/NFC in the domain, including the normalized name.
    [Required]
    public string Name { get; init; } = string.Empty;

    [Required]
    public string Kind { get; init; } = string.Empty;
}

public sealed class CategoryListRequest
{
    [Range(1, int.MaxValue)]
    [FromQuery(Name = "page")]
    public int Page { get; init; } = 1;

    [Range(1, 100)]
    [FromQuery(Name = "pageSize")]
    public int PageSize { get; init; } = 20;

    [FromQuery(Name = "kind")]
    public string? Kind { get; init; }

    [FromQuery(Name = "isActive")]
    public bool? IsActive { get; init; }

    [FromQuery(Name = "sort")]
    public string Sort { get; init; } = "name";

    [FromQuery(Name = "direction")]
    public string Direction { get; init; } = "asc";
}
