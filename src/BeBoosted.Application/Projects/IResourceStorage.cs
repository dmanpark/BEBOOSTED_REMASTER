namespace BeBoosted.Application.Projects;

/// <summary>
/// Byte storage for imported documents and images: an application-controlled directory
/// laid out one folder per Project and File, holding each document under the user's own
/// file name, so the folder is browsable outside the app.
/// </summary>
public interface IResourceStorage
{
    /// <summary>
    /// Copies the source file into <paramref name="relativeFolder"/> under
    /// <paramref name="preferredFileName"/>, disambiguating on collision. Returns the
    /// stored path actually used, relative to the resources root.
    /// </summary>
    string Store(string relativeFolder, string preferredFileName, string sourcePath);

    /// <summary>
    /// Moves an already-stored file into <paramref name="relativeFolder"/> under
    /// <paramref name="preferredFileName"/>. Returns the stored path actually used, or
    /// null when the move could not be performed — a locked or missing file leaves the
    /// resource exactly where it was.
    /// </summary>
    string? MoveInto(string currentStoredPath, string relativeFolder, string preferredFileName);

    /// <summary>Absolute path for a stored resource.</summary>
    string ResolvePath(string storedPath);

    bool Exists(string storedPath);

    void Delete(string storedPath);
}
