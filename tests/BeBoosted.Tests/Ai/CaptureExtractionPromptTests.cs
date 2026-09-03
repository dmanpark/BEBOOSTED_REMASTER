using BeBoosted.Application.Ai;

namespace BeBoosted.Tests.Ai;

public sealed class CaptureExtractionPromptTests
{
    private static CaptureRequest Request(string message) => new(
        message, ["Schoolwork", "College Admissions"], new DateOnly(2026, 9, 3));

    [Fact]
    public void TheUserPrompt_CarriesTheMessage_TheProjectNames_AndToday()
    {
        var prompt = CaptureExtractionPrompt.BuildUser(Request("Finish the essay"));

        Assert.Contains("Finish the essay", prompt, StringComparison.Ordinal);
        Assert.Contains("Schoolwork", prompt, StringComparison.Ordinal);
        Assert.Contains("College Admissions", prompt, StringComparison.Ordinal);
        Assert.Contains("2026-09-03", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The BB-QA-003 rule, stated as an instruction: an elaborating sentence is not a
    /// second task. If this drops out of the prompt the defect returns silently.
    /// </summary>
    [Fact]
    public void TheSystemPrompt_ForbidsTurningAnElaboratingSentenceIntoATask()
    {
        Assert.Contains("elaborat", CaptureExtractionPrompt.System, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JSON", CaptureExtractionPrompt.System, StringComparison.Ordinal);
        Assert.Contains("\"tasks\"", CaptureExtractionPrompt.System, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSystemPrompt_ForbidsInventingFacts()
    {
        Assert.Contains("Do not invent", CaptureExtractionPrompt.System, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoProjects_TheUserPromptStillBuilds()
    {
        var prompt = CaptureExtractionPrompt.BuildUser(
            new CaptureRequest("Email Ms. Rivera", [], new DateOnly(2026, 9, 3)));

        Assert.Contains("Email Ms. Rivera", prompt, StringComparison.Ordinal);
        Assert.Contains("none", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
