using AiReap.Application.Ai;
using Xunit;

namespace AiReap.Tests;

// PromptBudgetReducer's lossless clean-up (the step an over-budget prompt gets before any passage
// is omitted, and the only change ChunkPlanner.SendsWhole allows): it may drop page headers/footers
// and copies that add nothing, but never a repeated requirement statement, requirement ID or table
// cell. Each case builds a prompt that is over budget only because of blank-line padding, with a
// budget that fits exactly the expected clean-up, so the result must equal it with nothing omitted.
public class PromptBudgetReducerCleanupTests
{
    private const string System = "s";

    private static PromptReduction Cleanup(string[] blocks, string expected)
    {
        var budget = PromptBudgetReducer.EstimateTokens(System) + PromptBudgetReducer.RequestOverheadTokens
            + PromptBudgetReducer.EstimateTokens(expected);
        var padded = string.Join("\n\n" + new string('\n', 400), blocks.Select(b => b + "    "));

        var fit = PromptBudgetReducer.Fit(System, padded, budget);

        Assert.True(fit.WasReduced); // the padding put it over budget, so clean-up ran
        Assert.True(fit.Fits);
        Assert.Equal(0, fit.OmittedPassages);
        return fit;
    }

    private static void AssertKeptVerbatim(params string[] blocks)
    {
        var expected = string.Join("\n\n", blocks);
        Assert.Equal(expected, Cleanup(blocks, expected).UserPrompt);
    }

    [Fact]
    public void Repeated_requirement_statement_is_kept_in_every_section()
    {
        AssertKeptVerbatim(
            "1 Leave",
            "The system shall log the event.",
            "2 Expenses",
            "The system shall log the event.",
            "3 Travel",
            "The system shall log the event.",
            "Approvals\nThe system shall log the event.\nManagers approve requests.");
    }

    [Fact]
    public void Short_table_cell_values_are_kept_however_often_they_repeat()
    {
        AssertKeptVerbatim(
            "Role\nCan approve\nAdmin\nYes\nManager\nYes\nEmployee\nNo\nAuditor\nYes",
            "Yes",
            "High",
            "Yes",
            "High",
            "Yes",
            "High");
    }

    [Fact]
    public void Multi_word_table_cells_inside_a_block_are_kept()
    {
        AssertKeptVerbatim(
            "Department\nHuman Resources Department\nOwner\nHuman Resources Department\nBudget holder\nHuman Resources Department\nEnd of table");
    }

    [Fact]
    public void Repeated_table_rows_are_kept_even_when_adjacent()
    {
        AssertKeptVerbatim(
            "| Role | Access | Notes |",
            "| Admin | Full access | Granted by IT department |",
            "| Admin | Full access | Granted by IT department |",
            "| Admin | Full access | Granted by IT department |");
    }

    [Fact]
    public void Repeated_line_citing_a_requirement_id_is_kept()
    {
        AssertKeptVerbatim(
            "See FR-012 for details",
            "Background on leave types.",
            "See FR-012 for details",
            "Background on expense types.",
            "See FR-012 for details");
    }

    [Fact]
    public void Repeated_page_header_is_kept_once()
    {
        const string header = "ACME Corp - Internal - Confidential";
        string[] blocks =
        [
            "Intro text describing the overall scope.",
            header,
            "Users shall log in.",
            header,
            "Managers shall approve leave.",
            header,
            "Employees shall submit requests."
        ];
        var expected = string.Join("\n\n",
            "Intro text describing the overall scope.", header, "Users shall log in.",
            "Managers shall approve leave.", "Employees shall submit requests.");

        Assert.Equal(expected, Cleanup(blocks, expected).UserPrompt);
    }

    [Fact]
    public void Repeated_narrative_paragraph_without_requirement_content_is_kept_once()
    {
        const string background = "Background: the company has offices in many cities.";
        string[] blocks = [background, "Users shall log in.", background];
        var expected = string.Join("\n\n", background, "Users shall log in.");

        Assert.Equal(expected, Cleanup(blocks, expected).UserPrompt);
    }

    [Fact]
    public void Requirement_paragraph_repeated_directly_after_itself_is_collapsed()
    {
        const string requirement = "Managers must approve each leave request within 3 working days.";
        string[] blocks = [requirement, requirement, requirement, "Employees shall submit requests online."];
        var expected = string.Join("\n\n", requirement, "Employees shall submit requests online.");

        Assert.Equal(expected, Cleanup(blocks, expected).UserPrompt);
    }
}
