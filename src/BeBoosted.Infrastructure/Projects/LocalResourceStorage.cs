using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Projects;
using BeBoosted.Domain;

namespace BeBoosted.Infrastructure.Projects;

/// <summary>
/// Stores imported resource bytes under the app-controlled resources directory with
/// stable id-based names (e.g. resources/&lt;guid&gt;.pdf).
/// </summary>
public sealed class LocalResourceStorage(IAppDataPaths paths) : IResourceStorage
{
    public string Store(ResourceId id, string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new DomainException($"The file '{sourcePath}' could not be found.");
        }

        Directory.CreateDirectory(paths.ResourcesDirectory);
        var storedPath = id + Path.GetExtension(sourcePath).ToLowerInvariant();
        File.Copy(sourcePath, ResolvePath(storedPath), overwrite: true);
        return storedPath;
    }

    public string ResolvePath(string storedPath) => Path.Combine(paths.ResourcesDirectory, storedPath);

    public bool Exists(string storedPath) => File.Exists(ResolvePath(storedPath));

    public void Delete(string storedPath)
    {
        try
        {
            File.Delete(ResolvePath(storedPath));
        }
        catch (IOException)
        {
            // Best effort: an orphaned byte file is preferable to a failed delete flow.
        }
    }
}
