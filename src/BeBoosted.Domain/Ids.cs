namespace BeBoosted.Domain;

public readonly record struct TaskId(Guid Value)
{
    public static TaskId New() => new(Guid.NewGuid());

    public static TaskId Parse(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString();
}

public readonly record struct ProjectId(Guid Value)
{
    public static ProjectId New() => new(Guid.NewGuid());

    public static ProjectId Parse(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString();
}

public readonly record struct ProjectFileId(Guid Value)
{
    public static ProjectFileId New() => new(Guid.NewGuid());

    public static ProjectFileId Parse(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString();
}

public readonly record struct ResourceId(Guid Value)
{
    public static ResourceId New() => new(Guid.NewGuid());

    public static ResourceId Parse(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString();
}

public readonly record struct CalendarBlockId(Guid Value)
{
    public static CalendarBlockId New() => new(Guid.NewGuid());

    public static CalendarBlockId Parse(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString();
}

public readonly record struct PlanningProposalId(Guid Value)
{
    public static PlanningProposalId New() => new(Guid.NewGuid());

    public static PlanningProposalId Parse(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString();
}

public readonly record struct ComparisonId(Guid Value)
{
    public static ComparisonId New() => new(Guid.NewGuid());

    public static ComparisonId Parse(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString();
}

public readonly record struct AiProvenanceId(Guid Value)
{
    public static AiProvenanceId New() => new(Guid.NewGuid());

    public static AiProvenanceId Parse(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString();
}
