using FinanceFlow.Domain.Accounts;
using Xunit;

namespace FinanceFlow.Domain.Tests;

public sealed class FinancialAccountChangesTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedAt = CreatedAt.AddHours(1).ToOffset(TimeSpan.FromHours(-3));

    [Theory]
    [InlineData("checking")]
    [InlineData("savings")]
    [InlineData("cash")]
    [InlineData("other")]
    public void Editing_an_inactive_account_changes_only_editable_fields_and_timestamp(string type)
    {
        var account = Create();
        var id = account.Id;
        var owner = account.UserId;
        account.SetActivity(false, CreatedAt);

        account.UpdateDetails(" Cafe\u0301 ", type, UpdatedAt);

        Assert.Equal("Café", account.Name);
        Assert.Equal(type, account.Type);
        Assert.Equal(id, account.Id);
        Assert.Equal(owner, account.UserId);
        Assert.Equal("BRL", account.Currency);
        Assert.Equal(-50.25m, account.OpeningBalance);
        Assert.Equal(new DateOnly(2020, 1, 1), account.OpeningDate);
        Assert.Equal(CreatedAt, account.CreatedAtUtc);
        Assert.Equal(UpdatedAt.ToUniversalTime(), account.UpdatedAtUtc);
        Assert.Equal(TimeSpan.Zero, account.UpdatedAtUtc!.Value.Offset);
        Assert.False(account.IsActive);
    }

    [Theory]
    [InlineData(null, "cash")]
    [InlineData(" ", "cash")]
    [InlineData("Changed", "Cash")]
    [InlineData("Changed", "creditCard")]
    public void Invalid_edits_do_not_mutate_any_field(string? name, string type)
    {
        var account = Create();

        Assert.Throws<ArgumentException>(() => account.UpdateDetails(name!, type, UpdatedAt));

        Assert.Equal("Original", account.Name);
        Assert.Equal("checking", account.Type);
        Assert.Null(account.UpdatedAtUtc);
    }

    [Fact]
    public void Name_limits_match_the_existing_account_policy()
    {
        var account = Create();

        account.UpdateDetails(new string('a', 100), "cash", UpdatedAt);

        Assert.Equal(100, account.Name.Length);
        Assert.Throws<ArgumentException>(() => account.UpdateDetails(new string('a', 101), "cash", UpdatedAt));
    }

    [Fact]
    public void Activity_can_toggle_without_zero_balance_or_deletion()
    {
        var account = Create();

        account.SetActivity(false, UpdatedAt);

        Assert.False(account.IsActive);
        Assert.Equal(-50.25m, account.OpeningBalance);
        Assert.Equal("Original", account.Name);

        account.SetActivity(true, UpdatedAt.AddMinutes(1));

        Assert.True(account.IsActive);
        Assert.Equal(UpdatedAt.AddMinutes(1).ToUniversalTime(), account.UpdatedAtUtc);
        Assert.Equal(CreatedAt, account.CreatedAtUtc);
    }

    [Fact]
    public void Repeated_values_still_record_the_accepted_command_timestamp()
    {
        var account = Create();

        account.UpdateDetails("Original", "checking", UpdatedAt);
        account.SetActivity(true, UpdatedAt.AddMinutes(1));

        Assert.Equal("Original", account.Name);
        Assert.Equal(UpdatedAt.AddMinutes(1).ToUniversalTime(), account.UpdatedAtUtc);
    }

    private static FinancialAccount Create() => FinancialAccount.Create(
        Guid.NewGuid(), Guid.NewGuid(), "Original", "checking", "BRL", -50.25m,
        new DateOnly(2020, 1, 1), new DateOnly(2026, 11, 14), CreatedAt);
}
