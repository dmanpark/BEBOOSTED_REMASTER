namespace BeBoosted.Domain.Prioritization;

/// <summary>
/// The adaptive pairwise comparison flow (Beli-inspired). Tasks are placed one at a time
/// by binary insertion among the already-ordered groups, so every question is maximally
/// informative and the total stays near n·log n. "Too tough to decide" records a real
/// tie: the task joins the pivot's group and both share the ordinal rank.
///
/// The session is a pure, deterministic function of (candidates, answers): undo pops the
/// last answer and replays, which keeps undo trivially correct.
/// </summary>
public sealed class ComparisonSession
{
    private readonly List<TaskId> _candidates;
    private readonly List<ComparisonResult> _answers = [];
    private readonly List<(TaskId Left, TaskId Right, ComparisonResult Result)> _decisions = [];

    private List<List<TaskId>> _groups = [];
    private int _nextCandidateIndex;
    private TaskId? _inserting;
    private int _low;
    private int _high;

    public ComparisonSession(PlanningPeriod period, IEnumerable<TaskId> candidates)
    {
        Period = period;
        _candidates = candidates.Distinct().ToList();
        if (_candidates.Count == 0)
        {
            throw new DomainException("Priority Sort needs at least one task.");
        }

        Replay();
    }

    public PlanningPeriod Period { get; }

    /// <summary>The question currently on screen; null when the ordering is fully resolved.</summary>
    public (TaskId Left, TaskId Right)? CurrentComparison { get; private set; }

    public bool IsComplete => CurrentComparison is null;

    /// <summary>1-based number of the comparison being asked (or of the last one when complete).</summary>
    public int ComparisonNumber => Math.Max(_answers.Count + (IsComplete ? 0 : 1), 1);

    public int AnsweredCount => _answers.Count;

    public bool CanUndo => _answers.Count > 0;

    /// <summary>Pairs answered so far, in order — persisted when the session finishes.</summary>
    public IReadOnlyList<(TaskId Left, TaskId Right, ComparisonResult Result)> Decisions => _decisions;

    /// <summary>
    /// Estimated completeness in [0,1] for the progress strip. An estimate of remaining
    /// informative questions — never a fabricated precise score. It generally rises with
    /// each answer and reaches exactly 1 when the ordering is resolved.
    /// </summary>
    public double Progress
    {
        get
        {
            if (IsComplete)
            {
                return 1;
            }

            var done = _answers.Count;
            return (double)done / (done + EstimateRemaining());
        }
    }

    public void Record(ComparisonResult result)
    {
        if (CurrentComparison is not { } comparison)
        {
            throw new DomainException("The ordering is already resolved.");
        }

        _answers.Add(result);
        _decisions.Add((comparison.Left, comparison.Right, result));
        Apply(result);
        AdvanceToNextQuestion();
    }

    /// <summary>Reverts the latest comparison. Returns false when there is nothing to undo.</summary>
    public bool Undo()
    {
        if (_answers.Count == 0)
        {
            return false;
        }

        _answers.RemoveAt(_answers.Count - 1);
        _decisions.RemoveAt(_decisions.Count - 1);
        Replay();
        return true;
    }

    /// <summary>
    /// Ranks from the resolved ordering. Available at any time ("Build my plan now"):
    /// tasks not yet placed share the trailing ordinal. Dense ranking (1, 2, 2, 3);
    /// tiers split the list into rough thirds without ever splitting a tied group.
    /// </summary>
    public IReadOnlyList<PriorityRank> BuildRanking()
    {
        var groups = _groups.Select(g => (IReadOnlyList<TaskId>)[.. g]).ToList();

        var unplaced = new List<TaskId>();
        if (_inserting is { } inserting)
        {
            unplaced.Add(inserting);
        }

        for (var i = _nextCandidateIndex; i < _candidates.Count; i++)
        {
            unplaced.Add(_candidates[i]);
        }

        if (unplaced.Count > 0)
        {
            groups.Add(unplaced);
        }

        var total = groups.Sum(g => g.Count);
        var ranks = new List<PriorityRank>(total);
        var rank = 0;
        var seen = 0;
        foreach (var group in groups)
        {
            rank++;
            var tier = seen < Math.Ceiling(total / 3.0)
                ? PlanningTier.ProtectNow
                : seen < Math.Ceiling(total * 2 / 3.0)
                    ? PlanningTier.AdvanceNext
                    : PlanningTier.CanWait;
            foreach (var taskId in group)
            {
                ranks.Add(new PriorityRank(taskId, rank, tier));
            }

            seen += group.Count;
        }

        return ranks;
    }

    private void Replay()
    {
        _groups = [[_candidates[0]]];
        _nextCandidateIndex = 1;
        _inserting = null;
        var pending = new Queue<ComparisonResult>(_answers);
        AdvanceToNextQuestion();
        while (pending.Count > 0)
        {
            Apply(pending.Dequeue());
            AdvanceToNextQuestion();
        }
    }

    private void Apply(ComparisonResult result)
    {
        var mid = (_low + _high) / 2;
        switch (result)
        {
            case ComparisonResult.LeftWins:
                _high = mid;
                break;
            case ComparisonResult.RightWins:
                _low = mid + 1;
                break;
            case ComparisonResult.Tie:
                _groups[mid].Add(_inserting!.Value);
                _inserting = null;
                break;
        }
    }

    private void AdvanceToNextQuestion()
    {
        while (true)
        {
            if (_inserting is { } inserting)
            {
                if (_low >= _high)
                {
                    _groups.Insert(_low, [inserting]);
                    _inserting = null;
                    continue;
                }

                var mid = (_low + _high) / 2;
                CurrentComparison = (inserting, _groups[mid][0]);
                return;
            }

            if (_nextCandidateIndex >= _candidates.Count)
            {
                CurrentComparison = null;
                return;
            }

            _inserting = _candidates[_nextCandidateIndex++];
            _low = 0;
            _high = _groups.Count;
        }
    }

    private int EstimateRemaining()
    {
        var remaining = 0;
        if (_inserting is not null && _high > _low)
        {
            remaining += (int)Math.Ceiling(Math.Log2(_high - _low + 1));
        }

        var groupCount = _groups.Count + (_inserting is null ? 0 : 1);
        for (var i = _nextCandidateIndex; i < _candidates.Count; i++)
        {
            remaining += Math.Max((int)Math.Ceiling(Math.Log2(groupCount + 1)), 1);
            groupCount++;
        }

        return Math.Max(remaining, 1);
    }
}
