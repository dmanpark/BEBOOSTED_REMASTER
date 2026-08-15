using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;

namespace BeBoosted.Application.Calendar;

/// <summary>Per-occurrence completion records for local fixed commitments.</summary>
public interface ICommitmentCompletionRepository
{
    /// <summary>Records (or re-records) a completed occurrence — an upsert per occurrence.</summary>
    void Add(CommitmentCompletion completion);

    /// <summary>Reopens the occurrence; a missing row is a quiet no-op.</summary>
    void Remove(CalendarBlockId blockId, DateOnly occurrenceDate);

    CommitmentCompletion? Get(CalendarBlockId blockId, DateOnly occurrenceDate);

    /// <summary>Every completed occurrence of one block.</summary>
    IReadOnlyList<CommitmentCompletion> GetForBlock(CalendarBlockId blockId);

    /// <summary>Completions whose occurrence date falls within [from, to].</summary>
    IReadOnlyList<CommitmentCompletion> GetBetween(DateOnly from, DateOnly to);
}
