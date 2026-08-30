using BeBoosted.Domain;
using BeBoosted.Domain.Planning;
using BeBoosted.Domain.Prioritization;

namespace BeBoosted.Tests.Domain;

/// <summary>
/// A plan draft never survives as an empty active plan: losing its last pending
/// block settles it as approved (when approved work remains) or discarded. A
/// discarded plan is never resurrected by reverting one of its approvals.
/// </summary>
public sealed class PlanningProposalTests
{
    private static readonly DateOnly Tuesday = new(2026, 8, 11);
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 14, 10, 0, TimeSpan.FromHours(-7));

    private static ProposedBlock Propose(int startHour)
        => new(
            CalendarBlockId.New(), TaskId.New(), Tuesday,
            new TimeOnly(startHour, 0), new TimeOnly(startHour + 1, 0),
            new WhyEvidence(null, "1 h", null, "free", null));

    private static PlanningProposal Draft(params ProposedBlock[] blocks)
        => PlanningProposal.CreateDraft(PlanningPeriod.ForToday(Tuesday), blocks, Now);

    [Fact]
    public void RemovingTheOnlyPendingBlock_DiscardsTheProposal()
    {
        var only = Propose(15);
        var proposal = Draft(only);

        proposal.RemoveBlock(only.Id, Now);

        Assert.Equal(ProposalState.Discarded, proposal.State);
        Assert.Empty(proposal.PendingBlocks);
    }

    [Fact]
    public void RemovingTheFinalPendingBlock_AfterAnApproval_ApprovesTheProposal()
    {
        var approved = Propose(15);
        var last = Propose(16);
        var proposal = Draft(approved, last);
        proposal.ApproveBlock(approved.Id, Now);
        Assert.Equal(ProposalState.Draft, proposal.State); // one pending block remains

        proposal.RemoveBlock(last.Id, Now);

        Assert.Equal(ProposalState.Approved, proposal.State);
        Assert.Empty(proposal.PendingBlocks);
    }

    /// <summary>Removal only settles a plan that has nothing left to decide.</summary>
    [Fact]
    public void RemovingOneOfSeveralPendingBlocks_KeepsTheProposalADraft()
    {
        var removed = Propose(15);
        var kept = Propose(16);
        var proposal = Draft(removed, kept);

        proposal.RemoveBlock(removed.Id, Now);

        Assert.Equal(ProposalState.Draft, proposal.State);
        Assert.Equal(kept.Id, Assert.Single(proposal.PendingBlocks).Id);
    }

    [Fact]
    public void RevertingAnApprovalOnADiscardedProposal_IsRejected()
    {
        var block = Propose(15);
        var proposal = Draft(block);
        proposal.ApproveBlock(block.Id, Now);
        proposal.Discard(Now); // replaced by a newer plan

        Assert.Throws<DomainException>(() => proposal.RevertBlock(block.Id, Now));

        Assert.Equal(ProposalState.Discarded, proposal.State);
        Assert.Equal(ProposedBlockStatus.Approved, proposal.GetBlock(block.Id).Status);
    }
}
