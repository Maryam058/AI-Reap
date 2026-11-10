using System.Collections.Concurrent;

namespace AiReap.Api.Auth;

// Limits password guessing at /api/auth/login. Only failed attempts count, so normal sign-ins are never
// slowed. Within a 15-minute window an account accepts 5 failures from one client address, and 25 in
// all (so guessing from many addresses is limited too); past either, attempts for that account are
// refused with 429 until the window ends, before the password is checked. A successful sign-in clears
// that address's count. In memory only: nothing is written to the database, and Identity's lockout is
// not used because LockoutEnd already means "deactivated by an administrator" here (UsersController).
public sealed class LoginThrottle
{
    public const int MaxFailuresPerAddress = 5;
    public const int MaxFailuresPerAccount = 25;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private const int MaxTrackedKeys = 50_000;

    private readonly ConcurrentDictionary<string, Counter> _counters = new();
    private readonly TimeProvider _clock;

    public LoginThrottle(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    private sealed record Counter(int Failures, DateTimeOffset WindowStart);

    // How long until a blocked account may be tried again from this address; null when not blocked.
    public TimeSpan? RetryAfter(string email, string? address)
    {
        var now = _clock.GetUtcNow();
        TimeSpan? Blocked(string key, int max) =>
            _counters.TryGetValue(key, out var c) && now - c.WindowStart < Window && c.Failures >= max ? c.WindowStart + Window - now : null;

        var perAddress = Blocked(AddressKey(email, address), MaxFailuresPerAddress);
        var perAccount = Blocked(AccountKey(email), MaxFailuresPerAccount);
        return perAddress is null ? perAccount : perAccount is null ? perAddress : (perAddress > perAccount ? perAddress : perAccount);
    }

    public void RecordFailure(string email, string? address)
    {
        if (_counters.Count >= MaxTrackedKeys)
        {
            Prune();
        }

        var now = _clock.GetUtcNow();
        foreach (var key in new[] { AddressKey(email, address), AccountKey(email) })
        {
            _counters.AddOrUpdate(key, _ => new Counter(1, now),
                (_, c) => now - c.WindowStart >= Window ? new Counter(1, now) : c with { Failures = c.Failures + 1 });
        }
    }

    public void RecordSuccess(string email, string? address) => _counters.TryRemove(AddressKey(email, address), out _);

    private void Prune()
    {
        var now = _clock.GetUtcNow();
        foreach (var (key, counter) in _counters)
        {
            if (now - counter.WindowStart >= Window) _counters.TryRemove(key, out _);
        }
    }

    private static string AccountKey(string email) => "account:" + Normalize(email);

    private static string AddressKey(string email, string? address) => $"address:{address ?? "unknown"}:{Normalize(email)}";

    private static string Normalize(string email) => (email ?? string.Empty).Trim().ToUpperInvariant();
}
