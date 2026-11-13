using System.ComponentModel.DataAnnotations;

namespace FinanceFlow.Api.Configuration;

public sealed class ApiDocumentationOptions
{
    public const string SectionName = "OpenApi";

    [Required, StringLength(100, MinimumLength = 1)]
    public string Title { get; init; } = string.Empty;
}
