using System.Globalization;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Projects;
using BeBoosted.Domain;

namespace BeBoosted.Infrastructure.Projects;

/// <summary>
/// Stores imported resource bytes under the app-controlled resources directory, one
/// folder per Project and File, using the user's own file name (e.g.
/// resources/College/Metric Proof/Transcript.pdf).
/// </summary>
public sealed class LocalResourceStorage(IAppDataPaths paths) : IResourceStorage
{
    public string Store(string relativeFolder, string preferredFileName, string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new DomainException($"The file '{sourcePath}' could not be found.");
        }

        var storedPath = ReserveFreePath(relativeFolder, preferredFileName);
        var destination = ResolvePath(storedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(sourcePath, destination);
        return storedPath;
    }

    public string? MoveInto(string currentStoredPath, string relativeFolder, string preferredFileName)
    {
        string source;
        try
        {
            source = ResolvePath(currentStoredPath);
        }
        catch (DomainException)
        {
            // A tampered recorded path is treated exactly like a missing file.
            return null;
        }

        if (!File.Exists(source))
        {
            return null;
        }

        var storedPath = ReserveFreePath(relativeFolder, preferredFileName);
        var destination = ResolvePath(storedPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Locked by another program, or a permissions problem: the resource keeps
            // its current location and the next reconcile tries again.
            return null;
        }

        return storedPath;
    }

    /// <summary>
    /// Canonical absolute path, guaranteed to stay inside the resources root. A rooted
    /// or traversal stored path — possible only through a corrupted or tampered
    /// database — is rejected rather than resolved outside the profile (BB-QA-011).
    /// </summary>
    public string ResolvePath(string storedPath)
    {
        var root = Path.GetFullPath(paths.ResourcesDirectory);
        var combined = Path.GetFullPath(Path.Combine(root, storedPath));
        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("A stored resource path must stay inside the resources folder.");
        }

        return combined;
    }

    public bool Exists(string storedPath)
    {
        try
        {
            return File.Exists(ResolvePath(storedPath));
        }
        catch (DomainException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reserves a folder name inside <paramref name="relativeParent"/>, probing the
    /// "(2)", "(3)", … suffix shape on collision, and creates the directory before
    /// returning — the directory IS the claim, closing the gap between checking a name
    /// is free and using it. A candidate is occupied when it is in <paramref name="claimed"/>,
    /// or already exists on disk as a file or a directory; the one exception is
    /// <paramref name="ownedSegment"/>, this entity's own directory, which is handed back
    /// unchanged rather than displaced — unless <paramref name="claimed"/> already holds it,
    /// which always wins.
    /// </summary>
    public string ReserveFolderSegment(
        string relativeParent, string preferredSegment, IReadOnlySet<string> claimed, string? ownedSegment = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Folder titles routinely carry a period with no extension meaning ("Ch. 5
            // Notes"), so the suffix goes after the whole segment, not before the first
            // '.' the way ResourceLayout.CandidateName treats file extensions — that is
            // correct there because file names genuinely have extensions.
            var candidate = attempt <= 1 ? preferredSegment : $"{preferredSegment} ({attempt})";
            if (claimed.Contains(candidate))
            {
                continue;
            }

            var isOwned = ownedSegment is not null
                && string.Equals(candidate, ownedSegment, StringComparison.OrdinalIgnoreCase);
            var absolute = ResolvePath(Path.Combine(relativeParent, candidate));
            if (isOwned || (!File.Exists(absolute) && !Directory.Exists(absolute)))
            {
                Directory.CreateDirectory(absolute);
                return candidate;
            }
        }
    }

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
        catch (DomainException)
        {
            // An escaped path is never touched; the row can still be removed.
        }
    }

    /// <summary>First free name in the folder: "report.pdf", then "report (2).pdf", …</summary>
    private string ReserveFreePath(string relativeFolder, string preferredFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(preferredFileName);
        var extension = Path.GetExtension(preferredFileName);
        for (var attempt = 1; ; attempt++)
        {
            var candidate = attempt == 1
                ? preferredFileName
                : string.Create(CultureInfo.InvariantCulture, $"{stem} ({attempt}){extension}");
            var storedPath = Path.Combine(relativeFolder, candidate);
            var absolute = ResolvePath(storedPath);
            if (!File.Exists(absolute) && !Directory.Exists(absolute))
            {
                return storedPath;
            }
        }
    }
}
