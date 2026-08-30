using BeBoosted.Application.Calendar;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// Deleting a Task can never leave planning debris: its pending proposal blocks
/// are withdrawn with it (no "(deleted task)" ghosts, no empty active draft), the
/// surviving proposals stay approvable, and approval Undo can never resurrect a
/// deleted Task's proposal or session.
/// </summary>
public sealed class PlanningDeletionTests
{
    private static (ShellViewModel Shell, InMemoryTaskRepository Tasks, InMemoryCalendarBlockRepository Blocks)
        CreateShell()
    {
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        return (shell, tasks, blocks);
    }

    private static void Capture(ShellViewModel shell, string title)
    {
        shell.Inbox.CaptureText = title;
        shell.Inbox.CaptureCommand.Execute(null);
    }

    private static List<CalendarBlockViewModel> RenderedProposals(ShellViewModel shell)
        => shell.Calendar.Days.SelectMany(d => d.Blocks).Where(b => b.IsProposal).ToList();

    [Fact]
    public void DeletingAnInboxTask_WithdrawsItsPendingProposal_AndKeepsOthersApprovable()
    {
        var (shell, tasks, blocks) = CreateShell();
        Capture(shell, "Essay outline");
        Capture(shell, "Vocab review");
        shell.PlanCommand.Execute(null);
        Assert.True(shell.Calendar.HasDraft);
        Assert.Equal(2, RenderedProposals(shell).Count);

        shell.Inbox.Tasks.Single(r => r.Title == "Essay outline").DeleteCommand.Execute(null);

        // The deleted task's proposal is gone everywhere — never "(deleted task)".
        var survivorProposal = Assert.Single(RenderedProposals(shell));
        Assert.Equal("Vocab review", survivorProposal.Title);
        Assert.DoesNotContain(
            shell.Calendar.Days.SelectMany(d => d.Blocks), b => b.Title == "(deleted task)");

        // The survivor stays fully approvable.
        shell.Calendar.ApproveProposalBlock(survivorProposal.Id);
        var created = Assert.Single(blocks.GetAll());
        Assert.Equal("Vocab review", tasks.GetById(created.TaskId!.Value)!.Title);
    }

    [Fact]
    public void DeletingEveryPlannedTask_LeavesNoEmptyActiveDraft()
    {
        var (shell, _, _) = CreateShell();
        Capture(shell, "Essay outline");
        shell.PlanCommand.Execute(null);
        Assert.True(shell.Calendar.HasDraft);

        shell.Inbox.Tasks.Single().DeleteCommand.Execute(null);

        Assert.False(shell.Calendar.HasDraft);
        Assert.Empty(RenderedProposals(shell));
    }

    [Fact]
    public void UndoAfterTaskDeletion_ResurrectsNothing_AndHidesTheUndoOffer()
    {
        var (shell, tasks, blocks) = CreateShell();
        Capture(shell, "Essay outline");
        shell.PlanCommand.Execute(null);
        shell.Calendar.ApproveDraftCommand.Execute(null);
        Assert.True(shell.Calendar.IsUndoToastVisible);
        var taskId = tasks.GetAll().Single().Id;

        // Delete through the real editor path (the task is now scheduled).
        shell.Calendar.OpenTaskEditorForTask(taskId);
        var editor = (WholeTaskEditorViewModel)shell.Calendar.ActiveTaskEditor!;
        editor.RequestDeleteCommand.Execute(null);
        editor.ConfirmPromptCommand.Execute(null);
        Assert.Null(tasks.GetById(taskId));

        // Nothing remains undoable, and the affordance says so.
        Assert.False(shell.Calendar.IsUndoToastVisible);

        var exception = Record.Exception(
            () => shell.Calendar.UndoLastApprovalCommand.Execute(null));

        Assert.Null(exception);
        Assert.False(shell.Calendar.HasDraft);
        Assert.Empty(RenderedProposals(shell));
        Assert.Empty(blocks.GetAll());
        Assert.Null(tasks.GetById(taskId));
    }

    /// <summary>
    /// A visible Undo toast is bound to the approval that raised it: when that
    /// approval's blocks vanish with a deleted Task, the toast hides instead of
    /// silently retargeting an older approval. (Ctrl+Z session semantics keep the
    /// older entry reachable.)
    /// </summary>
    [Fact]
    public void AStaleUndoToast_HidesInsteadOfTargetingAnOlderApproval()
    {
        var (shell, tasks, blocks) = CreateShell();
        Capture(shell, "Essay outline");
        Capture(shell, "Vocab review");
        shell.PlanCommand.Execute(null);
        var essayProposal = RenderedProposals(shell).Single(p => p.Title == "Essay outline");
        var vocabProposal = RenderedProposals(shell).Single(p => p.Title == "Vocab review");
        shell.Calendar.ApproveProposalBlock(essayProposal.Id); // older undo entry
        shell.Calendar.ApproveProposalBlock(vocabProposal.Id); // the visible toast's entry
        Assert.True(shell.Calendar.IsUndoToastVisible);
        var vocabId = tasks.GetAll().Single(t => t.Title == "Vocab review").Id;

        shell.Calendar.OpenTaskEditorForTask(vocabId);
        var editor = (WholeTaskEditorViewModel)shell.Calendar.ActiveTaskEditor!;
        editor.RequestDeleteCommand.Execute(null);
        editor.ConfirmPromptCommand.Execute(null);

        // The toast's own approval is gone — it must hide, never undo the older one.
        Assert.False(shell.Calendar.IsUndoToastVisible);

        // Ctrl+Z still reaches the older approval per session semantics.
        shell.Calendar.UndoLastApprovalCommand.Execute(null);
        var reverted = Assert.Single(RenderedProposals(shell));
        Assert.Equal("Essay outline", reverted.Title);
        Assert.Empty(blocks.GetAll());
    }

    /// <summary>
    /// Undo removes its recovery entry only after the service commit succeeds: a
    /// failed undo keeps the entry (a retry still works) and announces nothing.
    /// </summary>
    [Fact]
    public void AFailedUndo_KeepsTheRecoveryEntry_AndAnnouncesNothing()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository { Tasks = tasks };
        var completions = new InMemoryOccurrenceCompletionRepository();
        var proposals = new InMemoryPlanningProposalRepository();
        var mutations = new InMemoryCalendarMutations(blocks, completions, tasks, proposals);
        var service = new CalendarService(blocks, completions, mutations, tasks, clock);
        var trip = new FailureSwitch();
        var planning = new BeBoosted.Application.Planning.PlanningService(
            proposals,
            new BeBoosted.Application.Tasks.InboxQueryService(tasks, blocks),
            new InMemoryPrioritizationRepository(), service,
            new SwitchedMutations(mutations, trip), clock);
        var calendar = TestShell.CreateCalendarViewModel(
            new InMemorySettingsStore(), clock, tasks, blocks,
            new InMemoryProjectRepository(), service, planning);
        tasks.Add(Domain.Tasks.TaskItem.Create("Essay outline", clock.Now));
        calendar.CreateDraft(Domain.Prioritization.PlanningPeriod.ForToday(TestShell.DesignDate));
        calendar.ApproveDraftCommand.Execute(null);
        Assert.Single(blocks.GetAll());
        var changes = 0;
        calendar.DataChanged += () => changes++;

        trip.Armed = true;
        Assert.ThrowsAny<InvalidOperationException>(
            () => calendar.UndoLastApprovalCommand.Execute(null));

        // Nothing announced, nothing half-undone.
        Assert.Equal(0, changes);
        Assert.Single(blocks.GetAll());

        // The recovery entry survived the failure: a plain retry undoes the approval.
        calendar.UndoLastApprovalCommand.Execute(null);
        Assert.Empty(blocks.GetAll());
        Assert.True(calendar.HasDraft);
        Assert.Equal(1, changes);
    }

    private sealed class FailureSwitch
    {
        public bool Armed { get; set; }

        public void Trip()
        {
            if (Armed)
            {
                Armed = false;
                throw new InvalidOperationException("injected failure");
            }
        }
    }

    /// <summary>Fails the next shared-transaction execution — the unit-of-work seam.</summary>
    private sealed class SwitchedMutations(
        BeBoosted.Application.Calendar.ICalendarMutations inner, FailureSwitch trip)
        : BeBoosted.Application.Calendar.ICalendarMutations
    {
        public void Execute(
            Action<BeBoosted.Application.Calendar.ICalendarBlockRepository,
                BeBoosted.Application.Calendar.IOccurrenceCompletionRepository,
                BeBoosted.Application.Tasks.ITaskRepository,
                BeBoosted.Application.Planning.IPlanningProposalRepository> mutation)
        {
            trip.Trip();
            inner.Execute(mutation);
        }
    }

    [Fact]
    public void UndoEntry_SpanningSeveralTasks_PrunesOnlyTheDeletedOnes()
    {
        var (shell, tasks, blocks) = CreateShell();
        Capture(shell, "Essay outline");
        Capture(shell, "Vocab review");
        shell.PlanCommand.Execute(null);
        shell.Calendar.ApproveDraftCommand.Execute(null);
        var essayId = tasks.GetAll().Single(t => t.Title == "Essay outline").Id;

        shell.Calendar.OpenTaskEditorForTask(essayId);
        var editor = (WholeTaskEditorViewModel)shell.Calendar.ActiveTaskEditor!;
        editor.RequestDeleteCommand.Execute(null);
        editor.ConfirmPromptCommand.Execute(null);

        // The other task's approval remains undoable.
        Assert.True(shell.Calendar.IsUndoToastVisible);
        shell.Calendar.UndoLastApprovalCommand.Execute(null);

        // Undo reverted only the surviving task's block — nothing for the deleted one.
        var reverted = Assert.Single(RenderedProposals(shell));
        Assert.Equal("Vocab review", reverted.Title);
        Assert.Empty(blocks.GetAll());
        Assert.Null(tasks.GetById(essayId));
        Assert.DoesNotContain(
            shell.Calendar.Days.SelectMany(d => d.Blocks), b => b.Title == "(deleted task)");
    }
}
