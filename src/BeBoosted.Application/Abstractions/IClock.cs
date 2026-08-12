namespace BeBoosted.Application.Abstractions;

/// <summary>Injectable time source so scheduling and UI logic stay deterministic under test.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }

    DateOnly Today { get; }
}
