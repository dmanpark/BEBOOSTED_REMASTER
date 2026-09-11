using BeBoosted.Application.Ai;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// A dogfooding session reported that a draft accepted with "Add" could not be found
/// anywhere afterwards once the chat panel was closed. Losing a task the user believes
/// they captured is the one failure this app must never have, so the whole path is
/// pinned here: accepted, persisted, and visible on a surface the user actually looks at.
/// </summary>
public sealed class AcceptedDraftSurvivalTests
{
    private sealed class OneDraftProvider : IAiProvider
    {
        public Task<CaptureExtractionResult> ExtractTasksAsync(
            string message, AiContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new CaptureExtractionResult(
                [new ExtractedTaskDraft(
                    "Finish DECA presentation", TimeSpan.FromMinutes(90),
                    new DateOnly(2026, 9, 11), null, "from your message")]));

        public Task<TaskMetadataSuggestion> SuggestMetadataAsync(
            string title, AiContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new TaskMetadataSuggestion(null, null));

        public Task<ProjectAnswerResult> AnswerQuestionAsync(
            ProjectId projectId, string question, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProjectAnswerResult("no answer", []));
    }

    [Fact]
    public async Task ADraftAccepted_ThenTheChatClosed_IsStillOnTheDailyList()
    {
        var tasks = new InMemoryTaskRepository();
        var shell = TestShell.Create(tasks: tasks, aiProvider: new OneDraftProvider());
        var chat = shell.Chat;

        chat.InputText = "Finish my DECA presentation before Friday.";
        await chat.SubmitCommand.ExecuteAsync(null);

        var review = Assert.Single(chat.Items.OfType<ChatReviewItemViewModel>());
        review.AddAllCommand.Execute(null);

        // The user's confirmation that it landed.
        Assert.Equal("Added 1 task to your Inbox.", review.ResolutionText);

        // Closing the panel must not undo it — the panel is documented as temporary.
        chat.CollapseCommand.Execute(null);

        Assert.Single(tasks.GetOpen());
        Assert.Contains(
            shell.Calendar.Daily.UnscheduledRows,
            row => row.Title == "Finish DECA presentation");
    }

    /// <summary>
    /// The other half of the same worry: drafts you have NOT resolved yet. The panel
    /// calls itself temporary, so closing it mid-review looks like it should discard
    /// them. It must not — the user would have to retype the message to get them back.
    /// </summary>
    [Fact]
    public async Task DraftsLeftUnresolved_SurviveClosingAndReopeningTheChat()
    {
        var tasks = new InMemoryTaskRepository();
        var shell = TestShell.Create(tasks: tasks, aiProvider: new OneDraftProvider());
        var chat = shell.Chat;

        chat.InputText = "Finish my DECA presentation before Friday.";
        await chat.SubmitCommand.ExecuteAsync(null);
        var review = Assert.Single(chat.Items.OfType<ChatReviewItemViewModel>());
        Assert.True(review.IsPending);

        chat.CollapseCommand.Execute(null);
        chat.IsExpanded = true;

        var reopened = Assert.Single(chat.Items.OfType<ChatReviewItemViewModel>());
        Assert.True(reopened.IsPending, "an unresolved review must still be waiting");
        Assert.Equal("Finish DECA presentation", Assert.Single(reopened.Drafts).EditTitle);
        Assert.Empty(tasks.GetOpen()); // and nothing was added behind the user's back
    }
}
