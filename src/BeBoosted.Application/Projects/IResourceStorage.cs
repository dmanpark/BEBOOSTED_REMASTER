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
    /// <param name="claimedFolders">See <see cref="MoveInto"/>; the rule is identical.</param>
    string Store(
        string relativeFolder,
        string preferredFileName,
        string sourcePath,
        IReadOnlySet<string> claimedFolders);

    /// <summary>
    /// Moves an already-stored file into <paramref name="relativeFolder"/> under
    /// <paramref name="preferredFileName"/>. Returns the stored path actually used, or
    /// null when the move could not be performed — a locked or missing file leaves the
    /// resource exactly where it was.
    /// </summary>
    /// <param name="claimedFolders">
    /// Stored paths — relative to the resources root, as
    /// <c>ResourceLayout.ClaimedFolders</c> renders them — that the destination File's
    /// groups have claimed as their directories. A candidate equal to one of these is never
    /// handed out, **whether or not anything currently occupies it on disk**. That is the
    /// whole difference between this and the disk probe beside it: after a parent rename the
    /// destination directories do not exist yet, and an empty group has no members to create
    /// one, so the disk has nothing to say in exactly the two moments a loose file could
    /// take a group's folder name. When it does, the group is split permanently —
    /// <c>Directory.CreateDirectory</c> throws onto the file, the member's move returns null,
    /// and every member is skipped silently on this and every later reconcile.
    ///
    /// Required rather than defaulted, and never inferred here: only the caller can see the
    /// File whose groups these are. Pass an empty set to mean "nothing is claimed", and mean
    /// it. The set is expected to compare <c>OrdinalIgnoreCase</c>, as
    /// <c>ResourceLayout.ClaimedFolders</c> builds it — this app also publishes osx-arm64,
    /// where an ordinal set would hand out "Notes" beside a group's "notes".
    ///
    /// This is prevention only. Nothing here moves a file already sitting at a claimed path;
    /// <c>ResourceLayout.IsAlreadyPlaced</c> is the half that heals that.
    /// </param>
    string? MoveInto(
        string currentStoredPath,
        string relativeFolder,
        string preferredFileName,
        IReadOnlySet<string> claimedFolders);

    /// <summary>
    /// Reserves a folder name inside <paramref name="relativeParent"/>, disambiguating on
    /// collision the same way <see cref="Store"/> does for file names, and creates the
    /// directory before returning. A candidate already present in
    /// <paramref name="claimed"/>, or already occupying disk space as a file or a
    /// directory, is skipped, except that a candidate matching
    /// <paramref name="ownedSegment"/> — this entity's own directory — is kept rather than
    /// displaced; <paramref name="claimed"/> still overrides that exception when a sibling
    /// has already taken the name.
    ///
    /// **Ownership is matched case-insensitively, and what comes back is
    /// <paramref name="ownedSegment"/>'s own spelling, not the requested one.** Answering
    /// "notes" for a directory called "Notes" would persist a folder name that exists under
    /// no such name. On a case-sensitive filesystem that is not cosmetic: the directory
    /// creation below makes a *second*, empty directory, and because
    /// <c>ResourceLayout.IsAlreadyPlaced</c> compares folders <c>OrdinalIgnoreCase</c> the
    /// reconciler judges the existing members correctly placed and never moves them, while
    /// every future member lands in the other directory — a permanently split group no
    /// reconcile can see.
    ///
    /// <paramref name="claimed"/> is expected to use an <c>OrdinalIgnoreCase</c> comparer, as
    /// every caller's does. It is tested against the requested candidate rather than the
    /// owned spelling, so an ordinal set could let a differently-cased owned segment through
    /// that it meant to exclude.
    ///
    /// What creating the directory buys, precisely: within one process, reserving
    /// sequentially, a later reservation cannot be handed a name an earlier one has
    /// already returned, because the earlier call left a directory there for the disk
    /// check to see. That is what makes a name usable rather than merely unoccupied at
    /// the instant it was checked — a checked-but-uncreated name can still be taken by a
    /// later import.
    ///
    /// It is not a lock and not atomic. <c>Directory.CreateDirectory</c> succeeds silently
    /// on a directory that already exists and takes nothing exclusively, so this excludes
    /// no other process, and two concurrent reservations — in this process or another —
    /// can still be handed the same name. Reservation is called from the UI thread on
    /// create and rename, and the startup backfill runs before the window opens, so
    /// sequential single-process use is the only case that arises today.
    /// </summary>
    string ReserveFolderSegment(
        string relativeParent, string preferredSegment, IReadOnlySet<string> claimed, string? ownedSegment = null);

    /// <summary>Absolute path for a stored resource.</summary>
    string ResolvePath(string storedPath);

    bool Exists(string storedPath);

    void Delete(string storedPath);
}
