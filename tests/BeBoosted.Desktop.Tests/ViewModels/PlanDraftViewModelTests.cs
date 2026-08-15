using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>Plan-draft behavior through the calendar ViewModel (shell-level wiring included).</summary>
public sealed class PlanDraftViewModelTests
{
    private static (Desktop.ViewModels.ShellViewModel Shell, InMemoryTaskRepository Tasks,
        InMemoryCalendarBlockRepository Blocks) CreateShell()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = TestShell.SeededTasks(clock);
        var blocks = new InMemoryCalendarBlockRepository();
        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        return (shell, tasks, blocks);
    }

    [Fact]
    public void Plan_CreatesDraftProposalsOnTheCalendar()
    {
        var (shell, _, _) = CreateShell();
        shell.ToggleInboxCommand.Execute(null);

        shell.PlanCommand.Execute(null);

        Assert.False(shell.IsInboxOpen);
        Assert.True(shell.Calendar.HasDraft);
        var proposals = shell.Calendar.Days
            .SelectMany(d => d.Blocks)
            .Where(b => b.IsProposal)
            .ToList();
        Assert.Equal(4, proposals.Count); // four seeded inbox tasks, all placeable
        Assert.All(proposals, p => Assert.True(p.CanMove && p.CanResize && p.CanDelete));
        Assert.Contains("4 blocks proposed", shell.Calendar.DraftSummaryText);
        Assert.Contains("4 tasks scheduled", shell.Calendar.DraftSummaryText);
        Assert.NotNull(proposals[0].Why);
    }

    [Fact]
    public void ApproveDraft_ConvertsProposalsIntoApprovedBlocks_WithUndoToast()
    {
        var (shell, _, blocks) = CreateShell();
        shell.PlanCommand.Execute(null);

        shell.Calendar.ApproveDraftCommand.Execute(null);

        Assert.False(shell.Calendar.HasDraft);
        Assert.True(shell.Calendar.IsUndoToastVisible);
        Assert.Contains("4 blocks", shell.Calendar.UndoToastText);
        Assert.Equal(4, blocks.GetAll().Count);
        Assert.Empty(shell.Inbox.Tasks); // scheduled work leaves the Inbox queue
        Assert.DoesNotContain(
            shell.Calendar.Days.SelectMany(d => d.Blocks),
            b => b.IsProposal);
    }

    [Fact]
    public void UndoLastApproval_RestoresTheDraftAndTheInbox()
    {
        var (shell, _, blocks) = CreateShell();
        shell.PlanCommand.Execute(null);
        shell.Calendar.ApproveDraftCommand.Execute(null);

        shell.UndoApprovalCommand.Execute(null);

        Assert.Empty(blocks.GetAll());
        Assert.True(shell.Calendar.HasDraft);
        Assert.False(shell.Calendar.IsUndoToastVisible);
        Assert.Equal(4, shell.Inbox.OpenCount);
    }

    [Fact]
    public void IndividualApproveAndRemove_ResolveSingleProposals()
    {
        var (shell, _, blocks) = CreateShell();
        shell.PlanCommand.Execute(null);
        var proposals = shell.Calendar.Days.SelectMany(d => d.Blocks).Where(b => b.IsProposal).ToList();

        proposals[0].ApproveThisCommand.Execute(null);
        Assert.Single(blocks.GetAll());
        Assert.True(shell.Calendar.IsUndoToastVisible);

        var remaining = shell.Calendar.Days.SelectMany(d => d.Blocks).Where(b => b.IsProposal).ToList();
        Assert.Equal(3, remaining.Count);

        remaining[0].RemoveFromDraftCommand.Execute(null);
        Assert.Equal(2, shell.Calendar.Days.SelectMany(d => d.Blocks).Count(b => b.IsProposal));
        Assert.Single(blocks.GetAll()); // removal never touches the calendar
    }

    [Fact]
    public void KeyboardNudge_MovesProposalsThroughThePlanningService()
    {
        var (shell, _, _) = CreateShell();
        shell.PlanCommand.Execute(null);
        var proposal = shell.Calendar.Days.SelectMany(d => d.Blocks).First(b => b.IsProposal);
        var originalStart = proposal.StartTime;

        shell.Calendar.NudgeBlock(proposal, 15);

        var moved = shell.Calendar.Days.SelectMany(d => d.Blocks)
            .First(b => b.IsProposal && b.Id == proposal.Id);
        Assert.Equal(originalStart.AddMinutes(15), moved.StartTime);
    }

    [Fact]
    public void ProposalOverlappingFixedEvent_IsConflicted_AndFixedEventUnchanged()
    {
        var (shell, _, _) = CreateShell();
        shell.Calendar.OpenNewCommitmentEditorCommand.Execute(null);
        var editor = shell.Calendar.CommitmentEditor!;
        editor.Title = "Lunch";
        editor.Date = new DateTimeOffset(TestShell.DesignDate.ToDateTime(TimeOnly.MinValue));
        editor.Start = new TimeSpan(12, 0, 0);
        editor.End = new TimeSpan(12, 45, 0);
        editor.SaveCommand.Execute(null);
        Assert.Null(shell.Calendar.CommitmentEditor);

        shell.PlanCommand.Execute(null);
        var proposal = shell.Calendar.Days.SelectMany(d => d.Blocks).First(b => b.IsProposal);

        // Drop the proposal onto the fixed Lunch block (12:00–12:45 on the design date).
        proposal.MoveTo(TestShell.DesignDate, new TimeOnly(12, 0));

        var refreshed = shell.Calendar.Days.SelectMany(d => d.Blocks).ToList();
        var movedProposal = refreshed.First(b => b.IsProposal && b.Id == proposal.Id);
        var lunch = refreshed.First(b => b.Title == "Lunch");
        Assert.True(movedProposal.IsConflicted);
        Assert.True(lunch.IsConflicted);
        Assert.Equal(new TimeOnly(12, 0), lunch.StartTime); // fixed events are never moved
    }

    [Fact]
    public void DiscardDraft_ClearsProposalsWithoutTouchingTheCalendar()
    {
        var (shell, _, blocks) = CreateShell();
        var before = blocks.GetAll().Count;
        shell.PlanCommand.Execute(null);

        shell.Calendar.DiscardDraftCommand.Execute(null);

        Assert.False(shell.Calendar.HasDraft);
        Assert.Equal(before, blocks.GetAll().Count);
        Assert.Equal(4, shell.Inbox.OpenCount);
    }

    [Fact]
    public void RanksInfluenceDraftOrdering()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = TestShell.SeededTasks(clock);
        var blocks = new InMemoryCalendarBlockRepository();
        var shell = TestShell.Create(tasks: tasks, blocks: blocks);

        // Rank via a full sort choosing the right card every time (keeps input order).
        shell.StartPrioritySortCommand.Execute(null);
        while (!shell.ActiveSort!.IsFinished)
        {
            shell.ActiveSort.ChooseRightCommand.Execute(null);
        }

        shell.ActiveSort.CloseCommand.Execute(null);
        shell.PlanCommand.Execute(null);

        var proposals = shell.Calendar.Days.SelectMany(d => d.Blocks)
            .Where(b => b.IsProposal)
            .OrderBy(b => b.Date).ThenBy(b => b.StartTime)
            .ToList();
        Assert.Equal("Finish DECA presentation", proposals[0].Title); // rank #1 gets the earliest slot
        Assert.Contains("Rank #1", proposals[0].Why!.Priority);
    }
}
