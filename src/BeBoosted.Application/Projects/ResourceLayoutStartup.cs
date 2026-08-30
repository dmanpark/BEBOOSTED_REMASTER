namespace BeBoosted.Application.Projects;

/// <summary>What the startup layout pass did, in the detail the caller needs to report it.</summary>
/// <param name="Backfill">Segments claimed, rows left unclaimed, and why.</param>
/// <param name="Moved">Documents relocated by the sweep; always zero when it was deferred.</param>
/// <param name="ReconcileDeferred">Whether the sweep was held back rather than run.</param>
public sealed record ResourceLayoutStartupResult(
    FolderBackfillOutcome Backfill, int Moved, bool ReconcileDeferred);

/// <summary>
/// The two-step layout pass a launch performs, and the order and the condition between
/// them. This exists as production code rather than as a few lines in the app's startup
/// because the condition is the whole point: expressed at a call site no test can reach,
/// it is one careless edit away from destroying a library's folder layout, and nothing
/// would go red.
///
/// Rows persisted before migration 0012 hold an empty folder segment, and
/// <see cref="ResourceLayout.FolderFor"/> returns segments verbatim — so reconciling
/// against such a row resolves its folder to the resources root and physically moves its
/// documents there. <see cref="FolderIdentityBackfill"/> claims each row's existing
/// directory first, and the sweep runs only if it left nothing behind.
///
/// Deferring is the cheap side of that trade: documents stay exactly where they are and
/// remain usable, whereas sweeping unclaimed rows relocates them irreversibly. Note the
/// deferral is global — one row that fails deterministically holds the whole sweep back on
/// every launch, not just its own project. That is deliberate. The sweep is cosmetic and
/// its work is not lost, only postponed; healthy projects still reach it through
/// <see cref="ResourceLayoutReconciler.ReconcileProject"/> on rename.
/// </summary>
public sealed class ResourceLayoutStartup(
    FolderIdentityBackfill backfill,
    ResourceLayoutReconciler reconciler)
{
    public ResourceLayoutStartupResult Run()
    {
        var outcome = backfill.Backfill();
        if (outcome.Skipped > 0)
        {
            return new ResourceLayoutStartupResult(outcome, Moved: 0, ReconcileDeferred: true);
        }

        return new ResourceLayoutStartupResult(outcome, reconciler.Reconcile(), ReconcileDeferred: false);
    }
}
