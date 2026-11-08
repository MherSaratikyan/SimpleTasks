using SimpleTasks.Identity.Data;
using SimpleTasks.Identity.Security;
using Xunit;

namespace SimpleTasks.UnitTests;

public class PasswordRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("          ")]
    [InlineData("me@example.test")]
    [InlineData(" ME@EXAMPLE.TEST ")]
    public void Rejects_weak_passwords(string? password) =>
        Assert.NotNull(PasswordRules.Problem(password, "me@example.test"));

    [Fact]
    public void Rejects_very_long_passwords() =>
        Assert.NotNull(PasswordRules.Problem(new string('a', PasswordRules.MaxLength + 1), "me@example.test"));

    [Theory]
    [InlineData("correct horse battery")]
    [InlineData("0123456789")]
    public void Accepts_reasonable_passwords(string password) =>
        Assert.Null(PasswordRules.Problem(password, "me@example.test"));
}

public class LockoutTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Locks_after_five_failures_for_fifteen_minutes()
    {
        var user = new User();
        for (var attempt = 0; attempt < Lockout.MaxFailures - 1; attempt++)
        {
            Lockout.RecordFailure(user, Now);
            Assert.False(Lockout.IsLocked(user, Now));
        }

        Lockout.RecordFailure(user, Now);

        Assert.True(Lockout.IsLocked(user, Now.AddMinutes(14)));
        Assert.False(Lockout.IsLocked(user, Now.AddMinutes(15)));
    }

    [Fact]
    public void A_success_clears_earlier_failures()
    {
        var user = new User();
        Lockout.RecordFailure(user, Now);
        Lockout.RecordFailure(user, Now);

        Lockout.Clear(user);

        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockedUntil);
    }
}

public class OneTimeTokenTests
{
    [Fact]
    public void Tokens_are_unique_and_url_safe()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => OneTimeToken.Create()).ToList();
        Assert.Equal(100, tokens.Distinct().Count());
        Assert.All(tokens, token => Assert.Matches("^[A-Za-z0-9_-]{43}$", token));
    }

    [Fact]
    public void Hashing_is_stable_and_hides_the_token()
    {
        var token = OneTimeToken.Create();
        Assert.Equal(OneTimeToken.Hash(token), OneTimeToken.Hash(token));
        Assert.DoesNotContain(token, OneTimeToken.Hash(token));
        Assert.NotEqual(OneTimeToken.Hash(token), OneTimeToken.Hash(OneTimeToken.Create()));
    }
}
