using System.Text;
using FinanceFlow.Domain.Categories;
using Xunit;

namespace FinanceFlow.Domain.Tests;

public sealed class CategoryTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 14, 12, 0, 0, TimeSpan.FromHours(-3));

    [Theory]
    [InlineData("income")]
    [InlineData("expense")]
    public void Valid_category_has_server_metadata_and_initial_state(string kind)
    {
        var id = Guid.NewGuid();
        var owner = Guid.NewGuid();

        var category = Category.Create(id, owner, " Alimentação ", kind, CreatedAt);

        Assert.Equal(id, category.Id);
        Assert.Equal(owner, category.UserId);
        Assert.Equal("Alimentação", category.Name);
        Assert.Equal("ALIMENTAÇÃO", category.NormalizedName);
        Assert.Equal(kind, category.Kind);
        Assert.True(category.IsActive);
        Assert.Equal(CreatedAt.ToUniversalTime(), category.CreatedAtUtc);
        Assert.Equal(TimeSpan.Zero, category.CreatedAtUtc.Offset);
        Assert.Null(category.UpdatedAtUtc);
        Assert.Empty(category.RowVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void Missing_names_are_invalid(string? name)
    {
        Assert.Throws<ArgumentException>(() => Create(name!));
    }

    [Theory]
    [InlineData("Café")]
    [InlineData(" café ")]
    [InlineData("CAFÉ")]
    [InlineData("Cafe\u0301")]
    public void Names_use_trim_nfc_invariant_uppercase_and_nfc(string name)
    {
        var category = Create(name);

        Assert.Equal(name.Trim().Normalize(NormalizationForm.FormC), category.Name);
        Assert.Equal("CAFÉ", category.NormalizedName);
        Assert.True(category.NormalizedName.IsNormalized(NormalizationForm.FormC));
    }

    [Fact]
    public void Accents_remain_significant()
    {
        var withoutAccent = Create("Cafe");
        var withAccent = Create("Café");

        Assert.NotEqual(withoutAccent.NormalizedName, withAccent.NormalizedName);
    }

    [Fact]
    public void Both_name_limits_are_checked_after_normalization_without_truncation()
    {
        var decomposedBoundary = string.Concat(Enumerable.Repeat("e\u0301", 100));

        var category = Create(" " + decomposedBoundary + " ");

        Assert.Equal(100, category.Name.Length);
        Assert.Equal(100, category.NormalizedName.Length);
        Assert.Throws<ArgumentException>(() => Create(new string('a', 101)));
        // NFC decomposes U+0344 into two combining characters; normalization can increase length.
        Assert.Throws<ArgumentException>(() => Create(new string('\u0344', 51)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Income")]
    [InlineData("expense ")]
    [InlineData("other")]
    public void Kind_is_a_closed_case_sensitive_code(string? kind)
    {
        Assert.Throws<ArgumentException>(() => Create("Category", kind!));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Empty_identifier_or_owner_is_invalid(bool emptyId)
    {
        var id = emptyId ? Guid.Empty : Guid.NewGuid();
        var owner = emptyId ? Guid.NewGuid() : Guid.Empty;

        Assert.Throws<ArgumentException>(() => Category.Create(id, owner, "Category", "income", CreatedAt));
    }

    [Fact]
    public void Fields_cannot_be_reassigned_through_public_setters()
    {
        foreach (var property in typeof(Category).GetProperties())
        {
            Assert.False(property.SetMethod?.IsPublic ?? false);
        }
    }

    private static Category Create(string name, string kind = "expense") =>
        Category.Create(Guid.NewGuid(), Guid.NewGuid(), name, kind, CreatedAt);
}
