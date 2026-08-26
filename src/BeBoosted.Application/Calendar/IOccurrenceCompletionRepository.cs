using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;

namespace BeBoosted.Application.Calendar;

/// <summary>Per-occurrence completion records for repeating local task sessions.</summary>
public interface IOccurrenceCompletionRepository
{
    /// <summary>Records (or re-records) a completed occurrence — an upsert per occurrence.</summary>
    void Add(OccurrenceCompletion completion);

    /// <summary>Reopens the occurrence; a missing row is a quiet no-op.</summary>
    void Remove(CalendarBlockId blockId, DateOnly occurrenceDate);

    OccurrenceCompletion? Get(CalendarBlockId blockId, DateOnly occurrenceDate);

    /// <summary>Every completed occurrence of one session.</summary>
    IReadOnlyList<OccurrenceCompletion> GetForBlock(CalendarBlockId blockId);

    /// <summary>Completions whose occurrence date falls within [from, to].</summary>
    IReadOnlyList<OccurrenceCompletion> GetBetween(DateOnly from, DateOnly to);
}
