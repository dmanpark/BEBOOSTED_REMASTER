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
    IClock clock)
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

            var folder = ResourceLayout.FolderFor(project, file);
            var fileResources = resources.GetForFile(file.Id);
            foreach (var resource in fileResources)
            {
                if (resource.StoredPath is not { } current)
                {
                    continue; // links and notes have no bytes
                }

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
                        relocated = FindUnrecordedPlacement(folder, desired, claimed);
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

    private string? FindUnrecordedPlacement(string folder, string desired, HashSet<string> claimed)
    {
        for (var attempt = 1; ; attempt++)
        {
            var candidate = Path.Combine(folder, ResourceLayout.CandidateName(desired, attempt));
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
