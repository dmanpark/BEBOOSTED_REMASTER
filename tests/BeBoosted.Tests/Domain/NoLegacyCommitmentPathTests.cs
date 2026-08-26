using System.Reflection;
using BeBoosted.Application.Calendar;
using BeBoosted.Domain.Calendar;

namespace BeBoosted.Tests.Domain;

/// <summary>
/// TDD phase 6: after unification no production path may create a new legacy local
/// "fixed commitment". The only creatable local block is a task-backed session; the
/// legacy factory and the commitment-specific service surface must be gone.
/// </summary>
public sealed class NoLegacyCommitmentPathTests
{
    private static IEnumerable<string> PublicMethodNames(Type type)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(m => m.Name);

    [Fact]
    public void CalendarBlock_HasNoFixedCommitmentFactory()
    {
        Assert.DoesNotContain("CreateFixedCommitment", PublicMethodNames(typeof(CalendarBlock)));
    }

    [Fact]
    public void CalendarService_HasNoCommitmentSurface()
    {
        var methods = PublicMethodNames(typeof(CalendarService)).ToList();
        Assert.DoesNotContain("CreateFixedCommitment", methods);
        Assert.DoesNotContain("UpdateFixedCommitment", methods);
        Assert.DoesNotContain("DeleteLocalCommitment", methods);
    }

    [Fact]
    public void BlockKind_ExposesNoUserFacingFixedFlexSplit()
    {
        var names = Enum.GetNames(typeof(BlockKind));
        Assert.DoesNotContain("FixedCommitment", names);
        Assert.DoesNotContain("TaskBlock", names);
    }
}
