using AiReap.Api.Auth;
using Xunit;

namespace AiReap.Tests;

// Password guessing at /api/auth/login is limited (LoginThrottle): only failures count, per account and
// client address, and the block lifts when the window ends. In memory; no database.
public class LoginThrottleTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();

    [Fact]
    public void Five_failures_from_one_address_block_that_account_there_until_the_window_ends()
    {
        var throttle = new LoginThrottle(_clock);
        for (var i = 0; i < LoginThrottle.MaxFailuresPerAddress - 1; i++) throttle.RecordFailure("ba@example.com", "10.0.0.1");
        Assert.Null(throttle.RetryAfter("ba@example.com", "10.0.0.1"));

        throttle.RecordFailure("BA@example.com ", "10.0.0.1"); // same account, however it is typed
        Assert.NotNull(throttle.RetryAfter("ba@example.com", "10.0.0.1"));
        Assert.Null(throttle.RetryAfter("ba@example.com", "10.0.0.2"));
        Assert.Null(throttle.RetryAfter("qa@example.com", "10.0.0.1"));

        _clock.Now += LoginThrottle.Window;
        Assert.Null(throttle.RetryAfter("ba@example.com", "10.0.0.1"));
    }

    [Fact]
    public void Guessing_one_account_from_many_addresses_is_limited_too()
    {
        var throttle = new LoginThrottle(_clock);
        for (var i = 0; i < LoginThrottle.MaxFailuresPerAccount; i++) throttle.RecordFailure("admin@example.com", $"10.0.1.{i}");

        Assert.NotNull(throttle.RetryAfter("admin@example.com", "10.0.9.9"));
    }

    [Fact]
    public void A_successful_sign_in_clears_the_count_for_that_address()
    {
        var throttle = new LoginThrottle(_clock);
        for (var i = 0; i < LoginThrottle.MaxFailuresPerAddress - 1; i++) throttle.RecordFailure("ba@example.com", "10.0.0.1");
        throttle.RecordSuccess("ba@example.com", "10.0.0.1");
        throttle.RecordFailure("ba@example.com", "10.0.0.1");

        Assert.Null(throttle.RetryAfter("ba@example.com", "10.0.0.1"));
    }
}
