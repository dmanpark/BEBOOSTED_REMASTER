using BeBoosted.Application.Abstractions;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Application.Projects;

/// <summary>
/// Moves stored documents into the layout <see cref="ResourceLayout"/> describes, and
/// records where they went. This is both the one-time migration for documents imported
/// under the old id-based names and the rename-sync afterwards — one mechanism, so the
/// two can never disagree.
///
/// A filesystem move and a database write cannot commit together, so each resource is
/// moved first and recorded only on success: the database never names a file that is
/// not there. A resource that cannot be moved keeps its current path, stays usable, and
/// is retried on the next run.
/// </summary>
public sealed class ResourceLayoutReconciler(
    IProjectRepository projects,
    IProjectFileRepository files,
    IResourceRepository resources,
    IResourceStorage storage,
    IClock clock,
    IResourceGroupRepository groups)
{
    /// <summary>Reconciles every project. Returns how many resources actually moved.</summary>
    public int Reconcile() => projects.GetAll().Sum(Reconcile);

    /// <summary>Reconciles one project. Returns how many resources actually moved.</summary>
    public int ReconcileProject(ProjectId id)
        => projects.GetById(id) is { } project ? Reconcile(project) : 0;

    private int Reconcile(Project project)
    {
        var moved = 0;

        // Every currently recorded stored path, across every Project and File — not just
        // the one File being processed, and not just this Project. A narrower set lets
        // FindUnrecordedPlacement below adopt a file that genuinely belongs to another
        // File or Project: the rename path reconciles a single Project through
        // ReconcileProject, which never walks the other owner's rows, so this set is the
        // only thing standing between an adoption and a sibling's resource.
        var claimed = AllRecordedStoredPaths();

        foreach (var file in files.GetForProject(project.Id))
        {
            if (file.FolderSegment.Length == 0 && project.FolderSegment.Length != 0)
            {
                // Half-backfilled: the Project holds a claimed segment, this File never
                // got one. FolderFor would combine them into the Project's own folder and
                // flatten every document here into it. Nothing legitimate produces this —
                // CreateFile always reserves — so it means FolderIdentityBackfill skipped
                // this Project and a rename has since given it a segment. Wait for the
                // backfill to finish the job; the documents stay usable meanwhile.
                //
                // Deliberately narrow: BOTH segments empty is the pure pre-0012 state and
                // must still reconcile.
                continue;
            }

            var fileGroups = groups.GetForFile(file.Id).ToDictionary(g => g.Id);

            // The directories this File's groups have reserved. A loose resource's desired
            // name can be one of them, in which case its bytes were parked at the numbered
            // candidate beside it — and FindUnrecordedPlacement, probing with a file-only
            // Exists, would read the directory as "nothing here" and stop one slot short.
            // Naming the claims outright lets the probe step over them; it stays a probe of
            // known reserved paths, not a scan for orphans and never an adoption of a folder.
            var directoryClaims = fileGroups.Values.Where(g => g.FolderSegment.Length > 0)
                .Select(g => ResourceLayout.FolderFor(project, file, g))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var fileResources = resources.GetForFile(file.Id);
            foreach (var resource in fileResources)
            {
                if (resource.StoredPath is not { } current)
                {
                    continue; // links and notes have no bytes
                }

                ResourceGroup? group = null;
                if (resource.GroupId is { } groupId)
                {
                    // A membership the layout cannot resolve is never treated as loose.
                    // Every branch here would otherwise combine into a folder no
                    // reservation ever claimed — and, because Path.Combine swallows an
                    // empty part, the unclaimed-parent and unclaimed-segment branches
                    // collapse silently onto the File root, moving a filed document out
                    // of its group. Leaving it exactly where it is costs nothing: it
                    // stays openable, and the next run retries once the row is repaired.
                    if (project.FolderSegment.Length == 0 || file.FolderSegment.Length == 0
                        || !fileGroups.TryGetValue(groupId, out group)
                        || group.FileId != file.Id || group.FolderSegment.Length == 0)
                    {
                        continue;
                    }
                }

                var folder = ResourceLayout.FolderFor(project, file, group);
                var desired = ResourceLayout.FileNameFor(resource.OriginalFileName, resource.Id.ToString());
                if (ResourceLayout.IsAlreadyPlaced(current, folder, desired))
                {
                    continue;
                }

                try
                {
                    var relocated = storage.MoveInto(current, folder, desired);
                    if (relocated is null && !storage.Exists(current))
                    {
                        // The bytes are gone from the recorded path. A move that was
                        // completed but never recorded left them at the desired
                        // location — adopt that file rather than stranding it, but
                        // never a slot another resource's recorded path claims.
                        relocated = FindUnrecordedPlacement(folder, desired, claimed, directoryClaims);
                    }

                    if (relocated is null)
                    {
                        continue; // locked or missing: retried next run
                    }

                    resource.RelocateTo(relocated, clock.Now);
                    resources.Update(resource);
                    claimed.Remove(current);
                    claimed.Add(relocated);
                    moved++;
                }
                catch (Exception)
                {
                    // A per-resource failure (a rejected update, a repository error)
                    // must never abort the pass: earlier moves stay recorded, later
                    // resources still get their turn, and this one is retried — or
                    // adopted — on the next run.
                }
            }
        }

        return moved;
    }

    private HashSet<string> AllRecordedStoredPaths()
        => projects.GetAll()
            .SelectMany(p => files.GetForProject(p.Id))
            .SelectMany(f => resources.GetForFile(f.Id))
            .Select(r => r.StoredPath)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private string? FindUnrecordedPlacement(
        string folder, string desired, HashSet<string> claimed, IReadOnlySet<string> directoryClaims)
    {
        for (var attempt = 1; ; attempt++)
        {
            var candidate = Path.Combine(folder, ResourceLayout.CandidateName(desired, attempt));
            if (directoryClaims.Contains(candidate))
            {
                // A group reserved this exact name as a directory, so the mover skipped
                // past it too. Step over rather than stop: the bytes are at a later
                // candidate, and the file-only Exists below would read a directory as
                // "missing" and end the probe short of them.
                continue;
            }

            if (!storage.Exists(candidate))
            {
                return null;
            }

            if (!claimed.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
