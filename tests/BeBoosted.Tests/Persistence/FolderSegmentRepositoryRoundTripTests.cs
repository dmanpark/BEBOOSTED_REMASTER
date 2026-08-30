using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Persistence;

/// <summary>
/// A migrated-default round-trip (folder_segment = "") cannot distinguish a working
/// write path from one that silently drops the column — SQLite's DEFAULT '' produces
/// the same observed value either way. These tests pin the write path with a value
/// that could never arise from the default: "College Admissions (2)".
/// </summary>
public sealed class FolderSegmentRepositoryRoundTripTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 9, 0, 0, TimeSpan.FromHours(-7));

    private readonly TempDatabase _database = new();

    public FolderSegmentRepositoryRoundTripTests()
        => new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());

    [Fact]
    public void Project_Add_PersistsANonEmptyFolderSegment_ThroughAFreshRepository()
    {
        var project = Project.Create("College Admissions", "#5B8DEF", Now);
        project.RelocateTo("College Admissions (2)", Now);
        new SqliteProjectRepository(_database.Factory).Add(project);

        var reloaded = new SqliteProjectRepository(_database.Factory).GetById(project.Id);

        Assert.NotNull(reloaded);
        Assert.Equal("College Admissions (2)", reloaded.FolderSegment);
    }

    [Fact]
    public void Project_Update_PersistsAChangedFolderSegment_ThroughAFreshRepository()
    {
        var project = Project.Create("College Admissions", "#5B8DEF", Now);
        project.RelocateTo("College Admissions (2)", Now);
        new SqliteProjectRepository(_database.Factory).Add(project);

        var toUpdate = new SqliteProjectRepository(_database.Factory).GetById(project.Id)!;
        toUpdate.RelocateTo("College Admissions (3)", Now.AddMinutes(5));
        new SqliteProjectRepository(_database.Factory).Update(toUpdate);

        var reloaded = new SqliteProjectRepository(_database.Factory).GetById(project.Id);

        Assert.NotNull(reloaded);
        Assert.Equal("College Admissions (3)", reloaded.FolderSegment);
    }

    [Fact]
    public void ProjectFile_Add_PersistsANonEmptyFolderSegment_ThroughAFreshRepository()
    {
        var project = Project.Create("College Admissions", "#5B8DEF", Now);
        new SqliteProjectRepository(_database.Factory).Add(project);

        var file = ProjectFile.Create(project.Id, "Transcripts", null, Now);
        file.RelocateTo("College Admissions (2)", Now);
        new SqliteProjectFileRepository(_database.Factory).Add(file);

        var reloaded = new SqliteProjectFileRepository(_database.Factory).GetById(file.Id);

        Assert.NotNull(reloaded);
        Assert.Equal("College Admissions (2)", reloaded.FolderSegment);
    }

    [Fact]
    public void ProjectFile_Update_PersistsAChangedFolderSegment_ThroughAFreshRepository()
    {
        var project = Project.Create("College Admissions", "#5B8DEF", Now);
        new SqliteProjectRepository(_database.Factory).Add(project);

        var file = ProjectFile.Create(project.Id, "Transcripts", null, Now);
        file.RelocateTo("College Admissions (2)", Now);
        new SqliteProjectFileRepository(_database.Factory).Add(file);

        var toUpdate = new SqliteProjectFileRepository(_database.Factory).GetById(file.Id)!;
        toUpdate.RelocateTo("College Admissions (3)", Now.AddMinutes(5));
        new SqliteProjectFileRepository(_database.Factory).Update(toUpdate);

        var reloaded = new SqliteProjectFileRepository(_database.Factory).GetById(file.Id);

        Assert.NotNull(reloaded);
        Assert.Equal("College Admissions (3)", reloaded.FolderSegment);
    }

    public void Dispose() => _database.Dispose();
}
