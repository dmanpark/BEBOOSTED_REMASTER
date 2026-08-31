using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Application.Projects;

/// <summary>
/// Where a stored resource belongs on disk: one folder per Project, one subfolder per
/// File, and the user's own file name inside it. Pure — every rule here is decidable
/// without touching the filesystem, which is what makes it testable and what keeps
/// collision handling (the one genuinely filesystem-dependent part) in the storage layer.
/// </summary>
public static partial class ResourceLayout
{
    private const int MaxSegmentLength = 80;

    private static readonly char[] InvalidCharacters =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRuns();

    [GeneratedRegex(@"^(?<stem>.+) \(\d+\)$")]
    private static partial Regex NumberedVariant();

    /// <summary>One path segment made safe, or <paramref name="fallback"/> if nothing survives.</summary>
    public static string Sanitize(string? segment, string fallback)
    {
        var builder = new StringBuilder(segment?.Length ?? 0);
        foreach (var character in segment ?? string.Empty)
        {
            builder.Append(
                char.IsControl(character) || Array.IndexOf(InvalidCharacters, character) >= 0
                    ? '-'
                    : character);
        }

        var cleaned = WhitespaceRuns().Replace(builder.ToString(), " ").Trim().TrimEnd('.', ' ').Trim();
        if (cleaned.Length == 0)
        {
            return fallback;
        }

        if (cleaned.Length > MaxSegmentLength)
        {
            cleaned = cleaned[..MaxSegmentLength].TrimEnd('.', ' ').Trim();
        }

        return IsReserved(cleaned) ? cleaned + "_" : cleaned;
    }

    /// <summary>
    /// The folder for one File: &lt;project&gt;/&lt;file&gt;, relative to the resources root.
    /// Combines the already-claimed segments verbatim — both were sanitized and reserved
    /// against collision when they were stored, so re-sanitizing here would be redundant
    /// at best and could disagree with what was actually claimed on disk.
    /// </summary>
    /// <param name="group">
    /// The group owning the resource, or null for a loose one. Its segment is appended
    /// verbatim for the same reason the other two are: it was sanitized and reserved —
    /// and its directory created — when the group claimed it, so re-deriving it here
    /// could name a folder nothing ever claimed.
    /// </param>
    public static string FolderFor(Project project, ProjectFile file, ResourceGroup? group = null)
        => group is null
            ? Path.Combine(project.FolderSegment, file.FolderSegment)
            : Path.Combine(project.FolderSegment, file.FolderSegment, group.FolderSegment);

    /// <summary>
    /// The user's own file name, made safe. The extension is protected from the length
    /// cap and from sanitizing so a document stays openable by its type.
    /// </summary>
    public static string FileNameFor(string? originalFileName, string fallbackStem)
    {
        var name = Path.GetFileName(originalFileName ?? string.Empty);
        var extension = Path.GetExtension(name);
        var stem = Sanitize(Path.GetFileNameWithoutExtension(name), fallbackStem);
        var safeExtension = extension.Length <= 1
            ? string.Empty
            : "." + Sanitize(extension[1..], string.Empty);
        return stem + safeExtension;
    }

    /// <summary>
    /// The nth candidate name for a desired file name, matching the storage layer's
    /// collision probe: "report.pdf", then "report (2).pdf", "report (3).pdf", …
    /// </summary>
    public static string CandidateName(string desiredFileName, int attempt)
        => attempt <= 1
            ? desiredFileName
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{Path.GetFileNameWithoutExtension(desiredFileName)} ({attempt}){Path.GetExtension(desiredFileName)}");

    /// <summary>
    /// Whether a stored path already satisfies the desired layout. A numbered variant
    /// counts as placed, so a resource that had to be disambiguated once is not shuffled
    /// on every later reconcile.
    /// </summary>
    public static bool IsAlreadyPlaced(string storedPath, string desiredFolder, string desiredFileName)
    {
        var folder = Path.GetDirectoryName(storedPath) ?? string.Empty;
        if (!string.Equals(folder, desiredFolder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var actual = Path.GetFileName(storedPath);
        if (string.Equals(actual, desiredFileName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(
            Path.GetExtension(actual), Path.GetExtension(desiredFileName), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = NumberedVariant().Match(Path.GetFileNameWithoutExtension(actual));
        return match.Success
            && string.Equals(
                match.Groups["stem"].Value,
                Path.GetFileNameWithoutExtension(desiredFileName),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReserved(string candidate)
        => Array.Exists(
            ReservedNames,
            reserved => string.Equals(
                Path.GetFileNameWithoutExtension(candidate),
                reserved,
                StringComparison.OrdinalIgnoreCase));
}
