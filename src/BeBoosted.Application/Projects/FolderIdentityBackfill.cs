using BeBoosted.Application.Abstractions;

namespace BeBoosted.Application.Projects;

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
/// That is why <see cref="BackfillFiles"/> states the invariant the old abort-everything
/// shape held implicitly: a Project still holding the sentinel has its Files skipped too,
/// so no File ever reserves beneath the empty string.
/// </summary>
public sealed class FolderIdentityBackfill(
    IProjectRepository projects,
    IProjectFileRepository files,
    IResourceStorage storage,
    IClock clock)
{
    /// <summary>
    /// Backfills every unclaimed row and returns how many segments were claimed.
    ///
    /// Two separate passes, and they must stay that way: a File is reserved inside its
    /// Project's claimed segment, so a File reached before its Project's backfill would
    /// resolve its parent to the empty sentinel and claim a directory in the resources
    /// root instead. Projects first, all of them, before any File is touched.
    /// </summary>
    public int Backfill() => BackfillProjects() + BackfillFiles();

    private int BackfillProjects()
    {
        var all = projects.GetAll();

        // Seeded with the segments live Projects already hold: provisional ownership
        // would otherwise let a legacy row walk straight into an occupied folder.
        var claimed = ClaimedSegments(all.Select(project => project.FolderSegment));

        var filled = 0;
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
                // same obstacle on every launch, and take the reconciler down with it.
                // This Project keeps the sentinel and is retried on the next run.
            }
        }

        return filled;
    }

    /// <summary>
    /// Runs only after <see cref="BackfillProjects"/>, and re-reads the Projects so every
    /// parent path is a persisted, claimed segment rather than an in-flight one.
    /// </summary>
    private int BackfillFiles()
    {
        var filled = 0;
        foreach (var project in projects.GetAll())
        {
            if (project.FolderSegment.Length == 0)
            {
                // Its own backfill failed. Reserving beneath the sentinel would put this
                // File's folder in the resources root, so the whole Project waits for the
                // next run. Running the passes in order is what makes this the only way a
                // Project can still be unclaimed by the time its Files are reached.
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
                }
            }
        }

        return filled;
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
