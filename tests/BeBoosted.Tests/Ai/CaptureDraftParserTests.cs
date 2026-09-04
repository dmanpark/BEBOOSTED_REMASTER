using BeBoosted.Application.Ai;

namespace BeBoosted.Tests.Ai;

public sealed class CaptureDraftParserTests
{
    [Fact]
    public void ParsesAWellFormedResponse()
    {
        var drafts = CaptureDraftParser.Parse("""
            {"tasks": [
              {"title": "Finish DECA presentation", "estimated_minutes": 90,
               "deadline": "2026-09-04", "project": "Schoolwork"}
            ]}
            """);

        var draft = Assert.Single(drafts);
        Assert.Equal("Finish DECA presentation", draft.Title);
        Assert.Equal(90, draft.EstimatedMinutes);
        Assert.Equal(new DateOnly(2026, 9, 4), draft.Deadline);
        Assert.Equal("Schoolwork", draft.ProjectName);
    }

    [Fact]
    public void AnEmptyTaskArray_IsAValidAnswer()
        => Assert.Empty(CaptureDraftParser.Parse("""{"tasks": []}"""));

    [Fact]
    public void OmittedOptionalFields_BecomeNull()
    {
        var draft = Assert.Single(CaptureDraftParser.Parse("""{"tasks":[{"title":"Email Ms. Rivera"}]}"""));

        Assert.Null(draft.EstimatedMinutes);
        Assert.Null(draft.Deadline);
        Assert.Null(draft.ProjectName);
    }

    [Fact]
    public void JsonFencedInProse_IsStillRead_BecauseSmallModelsDoThat()
    {
        var drafts = CaptureDraftParser.Parse("""
            Here you go:
            ```json
            {"tasks": [{"title": "Draft the essay"}]}
            ```
            """);

        Assert.Equal("Draft the essay", Assert.Single(drafts).Title);
    }

    [Fact]
    public void AnEntryWithNoTitle_IsSkipped_WithoutFailingTheBatch()
    {
        var drafts = CaptureDraftParser.Parse("""
            {"tasks": [{"estimated_minutes": 30}, {"title": "Real task"}]}
            """);

        Assert.Equal("Real task", Assert.Single(drafts).Title);
    }

    [Fact]
    public void AnUnparseableDeadline_DropsTheDeadline_NotTheTask()
    {
        var draft = Assert.Single(CaptureDraftParser.Parse(
            """{"tasks":[{"title":"Task","deadline":"next Friday"}]}"""));

        Assert.Null(draft.Deadline);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(100000)]
    public void AnOutOfRangeDuration_IsDropped(int minutes)
    {
        var draft = Assert.Single(CaptureDraftParser.Parse(
            $$"""{"tasks":[{"title":"Task","estimated_minutes":{{minutes}}}]}"""));

        Assert.Null(draft.EstimatedMinutes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1440)]
    public void TheInclusiveDurationBounds_AreAccepted(int minutes)
    {
        var draft = Assert.Single(CaptureDraftParser.Parse(
            $$"""{"tasks":[{"title":"Task","estimated_minutes":{{minutes}}}]}"""));

        Assert.Equal(minutes, draft.EstimatedMinutes);
    }

    [Fact]
    public void AnAbsurdlyLongTitle_IsTruncatedRatherThanStored()
    {
        var long_ = new string('x', 5000);
        var draft = Assert.Single(CaptureDraftParser.Parse(
            $$"""{"tasks":[{"title":"{{long_}}"}]}"""));

        Assert.True(draft.Title.Length <= 200, "a model must not be able to write an unbounded title");
    }

    /// <summary>
    /// Renamed from "AWhitespaceOnlyTitle_IsSkipped": under the all-malformed rule this
    /// single garbage entry is no longer silently swallowed. A non-empty "tasks" array
    /// where nothing survives validation must throw, exactly like any other malformed
    /// reply, so the router degrades with a notice instead of reporting false success.
    /// </summary>
    [Fact]
    public void AWhitespaceOnlyTitle_IsTheOnlyEntry_SoTheBatchFails()
        => Assert.Throws<FormatException>(() => CaptureDraftParser.Parse("""{"tasks":[{"title":"   "}]}"""));

    /// <summary>
    /// The other half of the same rule, distinct from the all-malformed case above: a
    /// message the model correctly judged to contain no task is a valid, successful
    /// answer and must not throw. Conflating "explicitly empty" with "all garbage"
    /// would turn a healthy "no task here" reply into a false degraded notice.
    /// </summary>
    [Fact]
    public void AnExplicitlyEmptyTaskArray_StillReturnsEmpty_WithoutThrowing()
        => Assert.Empty(CaptureDraftParser.Parse("""{"tasks": []}"""));

    /// <summary>
    /// The all-malformed case itself, with more than one bad entry: nothing in the
    /// batch survives validation, so this must fail the same way a single bad entry
    /// does — not degrade silently into an empty, "successful" draft list. Before this
    /// fix, this parsed without throwing into an empty list, and the router reported it
    /// as success with no degraded notice, telling the user a false "I couldn't find a
    /// task in that" even though the model's reply was the actual failure.
    /// </summary>
    [Fact]
    public void AllEntriesMalformed_Throws_RatherThanReportingFalseSuccess()
        => Assert.Throws<FormatException>(
            () => CaptureDraftParser.Parse("""{"tasks":[{"foo":1},{"bar":2}]}"""));

    [Fact]
    public void NonJson_Throws_SoTheRouterCanFallBack()
        => Assert.Throws<FormatException>(() => CaptureDraftParser.Parse("I'm not sure what you mean."));

    [Fact]
    public void AMissingTasksProperty_Throws()
        => Assert.Throws<FormatException>(() => CaptureDraftParser.Parse("""{"result": []}"""));
}
