using BeBoosted.Application.Abstractions;

namespace BeBoosted.Application.Projects;

/// <summary>
/// What one backfill run achieved and what it left behind. <paramref name="Skipped"/> is
/// the load-bearing half: a caller must not reconcile while any row still holds the empty
/// sentinel, because <see cref="ResourceLayout.FolderFor"/> resolves such a row to the
/// resources root and the reconciler would move its documents there.
/// </summary>
public readonly record struct FolderBackfillOutcome(int Claimed, int Skipped);

/// <summary>
/// Gives every Project and File persisted before migration 0012 the folder segment its
/// bytes already occupy. Those rows hold the empty sentinel, and <see
/// cref="ResourceLayout.FolderFor"/> returns persisted segments verbatim, so an
/// un-backfilled database resolves every folder to the resources root — reconciling
/// against it would flatten every legacy document into that root. This runs first and
/// makes the reconciler a no-op for anything already in the right place.
///
/// The reservation is deliberately not the naive one. A legacy Project's directory is
/// already on disk, so an ordinary reservation reads it as occupied and hands back
/// "College Admissions (2)" — moving every document the migration was meant to leave
/// alone. The derived segment is therefore offered as <c>ownedSegment</c>: provisionally
/// this entity's own rather than an obstacle. A sibling claim still outranks that, since
/// two entities cannot both own one directory, which is why every reserved segment joins
/// <c>claimed</c> as it is taken.
///
/// Like <see cref="ResourceLayoutReconciler"/> this is cosmetic bookkeeping and recovers
/// per entity rather than aborting. A transient fault would converge on its own — each row
/// is persisted the moment its directory is claimed, and the next run seeds <c>claimed</c>
/// from those persisted segments and carries on — but a deterministic one (a path-length
/// limit, an ACL, a rejected update) would meet the same row on every launch and strand
/// every row behind it forever. A skipped entity keeps the sentinel and costs only itself.
///
/// Recovering rather than throwing costs two guarantees the throw used to provide for
/// free, and both are restored explicitly. Inside, <see cref="BackfillFiles"/> skips any
/// Project still holding the sentinel, so no File ever reserves beneath the empty string.
/// Outside, the skipped count in <see cref="FolderBackfillOutcome"/> tells the caller not
/// to reconcile yet: an escaping exception used to stop the sweep, and a swallowed one
/// would let it run against exactly the unclaimed rows described above.
/// </summary>
public sealed class FolderIdentityBackfill(
    IProjectRepository projects,
    IProjectFileRepository files,
    IResourceStorage storage,
    IClock clock)
{
    /// <summary>
    /// Backfills every unclaimed row, reporting both what it claimed and what it could not.
    ///
    /// Two separate passes, and they must stay that way: a File is reserved inside its
    /// Project's claimed segment, so a File reached before its Project's backfill would
    /// resolve its parent to the empty sentinel and claim a directory in the resources
    /// root instead. Projects first, all of them, before any File is touched.
    /// </summary>
    public FolderBackfillOutcome Backfill()
    {
        var projectPass = BackfillProjects();
        var filePass = BackfillFiles();
        return new FolderBackfillOutcome(
            projectPass.Claimed + filePass.Claimed,
            projectPass.Skipped + filePass.Skipped);
    }

    private FolderBackfillOutcome BackfillProjects()
    {
        var all = projects.GetAll();

        // Seeded with the segments live Projects already hold: provisional ownership
        // would otherwise let a legacy row walk straight into an occupied folder.
        var claimed = ClaimedSegments(all.Select(project => project.FolderSegment));

        var filled = 0;
        var skipped = 0;
        foreach (var project in all.Where(project => project.FolderSegment.Length == 0))
        {
            try
            {
                var preferred = ResourceLayout.Sanitize(project.Name, project.Id.ToString());
                var reserved = storage.ReserveFolderSegment(
                    string.Empty, preferred, claimed, ownedSegment: preferred);
                claimed.Add(reserved);
                project.RelocateTo(reserved, clock.Now);
                projects.Update(project);
                filled++;
            }
            catch (Exception)
            {
                // A deterministic wall on one folder name — a path-length limit, an ACL,
                // a rejected update — would otherwise strand every later row against the
                // same obstacle on every launch. This Project keeps the sentinel and is
                // retried on the next run. Counted, not logged: this class takes no
                // logger, and the count reaches the caller that can report it.
                skipped++;
            }
        }

        return new FolderBackfillOutcome(filled, skipped);
    }

    /// <summary>
    /// Runs only after <see cref="BackfillProjects"/>, and re-reads the Projects so every
    /// parent path is a persisted, claimed segment rather than an in-flight one.
    /// </summary>
    private FolderBackfillOutcome BackfillFiles()
    {
        var filled = 0;
        var skipped = 0;
        foreach (var project in projects.GetAll())
        {
            if (project.FolderSegment.Length == 0)
            {
                // Its own backfill failed. Reserving beneath the sentinel would put this
                // File's folder in the resources root, so the whole Project waits for the
                // next run. Running the passes in order is what makes this the only way a
                // Project can still be unclaimed by the time its Files are reached.
                skipped += files.GetForProject(project.Id).Count(file => file.FolderSegment.Length == 0);
                continue;
            }

            var siblings = files.GetForProject(project.Id);

            // One set per Project, not one for the whole store: two Files of different
            // Projects live in different folders and may legitimately share a segment.
            var claimed = ClaimedSegments(siblings.Select(file => file.FolderSegment));

            foreach (var file in siblings.Where(file => file.FolderSegment.Length == 0))
            {
                try
                {
                    var preferred = ResourceLayout.Sanitize(file.Title, file.Id.ToString());
                    var reserved = storage.ReserveFolderSegment(
                        project.FolderSegment, preferred, claimed, ownedSegment: preferred);
                    claimed.Add(reserved);
                    file.RelocateTo(reserved, clock.Now);
                    files.Update(file);
                    filled++;
                }
                catch (Exception)
                {
                    // Same bargain as a Project: one unclaimable File keeps the sentinel
                    // and is retried, rather than stranding its siblings behind it.
                    skipped++;
                }
            }
        }

        return new FolderBackfillOutcome(filled, skipped);
    }

    /// <summary>
    /// The already-taken segments, ignoring the empty sentinel. Case-insensitive to match
    /// <see cref="ResourceLayout.IsAlreadyPlaced"/>: the storage layer's disk probe makes
    /// the comparer moot on Windows, but this app also publishes osx-arm64, where a
    /// case-sensitive filesystem would happily hand out "Notes" beside "notes".
    /// </summary>
    private static HashSet<string> ClaimedSegments(IEnumerable<string> segments)
        => segments.Where(segment => segment.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
