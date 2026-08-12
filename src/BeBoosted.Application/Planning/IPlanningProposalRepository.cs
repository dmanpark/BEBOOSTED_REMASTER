using BeBoosted.Domain.Planning;

namespace BeBoosted.Application.Planning;

public interface IPlanningProposalRepository
{
    /// <summary>Inserts or fully replaces the proposal and its blocks.</summary>
    void Save(PlanningProposal proposal);

    PlanningProposal? GetById(Domain.PlanningProposalId id);

    /// <summary>The single active draft, if any (only one draft exists at a time).</summary>
    PlanningProposal? GetActiveDraft();

    void Delete(Domain.PlanningProposalId id);
}
