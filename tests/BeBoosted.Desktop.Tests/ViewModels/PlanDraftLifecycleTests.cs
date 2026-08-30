using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Tasks;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;
using BeBoosted.Domain.Planning;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// The plan-draft lifecycle seen from the ViewModel: an approval stops being
/// undoable once a replacement plan supersedes it or its session becomes a
/// repeating series, an emptied plan closes instead of lingering, and a rejected
/// discard surfaces as a notice rather than an escaping exception.
/// </summary>
public sealed class PlanDraftLifecycleTests
{
    private static readonly PlanningPeriod Today = PlanningPeriod.ForToday(TestShell.DesignDate);

    private sealed record Harness(
        CalendarViewModel Calendar,
        InMemoryTaskRepository Tasks,
        InMemoryCalendarBlockRepository Blocks,
        InMemoryPlanningProposalRepository Proposals,
        PlanningService Planning,
        FakeClock Clock);

    /// <summary>
    /// A calendar VM over shared in-memory doubles. The mutation seam passes
    /// straight through with no rollback, so any write that happens before a
    /// rejection stays visible — which is exactly what these tests check.
    /// </summary>
    private static Harness CreateHarness()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository { Tasks = tasks };
        var completions = new InMemoryOccurrenceCompletionRepository();
        var proposals = new InMemoryPlanningProposalRepository();
        var mutations = new InMemoryCalendarMutations(blocks, completions, tasks, proposals);
        var service = new CalendarService(blocks, completions, mutations, tasks, clock);
        var planning = new PlanningService(
            proposals, new InboxQueryService(tasks, blocks),
            new InMemoryPrioritizationRepository(), service, mutations, clock);
        var calendar = TestShell.CreateCalendarViewModel(
            new InMemorySettingsStore(), clock, tasks, blocks,
            new InMemoryProjectRepository(), service, planning);
        return new Harness(calendar, tasks, blocks, proposals, planning, clock);
    }

    private static TaskItem AddTask(Harness harness, string title)
    {
        var task = TaskItem.Create(
            title, harness.Clock.Now, estimatedDuration: TimeSpan.FromMinutes(30));
        harness.Tasks.Add(task);
        return task;
    }

    // ---- A replacement plan withdraws the older plan's approval Undo ----

    [Fact]
    public void CreatingAReplacementPlan_WithdrawsTheObsoleteApprovalUndo()
    {
        var harness = CreateHarness();
        var (calendar, _, blocks, _, _, _) = harness;
        AddTask(harness, "Essay outline");
        calendar.CreateDraft(Today);
        calendar.ApproveDraftCommand.Execute(null);
        var approved = Assert.Single(blocks.GetAll());
        Assert.True(calendar.IsUndoToastVisible);

        // A fresh planning session supersedes that plan.
        AddTask(harness, "Vocab review");
        calendar.CreateDraft(Today);

        // The obsolete offer is gone from the toast…
        Assert.False(calendar.IsUndoToastVisible);
        var changes = 0;
        calendar.DataChanged += () => changes++;

        calendar.UndoLastApprovalCommand.Execute(null);

        // …and Ctrl+Z has nothing to target: no attempt, no rejection notice, and
        // certainly no damage to the approved block or the newer plan.
        Assert.False(calendar.IsUndoToastVisible);
        Assert.Equal(0, changes);
        Assert.NotNull(blocks.GetById(approved.Id));
        Assert.True(calendar.HasDraft);
    }

    /// <summary>
    /// The service is the backstop for stale or direct callers: it rejects before
    /// the first write, which on this rollback-free double is the only thing that
    /// keeps the block and the newer plan intact.
    /// </summary>
    [Fact]
    public void UndoApproval_AfterAReplacementPlan_IsRejectedBeforeTheFirstWrite()
    {
        var harness = CreateHarness();
        var (_, _, blocks, proposals, planning, _) = harness;
        AddTask(harness, "Essay outline");
        var first = planning.CreateDraft(Today).Proposal;
        var approved = planning.ApproveAll(first.Id).Single();
        AddTask(harness, "Vocab review");
        var replacement = planning.CreateDraft(Today).Proposal;

        Assert.Throws<DomainException>(() => planning.UndoApproval(first.Id, [approved]));

        Assert.NotNull(blocks.GetById(approved));
        Assert.Equal(ProposalState.Approved, proposals.GetById(first.Id)!.State);
        Assert.Equal(replacement.Id, proposals.GetActiveDraft()!.Id);
        Assert.Equal(
            [replacement.Id],
            proposals.GetAll().Where(p => p.State == ProposalState.Draft).Select(p => p.Id));
    }

    // ---- A session that became a series withdraws its approval Undo ----

    [Fact]
    public void ConvertingAnApprovedSessionIntoARepeatingSeries_WithdrawsItsApprovalUndo()
    {
        var harness = CreateHarness();
        var (calendar, _, blocks, _, _, _) = harness;
        AddTask(harness, "Essay outline");
        calendar.CreateDraft(Today);
        calendar.ApproveDraftCommand.Execute(null);
        var approvedId = Assert.Single(blocks.GetAll()).Id;
        Assert.True(calendar.IsUndoToastVisible);

        // The user edits that very session in the session editor and makes it weekly.
        calendar.OpenSessionEditorForBlock(approvedId, blocks.GetById(approvedId)!.Date);
        var editor = (SessionEditorViewModel)calendar.ActiveTaskEditor!;
        editor.Schedule.RepeatsWeekly = true;
        editor.SaveCommand.Execute(null);
        Assert.NotNull(blocks.GetById(approvedId)!.Recurrence);

        Assert.False(calendar.IsUndoToastVisible);
        var changes = 0;
        calendar.DataChanged += () => changes++;

        calendar.UndoLastApprovalCommand.Execute(null);

        // Undo can never take the series with it.
        Assert.False(calendar.IsUndoToastVisible);
        Assert.Equal(0, changes);
        Assert.NotNull(blocks.GetById(approvedId));
        Assert.NotNull(blocks.GetById(approvedId)!.Recurrence);
    }

    /// <summary>Rescheduling the one-off session it created keeps an approval undoable.</summary>
    [Fact]
    public void MovingAndResizingAnApprovedSession_KeepsItsApprovalUndo()
    {
        var harness = CreateHarness();
        var (calendar, _, blocks, _, _, _) = harness;
        AddTask(harness, "Essay outline");
        calendar.CreateDraft(Today);
        calendar.ApproveDraftCommand.Execute(null);
        var approvedId = Assert.Single(blocks.GetAll()).Id;

        calendar.MoveBlock(approvedId, TestShell.DesignDate, new TimeOnly(9, 0));
        calendar.ResizeBlockTo(approvedId, new TimeOnly(11, 0));

        calendar.UndoLastApprovalCommand.Execute(null);

        Assert.Empty(blocks.GetAll());
        Assert.True(calendar.HasDraft);
    }

    // ---- An emptied plan closes instead of lingering ----

    [Fact]
    public void RemovingTheLastPendingBlock_ClosesThePlanDraftInsteadOfRetainingIt()
    {
        var harness = CreateHarness();
        var (calendar, _, _, proposals, _, _) = harness;
        AddTask(harness, "Essay outline");
        AddTask(harness, "Vocab review");
        calendar.CreateDraft(Today);
        var draft = proposals.GetActiveDraft()!;
        var pending = draft.PendingBlocks.Select(b => b.Id).ToList();
        Assert.Equal(2, pending.Count);
        calendar.ApproveProposalBlock(pending[0]);

        calendar.RemoveProposalBlock(pending[1]);

        Assert.False(calendar.HasDraft);
        Assert.Null(proposals.GetActiveDraft());

        // The emptied plan is not retained: discarding must not clobber the
        // approval record it settled into.
        calendar.DiscardDraftCommand.Execute(null);
        Assert.Equal(ProposalState.Approved, proposals.GetById(draft.Id)!.State);
    }

    // ---- A rejected discard stays inside the command ----

    [Fact]
    public void DiscardDraft_WhenTheServiceRejectsIt_ShowsANoticeAndChangesNothing()
    {
        var harness = CreateHarness();
        var (calendar, _, blocks, proposals, planning, _) = harness;
        AddTask(harness, "Essay outline");
        calendar.CreateDraft(Today);
        var draft = proposals.GetActiveDraft()!;
        Assert.True(calendar.HasDraft);
        // Approved behind the ViewModel's back: its draft handle is now stale.
        planning.ApproveAll(draft.Id);
        var changes = 0;
        calendar.DataChanged += () => changes++;

        var exception = Record.Exception(() => calendar.DiscardDraftCommand.Execute(null));

        Assert.Null(exception); // never escapes the command
        Assert.True(calendar.IsUndoToastVisible);
        Assert.False(calendar.IsUndoAvailable); // a plain notice, with no Undo offer
        Assert.Contains("discard", calendar.UndoToastText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, changes); // nothing announced as changed
        Assert.Equal(ProposalState.Approved, proposals.GetById(draft.Id)!.State);
        Assert.Single(blocks.GetAll()); // the approval survives untouched
    }

    [Fact]
    public void DiscardDraft_OnTheActivePlan_ClearsItWithoutTouchingTheCalendar()
    {
        var harness = CreateHarness();
        var (calendar, _, blocks, proposals, _, _) = harness;
        AddTask(harness, "Essay outline");
        calendar.CreateDraft(Today);
        var draft = proposals.GetActiveDraft()!;

        calendar.DiscardDraftCommand.Execute(null);

        Assert.False(calendar.HasDraft);
        Assert.Null(proposals.GetActiveDraft());
        Assert.Equal(ProposalState.Discarded, proposals.GetById(draft.Id)!.State);
        Assert.Empty(blocks.GetAll());
        Assert.False(calendar.IsUndoToastVisible); // success is silent
    }
}
