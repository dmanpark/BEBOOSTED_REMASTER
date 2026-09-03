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

    [Fact]
    public void AWhitespaceOnlyTitle_IsSkipped()
        => Assert.Empty(CaptureDraftParser.Parse("""{"tasks":[{"title":"   "}]}"""));

    [Fact]
    public void NonJson_Throws_SoTheRouterCanFallBack()
        => Assert.Throws<FormatException>(() => CaptureDraftParser.Parse("I'm not sure what you mean."));

    [Fact]
    public void AMissingTasksProperty_Throws()
        => Assert.Throws<FormatException>(() => CaptureDraftParser.Parse("""{"result": []}"""));
}
