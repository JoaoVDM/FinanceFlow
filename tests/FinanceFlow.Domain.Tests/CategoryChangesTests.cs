using FinanceFlow.Domain.Categories;
using Xunit;

namespace FinanceFlow.Domain.Tests;

public sealed class CategoryChangesTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedAt = CreatedAt.AddHours(1).ToOffset(TimeSpan.FromHours(-3));

    [Theory]
    [InlineData("Café")]
    [InlineData(" CAFÉ ")]
    [InlineData("Cafe\u0301")]
    public void Rename_preserves_owner_kind_and_creation_and_normalizes_both_names(string name)
    {
        var category = Create();
        var owner = category.UserId;
        var id = category.Id;

        category.Rename(name, UpdatedAt);

        Assert.Equal("CAFÉ", category.NormalizedName);
        Assert.Equal(name.Trim().Normalize(), category.Name);
        Assert.Equal(owner, category.UserId);
        Assert.Equal(id, category.Id);
        Assert.Equal("expense", category.Kind);
        Assert.Equal(CreatedAt, category.CreatedAtUtc);
        Assert.Equal(UpdatedAt.ToUniversalTime(), category.UpdatedAtUtc);
        Assert.Equal(TimeSpan.Zero, category.UpdatedAtUtc!.Value.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Invalid_rename_leaves_names_and_timestamp_unchanged(string? name)
    {
        var category = Create();

        Assert.Throws<ArgumentException>(() => category.Rename(name!, UpdatedAt));

        Assert.Equal("Original", category.Name);
        Assert.Equal("ORIGINAL", category.NormalizedName);
        Assert.Null(category.UpdatedAtUtc);
    }

    [Fact]
    public void Normalized_lengths_and_accents_keep_the_approved_policy()
    {
        var category = Create();
        var decomposed = string.Concat(Enumerable.Repeat("e\u0301", 100));

        category.Rename(decomposed, UpdatedAt);

        Assert.Equal(new string('é', 100), category.Name);
        Assert.Equal(new string('É', 100), category.NormalizedName);
        Assert.Throws<ArgumentException>(() => category.Rename(new string('a', 101), UpdatedAt));
        Assert.Throws<ArgumentException>(() => category.Rename(new string('\u0344', 51), UpdatedAt));
        category.Rename("Cafe", UpdatedAt);
        Assert.Equal("CAFE", category.NormalizedName);
        category.Rename("Café", UpdatedAt);
        Assert.Equal("CAFÉ", category.NormalizedName);
    }

    [Fact]
    public void Inactive_categories_can_be_renamed_and_reactivated_without_changing_kind()
    {
        var category = Create();

        category.SetActivity(false, UpdatedAt);
        category.Rename("Changed", UpdatedAt.AddMinutes(1));

        Assert.False(category.IsActive);
        Assert.Equal("CHANGED", category.NormalizedName);
        category.SetActivity(true, UpdatedAt.AddMinutes(2));
        Assert.True(category.IsActive);
        Assert.Equal("expense", category.Kind);
        Assert.Equal(CreatedAt, category.CreatedAtUtc);
    }

    [Fact]
    public void Repeating_values_still_updates_the_command_timestamp()
    {
        var category = Create();

        category.Rename("Original", UpdatedAt);
        category.SetActivity(true, UpdatedAt.AddMinutes(1));

        Assert.Equal("ORIGINAL", category.NormalizedName);
        Assert.Equal(UpdatedAt.AddMinutes(1).ToUniversalTime(), category.UpdatedAtUtc);
    }

    private static Category Create() => Category.Create(Guid.NewGuid(), Guid.NewGuid(), "Original", "expense", CreatedAt);
}
