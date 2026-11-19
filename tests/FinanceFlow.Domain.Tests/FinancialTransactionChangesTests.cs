using FinanceFlow.Domain.Accounts;
using FinanceFlow.Domain.Categories;
using FinanceFlow.Domain.Transactions;
using Xunit;

namespace FinanceFlow.Domain.Tests;

public sealed class FinancialTransactionChangesTests
{
    private static readonly DateOnly Today = new(2026, 11, 19);
    private static readonly DateTimeOffset Created = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Updated = Created.AddDays(1).ToOffset(TimeSpan.FromHours(-3));

    [Theory]
    [InlineData("income", "200")]
    [InlineData("expense", "150")]
    public void Full_update_changes_financial_fields_without_touching_identity_creation_or_deletion(string kind, string amountText)
    {
        var (transaction, account, category) = State();
        var target = Account(transaction.UserId);
        var targetCategory = Category.Create(Guid.NewGuid(), transaction.UserId, "New category", kind, Created);
        var amount = decimal.Parse(amountText, System.Globalization.CultureInfo.InvariantCulture);
        var details = new TransactionDetails(target.Id, targetCategory.Id, kind, amount, new(2026, 11, 2), null);
        var id = transaction.Id;

        var result = transaction.UpdateDetails(details, account, target, targetCategory, Today, Updated);

        Assert.Equal(TransactionLinkStatus.Valid, result);
        Assert.Equal(id, transaction.Id);
        Assert.Equal(account.UserId, transaction.UserId);
        Assert.Equal(Created, transaction.CreatedAtUtc);
        Assert.Equal(target.Id, transaction.AccountId);
        Assert.Equal(targetCategory.Id, transaction.CategoryId);
        Assert.Equal(kind, transaction.Kind);
        Assert.Equal(amount, transaction.Amount);
        Assert.Equal(details.OccurredOn, transaction.OccurredOn);
        Assert.Null(transaction.Description);
        Assert.False(transaction.IsDeleted);
        Assert.Null(transaction.DeletedAtUtc);
        Assert.Equal(Updated.ToUniversalTime(), transaction.UpdatedAtUtc);
        Assert.Equal(TimeSpan.Zero, transaction.UpdatedAtUtc!.Value.Offset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Descriptive_and_no_op_updates_are_allowed_with_both_references_inactive(bool noOp)
    {
        var (transaction, account, category) = State();
        account.SetActivity(false, Updated);
        category.SetActivity(false, Updated);
        var details = Details(transaction) with { Description = noOp ? transaction.Description : "Corrected" };

        Assert.Equal(TransactionLinkStatus.Valid, transaction.UpdateDetails(details, account, account, category, Today, Updated));
        Assert.Equal(details.Description, transaction.Description);
        Assert.Equal(Updated.ToUniversalTime(), transaction.UpdatedAtUtc);
        Assert.Equal(100m, transaction.Amount);
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("date")]
    [InlineData("account")]
    [InlineData("category")]
    [InlineData("kind")]
    public void Inactive_source_account_rejects_every_non_descriptive_change_without_partial_mutation(string field)
    {
        var (transaction, account, category) = State();
        account.SetActivity(false, Updated);
        var target = field == "account" ? Account(transaction.UserId) : account;
        var targetCategory = field is "category" or "kind"
            ? Category.Create(Guid.NewGuid(), transaction.UserId, "New", field == "kind" ? "expense" : "income", Created) : category;
        var details = Details(transaction) with
        {
            AccountId = target.Id, CategoryId = targetCategory.Id, Kind = targetCategory.Kind,
            Amount = field == "amount" ? 200m : 100m,
            OccurredOn = field == "date" ? new(2026, 11, 2) : transaction.OccurredOn
        };
        Assert.Equal(TransactionLinkStatus.Inactive, transaction.UpdateDetails(details, account, target, targetCategory, Today, Updated));
        Assert.Equal(100m, transaction.Amount);
        Assert.Equal(account.Id, transaction.AccountId);
        Assert.Equal(category.Id, transaction.CategoryId);
        Assert.Equal("income", transaction.Kind);
        Assert.Null(transaction.UpdatedAtUtc);
    }

    [Fact]
    public void New_links_require_activity_but_unchanged_inactive_category_can_remain_linked()
    {
        var (transaction, account, category) = State();
        category.SetActivity(false, Updated);
        Assert.Equal(TransactionLinkStatus.Valid, transaction.UpdateDetails(Details(transaction) with { Amount = 200m }, account, account, category, Today, Updated));
        var inactiveCategory = Category.Create(Guid.NewGuid(), transaction.UserId, "Inactive", "income", Created);
        inactiveCategory.SetActivity(false, Updated);
        Assert.Equal(TransactionLinkStatus.Inactive, transaction.UpdateDetails(Details(transaction) with { CategoryId = inactiveCategory.Id }, account, account, inactiveCategory, Today, Updated));
        var inactiveAccount = Account(transaction.UserId);
        inactiveAccount.SetActivity(false, Updated);
        Assert.Equal(TransactionLinkStatus.Inactive, transaction.UpdateDetails(Details(transaction) with { AccountId = inactiveAccount.Id }, account, inactiveAccount, category, Today, Updated));
        Assert.Equal(200m, transaction.Amount);
        Assert.Equal(account.Id, transaction.AccountId);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("10.001")]
    [InlineData("1000000000000")]
    public void Invalid_amounts_never_mutate_the_transaction(string amountText)
    {
        var (transaction, account, category) = State();
        var details = Details(transaction) with { Amount = decimal.Parse(amountText, System.Globalization.CultureInfo.InvariantCulture) };
        Assert.Throws<ArgumentException>(() => transaction.UpdateDetails(details, account, account, category, Today, Updated));
        Assert.Equal(100m, transaction.Amount);
        Assert.Null(transaction.UpdatedAtUtc);
    }

    [Fact]
    public void Invalid_dates_kind_owner_and_description_preserve_the_original_state()
    {
        var (transaction, account, category) = State();
        foreach (var details in new[]
        {
            Details(transaction) with { Kind = "Income" }, Details(transaction) with { OccurredOn = Today.AddDays(1) },
            Details(transaction) with { Description = new string('x', 501) }, Details(transaction) with { AccountId = Guid.Empty }
        })
        {
            Assert.Throws<ArgumentException>(() => transaction.UpdateDetails(details, account, account, category, Today, Updated));
        }
        var laterOpening = Account(transaction.UserId, new(2026, 11, 2));
        Assert.Equal(TransactionLinkStatus.Invalid, transaction.UpdateDetails(Details(transaction) with { AccountId = laterOpening.Id }, account, laterOpening, category, Today, Updated));
        Assert.Equal(TransactionLinkStatus.Invalid, transaction.UpdateDetails(Details(transaction) with { Kind = "expense" }, account, account, category, Today, Updated));
        var foreignAccount = Account(Guid.NewGuid());
        Assert.Equal(TransactionLinkStatus.Invalid, transaction.UpdateDetails(Details(transaction) with { AccountId = foreignAccount.Id }, account, foreignAccount, category, Today, Updated));
        Assert.Null(transaction.UpdatedAtUtc);
        Assert.Equal("Original", transaction.Description);
    }

    [Fact]
    public void Cent_boundaries_are_accepted_and_delete_preserves_all_financial_data_and_cannot_repeat()
    {
        var (transaction, account, category) = State();
        foreach (var amount in new[] { 0.01m, 10.000m, 999999999999.99m })
        {
            Assert.Equal(TransactionLinkStatus.Valid, transaction.UpdateDetails(Details(transaction) with { Amount = amount, Description = new string('x', 500) }, account, account, category, Today, Updated));
        }
        var original = Details(transaction);
        transaction.Delete(Updated);
        Assert.True(transaction.IsDeleted);
        Assert.Equal(Updated.ToUniversalTime(), transaction.DeletedAtUtc);
        Assert.Equal(transaction.DeletedAtUtc, transaction.UpdatedAtUtc);
        Assert.Equal(original, Details(transaction));
        Assert.Equal(Created, transaction.CreatedAtUtc);
        Assert.Throws<InvalidOperationException>(() => transaction.Delete(Updated.AddDays(1)));
        Assert.Throws<InvalidOperationException>(() => transaction.UpdateDetails(original, account, account, category, Today, Updated));
        Assert.Equal(Updated.ToUniversalTime(), transaction.DeletedAtUtc);
    }

    private static TransactionDetails Details(FinancialTransaction value) => new(value.AccountId, value.CategoryId, value.Kind, value.Amount, value.OccurredOn, value.Description);
    private static FinancialAccount Account(Guid userId, DateOnly? opening = null) => FinancialAccount.Create(Guid.NewGuid(), userId,
        "Account", "cash", "BRL", 0m, opening ?? new(2026, 11, 1), Today, Created);
    private static (FinancialTransaction Transaction, FinancialAccount Account, Category Category) State()
    {
        var owner = Guid.NewGuid();
        var account = Account(owner);
        var category = Category.Create(Guid.NewGuid(), owner, "Category", "income", Created);
        var transaction = FinancialTransaction.Create(Guid.NewGuid(), owner, account.Id, category.Id, "income", 100m, new(2026, 11, 1), "Original", Today, Created);
        return (transaction, account, category);
    }
}
