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
        foreach (var file in files.GetForProject(project.Id))
        {
            var folder = ResourceLayout.FolderFor(project, file);
            var fileResources = resources.GetForFile(file.Id);
            var claimed = fileResources
                .Select(r => r.StoredPath)
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
