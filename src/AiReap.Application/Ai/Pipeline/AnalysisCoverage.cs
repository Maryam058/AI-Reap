using System.Text.RegularExpressions;

namespace AiReap.Application.Ai.Pipeline;

// Sections of the source each analysis request carried (SourceReferenceIndex headings; TOC lines left out).
public sealed record AnalysisPartCoverage(int Part, IReadOnlyList<string> Sections);

// Status: "complete" - every non-blank line of the source was found, in order, in the text the requests
// carried; "incomplete" - some were not (MissingSections names where they are; the analysis must not be
// presented as covering the whole source); "unverified" - the part texts are not known here (a batched
// run commits from stored responses, not from the part texts), so nothing is claimed either way.
public sealed record AnalysisCoverageReport(
    string Status,
    string Method,
    int PartCount,
    int SectionCount,
    int SectionsCovered,
    int LineCount,
    int LinesCovered,
    IReadOnlyList<string> MissingSections,
    IReadOnlyList<AnalysisPartCoverage> Parts,
    string? Note = null)
{
    public const string Complete = "complete", Incomplete = "incomplete", Unverified = "unverified";
}

// Deterministic check that an analysis actually sent every part of its source (no AI call). It does not
// judge whether the model understood what it was sent - only that nothing was left out of the requests.
// Whitespace is compared collapsed (the request budget's lossless clean-up may change it).
public static partial class AnalysisCoverage
{
    public static AnalysisCoverageReport Assess(string? sourceText, IReadOnlyList<string?> partTexts)
    {
        var source = sourceText ?? string.Empty;
        var lines = SourceReferenceIndex.Lines(source);
        var index = SourceReferenceIndex.Build(Guid.Empty, source);
        var sections = index.Sections.Where(s => !s.TableOfContents).ToList();
        var method = partTexts.Count <= 1 ? "whole" : "split";

        if (partTexts.Count == 0 || partTexts.Any(p => p is null))
        {
            return new AnalysisCoverageReport(AnalysisCoverageReport.Unverified, method, partTexts.Count, sections.Count, 0,
                lines.Count(l => l.Trim().Length > 0), 0, [], [],
                "The text each request carried is not available here, so coverage is not claimed.");
        }

        // Each part, collapsed, and where it starts in their concatenation.
        var collapsed = partTexts.Select(p => Collapse(p!)).ToList();
        var starts = new List<int>();
        var joined = new System.Text.StringBuilder();
        foreach (var part in collapsed)
        {
            starts.Add(joined.Length);
            joined.Append(part).Append('\n');
        }

        var all = joined.ToString();
        var lineFoundInPart = new int[lines.Length];
        var cursor = 0;
        int total = 0, covered = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = Collapse(lines[i]);
            lineFoundInPart[i] = -1;
            if (line.Length == 0)
            {
                continue;
            }

            total++;
            var at = all.IndexOf(line, cursor, StringComparison.Ordinal);
            if (at < 0)
            {
                at = all.IndexOf(line, StringComparison.Ordinal); // out of order: still sent
            }
            else
            {
                cursor = at + line.Length;
            }

            if (at >= 0)
            {
                covered++;
                lineFoundInPart[i] = starts.FindLastIndex(s => s <= at);
            }
        }

        var missing = new List<string>();
        var sectionsCovered = 0;
        foreach (var section in sections)
        {
            var gap = false;
            for (var l = section.Line; l < section.EndLine && !gap; l++)
            {
                gap = lines[l].Trim().Length > 0 && lineFoundInPart[l] < 0;
            }

            if (gap) missing.Add(section.HeadingLine.TrimStart('#', ' '));
            else sectionsCovered++;
        }

        // Lines outside every section (e.g. text before the first heading).
        var firstHeading = sections.Count > 0 ? sections.Min(s => s.Line) : lines.Length;
        for (var l = 0; l < firstHeading; l++)
        {
            if (lines[l].Trim().Length > 0 && lineFoundInPart[l] < 0)
            {
                missing.Insert(0, "(text before the first heading)");
                break;
            }
        }

        var parts = collapsed.Select((_, p) => new AnalysisPartCoverage(p + 1,
            sections.Where(s => lineFoundInPart[s.Line] == p).Select(s => s.HeadingLine.TrimStart('#', ' ')).ToList())).ToList();

        var status = covered == total ? AnalysisCoverageReport.Complete : AnalysisCoverageReport.Incomplete;
        return new AnalysisCoverageReport(status, method, partTexts.Count, sections.Count, sectionsCovered, total, covered, missing, parts,
            status == AnalysisCoverageReport.Incomplete
                ? $"{total - covered} of {total} non-blank source lines were not sent to the AI; the analysis does not cover the whole source."
                : null);
    }

    private static string Collapse(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
