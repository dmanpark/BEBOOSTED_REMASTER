using BeBoosted.Domain;

namespace BeBoosted.Application.Projects;

/// <summary>
/// Byte storage for imported documents and images: an application-controlled directory
/// keyed by stable resource ids, so citations survive reopening the application.
/// </summary>
public interface IResourceStorage
{
    /// <summary>Copies the source file in and returns the stored path (relative to the resources root).</summary>
    string Store(ResourceId id, string sourcePath);

    /// <summary>Absolute path for a stored resource.</summary>
    string ResolvePath(string storedPath);

    bool Exists(string storedPath);

    void Delete(string storedPath);
}
