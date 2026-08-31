using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Application.Projects;

/// <summary>
/// Where a stored resource belongs on disk: one folder per Project, one subfolder per
/// File, one more for the group holding the resource if it is in one, and the user's own
/// file name inside that. Pure — every rule here is decidable
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
    /// The folder a resource's bytes belong in, relative to the resources root:
    /// &lt;project&gt;/&lt;file&gt; for a loose resource, &lt;project&gt;/&lt;file&gt;/&lt;group&gt;
    /// for one held by a group. Combines the already-claimed segments verbatim — each was
    /// sanitized and reserved against collision when it was stored, so re-sanitizing here
    /// would be redundant at best and could disagree with what was actually claimed on disk.
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
    /// The folder paths <paramref name="groups"/> have claimed inside
    /// <paramref name="file"/> — each relative to the resources root, exactly as
    /// <see cref="FolderFor(Project, ProjectFile, ResourceGroup?)"/> renders it, and
    /// compared <c>OrdinalIgnoreCase</c> to match every other segment comparison here.
    ///
    /// This is the set that makes a group's folder name unavailable to a *file*. The claim
    /// lives on the group row, so it holds whether or not the directory currently exists on
    /// disk — which is the whole point. A directory check alone protects nothing in the two
    /// moments that matter: straight after a parent rename, when the destination folders do
    /// not exist yet, and for an empty group, whose members cannot create the directory
    /// first. Hand a loose file a group's folder path in either moment and the group is
    /// split for good: <c>Directory.CreateDirectory</c> throws onto the file, the move
    /// returns null, and every member is skipped silently on this and every later reconcile.
    ///
    /// A group still holding the empty sentinel claims nothing. Combining it would name the
    /// File's own folder — <c>Path.Combine</c> swallows an empty part — and forbid every
    /// loose file in the File from being placed at all.
    /// </summary>
    public static IReadOnlySet<string> ClaimedFolders(
        Project project, ProjectFile file, IEnumerable<ResourceGroup> groups)
        => groups
            .Where(group => group.FolderSegment.Length > 0)
            .Select(group => FolderFor(project, file, group))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
    /// <param name="claimedFolders">
    /// Folder paths a group of this File has claimed, from
    /// <see cref="ClaimedFolders"/>. A file sitting at one of them is never placed, however
    /// well it matches — this is the recovery half of the claim rule, and without it a
    /// stranded state can only be prevented, never healed. A loose file that reached
    /// <c>&lt;file&gt;/Notes</c> while <c>Notes</c> is a group's segment wants exactly that
    /// folder and exactly that name, so the plain comparison below blesses it, it is never
    /// moved, and the group's directory can never be created where a file already sits.
    ///
    /// Optional only because the pure-layout tests and any caller with no File in hand have
    /// no claims to offer; null means "no claim is known", which is the pre-group answer.
    /// The reconciler always passes the File's real set — read every call site deliberately.
    /// </param>
    public static bool IsAlreadyPlaced(
        string storedPath,
        string desiredFolder,
        string desiredFileName,
        IReadOnlySet<string>? claimedFolders = null)
    {
        // First, and independent of what this resource wants: a path a group claims as its
        // directory is not a place a file belongs, so no comparison below can rescue it.
        if (claimedFolders is not null && claimedFolders.Contains(storedPath))
        {
            return false;
        }

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
