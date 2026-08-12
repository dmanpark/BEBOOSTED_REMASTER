using BeBoosted.Domain;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Tasks;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Tasks;

public sealed class SqliteTaskRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 14, 0, 0, TimeSpan.FromHours(-7));

    private readonly TempDatabase _database = new();
    private readonly SqliteTaskRepository _repository;

    public SqliteTaskRepositoryTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _repository = new SqliteTaskRepository(_database.Factory);
    }

    [Fact]
    public void AddAndGetById_RoundTripsEveryField()
    {
        var projectId = ProjectId.New();
        var provenanceId = AiProvenanceId.New();
        var task = TaskItem.Create(
            "Finish DECA presentation",
            Now,
            TaskOrigin.Ai,
            estimatedDuration: TimeSpan.FromMinutes(90),
            deadline: new DateOnly(2026, 8, 14),
            projectId: projectId,
            constraints: new SchedulingConstraints(
                notBefore: new DateOnly(2026, 8, 12),
                earliestTime: new TimeOnly(15, 0),
                latestTime: new TimeOnly(21, 30)),
            recurrence: RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Thursday),
            provenanceId: provenanceId);

        _repository.Add(task);
        var loaded = _repository.GetById(task.Id);

        Assert.NotNull(loaded);
        Assert.Equal(task.Title, loaded.Title);
        Assert.Equal(TimeSpan.FromMinutes(90), loaded.EstimatedDuration);
        Assert.Equal(new DateOnly(2026, 8, 14), loaded.Deadline);
        Assert.Equal(projectId, loaded.ProjectId);
        Assert.Equal(new DateOnly(2026, 8, 12), loaded.Constraints!.NotBefore);
        Assert.Equal(new TimeOnly(15, 0), loaded.Constraints.EarliestTime);
        Assert.Equal(new TimeOnly(21, 30), loaded.Constraints.LatestTime);
        Assert.Equal(RecurrenceFrequency.Weekly, loaded.Recurrence!.Frequency);
        Assert.Equal(
            [DayOfWeek.Tuesday, DayOfWeek.Thursday],
            loaded.Recurrence.DaysOfWeek.OrderBy(d => d));
        Assert.Equal(TaskOrigin.Ai, loaded.Origin);
        Assert.Equal(provenanceId, loaded.ProvenanceId);
        Assert.False(loaded.IsCompleted);
        Assert.Equal(task.CreatedAt, loaded.CreatedAt);
        Assert.Equal(task.ModifiedAt, loaded.ModifiedAt);
    }

    [Fact]
    public void Update_PersistsChanges()
    {
        var task = TaskItem.Create("Draft essay outline", Now);
        _repository.Add(task);

        task.Rename("Draft college essay outline", Now.AddMinutes(1));
        task.Complete(Now.AddMinutes(2));
        _repository.Update(task);

        var loaded = _repository.GetById(task.Id);
        Assert.Equal("Draft college essay outline", loaded!.Title);
        Assert.True(loaded.IsCompleted);
        Assert.Equal(Now.AddMinutes(2), loaded.CompletedAt);
    }

    [Fact]
    public void Update_ThrowsWhenTaskWasDeleted()
    {
        var task = TaskItem.Create("Gone", Now);
        Assert.Throws<DomainException>(() => _repository.Update(task));
    }

    [Fact]
    public void GetInbox_ReturnsOpenTasksInCaptureOrder()
    {
        var first = TaskItem.Create("First", Now);
        var second = TaskItem.Create("Second", Now.AddMinutes(1));
        var done = TaskItem.Create("Done", Now.AddMinutes(2));
        done.Complete(Now.AddMinutes(3));
        _repository.Add(second);
        _repository.Add(first);
        _repository.Add(done);

        var inbox = _repository.GetInbox();

        Assert.Equal([first.Id, second.Id], inbox.Select(t => t.Id));
    }

    [Fact]
    public void Delete_RemovesTask()
    {
        var task = TaskItem.Create("Temp", Now);
        _repository.Add(task);
        _repository.Delete(task.Id);

        Assert.Null(_repository.GetById(task.Id));
    }

    public void Dispose() => _database.Dispose();
}
