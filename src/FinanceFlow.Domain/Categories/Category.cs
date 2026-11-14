using System.Text;

namespace FinanceFlow.Domain.Categories;

public sealed class Category
{
    private Category()
    {
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string NormalizedName { get; private set; } = string.Empty;
    public string Kind { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static bool IsKindValid(string? kind) => kind is "income" or "expense";

    public static Category Create(Guid id, Guid userId, string name, string kind, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A category identifier is required.", nameof(id));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("An owner is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A category name is required.", nameof(name));
        }

        var displayName = name.Trim().Normalize(NormalizationForm.FormC);
        var normalizedName = displayName.ToUpperInvariant().Normalize(NormalizationForm.FormC);
        if (displayName.Length is 0 or > 100 || normalizedName.Length is 0 or > 100)
        {
            throw new ArgumentException("Category names must contain 1 to 100 characters after normalization.", nameof(name));
        }

        if (!IsKindValid(kind))
        {
            throw new ArgumentException("Invalid category kind.", nameof(kind));
        }

        return new Category
        {
            Id = id,
            UserId = userId,
            Name = displayName,
            NormalizedName = normalizedName,
            Kind = kind,
            IsActive = true,
            CreatedAtUtc = createdAt.ToUniversalTime()
        };
    }
}
