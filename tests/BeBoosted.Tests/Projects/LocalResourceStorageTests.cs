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
        Assert.Null(_storage.MoveInto(escaped, "College", "probe.txt"));
    }

    [Fact]
    public void Store_DisambiguatesACollision()
    {
        const string folder = "College";
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
        const string folder = "College";
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
