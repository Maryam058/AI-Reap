using System.Text.RegularExpressions;
using AiReap.Tests.SrsEval;
using Xunit;

namespace AiReap.Tests;

// Phase 0 / T0 — every golden fixture is synthetic and carries nothing secret: no API key or token
// pattern, no connection string, no URL, and no email address outside the reserved example.test
// domain. Scans the fixture files as copied to the test output.
[Trait("Category", "Phase0")]
public partial class FixtureSafetyTests
{
    private static IReadOnlyList<string> FixtureFiles() =>
        Directory.GetFiles(GoldenFixture.Directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void Fixture_folder_is_present_and_complete()
    {
        var names = FixtureFiles().Select(f => Path.GetRelativePath(GoldenFixture.Directory, f).Replace('\\', '/')).ToList();

        Assert.Contains("README.md", names);
        Assert.Contains("clinic-booking.srs.md", names);
        Assert.Contains("clinic-booking.expected.json", names);
        Assert.Contains("vague-request.srs.txt", names);
        Assert.Contains("vague-request.expected.json", names);
        Assert.Equal(6, names.Count(n => n.StartsWith("candidates/", StringComparison.Ordinal)));
    }

    [Fact]
    public void Fixtures_contain_no_key_token_or_connection_string_patterns()
    {
        var hits = FixtureFiles()
            .SelectMany(f => SecretPatterns.Select(p => (File: Path.GetFileName(f), Pattern: p, Match: p.Match(File.ReadAllText(f)))))
            .Where(x => x.Match.Success)
            .Select(x => $"{x.File}: matches {x.Pattern}")
            .ToList();

        Assert.Empty(hits);
    }

    [Fact]
    public void Fixtures_use_only_example_test_email_addresses_and_no_urls()
    {
        foreach (var file in FixtureFiles())
        {
            var text = File.ReadAllText(file);
            Assert.All(Email().Matches(text), m => Assert.EndsWith("@example.test", m.Value, StringComparison.OrdinalIgnoreCase));
            Assert.False(Url().IsMatch(text), $"{Path.GetFileName(file)} contains a URL");
        }
    }

    [Fact]
    public void Every_fixture_is_marked_synthetic()
    {
        foreach (var file in FixtureFiles())
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            if (name.Contains(".srs.", StringComparison.Ordinal))
            {
                Assert.StartsWith("SYNTHETIC TEST FIXTURE", text);
            }
            else if (name.EndsWith(".expected.json", StringComparison.Ordinal))
            {
                Assert.Contains("\"synthetic\": true", text);
            }
            else if (name.EndsWith(".json", StringComparison.Ordinal))
            {
                Assert.Contains("\"_note\": \"SYNTHETIC", text);
            }
            else
            {
                Assert.Contains("synthetic", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static readonly Regex[] SecretPatterns =
    [
        GoogleKey(), GroqKey(), AnthropicKey(), GenericSecretKey(), BearerToken(), PrivateKey(), ConnectionString(), PasswordAssignment()
    ];

    [GeneratedRegex(@"AIza[0-9A-Za-z_\-]{20,}")] private static partial Regex GoogleKey();
    [GeneratedRegex(@"gsk_[0-9A-Za-z]{16,}")] private static partial Regex GroqKey();
    [GeneratedRegex(@"sk-ant-[0-9A-Za-z_\-]{8,}")] private static partial Regex AnthropicKey();
    [GeneratedRegex(@"\bsk-[0-9A-Za-z]{16,}")] private static partial Regex GenericSecretKey();
    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._\-]{16,}")] private static partial Regex BearerToken();
    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")] private static partial Regex PrivateKey();
    [GeneratedRegex(@"(?i)\b(server|data source|initial catalog|user id)\s*=")] private static partial Regex ConnectionString();
    [GeneratedRegex(@"(?i)\b(password|pwd|apikey|api_key|secret)\s*[=:]\s*\S")] private static partial Regex PasswordAssignment();
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")] private static partial Regex Email();
    [GeneratedRegex(@"(?i)\bhttps?://")] private static partial Regex Url();
}
