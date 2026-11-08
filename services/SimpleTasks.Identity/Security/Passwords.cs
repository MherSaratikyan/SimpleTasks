using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using SimpleTasks.Identity.Data;

namespace SimpleTasks.Identity.Security;

/// <summary>Link tokens for invites and resets. Only a SHA-256 hash is stored, so a database leak can't be used to log in.</summary>
public static class OneTimeToken
{
    public static string Create() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public static class PasswordRules
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    /// <summary>Returns a message to show the user, or null when the password is acceptable.</summary>
    public static string? Problem(string? password, string email)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
        {
            return $"Use at least {MinLength} characters.";
        }
        if (password.Length > MaxLength)
        {
            return $"Use {MaxLength} characters or fewer.";
        }
        if (string.IsNullOrWhiteSpace(password))
        {
            return "The password can't be only spaces.";
        }
        if (string.Equals(password.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "The password can't be your email address.";
        }
        return null;
    }
}

public static class Lockout
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(15);

    public static bool IsLocked(User user, DateTime now) => user.LockedUntil > now;

    public static void RecordFailure(User user, DateTime now)
    {
        user.FailedLoginCount++;
        if (user.FailedLoginCount >= MaxFailures)
        {
            user.LockedUntil = now + Duration;
            user.FailedLoginCount = 0;
        }
    }

    public static void Clear(User user)
    {
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
    }
}
