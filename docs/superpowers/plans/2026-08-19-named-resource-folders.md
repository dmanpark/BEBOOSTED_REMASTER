# Named Resource Folders Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Store imported documents under their original file names in `resources/<Project>/<File>/`, and move already-imported documents into that layout.

**Architecture:** A pure naming helper (`ResourceLayout`) decides the desired path; the storage layer owns collision handling because it is the only component that can see the filesystem; a reconciler moves anything not already in place and updates `StoredPath`. The reconciler runs at startup — which is the migration — and after a project rename.

**Tech Stack:** .NET 10, C#, Avalonia 12, xUnit, SQLite. No schema change.

Spec: `docs/superpowers/specs/2026-08-19-named-resource-folders-design.md`

## Global Constraints

- No database schema change and no SQL migration. `StoredPath` is already a relative path column.
- A title rename must always succeed; folder sync is best-effort and never throws for a per-resource failure.
- Move bytes first, write `StoredPath` only after the move succeeds — the database must never point at a path that does not exist.
- The reconciler must be idempotent: a second run moves nothing.
- Links and notes have no bytes and are never touched.
- Strict TDD: every production edit is preceded by a test watched failing for the right reason.
- Do not touch `docs/qa/` or screenshot baselines. The three screenshot-capture tests stay skipped.
- `dotnet format BeBoosted.slnx --verify-no-changes --no-restore` stays clean; build runs `-warnaserror`.
- Do not stage, commit, or push unless the user asks.

---

### Task 1: `Resource.RelocateTo`

**Files:**
- Modify: `src/BeBoosted.Domain/Projects/Resource.cs:69` (`StoredPath`), and add the method near `Rename`
- Test: `tests/BeBoosted.Tests/Domain/ResourceTests.cs` (create)

**Interfaces:**
- Produces: `Resource.RelocateTo(string storedPath, DateTimeOffset now)`; `StoredPath` becomes `{ get; private set; }`.

- [x] **Step 1: Write the failing tests**

Create `tests/BeBoosted.Tests/Domain/ResourceTests.cs`:

```csharp
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Tests.Domain;

/// <summary>
/// A stored resource records where its bytes now live after the layout reconciler
/// moves them. Links and notes have no bytes and can never be relocated.
/// </summary>
public sealed class ResourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 9, 0, 0, TimeSpan.FromHours(-7));

    private static Resource StoredDocument()
        => Resource.CreateStored(
            ProjectFileId.New(), ResourceKind.Document, "Transcript",
            "Transcript.pdf", "old-guid.pdf", Now);

    [Fact]
    public void RelocateTo_RecordsTheNewPath_AndTouchesTheResource()
    {
        var resource = StoredDocument();

        resource.RelocateTo("College/Metric Proof/Transcript.pdf", Now.AddMinutes(5));

        Assert.Equal("College/Metric Proof/Transcript.pdf", resource.StoredPath);
        Assert.Equal(Now.AddMinutes(5), resource.ModifiedAt);
    }

    [Fact]
    public void RelocateTo_RejectsABlankPath()
    {
        var resource = StoredDocument();

        Assert.Throws<DomainException>(() => resource.RelocateTo("  ", Now));

        Assert.Equal("old-guid.pdf", resource.StoredPath);
    }

    [Fact]
    public void RelocateTo_RejectsAResourceThatHasNoStoredBytes()
    {
        var link = Resource.CreateLink(ProjectFileId.New(), "Source", "https://example.com", Now);

        Assert.Throws<DomainException>(() => link.RelocateTo("Anywhere/thing.pdf", Now));

        Assert.Null(link.StoredPath);
    }
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~ResourceTests"
```
Expected: compile error — `Resource` has no `RelocateTo`.

- [x] **Step 3: Implement**

In `src/BeBoosted.Domain/Projects/Resource.cs`, change the property:

```csharp
    /// <summary>Documents/images: path relative to the resources directory.</summary>
    public string? StoredPath { get; private set; }
```

and add after `Rename`:

```csharp
    /// <summary>
    /// Records a new location after the bytes were moved on disk. Called only once the
    /// move has succeeded, so the stored path never names a file that is not there.
    /// </summary>
    public void RelocateTo(string storedPath, DateTimeOffset now)
    {
        if (StoredPath is null)
        {
            throw new DomainException("Only a stored document or image has a location to change.");
        }

        var trimmed = storedPath?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainException("A stored resource needs a path.");
        }

        StoredPath = trimmed;
        Touch(now);
    }
```

- [x] **Step 4: Run the tests to verify they pass**

Run:
```bash
dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~ResourceTests"
```
Expected: PASS.

---

### Task 2: `ResourceLayout` naming rules

**Files:**
- Create: `src/BeBoosted.Application/Projects/ResourceLayout.cs`
- Test: `tests/BeBoosted.Tests/Projects/ResourceLayoutTests.cs` (create)

**Interfaces:**
- Produces: `ResourceLayout.Sanitize(string?, string) -> string`, `ResourceLayout.FolderFor(Project, ProjectFile) -> string`, `ResourceLayout.FileNameFor(string?, string) -> string`, `ResourceLayout.IsAlreadyPlaced(string, string, string) -> bool`. All used by Tasks 3 and 4.

- [x] **Step 1: Write the failing tests**

Create `tests/BeBoosted.Tests/Projects/ResourceLayoutTests.cs`:

```csharp
using BeBoosted.Application.Projects;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Tests.Projects;

/// <summary>
/// Folder and file names come from user-typed titles, so every segment is made safe
/// for the filesystem without becoming unreadable.
/// </summary>
public sealed class ResourceLayoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 9, 0, 0, TimeSpan.FromHours(-7));

    [Theory]
    [InlineData("College Admissions", "College Admissions")]
    [InlineData("Q1/Q2 Report", "Q1-Q2 Report")]
    [InlineData("What: now?", "What- now-")]
    [InlineData("a\\b|c<d>e\"f*g", "a-b-c-d-e-f-g")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("trailing...", "trailing")]
    [InlineData("trailing dots. . .", "trailing dots")]
    public void Sanitize_MakesSegmentsSafe_WithoutManglingReadableText(string input, string expected)
        => Assert.Equal(expected, ResourceLayout.Sanitize(input, "fallback"));

    [Fact]
    public void Sanitize_ReplacesControlCharacters()
        => Assert.Equal("a-b", ResourceLayout.Sanitize("a\tb", "fallback"));

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("NUL")]
    public void Sanitize_SuffixesReservedDeviceNames(string reserved)
        => Assert.Equal(reserved + "_", ResourceLayout.Sanitize(reserved, "fallback"));

    [Fact]
    public void Sanitize_FallsBackWhenNothingSurvives()
    {
        Assert.Equal("fallback", ResourceLayout.Sanitize("///", "fallback"));
        Assert.Equal("fallback", ResourceLayout.Sanitize("   ", "fallback"));
        Assert.Equal("fallback", ResourceLayout.Sanitize(null, "fallback"));
    }

    [Fact]
    public void Sanitize_CapsLongSegments()
    {
        var sanitized = ResourceLayout.Sanitize(new string('x', 200), "fallback");

        Assert.Equal(80, sanitized.Length);
    }

    [Fact]
    public void FolderFor_NestsTheFileInsideTheProject()
    {
        var project = Project.Create("College: Admissions", "#ffffff", Now);
        var file = ProjectFile.Create(project.Id, "Metric/Proof", null, Now);

        var folder = ResourceLayout.FolderFor(project, file);

        Assert.Equal(Path.Combine("College- Admissions", "Metric-Proof"), folder);
    }

    [Fact]
    public void FileNameFor_KeepsTheOriginalNameAndExtension()
        => Assert.Equal("Transcript.pdf", ResourceLayout.FileNameFor("Transcript.pdf", "fallback"));

    [Fact]
    public void FileNameFor_SanitizesTheStemButProtectsTheExtension()
    {
        Assert.Equal("a-b.pdf", ResourceLayout.FileNameFor("a:b.pdf", "fallback"));

        var long_ = ResourceLayout.FileNameFor(new string('x', 200) + ".pdf", "fallback");
        Assert.EndsWith(".pdf", long_, StringComparison.Ordinal);
        Assert.Equal(84, long_.Length); // 80-char stem plus ".pdf"
    }

    [Fact]
    public void FileNameFor_StripsAnyDirectoryPartOfTheOriginalName()
        => Assert.Equal("Transcript.pdf", ResourceLayout.FileNameFor(@"C:\Users\x\Transcript.pdf", "fallback"));

    [Fact]
    public void FileNameFor_FallsBackWhenThereIsNoUsableName()
        => Assert.Equal("fallback", ResourceLayout.FileNameFor(null, "fallback"));

    [Fact]
    public void IsAlreadyPlaced_AcceptsTheExactNameAndNumberedVariants()
    {
        var folder = Path.Combine("Proj", "File");

        Assert.True(ResourceLayout.IsAlreadyPlaced(
            Path.Combine(folder, "report.pdf"), folder, "report.pdf"));
        Assert.True(ResourceLayout.IsAlreadyPlaced(
            Path.Combine(folder, "report (2).pdf"), folder, "report.pdf"));
        Assert.True(ResourceLayout.IsAlreadyPlaced(
            Path.Combine(folder, "report (17).pdf"), folder, "report.pdf"));
    }

    [Fact]
    public void IsAlreadyPlaced_RejectsADifferentFolderNameOrExtension()
    {
        var folder = Path.Combine("Proj", "File");

        Assert.False(ResourceLayout.IsAlreadyPlaced("report.pdf", folder, "report.pdf"));
        Assert.False(ResourceLayout.IsAlreadyPlaced(
            Path.Combine("Proj", "Other", "report.pdf"), folder, "report.pdf"));
        Assert.False(ResourceLayout.IsAlreadyPlaced(
            Path.Combine(folder, "other.pdf"), folder, "report.pdf"));
        Assert.False(ResourceLayout.IsAlreadyPlaced(
            Path.Combine(folder, "report.png"), folder, "report.pdf"));
        Assert.False(ResourceLayout.IsAlreadyPlaced(
            Path.Combine(folder, "report (x).pdf"), folder, "report.pdf"));
    }
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~ResourceLayoutTests"
```
Expected: compile error — `ResourceLayout` does not exist.

- [x] **Step 3: Implement**

Create `src/BeBoosted.Application/Projects/ResourceLayout.cs`:

```csharp
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

    /// <summary>The folder for one File: &lt;project&gt;/&lt;file&gt;, relative to the resources root.</summary>
    public static string FolderFor(Project project, ProjectFile file)
        => Path.Combine(
            Sanitize(project.Name, project.Id.ToString()),
            Sanitize(file.Title, file.Id.ToString()));

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
```

- [x] **Step 4: Run the tests to verify they pass**

Run:
```bash
dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~ResourceLayoutTests"
```
Expected: PASS.

---

### Task 3: Folder-aware storage

**Files:**
- Modify: `src/BeBoosted.Application/Projects/IResourceStorage.cs`
- Modify: `src/BeBoosted.Infrastructure/Projects/LocalResourceStorage.cs`
- Modify: `tests/BeBoosted.Desktop.Tests/Support/TestDoubles.cs` (`FakeResourceStorage`)
- Test: `tests/BeBoosted.Tests/Projects/LocalResourceStorageTests.cs` (create)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `IResourceStorage.Store(string relativeFolder, string preferredFileName, string sourcePath) -> string` and `IResourceStorage.MoveInto(string currentStoredPath, string relativeFolder, string preferredFileName) -> string?`. The `Store(ResourceId, string)` overload is removed.

- [x] **Step 1: Write the failing tests**

Create `tests/BeBoosted.Tests/Projects/LocalResourceStorageTests.cs`:

```csharp
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

        var stored = _storage.Store(folder, "Transcript.pdf", CreateSource("src.pdf"));

        Assert.Equal(Path.Combine(folder, "Transcript.pdf"), stored);
        Assert.True(_storage.Exists(stored));
        Assert.True(File.Exists(_storage.ResolvePath(stored)));
    }

    [Fact]
    public void Store_DisambiguatesACollision()
    {
        var folder = "College";
        var first = _storage.Store(folder, "Transcript.pdf", CreateSource("a.pdf", "first"));
        var second = _storage.Store(folder, "Transcript.pdf", CreateSource("b.pdf", "second"));

        Assert.Equal(Path.Combine(folder, "Transcript.pdf"), first);
        Assert.Equal(Path.Combine(folder, "Transcript (2).pdf"), second);
        Assert.Equal("first", File.ReadAllText(_storage.ResolvePath(first)));
        Assert.Equal("second", File.ReadAllText(_storage.ResolvePath(second)));
    }

    [Fact]
    public void Store_RejectsAMissingSource()
        => Assert.Throws<DomainException>(
            () => _storage.Store("College", "x.pdf", Path.Combine(_paths.DataDirectory, "nope.pdf")));

    [Fact]
    public void MoveInto_RelocatesBytes_AndReturnsTheNewPath()
    {
        var original = _storage.Store(string.Empty, "guid.pdf", CreateSource("src.pdf", "payload"));
        var folder = Path.Combine("College", "Metric Proof");

        var moved = _storage.MoveInto(original, folder, "Transcript.pdf");

        Assert.Equal(Path.Combine(folder, "Transcript.pdf"), moved);
        Assert.False(_storage.Exists(original));
        Assert.Equal("payload", File.ReadAllText(_storage.ResolvePath(moved!)));
    }

    [Fact]
    public void MoveInto_DisambiguatesAgainstAnExistingFile()
    {
        var folder = "College";
        _storage.Store(folder, "Transcript.pdf", CreateSource("a.pdf", "first"));
        var other = _storage.Store(string.Empty, "guid.pdf", CreateSource("b.pdf", "second"));

        var moved = _storage.MoveInto(other, folder, "Transcript.pdf");

        Assert.Equal(Path.Combine(folder, "Transcript (2).pdf"), moved);
        Assert.Equal("second", File.ReadAllText(_storage.ResolvePath(moved!)));
    }

    [Fact]
    public void MoveInto_ReturnsNullWhenTheSourceIsGone()
        => Assert.Null(_storage.MoveInto("missing.pdf", "College", "Transcript.pdf"));

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
```

- [x] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~LocalResourceStorageTests"
```
Expected: compile errors — no three-argument `Store`, no `MoveInto`.

- [x] **Step 3: Implement the interface**

Replace the `Store` declaration in `src/BeBoosted.Application/Projects/IResourceStorage.cs`:

```csharp
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
```

Its `using BeBoosted.Domain;` is now unused; remove it if the build warns.

- [x] **Step 4: Implement `LocalResourceStorage`**

Replace the body of `src/BeBoosted.Infrastructure/Projects/LocalResourceStorage.cs` (keeping the class declaration and `ResolvePath`/`Exists`/`Delete`):

```csharp
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
        var source = ResolvePath(currentStoredPath);
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
            if (!File.Exists(ResolvePath(storedPath)))
            {
                return storedPath;
            }
        }
    }
}
```

Add `using System.Globalization;` to the file's usings.

- [x] **Step 5: Update the desktop test double**

In `tests/BeBoosted.Desktop.Tests/Support/TestDoubles.cs`, replace `FakeResourceStorage`:

```csharp
public sealed class FakeResourceStorage : IResourceStorage
{
    private readonly HashSet<string> _stored = [];

    public string Store(string relativeFolder, string preferredFileName, string sourcePath)
    {
        var storedPath = FreePath(relativeFolder, preferredFileName);
        _stored.Add(storedPath);
        return storedPath;
    }

    public string? MoveInto(string currentStoredPath, string relativeFolder, string preferredFileName)
    {
        if (!_stored.Remove(currentStoredPath))
        {
            return null;
        }

        var storedPath = FreePath(relativeFolder, preferredFileName);
        _stored.Add(storedPath);
        return storedPath;
    }

    public string ResolvePath(string storedPath) => Path.Combine("fake-resources", storedPath);

    public bool Exists(string storedPath) => _stored.Contains(storedPath);

    public void Delete(string storedPath) => _stored.Remove(storedPath);

    private string FreePath(string relativeFolder, string preferredFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(preferredFileName);
        var extension = Path.GetExtension(preferredFileName);
        for (var attempt = 1; ; attempt++)
        {
            var candidate = attempt == 1 ? preferredFileName : $"{stem} ({attempt}){extension}";
            var storedPath = Path.Combine(relativeFolder, candidate);
            if (!_stored.Contains(storedPath))
            {
                return storedPath;
            }
        }
    }
}
```

- [x] **Step 6: Point `ImportFile` at the new storage API**

Removing the old overload breaks `ProjectService`, so it is repaired here rather than
in Task 4 — this needs only `ResourceLayout`, not the reconciler, so the build stays
green at every task boundary. Replace `ImportFile` in
`src/BeBoosted.Application/Projects/ProjectService.cs`:

```csharp
    /// <summary>Imports a document or image: bytes are copied into app-controlled storage.</summary>
    public Resource ImportFile(ProjectFileId fileId, ResourceKind kind, string sourcePath, string? title = null)
    {
        var file = files.GetById(fileId)
            ?? throw new DomainException("That file no longer exists.");
        var project = Require(file.ProjectId);
        var originalName = Path.GetFileName(sourcePath);
        var id = ResourceId.New();
        var storedPath = storage.Store(
            ResourceLayout.FolderFor(project, file),
            ResourceLayout.FileNameFor(originalName, id.ToString()),
            sourcePath);
        var resource = Resource.Rehydrate(
            id, fileId, kind,
            string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(originalName) : title.Trim(),
            null, null, originalName, storedPath, clock.Now, ResourceIndexState.Pending, clock.Now);
        return AddAndIndex(resource);
    }
```

Then update the one existing assertion in
`tests/BeBoosted.Tests/Projects/ProjectServiceTests.cs` (~line 141), replacing

```csharp
        Assert.Equal(resource.Id + ".pdf", resource.StoredPath);
```

with

```csharp
        Assert.Equal(
            Path.Combine("College Admissions", "Metric Proof", "Transcript.pdf"),
            resource.StoredPath);
```

- [x] **Step 7: Run the tests to verify they pass**

Run:
```bash
dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj \
  --filter "FullyQualifiedName~LocalResourceStorageTests|FullyQualifiedName~ProjectServiceTests"
```
Expected: PASS, including the renamed-path assertion.

---

### Task 4: The reconciler, and wiring it into `ProjectService`

**Files:**
- Create: `src/BeBoosted.Application/Projects/ResourceLayoutReconciler.cs`
- Modify: `src/BeBoosted.Application/Projects/ProjectService.cs:13-23` (constructor), `:33-37` (`RenameProject`)
- Test: `tests/BeBoosted.Tests/Projects/ResourceLayoutReconcilerTests.cs` (create)
- Test: `tests/BeBoosted.Tests/Projects/ProjectServiceTests.cs` (fixture wiring, rename test)

**Interfaces:**
- Consumes: `ResourceLayout` (Task 2), `IResourceStorage.Store`/`MoveInto` (Task 3), `Resource.RelocateTo` (Task 1).
- Produces: `ResourceLayoutReconciler.Reconcile() -> int` and `.ReconcileProject(ProjectId) -> int`, used by Task 5.

- [x] **Step 1: Write the failing tests**

Create `tests/BeBoosted.Tests/Projects/ResourceLayoutReconcilerTests.cs`:

```csharp
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Projects;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Projects;

/// <summary>
/// The reconciler is both the migration for already-imported documents and the
/// rename-sync afterwards. It must be idempotent, and a resource it cannot move must
/// keep working exactly where it is.
/// </summary>
public sealed class ResourceLayoutReconcilerTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 8, 19, 9, 0, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    private sealed class TestPaths : IAppDataPaths
    {
        public TestPaths()
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), $"beboosted-recontest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DataDirectory);
        }

        public string DataDirectory { get; }

        public string LogsDirectory => Path.Combine(DataDirectory, "logs");

        public string ResourcesDirectory => Path.Combine(DataDirectory, "resources");
    }

    private readonly TempDatabase _database = new();
    private readonly TestPaths _paths = new();
    private readonly FixedClock _clock = new();
    private readonly SqliteProjectRepository _projects;
    private readonly SqliteProjectFileRepository _files;
    private readonly SqliteResourceRepository _resources;
    private readonly LocalResourceStorage _storage;

    public ResourceLayoutReconcilerTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _projects = new SqliteProjectRepository(_database.Factory);
        _files = new SqliteProjectFileRepository(_database.Factory);
        _resources = new SqliteResourceRepository(_database.Factory);
        _storage = new LocalResourceStorage(_paths);
    }

    private ResourceLayoutReconciler CreateReconciler()
        => new(_projects, _files, _resources, _storage, _clock);

    /// <summary>A project/file pair plus a legacy guid-named document already on disk.</summary>
    private (Project Project, ProjectFile File, Resource Resource) SeedLegacyDocument(
        string projectName = "College Admissions",
        string fileTitle = "Metric Proof",
        string originalName = "Transcript.pdf",
        string content = "payload")
    {
        var project = Project.Create(projectName, "#ffffff", _clock.Now);
        _projects.Add(project);
        var file = ProjectFile.Create(project.Id, fileTitle, null, _clock.Now);
        _files.Add(file);

        var resource = Resource.CreateStored(
            file.Id, ResourceKind.Document, Path.GetFileNameWithoutExtension(originalName),
            originalName, Guid.NewGuid().ToString("N") + ".pdf", _clock.Now);
        Directory.CreateDirectory(_paths.ResourcesDirectory);
        File.WriteAllText(_storage.ResolvePath(resource.StoredPath!), content);
        _resources.Add(resource);
        return (project, file, resource);
    }

    [Fact]
    public void Reconcile_MigratesGuidNamedFilesIntoNamedFolders()
    {
        var seeded = SeedLegacyDocument();

        var moved = CreateReconciler().Reconcile();

        Assert.Equal(1, moved);
        var reloaded = _resources.GetById(seeded.Resource.Id)!;
        Assert.Equal(
            Path.Combine("College Admissions", "Metric Proof", "Transcript.pdf"),
            reloaded.StoredPath);
        Assert.True(_storage.Exists(reloaded.StoredPath!));
        Assert.Equal("payload", File.ReadAllText(_storage.ResolvePath(reloaded.StoredPath!)));
    }

    [Fact]
    public void Reconcile_IsIdempotent()
    {
        SeedLegacyDocument();
        Assert.Equal(1, CreateReconciler().Reconcile());

        Assert.Equal(0, CreateReconciler().Reconcile());
        Assert.Equal(0, CreateReconciler().Reconcile());
    }

    [Fact]
    public void Reconcile_DisambiguatesTwoDocumentsSharingAName()
    {
        var first = SeedLegacyDocument(content: "first");
        var second = Resource.CreateStored(
            first.File.Id, ResourceKind.Document, "Transcript",
            "Transcript.pdf", Guid.NewGuid().ToString("N") + ".pdf", _clock.Now);
        File.WriteAllText(_storage.ResolvePath(second.StoredPath!), "second");
        _resources.Add(second);

        Assert.Equal(2, CreateReconciler().Reconcile());

        var folder = Path.Combine("College Admissions", "Metric Proof");
        var paths = _resources.GetForFile(first.File.Id).Select(r => r.StoredPath).ToList();
        Assert.Contains(Path.Combine(folder, "Transcript.pdf"), paths);
        Assert.Contains(Path.Combine(folder, "Transcript (2).pdf"), paths);

        // A second run must not shuffle the disambiguated one.
        Assert.Equal(0, CreateReconciler().Reconcile());
    }

    [Fact]
    public void Reconcile_LeavesAResourceWhoseBytesAreMissing_AndStillMigratesItsSiblings()
    {
        var seeded = SeedLegacyDocument();
        var orphan = Resource.CreateStored(
            seeded.File.Id, ResourceKind.Document, "Ghost",
            "Ghost.pdf", Guid.NewGuid().ToString("N") + ".pdf", _clock.Now);
        _resources.Add(orphan); // no bytes ever written

        var moved = CreateReconciler().Reconcile();

        Assert.Equal(1, moved);
        Assert.Equal(
            Path.Combine("College Admissions", "Metric Proof", "Transcript.pdf"),
            _resources.GetById(seeded.Resource.Id)!.StoredPath);
        Assert.Equal(orphan.StoredPath, _resources.GetById(orphan.Id)!.StoredPath);
    }

    [Fact]
    public void Reconcile_IgnoresLinksAndNotes()
    {
        var seeded = SeedLegacyDocument();
        _resources.Add(Resource.CreateLink(seeded.File.Id, "Source", "https://example.com", _clock.Now));
        _resources.Add(Resource.CreateNote(seeded.File.Id, "Idea", "text", _clock.Now));

        Assert.Equal(1, CreateReconciler().Reconcile());
        Assert.All(
            _resources.GetForFile(seeded.File.Id).Where(r => r.Kind != ResourceKind.Document),
            r => Assert.Null(r.StoredPath));
    }

    [Fact]
    public void ReconcileProject_MovesOnlyThatProject()
    {
        var kept = SeedLegacyDocument("Other Project", "Other File", "Other.pdf");
        var target = SeedLegacyDocument();

        Assert.Equal(1, CreateReconciler().ReconcileProject(target.Project.Id));

        Assert.Equal(
            Path.Combine("College Admissions", "Metric Proof", "Transcript.pdf"),
            _resources.GetById(target.Resource.Id)!.StoredPath);
        Assert.Equal(kept.Resource.StoredPath, _resources.GetById(kept.Resource.Id)!.StoredPath);
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
```

Then append this test to `tests/BeBoosted.Tests/Projects/ProjectServiceTests.cs` (its
`StoredPath` assertion was already updated in Task 3):

```csharp
    [Fact]
    public void RenameProject_MovesTheFolder_AndKeepsStoredPathsResolvable()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var source = Path.Combine(_paths.DataDirectory, "Transcript.pdf");
        File.WriteAllText(source, "fake pdf bytes");
        var resource = _service.ImportFile(file.Id, ResourceKind.Document, source);

        _service.RenameProject(project.Id, "College Apps");

        var reloaded = _resources.GetById(resource.Id)!;
        Assert.Equal(
            Path.Combine("College Apps", "Metric Proof", "Transcript.pdf"),
            reloaded.StoredPath);
        Assert.True(_storage.Exists(reloaded.StoredPath!));
        Assert.Equal("fake pdf bytes", File.ReadAllText(_service.ResolveStoredPath(reloaded)!));
    }
```

This test requires the service to be constructed with a reconciler; update the constructor call in the fixture to:

```csharp
        _service = new ProjectService(
            _projects, _files, _resources, _storage,
            new SimpleLocalIndexer(_resources, _storage, _clock), _tasks, blocks, _completions, _clock,
            provenanceInvalidator: null,
            reconciler: new ResourceLayoutReconciler(_projects, _files, _resources, _storage, _clock));
```

- [x] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~ResourceLayoutReconcilerTests|FullyQualifiedName~ProjectServiceTests"
```
Expected: compile errors — `ResourceLayoutReconciler` does not exist and `ProjectService` has no `reconciler` parameter.

- [x] **Step 3: Implement the reconciler**

Create `src/BeBoosted.Application/Projects/ResourceLayoutReconciler.cs`:

```csharp
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
            foreach (var resource in resources.GetForFile(file.Id))
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

                if (storage.MoveInto(current, folder, desired) is not { } relocated)
                {
                    continue; // locked or missing: retried next run
                }

                resource.RelocateTo(relocated, clock.Now);
                resources.Update(resource);
                moved++;
            }
        }

        return moved;
    }
}
```

- [x] **Step 4: Wire it into `ProjectService`**

Add the trailing constructor parameter:

```csharp
    IProvenanceInvalidator? provenanceInvalidator = null,
    ResourceLayoutReconciler? reconciler = null)
```

Replace `RenameProject`:

```csharp
    public void RenameProject(ProjectId id, string name)
    {
        var project = Require(id);
        project.Rename(name, clock.Now);
        projects.Update(project);

        // The rename itself has already succeeded; moving the folder to match is
        // best-effort and never undoes it.
        reconciler?.ReconcileProject(id);
    }
```

`ImportFile` was already pointed at the new storage API in Task 3 and needs no further
change here.

- [x] **Step 5: Run the tests to verify they pass**

Run:
```bash
dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~ResourceLayoutReconcilerTests|FullyQualifiedName~ProjectServiceTests"
```
Expected: PASS.

---

### Task 5: Reconcile at startup

**Files:**
- Modify: `src/BeBoosted.Infrastructure/ServiceCollectionExtensions.cs:45` (registration)
- Modify: `src/BeBoosted.Desktop/App.axaml.cs:61-73` (after the migration block)
- Test: `tests/BeBoosted.Tests/Projects/ResourceLayoutReconcilerTests.cs` (DI resolution)

**Interfaces:**
- Consumes: `ResourceLayoutReconciler` from Task 4.
- Produces: nothing consumed by later tasks.

- [x] **Step 1: Write the failing test**

Append to `tests/BeBoosted.Tests/Projects/ResourceLayoutReconcilerTests.cs`:

```csharp
    [Fact]
    public void Reconcile_OnAnEmptyStore_IsAQuietNoOp()
    {
        Assert.Equal(0, CreateReconciler().Reconcile());
        Assert.Equal(0, CreateReconciler().ReconcileProject(ProjectId.New()));
    }
```

- [x] **Step 2: Run the test to verify it passes**

Run:
```bash
dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~ResourceLayoutReconcilerTests"
```
Expected: PASS. This is a guard, not a red test — startup safety is that an empty or brand-new install reconciles without throwing.

- [x] **Step 3: Register and call it**

In `src/BeBoosted.Infrastructure/ServiceCollectionExtensions.cs`, beside the storage registration:

```csharp
        services.AddSingleton<ResourceLayoutReconciler>();
```

In `src/BeBoosted.Desktop/App.axaml.cs`, after the `try/catch` around `MigrationRunner.Apply(...)` and before the main window is built:

```csharp
            try
            {
                var moved = _services.GetRequiredService<ResourceLayoutReconciler>().Reconcile();
                if (moved > 0)
                {
                    Log.Information("Moved {Count} stored resources into named folders", moved);
                }
            }
            catch (Exception exception)
            {
                // Layout is cosmetic: every resource still resolves through its recorded
                // path, so a failure here must never stop the app from starting.
                Log.Warning(exception, "Resource folder layout could not be reconciled");
            }
```

Add `using BeBoosted.Application.Projects;` to `App.axaml.cs` if not already present.

- [x] **Step 4: Full verification**

Run, in order:
```bash
dotnet format BeBoosted.slnx --verify-no-changes --no-restore
dotnet build BeBoosted.slnx --no-restore -warnaserror
dotnet test BeBoosted.slnx --no-restore --no-build
git diff --check
```
Expected: format clean, 0 warnings / 0 errors, all tests pass (3 screenshot tests still skipped), `git diff --check` exit 0.

---

## Notes for the implementer

- `Resource.OriginalFileName` (not `OriginalName`) is the property holding the user's file name. It is already populated for every imported document.
- `AiServiceTests` also constructs a `LocalResourceStorage`; it does not call `Store`, so it needs no change, but check it compiles.
- `File.Copy` in `Store` deliberately drops `overwrite: true` — `ReserveFreePath` has already guaranteed a free name, so overwriting would mean a race we would rather see fail loudly.
- The reconciler enumerates through `GetAll` → `GetForProject` → `GetForFile`; no repository interface changes are needed.
- Do not add empty-folder pruning to `Delete`. It is listed as a known limitation in the spec and is out of scope here.
