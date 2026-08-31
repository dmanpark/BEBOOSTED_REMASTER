using BeBoosted.Application.Abstractions;
using BeBoosted.Domain;
using BeBoosted.Infrastructure.Projects;

namespace BeBoosted.Tests.Projects;

/// <summary>
/// Storage owns collision handling because it is the only layer that can see the
/// filesystem. Exercised against a real temporary directory.
/// </summary>
public sealed class LocalResourceStorageTests : IDisposable
{
    private sealed class TestPaths : IAppDataPaths
    {
        public TestPaths()
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), $"beboosted-storetest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DataDirectory);
        }

        public string DataDirectory { get; }

        public string LogsDirectory => Path.Combine(DataDirectory, "logs");

        public string ResourcesDirectory => Path.Combine(DataDirectory, "resources");
    }

    /// <summary>
    /// "No group has claimed a folder here" — the honest answer for every test below that is
    /// not about the claim rule. Spelled out rather than defaulted, because a placement made
    /// without the claims is exactly the bug the parameter exists to prevent.
    /// </summary>
    private static readonly IReadOnlySet<string> NoClaims =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly TestPaths _paths = new();
    private readonly LocalResourceStorage _storage;

    public LocalResourceStorageTests() => _storage = new LocalResourceStorage(_paths);

    private string CreateSource(string name, string content = "bytes")
    {
        var path = Path.Combine(_paths.DataDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Store_CreatesNestedFolders_AndKeepsTheName()
    {
        var folder = Path.Combine("College", "Metric Proof");

        var stored = _storage.Store(folder, "Transcript.pdf", CreateSource("src.pdf"), NoClaims);

        Assert.Equal(Path.Combine(folder, "Transcript.pdf"), stored);
        Assert.True(_storage.Exists(stored));
        Assert.True(File.Exists(_storage.ResolvePath(stored)));
    }

    [Fact]
    public void ResolvePath_RejectsAPathThatEscapesTheResourcesRoot()
    {
        Assert.Throws<DomainException>(() => _storage.ResolvePath(Path.Combine("..", "escape.txt")));
        Assert.Throws<DomainException>(() => _storage.ResolvePath(@"C:\Windows\evil.txt"));
    }

    /// <summary>
    /// A tampered stored path (BB-QA-011) must be treated as absent everywhere — and
    /// Delete must never reach a file outside the root.
    /// </summary>
    [Fact]
    public void ExistsDeleteAndMove_TreatAnEscapedPathAsAbsent_AndNeverTouchIt()
    {
        var probe = CreateSource("probe.txt"); // sits above the resources root
        var escaped = Path.Combine("..", "probe.txt");

        Assert.False(_storage.Exists(escaped));
        _storage.Delete(escaped);
        Assert.True(File.Exists(probe));
        Assert.Null(_storage.MoveInto(escaped, "College", "probe.txt", NoClaims));
    }

    [Fact]
    public void Store_DisambiguatesACollision()
    {
        const string folder = "College";
        var first = _storage.Store(folder, "Transcript.pdf", CreateSource("a.pdf", "first"), NoClaims);
        var second = _storage.Store(folder, "Transcript.pdf", CreateSource("b.pdf", "second"), NoClaims);

        Assert.Equal(Path.Combine(folder, "Transcript.pdf"), first);
        Assert.Equal(Path.Combine(folder, "Transcript (2).pdf"), second);
        Assert.Equal("first", File.ReadAllText(_storage.ResolvePath(first)));
        Assert.Equal("second", File.ReadAllText(_storage.ResolvePath(second)));
    }

    [Fact]
    public void Store_RejectsAMissingSource()
        => Assert.Throws<DomainException>(
            () => _storage.Store(
                "College", "x.pdf", Path.Combine(_paths.DataDirectory, "nope.pdf"), NoClaims));

    [Fact]
    public void MoveInto_RelocatesBytes_AndReturnsTheNewPath()
    {
        var original = _storage.Store(string.Empty, "guid.pdf", CreateSource("src.pdf", "payload"), NoClaims);
        var folder = Path.Combine("College", "Metric Proof");

        var moved = _storage.MoveInto(original, folder, "Transcript.pdf", NoClaims);

        Assert.Equal(Path.Combine(folder, "Transcript.pdf"), moved);
        Assert.False(_storage.Exists(original));
        Assert.Equal("payload", File.ReadAllText(_storage.ResolvePath(moved!)));
    }

    [Fact]
    public void MoveInto_DisambiguatesAgainstAnExistingFile()
    {
        const string folder = "College";
        _storage.Store(folder, "Transcript.pdf", CreateSource("a.pdf", "first"), NoClaims);
        var other = _storage.Store(string.Empty, "guid.pdf", CreateSource("b.pdf", "second"), NoClaims);

        var moved = _storage.MoveInto(other, folder, "Transcript.pdf", NoClaims);

        Assert.Equal(Path.Combine(folder, "Transcript (2).pdf"), moved);
        Assert.Equal("second", File.ReadAllText(_storage.ResolvePath(moved!)));
    }

    [Fact]
    public void MoveInto_ReturnsNullWhenTheSourceIsGone()
        => Assert.Null(_storage.MoveInto("missing.pdf", "College", "Transcript.pdf", NoClaims));

    /// <summary>A file occupying the candidate name must not be handed out as a folder segment.</summary>
    [Fact]
    public void ReserveFolderSegment_SkipsACandidateOccupiedByAFile()
    {
        const string parent = "College";
        Directory.CreateDirectory(_storage.ResolvePath(parent));
        File.WriteAllText(_storage.ResolvePath(Path.Combine(parent, "Notes")), "not a folder");

        var segment = _storage.ReserveFolderSegment(parent, "Notes", new HashSet<string>());

        Assert.NotEqual("Notes", segment);
    }

    /// <summary>
    /// A directory occupying the preferred file name must not be targeted by <see cref="Store"/> —
    /// this fails today: <see cref="File.Copy(string, string)"/> onto a directory throws.
    /// </summary>
    [Fact]
    public void Store_SkipsAFileNameThatCollidesWithADirectory()
    {
        const string folder = "College";
        Directory.CreateDirectory(_storage.ResolvePath(Path.Combine(folder, "Notes")));

        var stored = _storage.Store(folder, "Notes", CreateSource("src.notes"), NoClaims);

        Assert.Equal(Path.Combine(folder, "Notes (2)"), stored);
        Assert.True(File.Exists(_storage.ResolvePath(stored)));
    }

    /// <summary>
    /// The claim, not the disk. Nothing occupies the path — this is the state straight after
    /// a parent rename, and the permanent state of an empty group — and the name must still
    /// be unavailable to a file. A disk-only check hands it over, and the group can then
    /// never create its directory: every member's move throws onto the file and returns null.
    ///
    /// Both directions are pinned, because a claim that is never consulted and a claim that
    /// swallows every name are equally wrong: "Transcript.pdf" is a sibling, not the claim,
    /// and must still be handed out.
    /// </summary>
    [Fact]
    public void StoreAndMoveInto_SkipAClaimedFolderName_ThatNothingOccupiesOnDisk()
    {
        const string folder = "College";
        var claims = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(folder, "Notes"),
        };
        Assert.False(Directory.Exists(_storage.ResolvePath(Path.Combine(folder, "Notes"))));

        var stored = _storage.Store(folder, "Notes", CreateSource("a.notes", "first"), claims);
        Assert.Equal(Path.Combine(folder, "Notes (2)"), stored);

        var loose = _storage.Store(string.Empty, "guid.notes", CreateSource("b.notes", "second"), claims);
        var moved = _storage.MoveInto(loose, folder, "Notes", claims);
        Assert.Equal(Path.Combine(folder, "Notes (3)"), moved);
        Assert.Equal("second", File.ReadAllText(_storage.ResolvePath(moved!)));

        // The claim covers one name, not the folder: an unclaimed sibling still lands first.
        Assert.Equal(
            Path.Combine(folder, "Transcript.pdf"),
            _storage.Store(folder, "Transcript.pdf", CreateSource("c.pdf"), claims));
    }

    /// <summary>
    /// The claim is matched case-insensitively. Windows would catch "notes" against a real
    /// "Notes" directory by luck of the filesystem, but this app also publishes osx-arm64,
    /// where only the comparer stands between a loose file and a group's folder.
    /// </summary>
    [Fact]
    public void Store_SkipsAClaimedFolderName_WhateverItsCase()
    {
        const string folder = "College";
        var claims = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(folder, "NOTES"),
        };

        Assert.Equal(
            Path.Combine(folder, "Notes (2)"),
            _storage.Store(folder, "Notes", CreateSource("src.notes"), claims));
    }

    /// <summary>Reserving a folder segment IS the claim: the directory must exist afterward.</summary>
    [Fact]
    public void ReserveFolderSegment_CreatesTheDirectory_AsItsClaim()
    {
        const string parent = "College";

        var segment = _storage.ReserveFolderSegment(parent, "Metric Proof", new HashSet<string>());

        Assert.True(Directory.Exists(_storage.ResolvePath(Path.Combine(parent, segment))));
    }

    /// <summary>
    /// A rename that lands back on the entity's own directory must keep it rather than
    /// advancing to "(2)" and moving every byte for nothing.
    /// </summary>
    [Fact]
    public void ReserveFolderSegment_KeepsTheOwnedSegment_EvenThoughItExistsOnDisk()
    {
        const string parent = "College";
        Directory.CreateDirectory(_storage.ResolvePath(Path.Combine(parent, "Notes")));

        var segment = _storage.ReserveFolderSegment(parent, "Notes", new HashSet<string>(), ownedSegment: "Notes");

        Assert.Equal("Notes", segment);
    }

    /// <summary>
    /// Ownership is matched case-insensitively, so a case-only rename lands on the owned
    /// segment — and what comes back must be the name the directory actually has, not the
    /// requested spelling of it. Returning "notes" would persist a folder_segment that no
    /// directory is called: harmless on this filesystem only by luck, and on a
    /// case-sensitive one it names a second, empty directory beside the group's bytes.
    /// </summary>
    [Fact]
    public void ReserveFolderSegment_ReturnsTheOwnedSpelling_WhenOnlyTheCaseChanged()
    {
        const string parent = "College";
        Directory.CreateDirectory(_storage.ResolvePath(Path.Combine(parent, "Notes")));

        var segment = _storage.ReserveFolderSegment(parent, "notes", new HashSet<string>(), ownedSegment: "Notes");

        Assert.Equal("Notes", segment);
        Assert.False(Directory.Exists(_storage.ResolvePath(Path.Combine(parent, "notes (2)"))));
    }

    /// <summary>A sibling's in-flight claim outranks this entity's own remembered segment.</summary>
    [Fact]
    public void ReserveFolderSegment_ClaimedBeatsOwnedSegment()
    {
        const string parent = "College";
        Directory.CreateDirectory(_storage.ResolvePath(Path.Combine(parent, "Notes")));

        var segment = _storage.ReserveFolderSegment(
            parent, "Notes", new HashSet<string> { "Notes" }, ownedSegment: "Notes");

        Assert.NotEqual("Notes", segment);
    }

    /// <summary>
    /// A folder segment routinely contains a period with no extension meaning ("Ch. 5
    /// Notes"). The collision suffix must land after the whole segment — "Ch. 5 Notes
    /// (2)" — not before the first period the way <see cref="ResourceLayout.CandidateName"/>
    /// treats file extensions, which would mangle it into "Ch (2). 5 Notes".
    /// </summary>
    [Fact]
    public void ReserveFolderSegment_SuffixesAfterTheWholeSegment_WhenItContainsAPeriod()
    {
        const string parent = "College";
        Directory.CreateDirectory(_storage.ResolvePath(Path.Combine(parent, "Ch. 5 Notes")));

        var segment = _storage.ReserveFolderSegment(parent, "Ch. 5 Notes", new HashSet<string>());

        Assert.Equal("Ch. 5 Notes (2)", segment);
    }

    /// <summary>
    /// A read-only file is routine for user-managed documents, and File.Delete answers it
    /// with UnauthorizedAccessException — which does NOT derive from IOException, so the
    /// existing clause never covered it. Delete is best-effort by contract, and its caller
    /// has already committed the row, so this has to be absorbed like any other failure.
    ///
    /// Windows-only, because the premise is. .NET maps FileAttributes.ReadOnly onto the
    /// file's own write bits, but POSIX takes unlink permission from the *directory*, so
    /// on Linux and macOS this delete simply succeeds and there is no absorption to
    /// observe. Making the parent directory unwritable instead would reach the same
    /// UnauthorizedAccessException, but it is silently bypassed by root — which is how
    /// containerised CI usually runs — so it would trade a skip for a test that passes
    /// without exercising anything.
    ///
    /// The isolation this protects is NOT Windows-only and is not skipped with it:
    /// ProjectServiceTests drives the same paths through a sabotaged IResourceStorage
    /// that throws an ordinary exception, so the call-site guarding runs everywhere.
    /// What is pinned only here is LocalResourceStorage's own UnauthorizedAccessException
    /// clause.
    /// </summary>
    [Fact]
    public void Delete_AbsorbsAReadOnlyFile_RatherThanThrowing()
    {
        Assert.SkipWhen(
            !OperatingSystem.IsWindows(),
            "A read-only file is deletable on POSIX; unlink permission comes from the directory.");

        var stored = _storage.Store("Notes", "Locked.pdf", CreateSource("Locked.pdf"), NoClaims);
        var absolute = _storage.ResolvePath(stored);
        File.SetAttributes(absolute, FileAttributes.ReadOnly);

        try
        {
            _storage.Delete(stored);

            // Absorbed, and honest about it: the bytes are still there, which is the
            // tolerated orphan the reconciler already copes with.
            Assert.True(_storage.Exists(stored));
        }
        finally
        {
            File.SetAttributes(absolute, FileAttributes.Normal);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
