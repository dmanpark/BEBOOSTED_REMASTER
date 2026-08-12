using BeBoosted.Application.Ai;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.ViewModels;

public sealed class ChatViewModelTests
{
    [Theory]
    [InlineData("plan my week", true)]
    [InlineData("Plan today", true)]
    [InlineData("please plan my day", true)]
    [InlineData("explain the plan", false)]
    public void PlanIntent_IsRecognized(string text, bool expected)
        => Assert.Equal(expected, ChatViewModel.IsPlanRequest(text));

    [Theory]
    [InlineData("What GPA should I report?", true)]
    [InlineData("how do I start", true)]
    [InlineData("Finish the essay", false)]
    public void QuestionIntent_IsRecognized(string text, bool expected)
        => Assert.Equal(expected, ChatViewModel.IsQuestion(text));

    [Fact]
    public void CaptureMessage_ProducesAReviewList_NothingAddedUntilApproved()
    {
        var shell = TestShell.Create();
        shell.Chat.InputText = "Draft the essay outline before Sunday. Also email the recommendation request.";
        shell.Chat.SubmitCommand.Execute(null);

        Assert.True(shell.Chat.IsExpanded);
        Assert.Equal(string.Empty, shell.Chat.InputText);
        var review = Assert.IsType<ChatReviewItemViewModel>(shell.Chat.Items[^1]);
        Assert.Equal(2, review.Drafts.Count);
        Assert.Empty(shell.Inbox.Tasks); // review-first default

        review.AddAllCommand.Execute(null);
        Assert.Equal(2, shell.Inbox.Tasks.Count);
        Assert.All(shell.Inbox.Tasks, row => Assert.True(row.IsAiOrigin));
        Assert.False(review.IsPending);
        Assert.Contains("Added 2 tasks", review.ResolutionText);
    }

    [Fact]
    public void DraftRows_CanBeEditedAndDismissed()
    {
        var shell = TestShell.Create();
        shell.Chat.InputText = "Draft the essay outline. Also email the recommendation request.";
        shell.Chat.SubmitCommand.Execute(null);
        var review = Assert.IsType<ChatReviewItemViewModel>(shell.Chat.Items[^1]);

        review.Drafts[0].EditTitle = "Draft the COMMON APP essay outline";
        review.Drafts[1].DismissCommand.Execute(null);
        Assert.Single(review.Drafts);

        review.AddAllCommand.Execute(null);
        var row = Assert.Single(shell.Inbox.Tasks);
        Assert.Equal("Draft the COMMON APP essay outline", row.Title);
    }

    [Fact]
    public void AutoAddPermission_AddsWithLabel_AndSaysSo()
    {
        var store = new InMemorySettingsStore();
        new AiPermissionSettings(store).TaskCapture = TaskCapturePermission.AddAutomatically;
        var shell = TestShell.Create(store: store);

        shell.Chat.InputText = "Review the economics chapter";
        shell.Chat.SubmitCommand.Execute(null);

        var message = Assert.IsType<ChatAssistantMessageViewModel>(shell.Chat.Items[^1]);
        Assert.Contains("AI added", message.Text);
        var row = Assert.Single(shell.Inbox.Tasks);
        Assert.True(row.IsAiOrigin);
    }

    [Fact]
    public void ProjectQuestion_AnswersWithCitations_AndCitationNavigates()
    {
        var shell = TestShell.Create();
        shell.NavigateCommand.Execute(AppSection.Projects);
        shell.Projects.NewProjectName = "College Admissions";
        shell.Projects.TryCreateProject();
        shell.Projects.Detail!.NewFileTitle = "Metric Proof";
        shell.Projects.Detail.TryCreateFile();
        shell.Projects.FileDetail!.NewNoteTitle = "Leadership metrics";
        shell.Projects.FileDetail.NewNoteContent = "Led three DECA teams.";
        shell.Projects.FileDetail.TryAddNote();
        Assert.Equal("Ask about College Admissions…", shell.Chat.Placeholder);

        shell.Chat.InputText = "What leadership metrics do I have?";
        shell.Chat.SubmitCommand.Execute(null);

        var answer = Assert.IsType<ChatAssistantMessageViewModel>(shell.Chat.Items[^1]);
        Assert.True(answer.HasCitations);
        var citation = Assert.Single(answer.Citations);
        Assert.Equal("Leadership metrics", citation.Label);

        citation.OpenCommand.Execute(null);
        Assert.False(shell.Chat.IsExpanded);
        Assert.True(shell.Projects.IsFileVisible);
        Assert.Equal("Leadership metrics", shell.Projects.FileDetail!.Selected!.Title);
        Assert.Single(shell.Projects.FileDetail.Selected.Derivations); // "Cited in …"
    }

    [Fact]
    public void QuestionWithoutProjectScope_ExplainsHowToAsk()
    {
        var shell = TestShell.Create();
        shell.Chat.InputText = "What should I do first?";
        shell.Chat.SubmitCommand.Execute(null);

        var message = Assert.IsType<ChatAssistantMessageViewModel>(shell.Chat.Items[^1]);
        Assert.Contains("Open a project", message.Text);
    }

    [Fact]
    public void PlanRequest_DraftsOnTheCalendar_ReviewFirstByDefault()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var shell = TestShell.Create(tasks: TestShell.SeededTasks(clock));

        shell.Chat.InputText = "plan my week";
        shell.Chat.SubmitCommand.Execute(null);

        Assert.True(shell.Calendar.HasDraft);
        var message = Assert.IsType<ChatAssistantMessageViewModel>(shell.Chat.Items[^1]);
        Assert.Contains("drafted a plan", message.Text);
        Assert.Equal(AppSection.Calendar, shell.ActiveSection);
    }

    [Fact]
    public void PlanRequest_WithAutoApplyPermission_AppliesAndStaysUndoable()
    {
        var store = new InMemorySettingsStore();
        new AiPermissionSettings(store).CalendarPlanning = CalendarPlanningPermission.ApplyAutomatically;
        var clock = new FakeClock(TestShell.DesignDate);
        var blocks = new InMemoryCalendarBlockRepository();
        var shell = TestShell.Create(store: store, tasks: TestShell.SeededTasks(clock), blocks: blocks);

        shell.Chat.InputText = "plan my week";
        shell.Chat.SubmitCommand.Execute(null);

        Assert.False(shell.Calendar.HasDraft);
        Assert.Equal(4, blocks.GetAll().Count);
        var message = Assert.IsType<ChatAssistantMessageViewModel>(shell.Chat.Items[^1]);
        Assert.Contains("undoable", message.Text);

        shell.UndoApprovalCommand.Execute(null);
        Assert.Empty(blocks.GetAll());
        Assert.True(shell.Calendar.HasDraft);
    }

    [Fact]
    public void Escape_CollapsesTheChatBeforeTheDrawer()
    {
        var shell = TestShell.Create();
        shell.ToggleInboxCommand.Execute(null);
        shell.Chat.InputText = "Capture something";
        shell.Chat.SubmitCommand.Execute(null);
        Assert.True(shell.Chat.IsExpanded);

        shell.EscapePressedCommand.Execute(null);
        Assert.False(shell.Chat.IsExpanded);
        Assert.True(shell.IsInboxOpen);
    }
}

public sealed class SettingsViewModelTests
{
    [Fact]
    public void Permissions_PersistIndependently()
    {
        var store = new InMemorySettingsStore();
        var permissions = new AiPermissionSettings(store);
        var settings = new SettingsViewModel(
            new BeBoosted.Infrastructure.Storage.DefaultAppDataPaths(Path.GetTempPath()), permissions);

        Assert.True(settings.IsTaskCaptureReview);
        Assert.True(settings.IsPlanningReview);

        settings.IsTaskCaptureAuto = true;
        Assert.Equal(TaskCapturePermission.AddAutomatically, permissions.TaskCapture);
        // Granting task capture never grants calendar automation.
        Assert.Equal(CalendarPlanningPermission.ReviewEveryPlan, permissions.CalendarPlanning);
        Assert.True(settings.IsPlanningReview);

        settings.IsPlanningAuto = true;
        Assert.Equal(CalendarPlanningPermission.ApplyAutomatically, permissions.CalendarPlanning);

        var reloaded = new AiPermissionSettings(store);
        Assert.Equal(TaskCapturePermission.AddAutomatically, reloaded.TaskCapture);
        Assert.Equal(CalendarPlanningPermission.ApplyAutomatically, reloaded.CalendarPlanning);
    }
}
