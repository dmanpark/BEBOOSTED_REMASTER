using System.Reflection;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;

namespace BeBoosted.Tests.Calendar;

/// <summary>
/// Guards the completion authority at the API surface: no public Application-layer
/// service other than <see cref="CalendarService"/> may expose a public task
/// completion, reopen, or deletion method, so production code cannot bypass the
/// aggregate reconciliation and transactional deletion. (Domain entities keep
/// their own transitions, and repository ports stay contracts for the
/// infrastructure — the scan covers concrete service classes.)
/// </summary>
public sealed class CompletionApiSurfaceTests
{
    [Fact]
    public void TheApplicationLayer_ExposesNoTaskCompletionOrDeletionApi_OutsideTheCalendarService()
    {
        var offenders = typeof(TaskService).Assembly.GetTypes()
            .Where(type => type.IsClass && type.IsPublic && type != typeof(CalendarService))
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.Name
                    is "Complete" or "Reopen" or "CompleteTask" or "ReopenTask"
                    or "Delete" or "DeleteTask"
                && method.GetParameters().Any(p => p.ParameterType == typeof(TaskId)))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .ToList();

        Assert.Empty(offenders);
    }
}
