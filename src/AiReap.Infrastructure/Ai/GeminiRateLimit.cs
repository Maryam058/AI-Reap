using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiReap.Infrastructure.Ai;

// Reads the back-off hints Google attaches to a 429 RESOURCE_EXHAUSTED response, shared by the chat
// and embeddings clients. Only durations and quota ids are extracted - never anything that could
// carry the key or prompt text.
internal static partial class GeminiRateLimit
{
    // Anything beyond this is not a meaningful "try again in" hint for a user.
    private static readonly TimeSpan MaxHint = TimeSpan.FromDays(1);

    // Retry-After header (seconds or HTTP date) first, then google.rpc.RetryInfo in the error details:
    // {"@type": ".../google.rpc.RetryInfo", "retryDelay": "17s"}
    public static TimeSpan? RetryDelay(HttpResponseHeaders headers, IEnumerable<JsonElement>? details, DateTimeOffset now)
    {
        if (headers.RetryAfter is { } header)
        {
            if (header.Delta is { } delta) return Clamp(delta);
            if (header.Date is { } date) return Clamp(date - now);
        }

        foreach (var detail in details ?? [])
        {
            if (detail.ValueKind == JsonValueKind.Object
                && detail.TryGetProperty("retryDelay", out var delay) && delay.ValueKind == JsonValueKind.String
                && delay.GetString() is { } value && value.EndsWith('s')
                && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return Clamp(TimeSpan.FromSeconds(seconds));
            }
        }

        return null;
    }

    // True when the 429 is a quota that will not recover within seconds: a per-day limit in
    // google.rpc.QuotaFailure (quotaId "GenerateRequestsPerDayPerProjectPerModel-FreeTier"), or a
    // limit of 0 (the model has no quota at all for this key/tier). Per-minute limits return false.
    public static bool IsLongWindowQuota(IEnumerable<JsonElement>? details, string? message)
    {
        if (message is not null && ZeroLimitPattern().IsMatch(message))
        {
            return true;
        }

        foreach (var detail in details ?? [])
        {
            if (detail.ValueKind != JsonValueKind.Object
                || !detail.TryGetProperty("violations", out var violations) || violations.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var violation in violations.EnumerateArray())
            {
                if (violation.ValueKind != JsonValueKind.Object) continue;
                foreach (var field in new[] { "quotaId", "quotaMetric" })
                {
                    if (violation.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                        && value.GetString() is { } text
                        && (text.Contains("PerDay", StringComparison.OrdinalIgnoreCase) || text.Contains("per_day", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }

                if (violation.TryGetProperty("quotaValue", out var quotaValue)
                    && ((quotaValue.ValueKind == JsonValueKind.String && quotaValue.GetString() == "0")
                        || (quotaValue.ValueKind == JsonValueKind.Number && quotaValue.TryGetInt64(out var n) && n == 0)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // "Quota exceeded for metric: ..., limit: 0, model: ..." - but not "limit: 50".
    [GeneratedRegex(@"\blimit:\s*0(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex ZeroLimitPattern();

    private static TimeSpan? Clamp(TimeSpan delay) =>
        delay <= TimeSpan.Zero ? null : delay > MaxHint ? MaxHint : delay;
}
