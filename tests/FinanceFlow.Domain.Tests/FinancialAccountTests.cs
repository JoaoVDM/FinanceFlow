using FinanceFlow.Domain.Accounts;
using Xunit;

namespace FinanceFlow.Domain.Tests;

public sealed class FinancialAccountTests
{
    private static readonly DateOnly Today = new(2026, 11, 14);
    private static FinancialAccount Create(string name = " Account ", string type = "checking", string currency = "BRL",
        decimal balance = 0m, DateOnly? openingDate = null) => FinancialAccount.Create(Guid.NewGuid(), Guid.NewGuid(),
            name, type, currency, balance, openingDate ?? Today, Today, new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("checking")]
    [InlineData("savings")]
    [InlineData("cash")]
    [InlineData("other")]
    public void Approved_types_create_active_accounts(string type)
    {
        var account = Create(type: type);
        Assert.Equal(type, account.Type);
        Assert.Equal("Account", account.Name);
        Assert.True(account.IsActive);
        Assert.Equal(TimeSpan.Zero, account.CreatedAtUtc.Offset);
        Assert.Null(account.UpdatedAtUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Empty_names_are_invalid(string name) => Assert.Throws<ArgumentException>(() => Create(name: name));

    [Fact]
    public void Name_limits_and_unicode_are_preserved()
    {
        Assert.Equal(100, Create(name: new string('x', 100)).Name.Length);
        Assert.Throws<ArgumentException>(() => Create(name: new string('x', 101)));
        Assert.Equal("Café", Create(name: " Cafe\u0301 ").Name);
    }

    [Theory]
    [InlineData("Checking")]
    [InlineData("investment")]
    [InlineData("creditCard")]
    [InlineData("0")]
    public void Unknown_or_noncanonical_types_are_invalid(string type) => Assert.Throws<ArgumentException>(() => Create(type: type));

    [Theory]
    [InlineData("USD")]
    [InlineData("brl")]
    public void Currency_is_strictly_brl(string currency) => Assert.Throws<ArgumentException>(() => Create(currency: currency));

    [Fact]
    public void Exact_cents_and_signed_boundaries_are_supported()
    {
        foreach (var value in new[] { 0m, 10.000m, 123.45m, -123.45m, FinancialAccount.MaximumOpeningBalance, -FinancialAccount.MaximumOpeningBalance })
            Assert.Equal(value, Create(balance: value).OpeningBalance);
        foreach (var value in new[] { 10.001m, -0.001m, FinancialAccount.MaximumOpeningBalance + 0.01m, -FinancialAccount.MaximumOpeningBalance - 0.01m })
            Assert.Throws<ArgumentException>(() => Create(balance: value));
    }

    [Fact]
    public void Opening_date_may_be_today_or_retroactive_but_not_future()
    {
        Assert.Equal(Today.AddDays(-1), Create(openingDate: Today.AddDays(-1)).OpeningDate);
        Assert.Throws<ArgumentException>(() => Create(openingDate: Today.AddDays(1)));
    }

    [Fact]
    public void Owner_and_identifier_are_required()
    {
        Assert.Throws<ArgumentException>(() => FinancialAccount.Create(Guid.Empty, Guid.NewGuid(), "A", "cash", "BRL", 0, Today, Today, DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() => FinancialAccount.Create(Guid.NewGuid(), Guid.Empty, "A", "cash", "BRL", 0, Today, Today, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Immutable_financial_fields_have_no_public_setters()
    {
        foreach (var name in new[] { "Id", "UserId", "Currency", "OpeningDate", "OpeningBalance" })
            Assert.False(typeof(FinancialAccount).GetProperty(name)!.SetMethod!.IsPublic);
    }
}
