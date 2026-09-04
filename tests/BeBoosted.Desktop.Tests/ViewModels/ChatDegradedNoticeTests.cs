using BeBoosted.Application.Ai;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// A capture that fell back to the heuristic says so in the reply. Without this the
/// downgrade is invisible and the user cannot tell a model answer from a rule answer.
/// </summary>
public sealed class ChatDegradedNoticeTests
{
    private sealed class DegradingProvider(string? notice) : IAiProvider
    {
        public Task<CaptureExtractionResult> ExtractTasksAsync(
            string message, AiContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new CaptureExtractionResult(
                [new ExtractedTaskDraft("Finish DECA presentation", null, null, null, "from your message")],
                notice));

        public Task<TaskMetadataSuggestion> SuggestMetadataAsync(
            string title, AiContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new TaskMetadataSuggestion(null, null));

        public Task<ProjectAnswerResult> AnswerQuestionAsync(
            ProjectId projectId, string question, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProjectAnswerResult("no answer", []));
    }

    /// <summary>A List, not IReadOnlyList: the ordering assertion needs IndexOf.</summary>
    private static async Task<List<ChatItemViewModel>> SendAsync(string? notice)
    {
        var shell = TestShell.Create(aiProvider: new DegradingProvider(notice));
        var chat = shell.Chat;
        chat.InputText = "Finish my DECA presentation before Friday.";
        await chat.SubmitCommand.ExecuteAsync(null);
        return [.. chat.Items];
    }

    [Fact]
    public async Task ADegradedCapture_RendersItsNoticeBeforeTheReviewList()
    {
        var items = await SendAsync("Parsed locally — Claude couldn't be reached.");

        var notice = items.OfType<ChatAssistantMessageViewModel>()
            .FirstOrDefault(item => item.Text.Contains("Parsed locally", StringComparison.Ordinal));
        Assert.NotNull(notice);
        Assert.Contains(items, item => item is ChatReviewItemViewModel);
        Assert.True(
            items.IndexOf(notice) < items.IndexOf(items.First(i => i is ChatReviewItemViewModel)),
            "the notice explains the drafts, so it comes before them");
    }

    [Fact]
    public async Task AHealthyCapture_RendersNoNotice()
    {
        var items = await SendAsync(notice: null);

        Assert.DoesNotContain(
            items.OfType<ChatAssistantMessageViewModel>(),
            item => item.Text.Contains("Parsed locally", StringComparison.Ordinal));
    }
}
