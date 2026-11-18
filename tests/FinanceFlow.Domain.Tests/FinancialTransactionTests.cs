using FinanceFlow.Domain.Accounts;
using FinanceFlow.Domain.Categories;
using FinanceFlow.Domain.Transactions;
using Xunit;

namespace FinanceFlow.Domain.Tests;

public sealed class FinancialTransactionTests
{
    private static readonly DateOnly Today = new(2026, 11, 17);
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.FromHours(-3));

    [Theory]
    [InlineData("income", "0.01")]
    [InlineData("expense", "12.34")]
    [InlineData("income", "999999999999.99")]
    [InlineData("expense", "10.000")]
    public void Creation_preserves_positive_exact_amount_and_server_metadata(string kind, string amount)
    {
        var transaction = Create(kind, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));

        Assert.NotEqual(Guid.Empty, transaction.Id);
        Assert.Equal(kind, transaction.Kind);
        Assert.True(transaction.Amount > 0);
        Assert.Equal(Today, transaction.OccurredOn);
        Assert.Equal(Now.ToUniversalTime(), transaction.CreatedAtUtc);
        Assert.Equal(TimeSpan.Zero, transaction.CreatedAtUtc.Offset);
        Assert.Null(transaction.UpdatedAtUtc);
        Assert.False(transaction.IsDeleted);
        Assert.Null(transaction.DeletedAtUtc);
        Assert.Empty(transaction.RowVersion);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("1000000000000")]
    [InlineData("10.001")]
    [InlineData("0.0000000000000000000000000001")]
    public void Nonpositive_out_of_range_or_fractional_values_are_rejected(string amount) =>
        Assert.Throws<ArgumentException>(() => Create("income", decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("Income")]
    [InlineData("expense ")]
    [InlineData("transfer")]
    [InlineData("")]
    public void Kind_codes_are_exact(string kind) => Assert.Throws<ArgumentException>(() => Create(kind, 1m));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Every_identifier_is_required(int index)
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        ids[index] = Guid.Empty;
        Assert.Throws<ArgumentException>(() => FinancialTransaction.Create(ids[0], ids[1], ids[2], ids[3], "income", 1m, Today, null, Today, Now));
    }

    [Fact]
    public void Future_dates_and_long_descriptions_are_rejected_but_text_is_otherwise_preserved()
    {
        Assert.Throws<ArgumentException>(() => Create("income", 1m, Today.AddDays(1)));
        Assert.Throws<ArgumentException>(() => Create(description: new string('a', 501)));
        Assert.Equal(new string('a', 500), Create(description: new string('a', 500)).Description);
        Assert.Equal(" unchanged \n", Create(description: " unchanged \n").Description);
    }

    [Fact]
    public void Links_require_matching_owner_kind_activity_and_opening_date()
    {
        var owner = Guid.NewGuid();
        var opening = Today.AddDays(-5);
        var account = FinancialAccount.Create(Guid.NewGuid(), owner, "Account", "cash", "BRL", -10m, opening, Today, Now);
        var category = Category.Create(Guid.NewGuid(), owner, "Category", "income", Now);
        var transaction = FinancialTransaction.Create(Guid.NewGuid(), owner, account.Id, category.Id, "income", 1m, opening, null, Today, Now);

        Assert.Equal(TransactionLinkStatus.Valid, transaction.CheckLinks(account, category));
        account.SetActivity(false, Now);
        Assert.Equal(TransactionLinkStatus.Inactive, transaction.CheckLinks(account, category));
        account.SetActivity(true, Now);
        category.SetActivity(false, Now);
        Assert.Equal(TransactionLinkStatus.Inactive, transaction.CheckLinks(account, category));
        category.SetActivity(true, Now);
        var tooEarly = FinancialTransaction.Create(Guid.NewGuid(), owner, account.Id, category.Id, "income", 1m, opening.AddDays(-1), null, Today, Now);
        Assert.Equal(TransactionLinkStatus.Invalid, tooEarly.CheckLinks(account, category));
        var expense = FinancialTransaction.Create(Guid.NewGuid(), owner, account.Id, category.Id, "expense", 1m, opening, null, Today, Now);
        Assert.Equal(TransactionLinkStatus.Invalid, expense.CheckLinks(account, category));
        var foreign = FinancialTransaction.Create(Guid.NewGuid(), Guid.NewGuid(), account.Id, category.Id, "income", 1m, opening, null, Today, Now);
        Assert.Equal(TransactionLinkStatus.Invalid, foreign.CheckLinks(account, category));
    }

    private static FinancialTransaction Create(string kind = "income", decimal amount = 1m, DateOnly? date = null, string? description = null) =>
        FinancialTransaction.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), kind, amount, date ?? Today, description, Today, Now);
}
