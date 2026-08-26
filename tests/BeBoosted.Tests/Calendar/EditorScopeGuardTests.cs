using System.Reflection;
using BeBoosted.Application.Calendar;

namespace BeBoosted.Tests.Calendar;

/// <summary>No silent task-to-session selection path may return (F-03 root cause).</summary>
public sealed class EditorScopeGuardTests
{
    [Fact]
    public void CalendarService_ExposesNoCombinedSave_AndNoEditableSessionSelection()
    {
        var names = typeof(CalendarService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name).ToHashSet();
        Assert.DoesNotContain("UpdateTask", names);
        Assert.DoesNotContain("GetEditableSessionForTask", names);
        Assert.Contains("UpdateTaskDetails", names);
        Assert.Contains("UpdateSessionSchedule", names);
        Assert.Contains("AddSession", names);
        Assert.Contains("UnscheduleAllSessions", names);
    }
}
