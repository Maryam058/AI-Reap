using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using AiReap.Tests.SrsEval;
using Xunit;
using Xunit.Abstractions;

namespace AiReap.Tests;

// The cold (first after a restart) read of a large source's clarification questions, offline: a
// synthetic ~250k-character SRS and 24 questions shaped like real ones. Times each stage of the read.
public class ClarificationReadProfileTests(ITestOutputHelper output)
{
    internal static string LargeSrs(int sections = 120, int requirementsPerSection = 12)
    {
        var sb = new StringBuilder("# Hospital Triage System - Software Requirements Specification\n\n");
        var n = 0;
        for (var s = 1; s <= sections; s++)
        {
            sb.Append($"## {s}. Area {s}\n");
            for (var r = 0; r < requirementsPerSection; r++)
            {
                n++;
                sb.Append($"FR-{n:000} The triage nurse shall record observation set {n} for the patient in area {s} within the ward workflow.\n");
            }

            sb.Append($"Records of area {s} are stored in the clinical register for seven years.\n\n");
        }

        return sb.ToString();
    }

    internal static (InMemoryAiReapDb Db, RequirementSource Source, List<Artifact> Questions) Seed(string srs)
    {
        var db = new InMemoryAiReapDb();
        var project = new Project { Id = Guid.NewGuid(), Name = "Profile", CreatedByUserId = "u" };
        var source = new RequirementSource
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceType = RequirementSourceType.Paste, RawText = srs, CreatedByUserId = "u",
            CreatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.RequirementSources.Add(source);

        var index = SourceReferenceIndex.Build(source.Id, srs);
        var questions = new List<Artifact>();
        for (var i = 0; i < 24; i++)
        {
            var quote = $"record observation set {i * 40 + 7} for the patient in area";
            object payload = (i % 3) switch
            {
                // Evidence placed when made: the usual case.
                0 => new
                {
                    Question = $"What does observation set {i * 40 + 7} contain?", ClarificationStatus = "Open", Kind = "ambiguous", Impact = "x",
                    SourceEvidence = index.PhraseOccurrences(quote).Select(o => index.LocateRange(o.Start, o.End)!).Take(1).ToArray()
                },
                // Saved before evidence was stored: recovered on read.
                1 => new
                {
                    Question = $"What does 'record observation set {i * 40 + 7}' include?", Reason = "The content is not defined.", ClarificationStatus = "Open"
                },
                // Nothing verifiable: hidden.
                _ => new { Question = $"How is triage escalation {i} personalised?", Reason = "Not described.", ClarificationStatus = "Open" }
            };
            var artifact = new Artifact
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, ArtifactType = ArtifactType.ClarificationQuestion, Code = $"CQ-{i + 1:000}",
                Title = $"CQ-{i + 1:000}", Status = ArtifactStatus.AiGenerated, Origin = ArtifactOrigin.Ai, RequirementSourceId = source.Id,
                DataJson = JsonSerializer.Serialize(payload), CurrentVersion = 1, CreatedByUserId = "u", CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Artifacts.Add(artifact);
            questions.Add(artifact);
        }

        return (db, source, questions);
    }

    [Fact]
    public async Task Cold_read_stages_of_a_large_source()
    {
        var srs = LargeSrs();
        var (db, source, questions) = Seed(srs);
        output.WriteLine($"source: {srs.Length:N0} chars");

        var sw = Stopwatch.StartNew();
        var references = SourceReferenceIndex.Build(source.Id, srs);
        output.WriteLine($"SourceReferenceIndex.Build: {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        var evidence = SourceEvidenceIndex.Build(srs);
        output.WriteLine($"SourceEvidenceIndex.Build: {sw.ElapsedMilliseconds} ms");

        var locator = new ClarificationEvidenceLocator(references, evidence, srs);
        foreach (var kind in new[] { 0, 1, 2 })
        {
            sw.Restart();
            foreach (var q in questions.Where((_, i) => i % 3 == kind))
            {
                ClarificationSourceReferences.IsGrounded(ClarificationSourceReferences.WithSourceLinks(q.DataJson, source.Id, locator));
            }

            output.WriteLine($"WithSourceLinks x8 (shape {kind}): {sw.ElapsedMilliseconds} ms");
        }

        ClarificationVisibility.Results.Clear();
        sw.Restart();
        var shown = await ClarificationVisibility.ShownAsync(db, questions, CancellationToken.None);
        output.WriteLine($"ShownAsync cold: {sw.ElapsedMilliseconds} ms, {shown.Count} shown");
        Assert.Equal(16, shown.Count); // 8 with stored evidence + 8 recovered; the 8 unverifiable stay hidden
        sw.Restart();
        await ClarificationVisibility.ShownAsync(db, questions, CancellationToken.None);
        output.WriteLine($"ShownAsync warm: {sw.ElapsedMilliseconds} ms");
    }
}
