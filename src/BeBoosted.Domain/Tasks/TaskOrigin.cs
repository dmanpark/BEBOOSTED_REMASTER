namespace BeBoosted.Domain.Tasks;

/// <summary>Who created a task. AI-created tasks keep this origin forever.</summary>
public enum TaskOrigin
{
    User = 0,
    Ai = 1,
}
