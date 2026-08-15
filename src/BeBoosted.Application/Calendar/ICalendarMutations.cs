using BeBoosted.Domain.Calendar;

namespace BeBoosted.Application.Calendar;

/// <summary>
/// Runs a calendar block mutation together with its completion reconciliation as one
/// atomic unit: everything commits, or an exception rolls the whole mutation back and
/// rethrows. Implementations provide repositories bound to that single unit of work.
/// </summary>
public interface ICalendarMutations
{
    void Execute(Action<ICalendarBlockRepository, ICommitmentCompletionRepository> mutation);
}

/// <summary>
/// The editor's requested completion state, applied inside the same atomic save as the
/// other commitment fields. <paramref name="OpenedOccurrence"/> is the occurrence the
/// editor was opened for (a series completes per occurrence; a one-off follows its date).
/// </summary>
public sealed record CommitmentCompletionRequest(DateOnly OpenedOccurrence, bool Completed);
