using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// The File surface once a File may hold groups and loose resources side by side.
///
/// Two things are load-bearing throughout and are asserted rather than assumed. First,
/// <c>Resources</c> stays the canonical all-resources index and <c>Groups</c>/
/// <c>LooseResources</c> project the very same row instances — a copy would give the
/// reading pane a different object than the list the user clicked. Second, there is one
/// canonical selection: several ListBoxes bind to these collections, and a list that does
/// not hold the selected row must report null without writing null back.
///
/// No test refreshes the view model by hand. Every action here is supposed to refresh, so
/// a manual <c>Refresh()</c> would prove the opposite of what the test claims.
/// </summary>
public sealed class ResourceGroupsViewModelTests
{
    private static FileDetailViewModel OpenFile(ShellViewModel? shell = null)
    {
        var projects = (shell ?? TestShell.Create()).Projects;
        projects.NewProjectName = "Schoolwork";
        Assert.True(projects.TryCreateProject());
        projects.Detail!.NewFileTitle = "Spanish";
        Assert.True(projects.Detail.TryCreateFile());
        return projects.FileDetail!;
    }

    private static ResourceId AddNote(FileDetailViewModel file, string title)
    {
        file.NewNoteTitle = title;
        file.NewNoteContent = $"{title} body";
        Assert.True(file.TryAddNote());
        return file.Resources.Single(r => r.Title == title).Resource.Id;
    }

    private static ResourceId ImportDocument(FileDetailViewModel file, string fileName)
    {
        file.Import(ResourceKind.Document, [Path.Combine(@"C:\anywhere", fileName)]);
        return file.Resources.Single(r => r.Title == Path.GetFileNameWithoutExtension(fileName))
            .Resource.Id;
    }

    private static ResourceGroupViewModel CreateGroup(FileDetailViewModel file, string title)
    {
        file.NewGroupTitle = title;
        Assert.True(file.TryCreateGroup());
        return file.Groups.Single(g => g.Title == title);
    }

    /// <summary>Files a resource through the row's own Move-to flyout, as the view does.</summary>
    private static void MoveInto(FileDetailViewModel file, ResourceId id, string groupTitle)
    {
        var group = file.Groups.Single(g => g.Title == groupTitle);
        var row = file.Resources.Single(r => r.Resource.Id == id);
        Assert.True(row.MoveTargets.Single(t => t.GroupId == group.Id).TryMove());
    }

    /// <summary>
    /// A File with no groups is the flat list it has always been — same rows, same count,
    /// no group chrome and no "loose in this File" header. The feature is invisible until
    /// used, so the loose list must be a projection of the canonical one, not a copy.
    /// </summary>
    [Fact]
    public void NoGroups_IsTheExistingFlatList()
    {
        var file = OpenFile();
        AddNote(file, "Vocab");
        ImportDocument(file, "Verbs.pdf");

        Assert.Equal(2, file.Resources.Count);
        Assert.Equal(file.Resources.Count, file.LooseResources.Count);
        for (var index = 0; index < file.Resources.Count; index++)
        {
            Assert.Same(file.Resources[index], file.LooseResources[index]);
        }

        Assert.False(file.HasGroups);
        Assert.False(file.ShowLooseHeader);
        Assert.False(file.ShowEmptyState);
        Assert.Equal("2 resources", file.CountText);
    }

    /// <summary>
    /// Creating "Unit 5" before filling it is a normal way to work: the header renders with
    /// a count of 0, and the File is no longer empty even though it holds no resources.
    ///
    /// It is also the case where the refresh changes the selection from null to null, so
    /// nothing raises a change for it. The lists were still rebuilt underneath, and have
    /// to be told to re-read what they report — otherwise a refresh that leaves the File
    /// empty leaves stale selection state bound to collections that no longer exist.
    /// </summary>
    [Fact]
    public void EmptyGroup_HasHeaderAndZeroCount()
    {
        var file = OpenFile();
        Assert.Null(file.Selected);
        var notified = new List<string?>();
        file.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        var group = CreateGroup(file, "Unit 5");

        Assert.DoesNotContain(nameof(FileDetailViewModel.Selected), notified);
        Assert.Contains(nameof(FileDetailViewModel.LooseSelectedResource), notified);
        Assert.Same(group, Assert.Single(file.Groups));
        Assert.Equal(0, group.Count);
        Assert.Equal("0 items", group.CountText);
        Assert.Empty(group.Resources);
        Assert.Empty(file.Resources);
        Assert.Empty(file.LooseResources);
        Assert.True(file.HasGroups);
        Assert.False(file.HasLooseResources);
        Assert.False(file.ShowLooseHeader);
        Assert.False(file.ShowEmptyState);
        Assert.Equal(string.Empty, file.NewGroupTitle);
    }

    /// <summary>
    /// Phase 1 ships no group-targeted import: a link, a note and a stored document all
    /// arrive loose, and the group they were added alongside keeps exactly its members.
    /// </summary>
    [Fact]
    public void ImportWithGroups_StaysLoose()
    {
        var file = OpenFile();
        CreateGroup(file, "Unit 3");
        var vocab = AddNote(file, "Vocab");
        MoveInto(file, vocab, "Unit 3");

        file.NewLinkTitle = "Conjugator";
        file.NewLinkUrl = "https://conjuguemos.com";
        Assert.True(file.TryAddLink());
        AddNote(file, "Exam date");
        ImportDocument(file, "Verbs.pdf");

        var unit3 = Assert.Single(file.Groups);
        Assert.Equal(vocab, Assert.Single(unit3.Resources).Resource.Id);
        Assert.Equal(
            ["Conjugator", "Exam date", "Verbs"],
            file.LooseResources.Select(r => r.Title).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(4, file.Resources.Count);
        Assert.Equal("4 resources", file.CountText);
        Assert.True(file.ShowLooseHeader);
        foreach (var row in file.LooseResources.Concat(unit3.Resources))
        {
            Assert.Same(row, file.Resources.Single(r => r.Resource.Id == row.Resource.Id));
        }
    }

    /// <summary>
    /// A rename has to reach every projection of the title at once: the group header, the
    /// Move-to flyout on a row that is not in it, and the reading pane's hold on the
    /// selected member — which is a brand new row instance after the refresh.
    /// </summary>
    [Fact]
    public void RenameGroup_RefreshesHeaderAndTargets()
    {
        var file = OpenFile();
        CreateGroup(file, "Unit 3");
        var marbury = ImportDocument(file, "Marbury.pdf");
        var syllabus = AddNote(file, "Syllabus");
        MoveInto(file, marbury, "Unit 3");
        file.SelectResource(marbury);
        var storedBefore = file.Selected!.StoredAbsolutePath;
        Assert.NotNull(storedBefore);

        var group = Assert.Single(file.Groups);
        group.BeginRename();
        Assert.Equal("Unit 3", group.RenameTitle);
        group.RenameTitle = "Unit 3 - Federalism";
        Assert.True(group.TryCommitRename());

        var renamed = Assert.Single(file.Groups);
        Assert.Equal("Unit 3 - Federalism", renamed.Title);
        Assert.Equal(marbury, Assert.Single(renamed.Resources).Resource.Id);
        var loose = Assert.Single(file.LooseResources);
        Assert.Equal(syllabus, loose.Resource.Id);
        Assert.Equal("Unit 3 - Federalism", Assert.Single(loose.MoveTargets).Title);
        Assert.Equal(marbury, file.Selected!.Resource.Id);
        Assert.Same(file.Selected, renamed.Resources.Single());
        Assert.Same(file.Selected, renamed.SelectedResource);
        Assert.Equal(storedBefore, file.Selected.StoredAbsolutePath);
        Assert.Null(file.GroupNotice);
    }

    /// <summary>
    /// Expansion is restored by group id, not by position — the group view models are
    /// thrown away and rebuilt on every refresh, and a new group arrives expanded. A
    /// collapsed group still answers a search: selecting a member by id opens the group
    /// that holds it and nothing else.
    /// </summary>
    [Fact]
    public void Collapse_SurvivesRefresh_SearchSelectionExpands()
    {
        var file = OpenFile();
        CreateGroup(file, "Unit 2");
        CreateGroup(file, "Unit 3");
        CreateGroup(file, "Unit 4");
        var marbury = AddNote(file, "Marbury notes");
        var brown = AddNote(file, "Brown v Board");
        MoveInto(file, marbury, "Unit 3");
        MoveInto(file, brown, "Unit 4");
        file.Groups.Single(g => g.Title == "Unit 4").IsExpanded = false;

        AddNote(file, "Syllabus"); // an unrelated loose add, which refreshes everything

        Assert.True(file.Groups.Single(g => g.Title == "Unit 3").IsExpanded);
        Assert.False(file.Groups.Single(g => g.Title == "Unit 4").IsExpanded);

        // Removing the group ahead of them shifts every position by one. Expansion is
        // restored by group id, so the collapsed group is still the one that was collapsed
        // — restoring by index would hand Unit 4 the state that belonged to Unit 3.
        file.Groups.Single(g => g.Title == "Unit 2").UngroupCommand.Execute(null);

        Assert.Equal(["Unit 3", "Unit 4"], file.Groups.Select(g => g.Title));
        Assert.True(file.Groups.Single(g => g.Title == "Unit 3").IsExpanded);
        Assert.False(file.Groups.Single(g => g.Title == "Unit 4").IsExpanded);

        file.SelectResource(brown);

        var unit4 = file.Groups.Single(g => g.Title == "Unit 4");
        Assert.True(unit4.IsExpanded);
        Assert.Equal(brown, file.Selected!.Resource.Id);
        Assert.Same(file.Selected, unit4.SelectedResource);
        Assert.Same(file.Selected, Assert.Single(unit4.Resources));
        Assert.Null(file.Groups.Single(g => g.Title == "Unit 3").SelectedResource);
        Assert.Null(file.LooseSelectedResource);
    }

    /// <summary>
    /// The one that the multi-ListBox layout makes dangerous. Each list reports the
    /// canonical selection only when it holds it, and a list that does not hold it clears
    /// its own SelectedItem — which arrives here as a null write that must be refused.
    /// Collapsing a group is the same event by another route.
    /// </summary>
    [Fact]
    public void SelectingBetweenLists_DoesNotClearTheReader()
    {
        var file = OpenFile();
        CreateGroup(file, "Unit 3");
        CreateGroup(file, "Unit 4");
        var marbury = AddNote(file, "Marbury notes");
        var brown = AddNote(file, "Brown v Board");
        AddNote(file, "Syllabus");
        MoveInto(file, marbury, "Unit 3");
        MoveInto(file, brown, "Unit 4");

        var unit3 = file.Groups.Single(g => g.Title == "Unit 3");
        var unit4 = file.Groups.Single(g => g.Title == "Unit 4");
        var loose = Assert.Single(file.LooseResources);

        unit3.SelectedResource = Assert.Single(unit3.Resources);
        // Every other list now clears its own selection, exactly as an unselected ListBox does.
        unit4.SelectedResource = null;
        file.LooseSelectedResource = null;
        AssertExactlySelected(file, marbury);

        file.LooseSelectedResource = loose;
        unit3.SelectedResource = null;
        unit4.SelectedResource = null;
        AssertExactlySelected(file, loose.Resource.Id);

        unit4.SelectedResource = Assert.Single(unit4.Resources);
        unit3.SelectedResource = null;
        file.LooseSelectedResource = null;
        AssertExactlySelected(file, brown);

        // Collapsing is not a deselection: the reading pane keeps the row it is showing.
        unit4.IsExpanded = false;
        AssertExactlySelected(file, brown);
    }

    /// <summary>Exactly one list reports the canonical row, and it is the canonical instance.</summary>
    private static void AssertExactlySelected(FileDetailViewModel file, ResourceId expected)
    {
        var selected = file.Selected;
        Assert.NotNull(selected);
        Assert.Equal(expected, selected.Resource.Id);
        Assert.Same(file.Resources.Single(r => r.Resource.Id == expected), selected);

        var reported = file.Groups
            .Select(g => g.SelectedResource)
            .Append(file.LooseSelectedResource)
            .Where(r => r is not null)
            .ToList();
        Assert.Same(selected, Assert.Single(reported));
        Assert.Same(selected.Derivations, Assert.Single(reported)!.Derivations);
    }

    /// <summary>
    /// Ungroup destroys nothing, so it asks nothing: the header goes, every member becomes
    /// loose, and no confirmation is ever raised.
    /// </summary>
    [Fact]
    public void Ungroup_RequiresNoConfirmation_AndPreservesRows()
    {
        var file = OpenFile();
        CreateGroup(file, "Unit 3");
        var marbury = AddNote(file, "Marbury notes");
        var federalist = AddNote(file, "Federalist 10");
        MoveInto(file, marbury, "Unit 3");
        MoveInto(file, federalist, "Unit 3");

        Assert.Single(file.Groups).UngroupCommand.Execute(null);

        Assert.Null(file.Confirmation);
        Assert.Empty(file.Groups);
        Assert.False(file.HasGroups);
        Assert.False(file.ShowLooseHeader);
        Assert.Equal(
            new HashSet<ResourceId> { marbury, federalist },
            file.LooseResources.Select(r => r.Resource.Id).ToHashSet());
        Assert.Equal(2, file.Resources.Count);
        Assert.Equal("2 resources", file.CountText);
    }

    /// <summary>
    /// Deleting a group destroys its documents, so it goes behind the same two-step prompt
    /// a File deletion uses, and the prompt names the group and counts what goes with it —
    /// pluralized, including the empty case. Keep leaves everything; Confirm takes the
    /// group's members and nothing else.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DeleteGroup_ConfirmAndCancel(int members)
    {
        var file = OpenFile();
        CreateGroup(file, "Unit 3");
        var doomed = new List<ResourceId>();
        for (var index = 0; index < members; index++)
        {
            var id = AddNote(file, $"Member {index}");
            MoveInto(file, id, "Unit 3");
            doomed.Add(id);
        }

        var survivor = AddNote(file, "Syllabus");

        Assert.Single(file.Groups).RequestDeleteCommand.Execute(null);

        Assert.NotNull(file.Confirmation);
        Assert.Equal(
            $"Delete 'Unit 3'? Its {members} resource{(members == 1 ? string.Empty : "s")} "
                + "and any stored files are deleted too.",
            file.Confirmation!.Message);
        Assert.Equal("Delete group", file.Confirmation.ConfirmLabel);
        Assert.False(file.Confirmation.IsTaskDeletion);

        file.KeepPromptCommand.Execute(null);

        Assert.Null(file.Confirmation);
        Assert.Equal(members, Assert.Single(file.Groups).Count);
        Assert.Equal(members + 1, file.Resources.Count);

        Assert.Single(file.Groups).RequestDeleteCommand.Execute(null);
        file.ConfirmPromptCommand.Execute(null);

        Assert.Empty(file.Groups);
        Assert.Equal([survivor], file.Resources.Select(r => r.Resource.Id));
        Assert.Equal([survivor], file.LooseResources.Select(r => r.Resource.Id));
        foreach (var id in doomed)
        {
            Assert.DoesNotContain(file.Resources, r => r.Resource.Id == id);
        }
    }

    /// <summary>
    /// A mutation that throws is a failure, never a rollback silently reported as success.
    /// The view model shows why and leaves every collection — and the reading pane — exactly
    /// as they were, rather than optimistically rebuilding around a change that never landed.
    /// </summary>
    [Fact]
    public void FailedMutation_ShowsNoticeWithoutOptimisticRefresh()
    {
        var file = OpenFile(TestShell.Create(projectMutations: new FailingProjectMutations()));
        CreateGroup(file, "Unit 3");
        var marbury = AddNote(file, "Marbury notes");
        MoveInto(file, marbury, "Unit 3");
        var group = Assert.Single(file.Groups);
        var selectedBefore = file.Selected;
        var rowBefore = Assert.Single(group.Resources);

        group.UngroupCommand.Execute(null);

        Assert.False(string.IsNullOrWhiteSpace(file.GroupNotice));
        Assert.Same(group, Assert.Single(file.Groups));
        Assert.Same(rowBefore, Assert.Single(group.Resources));
        Assert.Same(rowBefore, Assert.Single(file.Resources));
        Assert.Same(selectedBefore, file.Selected);
        Assert.Empty(file.LooseResources);
    }

    /// <summary>
    /// A blank title is refused at the domain and reported at the presentation boundary.
    /// The flyout keeps what the user typed so they can fix it, and neither the persisted
    /// group nor the header changes.
    /// </summary>
    [Fact]
    public void BlankCreateOrRename_KeepsDialogData()
    {
        var file = OpenFile();

        file.NewGroupTitle = "   ";
        Assert.False(file.TryCreateGroup());
        Assert.False(string.IsNullOrWhiteSpace(file.GroupNotice));
        Assert.Equal("   ", file.NewGroupTitle);
        Assert.Empty(file.Groups);

        var group = CreateGroup(file, "Unit 3");
        Assert.Null(file.GroupNotice);
        group.BeginRename();
        group.RenameTitle = " ";

        Assert.False(group.TryCommitRename());
        Assert.False(string.IsNullOrWhiteSpace(file.GroupNotice));
        Assert.Equal(" ", group.RenameTitle);
        Assert.Equal("Unit 3", group.Title);

        // A later, unrelated action refreshes from the repository: the persisted title is
        // still the old one, so nothing was written before the refusal.
        AddNote(file, "Syllabus");
        Assert.Equal("Unit 3", Assert.Single(file.Groups).Title);
    }

    /// <summary>
    /// A collapsed group hides rows from the eye, not from the File. Its members still
    /// count towards the File deletion prompt, and are still reachable by resource id from
    /// search navigation.
    /// </summary>
    [Fact]
    public void TotalCountAndSearchIncludeCollapsedMembers()
    {
        var file = OpenFile();
        CreateGroup(file, "Unit 3");
        var marbury = AddNote(file, "Marbury notes");
        AddNote(file, "Syllabus");
        MoveInto(file, marbury, "Unit 3");
        var unit3 = Assert.Single(file.Groups);
        unit3.IsExpanded = false;

        file.RequestDeleteCommand.Execute(null);

        Assert.NotNull(file.Confirmation);
        Assert.Contains("2 resources", file.Confirmation!.Message, StringComparison.Ordinal);
        file.KeepPromptCommand.Execute(null);
        Assert.Equal("2 resources", file.CountText);

        file.SelectResource(marbury);

        Assert.Equal(marbury, file.Selected!.Resource.Id);
        Assert.Same(file.Selected, Assert.Single(unit3.Resources));
        Assert.True(unit3.IsExpanded);
    }

    /// <summary>
    /// The Move-to flyout never offers the container the row is already in, and filing a
    /// resource rebuilds the surface itself — no caller refreshes on its behalf. The
    /// canonical count is unchanged either way: filing moves a resource, it does not add one.
    /// </summary>
    [Fact]
    public void MoveTargets_ExcludeCurrentContainer_AndMoveWithoutManualRefresh()
    {
        var file = OpenFile();
        file.NewNoteTitle = "Vocab";
        file.NewNoteContent = "hola";
        Assert.True(file.TryAddNote());
        var id = Assert.Single(file.Resources).Resource.Id;
        file.NewGroupTitle = "Unit 3";
        Assert.True(file.TryCreateGroup());
        file.NewGroupTitle = "Unit 4";
        Assert.True(file.TryCreateGroup());
        var firstId = file.Groups[0].Id;
        var secondId = file.Groups[1].Id;
        var looseRow = Assert.Single(file.LooseResources);
        Assert.DoesNotContain(looseRow.MoveTargets, t => t.GroupId is null);
        Assert.True(looseRow.MoveTargets.Single(t => t.GroupId == firstId).TryMove());
        var moved = Assert.Single(file.Groups.Single(g => g.Id == firstId).Resources);
        Assert.Equal(id, moved.Resource.Id);
        Assert.Same(moved, file.Selected);
        Assert.DoesNotContain(moved.MoveTargets, t => t.GroupId == firstId);
        Assert.Contains(moved.MoveTargets, t => t.GroupId == secondId);
        Assert.True(moved.MoveTargets.Single(t => t.GroupId is null).TryMove());
        Assert.Equal(id, Assert.Single(file.LooseResources).Resource.Id);
        Assert.Equal("1 resource", file.CountText);
    }

    /// <summary>
    /// The desktop double must refuse the write SQLite refuses. A freshly created group
    /// holds the empty "not yet reserved" segment, and persisting that would leave a row
    /// naming a directory nothing ever claimed — the same refusal
    /// <c>SqliteResourceGroupRepositoryTests.Add_WithAnUnreservedFolderSegment_IsRefused</c>
    /// pins on the real repository. Nothing reached this double until group actions had a
    /// view-model caller, so it has been an unproven claim until now.
    /// </summary>
    [Fact]
    public void UnreservedFolderSegment_IsRefusedByTheInMemoryGroupRepository()
    {
        var groups = new InMemoryResourceGroupRepository();
        var unreserved = ResourceGroup.Create(
            ProjectFileId.New(), "Unit 3", 0, DateTimeOffset.UnixEpoch);

        var error = Assert.Throws<DomainException>(() => groups.Add(unreserved));

        Assert.Equal("A group needs a claimed folder segment.", error.Message);
        Assert.Null(groups.GetById(unreserved.Id));
    }
}
