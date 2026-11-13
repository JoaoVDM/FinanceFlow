using FinanceFlow.Application.Users;
using Xunit;

namespace FinanceFlow.Application.Tests;

public sealed class AccountInputRulesTests
{
    [Fact]
    public void Password_allows_spaces_in_phrases_but_not_whitespace_only()
    {
        Assert.False(AccountInputRules.IsPasswordValid(new string(' ', 12)));
        Assert.True(AccountInputRules.IsPasswordValid("  long passphrase  "));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(11, false)]
    [InlineData(12, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void Password_boundaries_are_exact(int length, bool expected) =>
        Assert.Equal(expected, AccountInputRules.IsPasswordValid(new string('p', length)));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Fictional User", true)]
    public void Names_require_non_whitespace_content(string? name, bool expected) =>
        Assert.Equal(expected, AccountInputRules.IsDisplayNameValid(name));

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void Display_name_length_is_bounded(int length, bool expected) =>
        Assert.Equal(expected, AccountInputRules.IsDisplayNameValid(new string('n', length)));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("not-an-email", false)]
    [InlineData("person@example.invalid", true)]
    public void Email_requires_a_valid_address(string? email, bool expected) =>
        Assert.Equal(expected, AccountInputRules.IsEmailValid(email));
}
