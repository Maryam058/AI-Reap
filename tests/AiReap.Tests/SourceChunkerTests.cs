using System.Text;
using AiReap.Application.Ai;
using Xunit;

namespace AiReap.Tests;

// SourceChunker: splits an over-budget requirement source at logical boundaries without losing
// anything; AiResultMerge: deterministic de-duplication of the parts' results.
public class SourceChunkerTests
{
    private static int Tokens(string text) => PromptBudgetReducer.EstimateTokens(text);

    // Every non-blank line of the source, in order, is in exactly one part.
    private static void AssertLossless(string source, IReadOnlyList<string> parts)
    {
        static IEnumerable<string> Words(string text) => text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(Words(source), parts.SelectMany(Words));
    }

    [Fact]
    public void Source_within_budget_is_one_unchanged_part()
    {
        const string source = "# Leave\n\nEmployees request leave.\n\n- Managers approve.";

        Assert.Equal([source], SourceChunker.Split(source, 1000));
    }

    [Fact]
    public void Parts_start_at_section_headings_and_never_end_on_one()
    {
        var sb = new StringBuilder();
        for (var s = 1; s <= 12; s++)
        {
            sb.Append($"## {s}. Area {s}\n\n");
            sb.Append(string.Concat(Enumerable.Repeat($"The system must handle area {s} requests promptly and correctly. ", 6))).Append("\n\n");
            sb.Append($"- Employees use area {s}.\n- Managers review area {s}.\n\n");
        }

        var source = sb.ToString().TrimEnd();
        var parts = SourceChunker.Split(source, 400);

        Assert.True(parts.Count > 2);
        Assert.All(parts, p => Assert.True(Tokens(p) <= 400, $"part of ~{Tokens(p)} tokens exceeds 400"));
        Assert.All(parts, p => Assert.StartsWith("## ", p));
        Assert.All(parts, p => Assert.DoesNotMatch(@"## \d+\. Area \d+$", p));
        AssertLossless(source, parts);
    }

    [Fact]
    public void Paragraphs_and_list_items_are_not_cut_when_they_fit()
    {
        var paragraphs = Enumerable.Range(1, 30).Select(i =>
            $"Paragraph {i}: the approval workflow must notify the employee and the manager when the request changes state.").ToList();
        var numbered = Enumerable.Range(1, 30).Select(i => $"{i}. REQ-{i}: The system shall keep leave request {i} for seven years.").ToList();
        var source = string.Join("\n\n", paragraphs) + "\n\n" + string.Join("\n", numbered);

        var parts = SourceChunker.Split(source, 300);

        Assert.True(parts.Count > 1);
        foreach (var line in paragraphs.Concat(numbered))
        {
            Assert.Single(parts, p => p.Contains(line));
        }

        AssertLossless(source, parts);
    }

    [Fact]
    public void An_oversized_paragraph_is_split_at_sentences_and_nothing_is_lost()
    {
        var sentences = Enumerable.Range(1, 80).Select(i => $"Sentence {i} states that managers must approve leave request type {i} within three days.").ToList();
        var source = "Intro.\n\n" + string.Join(" ", sentences);

        var parts = SourceChunker.Split(source, 250);

        Assert.All(parts, p => Assert.True(Tokens(p) <= 250));
        foreach (var sentence in sentences)
        {
            Assert.Single(parts, p => p.Contains(sentence));
        }

        AssertLossless(source, parts);
    }

    [Fact]
    public void A_single_enormous_sentence_is_split_at_words_as_a_last_resort()
    {
        var source = string.Join(" ", Enumerable.Range(1, 2000).Select(i => $"word{i}"));

        var parts = SourceChunker.Split(source, 200);

        Assert.True(parts.Count > 1);
        Assert.All(parts, p => Assert.True(Tokens(p) <= 200));
        AssertLossless(source, parts);
    }

    [Fact]
    public void Non_ascii_text_is_measured_with_the_budget_estimate()
    {
        var source = string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"Anforderung {i}: Mitarbeiter müssen Urlaub über das Portal beantragen – für Genehmigungen gilt §{i}."));

        var parts = SourceChunker.Split(source, 150);

        Assert.All(parts, p => Assert.True(Tokens(p) <= 150));
        AssertLossless(source, parts);
    }

    [Fact]
    public void Merge_keeps_first_wording_and_source_order_and_ignores_case_articles_punctuation_and_plurals()
    {
        var merged = AiResultMerge.DistinctTexts(["Employee", "Manager", "employees", "The Manager.", "HR Administrator", "  ", "HR administrators"]);

        Assert.Equal(["Employee", "Manager", "HR Administrator"], merged);
    }

    private sealed class Question(string topic, string question, string reason)
    {
        public string Topic { get; } = topic;
        public string Text { get; } = question;
        public string Reason { get; set; } = reason;
    }

    [Fact]
    public void Merge_removes_repeated_questions_but_keeps_different_questions_on_the_same_topic()
    {
        var questions = new[]
        {
            new Question("Carry-over", "Can unused leave carry over to next year?", ""),
            new Question("Approval limit", "How many days can a manager approve?", "Not stated"),
            new Question("carry over", "Can unused leave carry over to the next year?", "Unclear"),
            new Question("Approval limit", "Who approves requests longer than ten days?", "Not stated"),
            new Question("Other topic", "can unused leave carry over to next year", "dup by question")
        };

        var merged = AiResultMerge.DistinctQuestions(questions, q => q.Topic, q => q.Text, q => q.Reason, (q, r) => q.Reason = r);

        Assert.Equal(
            ["Can unused leave carry over to next year?", "How many days can a manager approve?", "Who approves requests longer than ten days?"],
            merged.Select(q => q.Text));
        Assert.Equal("Unclear", merged[0].Reason); // filled from the duplicate
    }
}
