using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
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

    /// <summary>Leftover tasks are described in Task language — never as "flexible".</summary>
    [Fact]
    public void DraftSummary_DescribesLeftovers_AsUnscheduled()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        // The whole planning day is occupied, so the deadline-today task cannot fit.
        var busy = TaskItem.Create("All-day", clock.Now);
        tasks.Add(busy);
        blocks.Add(Domain.Calendar.CalendarBlock.CreateTaskSession(
            busy.Id, TestShell.DesignDate, new TimeOnly(8, 0), new TimeOnly(21, 0), clock.Now));
        tasks.Add(TaskItem.Create(
            "Urgent today", clock.Now,
            estimatedDuration: TimeSpan.FromMinutes(45), deadline: TestShell.DesignDate));
        tasks.Add(TaskItem.Create(
            "Placeable", clock.Now, estimatedDuration: TimeSpan.FromMinutes(30)));
        // Plan the week: the flexible-deadline task lands on another day while the
        // deadline-today task stays unplaceable.
        shell.Calendar.ViewKind = Application.Settings.CalendarViewKind.Week;
        shell.Calendar.Reload();

        shell.PlanCommand.Execute(null);

        Assert.True(shell.Calendar.HasDraft);
        Assert.Contains("1 remains unscheduled", shell.Calendar.DraftSummaryText);
        Assert.DoesNotContain(
            "flexible", shell.Calendar.DraftSummaryText, StringComparison.OrdinalIgnoreCase);
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
        shell.Calendar.OpenNewTaskEditorCommand.Execute(null);
        var editor = (WholeTaskEditorViewModel)shell.Calendar.ActiveTaskEditor!;
        editor.Title = "Lunch";
        editor.AddSessionCommand.Execute(null); // create mode: reveal the first session
        editor.InlineSchedule.Date = new DateTimeOffset(
            TestShell.DesignDate.ToDateTime(TimeOnly.MinValue));
        editor.InlineSchedule.Start = new TimeSpan(12, 0, 0);
        editor.InlineSchedule.End = new TimeSpan(12, 45, 0);
        editor.SaveCommand.Execute(null);
        Assert.Null(shell.Calendar.ActiveTaskEditor);

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

    /// <summary>A calendar VM over shared in-memory doubles (no shell), for guard tests.</summary>
    private static (Desktop.ViewModels.CalendarViewModel Calendar, InMemoryTaskRepository Tasks,
        InMemoryCalendarBlockRepository Blocks, InMemoryPlanningProposalRepository Proposals,
        Application.Calendar.CalendarService Service, Application.Planning.PlanningService Planning,
        FakeClock Clock)
        CreateBareCalendar()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var completions = new InMemoryOccurrenceCompletionRepository();
        var proposals = new InMemoryPlanningProposalRepository();
        var mutations = new InMemoryCalendarMutations(blocks, completions, tasks, proposals);
        var service = new Application.Calendar.CalendarService(
            blocks, completions, mutations, tasks, clock);
        var planning = new Application.Planning.PlanningService(
            proposals, new Application.Tasks.InboxQueryService(tasks, blocks),
            new InMemoryPrioritizationRepository(), service, mutations, clock);
        var calendar = TestShell.CreateCalendarViewModel(
            new InMemorySettingsStore(), clock, tasks, blocks,
            new InMemoryProjectRepository(), service, planning);
        return (calendar, tasks, blocks, proposals, service, planning, clock);
    }

    /// <summary>
    /// Completing a Task after planning but before approval rejects the approval:
    /// the VM surfaces the rejection as a plain notice and creates no block, no
    /// Undo entry, and no refresh.
    /// </summary>
    [Fact]
    public void ApprovingADraftForACompletedTask_ShowsANoticeAndChangesNothing()
    {
        var (calendar, tasks, blocks, _, service, _, clock) = CreateBareCalendar();
        var task = TaskItem.Create("Essay outline", clock.Now);
        tasks.Add(task);
        calendar.CreateDraft(PlanningPeriod.ForToday(TestShell.DesignDate));
        Assert.True(calendar.HasDraft);
        service.CompleteTask(task.Id); // completed after planning, before approval
        var changes = 0;
        calendar.DataChanged += () => changes++;

        calendar.ApproveDraftCommand.Execute(null);

        // The rejection surfaces as a plain notice — no Undo offer…
        Assert.True(calendar.IsUndoToastVisible);
        Assert.False(calendar.IsUndoAvailable);
        Assert.Contains("reopen", calendar.UndoToastText, StringComparison.OrdinalIgnoreCase);
        // …and nothing was created, refreshed, or made undoable.
        Assert.Empty(blocks.GetAll());
        Assert.Equal(0, changes);
        Assert.True(calendar.HasDraft);
        calendar.UndoLastApprovalCommand.Execute(null); // no recovery entry exists
        Assert.Empty(blocks.GetAll());
        Assert.Equal(0, changes);
    }

    /// <summary>
    /// Undo validates every physical block before deleting anything — safe even on
    /// the pass-through in-memory double, which has no rollback: a rejected undo
    /// keeps its recovery entry, announces no success, and refreshes nothing.
    /// </summary>
    [Fact]
    public void AnUndoAgainstACorruptedBlock_KeepsTheEntry_AndAnnouncesNoSuccess()
    {
        var (calendar, tasks, blocks, _, _, _, clock) = CreateBareCalendar();
        var task = TaskItem.Create("Essay outline", clock.Now);
        tasks.Add(task);
        calendar.CreateDraft(PlanningPeriod.ForToday(TestShell.DesignDate));
        calendar.ApproveDraftCommand.Execute(null);
        var block = Assert.Single(blocks.GetAll());
        var stranger = TaskItem.Create("Someone else's work", clock.Now);
        tasks.Add(stranger);
        // Corruption: the physical block now belongs to a different Task.
        blocks.Update(Domain.Calendar.CalendarBlock.Rehydrate(
            block.Id, stranger.Id, title: null, block.Date, block.StartTime, block.EndTime,
            Domain.Calendar.BlockKind.TaskSession, recurrence: null,
            Domain.Calendar.CalendarBlock.LocalProvider, externalId: null, syncState: 0,
            Domain.Calendar.BlockOutcome.None, outcomeRecordedAt: null, clock.Now, clock.Now));
        var changes = 0;
        calendar.DataChanged += () => changes++;

        calendar.UndoLastApprovalCommand.Execute(null);

        // Rejected before any write: the block survives, nothing announced as undone.
        Assert.False(calendar.IsUndoAvailable);
        Assert.Equal(0, changes);
        Assert.NotNull(blocks.GetById(block.Id));

        // Restoring the block's owner lets a plain retry undo the approval.
        blocks.Update(Domain.Calendar.CalendarBlock.Rehydrate(
            block.Id, task.Id, title: null, block.Date, block.StartTime, block.EndTime,
            Domain.Calendar.BlockKind.TaskSession, recurrence: null,
            Domain.Calendar.CalendarBlock.LocalProvider, externalId: null, syncState: 0,
            Domain.Calendar.BlockOutcome.None, outcomeRecordedAt: null, clock.Now, clock.Now));
        calendar.UndoLastApprovalCommand.Execute(null);
        Assert.Empty(blocks.GetAll());
        Assert.Equal(1, changes);
        Assert.True(calendar.HasDraft);
    }

    /// <summary>
    /// A duplicate-id undo request is rejected before the first write — on the
    /// in-memory double a mid-write rejection would tear state permanently.
    /// </summary>
    [Fact]
    public void UndoApproval_WithDuplicateIds_LeavesTheInMemoryStateIntact()
    {
        var (_, tasks, blocks, proposals, _, planning, clock) = CreateBareCalendar();
        tasks.Add(TaskItem.Create("Essay outline", clock.Now));
        var draft = planning.CreateDraft(PlanningPeriod.ForToday(TestShell.DesignDate)).Proposal;
        var blockId = planning.ApproveAll(draft.Id).Single();

        Assert.Throws<Domain.DomainException>(
            () => planning.UndoApproval(draft.Id, [blockId, blockId]));

        Assert.NotNull(blocks.GetById(blockId));
        Assert.Equal(
            Domain.Planning.ProposedBlockStatus.Approved,
            proposals.GetById(draft.Id)!.GetBlock(blockId).Status);

        planning.UndoApproval(draft.Id, [blockId]); // a valid retry still works
        Assert.Null(blocks.GetById(blockId));
    }
}
