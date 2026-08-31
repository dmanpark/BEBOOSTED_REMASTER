using BeBoosted.Application.Projects;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Tests.Projects;

/// <summary>
/// Folder and file names come from user-typed titles, so every segment is made safe
/// for the filesystem without becoming unreadable.
/// </summary>
public sealed class ResourceLayoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 9, 0, 0, TimeSpan.FromHours(-7));

    [Theory]
    [InlineData("College Admissions", "College Admissions")]
    [InlineData("Q1/Q2 Report", "Q1-Q2 Report")]
    [InlineData("What: now?", "What- now-")]
    [InlineData("a\\b|c<d>e\"f*g", "a-b-c-d-e-f-g")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("trailing...", "trailing")]
    [InlineData("trailing dots. . .", "trailing dots")]
    public void Sanitize_MakesSegmentsSafe_WithoutManglingReadableText(string input, string expected)
        => Assert.Equal(expected, ResourceLayout.Sanitize(input, "fallback"));

    [Fact]
    public void Sanitize_ReplacesControlCharacters()
        => Assert.Equal("a-b", ResourceLayout.Sanitize("a\tb", "fallback"));

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("NUL")]
    public void Sanitize_SuffixesReservedDeviceNames(string reserved)
        => Assert.Equal(reserved + "_", ResourceLayout.Sanitize(reserved, "fallback"));

    [Fact]
    public void Sanitize_FallsBackOnlyWhenNothingSurvives()
    {
        Assert.Equal("fallback", ResourceLayout.Sanitize("   ", "fallback"));
        Assert.Equal("fallback", ResourceLayout.Sanitize(null, "fallback"));
        Assert.Equal("fallback", ResourceLayout.Sanitize(". . .", "fallback"));

        // Replaced characters still count as content: a project named "///" becomes
        // "---" rather than a meaningless id, and one genuinely named "---" survives.
        Assert.Equal("---", ResourceLayout.Sanitize("///", "fallback"));
        Assert.Equal("---", ResourceLayout.Sanitize("---", "fallback"));
    }

    [Fact]
    public void Sanitize_CapsLongSegments()
    {
        var sanitized = ResourceLayout.Sanitize(new string('x', 200), "fallback");

        Assert.Equal(80, sanitized.Length);
    }

    [Fact]
    public void FolderFor_NestsTheFilesSegmentInsideTheProjectsSegment()
    {
        var project = Project.Create("College Admissions", "#ffffff", Now);
        project.RelocateTo("College Admissions", Now);
        var file = ProjectFile.Create(project.Id, "Metric Proof", null, Now);
        file.RelocateTo("Metric Proof", Now);

        var folder = ResourceLayout.FolderFor(project, file);

        Assert.Equal(Path.Combine("College Admissions", "Metric Proof"), folder);
    }

    /// <summary>
    /// FolderFor combines the persisted segments as-is: all three were already sanitized
    /// and claimed when they were reserved, so re-sanitizing here would be redundant at
    /// best and could silently disagree with what was actually claimed on disk.
    ///
    /// Every segment used elsewhere in the suite ("Notes", "Notes (2)", "Sources") is a
    /// Sanitize fixpoint, so a re-sanitizing FolderFor would leave the whole suite green.
    /// These values are deliberately not fixpoints — that is the entire point of them.
    /// </summary>
    [Fact]
    public void FolderFor_TrustsThePersistedSegments_WithoutSanitizingThemAgain()
    {
        var project = Project.Create("College Admissions", "#ffffff", Now);
        project.RelocateTo("Not: Sanitized", Now);
        var file = ProjectFile.Create(project.Id, "Metric Proof", null, Now);
        file.RelocateTo("Also raw...", Now);
        var group = ResourceGroup.Create(file.Id, "Unit 3", 0, Now);
        group.RelocateTo("Raw: Group...", Now);

        Assert.Equal(
            Path.Combine("Not: Sanitized", "Also raw..."), ResourceLayout.FolderFor(project, file));
        Assert.Equal(
            Path.Combine("Not: Sanitized", "Also raw...", "Raw: Group..."),
            ResourceLayout.FolderFor(project, file, group));

        // Guards the guard: if these ever became fixpoints the assertions above would still
        // pass under a re-sanitizing FolderFor, and would be pinning nothing.
        Assert.NotEqual("Not: Sanitized", ResourceLayout.Sanitize("Not: Sanitized", "fallback"));
        Assert.NotEqual("Also raw...", ResourceLayout.Sanitize("Also raw...", "fallback"));
        Assert.NotEqual("Raw: Group...", ResourceLayout.Sanitize("Raw: Group...", "fallback"));
    }

    [Fact]
    public void FileNameFor_KeepsTheOriginalNameAndExtension()
        => Assert.Equal("Transcript.pdf", ResourceLayout.FileNameFor("Transcript.pdf", "fallback"));

    [Fact]
    public void FileNameFor_SanitizesTheStemButProtectsTheExtension()
    {
        Assert.Equal("a-b.pdf", ResourceLayout.FileNameFor("a?b.pdf", "fallback"));

        var capped = ResourceLayout.FileNameFor(new string('x', 200) + ".pdf", "fallback");
        Assert.EndsWith(".pdf", capped, StringComparison.Ordinal);
        Assert.Equal(84, capped.Length); // 80-char stem plus ".pdf"
    }

    [Fact]
    public void FileNameFor_StripsAnyDirectoryPartOfTheOriginalName()
        => Assert.Equal(
            "Transcript.pdf",
            ResourceLayout.FileNameFor(Path.Combine("some", "where", "Transcript.pdf"), "fallback"));

    [Fact]
    public void FileNameFor_FallsBackWhenThereIsNoUsableName()
        => Assert.Equal("fallback", ResourceLayout.FileNameFor(null, "fallback"));

    [Fact]
    public void IsAlreadyPlaced_AcceptsTheExactNameAndNumberedVariants()
    {
        var folder = Path.Combine("Proj", "File");

        Assert.True(ResourceLayout.IsAlreadyPlaced(
            Path.Combine(folder, "report.pdf"), folder, "report.pdf"));
        Assert.True(ResourceLayout.IsAlreadyPlaced(
            Path.Combine(folder, "report (2).pdf"), folder, "report.pdf"));
        Assert.True(ResourceLayout.IsAlreadyPlaced(
            Path.Combine(folder, "report (17).pdf"), folder, "report.pdf"));
    }

    [Fact]
    public void IsAlreadyPlaced_RejectsADifferentFolderNameOrExtension()
    {
        var folder = Path.Combine("Proj", "File");

        Assert.False(ResourceLayout.IsAlreadyPlaced("report.pdf", folder, "report.pdf"));
        Assert.False(ResourceLayout.IsAlreadyPlaced(
            Path.Combine("Proj", "Other", "report.pdf"), folder, "report.pdf"));
        Assert.False(ResourceLayout.IsAlreadyPlaced(
            Path.Combine(folder, "other.pdf"), folder, "report.pdf"));
        Assert.False(ResourceLayout.IsAlreadyPlaced(
            Path.Combine(folder, "report.png"), folder, "report.pdf"));
        Assert.False(ResourceLayout.IsAlreadyPlaced(
            Path.Combine(folder, "report (x).pdf"), folder, "report.pdf"));
    }
}
