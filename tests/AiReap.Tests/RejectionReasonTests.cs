using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// §24 — rejecting an artifact requires a reason, enforced by the API as well as the UI. Approval and
// comment-only reviews keep their existing rules, and who may reject is unchanged.
public class RejectionReasonTests : IAsyncLifetime
{
    private const int AiGenerated = 0, Approved = 3, Rejected = 4;
    private const int ReviewRejected = 1, ReviewCommentOnly = 2; // ReviewDecision

    private readonly TestHost _host = new();
    private ApiUser _ba = null!, _reviewer = null!, _dev = null!, _qa = null!, _admin = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.RegisterAsync("BusinessAnalyst");
        _reviewer = await _host.RegisterAsync("Reviewer");
        _dev = await _host.RegisterAsync("Developer");
        _qa = await _host.RegisterAsync("QA");
        _admin = await _host.RegisterAsync("Administrator");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();

    private async Task<string> NewFunctionalRequirementAsync()
    {
        var (ps, project) = await _ba.PostAsync("/api/projects", new { name = "Rejection", description = "d" });
        Assert.Equal(201, ps);
        foreach (var member in new[] { _reviewer, _dev, _qa })
        {
            Assert.InRange((await _host.AddMemberAsync(_ba, Id(project), member)).Status, 200, 201);
        }

        var (_, source) = await _ba.PostAsync($"/api/projects/{Id(project)}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve them." });
        var (gs, generated) = await _ba.PostAsync($"/api/requirement-sources/{Id(source)}/generate-requirements");
        Assert.Equal(200, gs);
        return Id(generated!["functionalRequirements"]![0]);
    }

    private static Task<(int Status, JsonNode? Body)> SetStatusAsync(ApiUser user, string id, int status, string? comment) =>
        user.SendAsync(HttpMethod.Patch, $"/api/artifacts/{id}/status", new { status, comment });

    private async Task<int> StatusOfAsync(string id) =>
        (await _ba.GetAsync($"/api/artifacts/{id}")).Body!["status"]!.GetValue<int>();

    private async Task<JsonArray> ReviewsOfAsync(string id) =>
        (await _ba.GetAsync($"/api/artifacts/{id}/reviews")).Body!.AsArray();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t\n ")]
    public async Task Rejection_without_a_reason_fails_with_400_and_changes_nothing(string? comment)
    {
        var frId = await NewFunctionalRequirementAsync();

        var (s, body) = await SetStatusAsync(_reviewer, frId, Rejected, comment);

        Assert.Equal(400, s);
        Assert.Equal("validation_failed", body!["code"]!.GetValue<string>());
        Assert.Equal(AiGenerated, await StatusOfAsync(frId));
        Assert.Empty(await ReviewsOfAsync(frId));
    }

    [Fact]
    public async Task Rejection_with_the_comment_field_omitted_fails_with_400()
    {
        var frId = await NewFunctionalRequirementAsync();

        var (s, _) = await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}/status", new { status = Rejected });

        Assert.Equal(400, s);
        Assert.Equal(AiGenerated, await StatusOfAsync(frId));
    }

    [Fact]
    public async Task Rejection_with_a_reason_succeeds_and_records_the_review()
    {
        var frId = await NewFunctionalRequirementAsync();

        var (s, body) = await SetStatusAsync(_reviewer, frId, Rejected, "Acceptance criteria are missing.");

        Assert.Equal(200, s);
        Assert.Equal(Rejected, body!["status"]!.GetValue<int>());
        var review = Assert.Single(await ReviewsOfAsync(frId))!;
        Assert.Equal(ReviewRejected, review["decision"]!.GetValue<int>());
        Assert.Equal("Acceptance criteria are missing.", review["comment"]!.GetValue<string>());
    }

    [Fact]
    public async Task Approval_without_a_reason_still_succeeds()
    {
        var frId = await NewFunctionalRequirementAsync();

        Assert.Equal(200, (await SetStatusAsync(_reviewer, frId, Approved, null)).Status);
        Assert.Equal(Approved, await StatusOfAsync(frId));
    }

    [Fact]
    public async Task Comment_only_review_is_unaffected()
    {
        var frId = await NewFunctionalRequirementAsync();

        var (s, review) = await _reviewer.PostAsync($"/api/artifacts/{frId}/reviews", new { comment = "Consider an attachment limit." });

        Assert.Equal(201, s);
        Assert.Equal(ReviewCommentOnly, review!["decision"]!.GetValue<int>());
        Assert.Equal(AiGenerated, await StatusOfAsync(frId));
    }

    [Fact]
    public async Task Developer_and_QA_still_cannot_reject_even_with_a_reason()
    {
        var frId = await NewFunctionalRequirementAsync();

        Assert.Equal(403, (await SetStatusAsync(_dev, frId, Rejected, "Not feasible.")).Status);
        Assert.Equal(403, (await SetStatusAsync(_qa, frId, Rejected, "Not testable.")).Status);
        Assert.Equal(AiGenerated, await StatusOfAsync(frId));
        Assert.Empty(await ReviewsOfAsync(frId));
    }

    [Fact]
    public async Task Reviewer_BA_and_Administrator_can_reject_with_a_reason()
    {
        foreach (var user in new[] { _reviewer, _ba, _admin })
        {
            var frId = await NewFunctionalRequirementAsync();
            Assert.Equal(200, (await SetStatusAsync(user, frId, Rejected, "Needs rework.")).Status);
            Assert.Equal(Rejected, await StatusOfAsync(frId));
        }
    }
}
