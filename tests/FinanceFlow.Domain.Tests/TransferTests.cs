using System.Globalization;
using FinanceFlow.Domain.Accounts;
using FinanceFlow.Domain.Transfers;
using Xunit;

namespace FinanceFlow.Domain.Tests;

public sealed class TransferTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.FromHours(-3));

    [Theory]
    [InlineData("0.01")]
    [InlineData("10.000")]
    [InlineData("999999999999.99")]
    public void Creation_preserves_exact_money_description_and_initial_server_fields(string amountText)
    {
        var amount = decimal.Parse(amountText, CultureInfo.InvariantCulture);
        var value = Create(amount: amount);
        Assert.Equal(amount, value.Amount);
        Assert.Equal(Now.ToUniversalTime(), value.CreatedAtUtc);
        Assert.Equal(TimeSpan.Zero, value.CreatedAtUtc.Offset);
        Assert.Null(value.UpdatedAtUtc);
        Assert.Null(value.CanceledAtUtc);
        Assert.False(value.IsCanceled);
        Assert.Empty(value.RowVersion);
        Assert.Null(value.Description);
        Assert.NotEqual(value.SourceAccountId, value.DestinationAccountId);
        Assert.All(typeof(Transfer).GetProperties(), property => Assert.False(property.SetMethod?.IsPublic));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("owner")]
    [InlineData("source")]
    [InlineData("destination")]
    [InlineData("same")]
    public void Missing_or_identical_identifiers_are_rejected(string field)
    {
        var source = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => Transfer.Create(field == "id" ? Guid.Empty : Guid.NewGuid(),
            field == "owner" ? Guid.Empty : Guid.NewGuid(), field == "source" ? Guid.Empty : source,
            field == "destination" ? Guid.Empty : field == "same" ? source : Guid.NewGuid(), 1m, Today, null, Today, Now));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("10.001")]
    [InlineData("1000000000000")]
    public void Invalid_money_is_rejected_without_rounding(string value) =>
        Assert.Throws<ArgumentException>(() => Create(amount: decimal.Parse(value, CultureInfo.InvariantCulture)));

    [Fact]
    public void Future_and_long_description_are_rejected_but_null_empty_and_limit_are_preserved()
    {
        Assert.Throws<ArgumentException>(() => Create(date: Today.AddDays(1)));
        Assert.Throws<ArgumentException>(() => Create(description: new string('x', 501)));
        foreach (var description in new[] { null, "", "  preserved  ", new string('x', 500) })
        {
            Assert.Equal(description, Create(description: description).Description);
        }
    }

    [Theory]
    [InlineData("source-owner")]
    [InlineData("destination-owner")]
    [InlineData("source-id")]
    [InlineData("destination-id")]
    [InlineData("source-opening")]
    [InlineData("destination-opening")]
    public void Links_require_both_owners_identifiers_and_opening_dates(string invalid)
    {
        var value = Create(date: Today.AddDays(-1));
        var source = Account(invalid == "source-id" ? Guid.NewGuid() : value.SourceAccountId,
            invalid == "source-owner" ? Guid.NewGuid() : value.UserId, invalid == "source-opening" ? Today : Today.AddDays(-1));
        var destination = Account(invalid == "destination-id" ? Guid.NewGuid() : value.DestinationAccountId,
            invalid == "destination-owner" ? Guid.NewGuid() : value.UserId, invalid == "destination-opening" ? Today : Today.AddDays(-1));
        Assert.Equal(TransferAccountStatus.Invalid, value.CheckAccounts(source, destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_accounts_must_be_active_but_available_funds_are_not_required(bool sourceInactive)
    {
        var value = Create(amount: 999999999999.99m);
        var source = Account(value.SourceAccountId, value.UserId, Today);
        var destination = Account(value.DestinationAccountId, value.UserId, Today);
        Assert.Equal(TransferAccountStatus.Valid, value.CheckAccounts(source, destination));
        (sourceInactive ? source : destination).SetActivity(false, Now);
        Assert.Equal(TransferAccountStatus.Inactive, value.CheckAccounts(source, destination));
    }

    private static Transfer Create(decimal amount = 1m, DateOnly? date = null, string? description = null) =>
        Transfer.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), amount, date ?? Today, description, Today, Now);

    private static FinancialAccount Account(Guid id, Guid owner, DateOnly opening) =>
        FinancialAccount.Create(id, owner, "Transfer account", "cash", "BRL", -100m, opening, Today, Now);
}
