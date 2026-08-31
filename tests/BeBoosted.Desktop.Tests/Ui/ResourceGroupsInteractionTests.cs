using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// The File surface's group chrome, driven through the controls the user actually
/// touches. The view-model tests next door model ListBox behaviour by hand and stay green
/// against a binding pointed at the wrong existing property; these do not, because every
/// action here starts at a rendered control found by its accessible name and every
/// assertion reads what the window shows afterwards.
///
/// Setup may use view-model entry points — creating the project, the File and its
/// resources is not what is under test. The action under test never does.
/// </summary>
public sealed class ResourceGroupsInteractionTests
{
    private sealed record Fixture(MainWindow Window, ShellViewModel Shell, FileDetailViewModel File);

    /// <summary>Schoolwork → Spanish, open on the File surface, as the app opens it.</summary>
    private static Fixture OpenSpanishFile(double width = 1440, double height = 960)
    {
        var shell = TestShell.Create();
        var window = new MainWindow { DataContext = shell, Width = width, Height = height };
        window.Show();
        shell.NavigateCommand.Execute(AppSection.Projects);
        var projects = shell.Projects;
        projects.NewProjectName = "Schoolwork";
        Assert.True(projects.TryCreateProject());
        projects.Detail!.NewFileTitle = "Spanish";
        Assert.True(projects.Detail.TryCreateFile());
        window.CaptureRenderedFrame();
        window.CaptureRenderedFrame();
        return new Fixture(window, shell, projects.FileDetail!);
    }

    private static void AddNote(FileDetailViewModel file, string title)
    {
        file.NewNoteTitle = title;
        file.NewNoteContent = $"{title} body";
        Assert.True(file.TryAddNote());
    }

    private static ResourceGroupViewModel AddGroup(FileDetailViewModel file, string title)
    {
        file.NewGroupTitle = title;
        Assert.True(file.TryCreateGroup());
        return file.Groups.Single(group => group.Title == title);
    }

    private static void MoveByViewModel(FileDetailViewModel file, string row, string groupTitle)
    {
        var group = file.Groups.Single(g => g.Title == groupTitle);
        var target = file.Resources.Single(r => r.Title == row)
            .MoveTargets.Single(t => t.GroupId == group.Id);
        Assert.True(target.TryMove());
    }

    // ---- rendered-control lookup -------------------------------------------------

    private static IEnumerable<Button> VisibleButtons(Visual root)
        => root.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible);

    private static Button ButtonNamed(Visual root, string automationName)
        => VisibleButtons(root).Single(b => AutomationProperties.GetName(b) == automationName);

    private static Button? MaybeButtonNamed(Visual root, string automationName)
        => VisibleButtons(root).SingleOrDefault(b => AutomationProperties.GetName(b) == automationName);

    private static void Click(TopLevel host, Control control)
    {
        var point = control.TranslatePoint(
            new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), host)!.Value;
        host.MouseDown(point, MouseButton.Left);
        host.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        host.CaptureRenderedFrame();
    }

    private static void ClickByName(MainWindow window, string automationName)
        => Click(window, ButtonNamed(window, automationName));

    /// <summary>Opens a named button's flyout by pointer and returns its realized content.</summary>
    private static (Flyout Flyout, Control Content) OpenFlyout(MainWindow window, string automationName)
    {
        var button = ButtonNamed(window, automationName);
        Click(window, button);
        var flyout = Assert.IsType<Flyout>(button.Flyout);
        Assert.True(flyout.IsOpen, $"'{automationName}' did not open its flyout.");
        var content = Assert.IsAssignableFrom<Control>(flyout.Content);
        Dispatcher.UIThread.RunJobs();
        TopLevel.GetTopLevel(content)?.CaptureRenderedFrame();
        return (flyout, content);
    }

    private static void ClickInPopup(Control popupChild)
    {
        var host = TopLevel.GetTopLevel(popupChild)!;
        Click(host, popupChild);
    }

    private static bool ShowsText(MainWindow window, string text)
        => window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Any(block => block.Text == text && block.IsEffectivelyVisible);

    /// <summary>
    /// The reading pane's title, by accessible name like every other finder here. Located
    /// by font size it would go null the moment somebody restyled the pane, and four tests
    /// would fail saying nothing about why.
    /// </summary>
    private static string? ReadingPaneTitle(MainWindow window)
        => window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible
                && AutomationProperties.GetName(block) == "Selected resource")
            .Select(block => block.Text)
            .FirstOrDefault();

    /// <summary>The colour behind a palette token, so tests name the token, not a hex.</summary>
    private static Color Token(string key)
    {
        Assert.True(
            Avalonia.Application.Current!.TryFindResource(key, ThemeVariant.Light, out var value),
            $"no such palette token: {key}");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static Color Painted(IBrush? brush)
        => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static void Hover(MainWindow window, Control control)
    {
        var point = control.TranslatePoint(new Point(12, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
    }

    /// <summary>
    /// Fluent draws the expander's header and its body from two unrelated resource sets, so
    /// these are the parts a repaint has to reach. Named parts, from the 12.1.1 template.
    /// </summary>
    private static (Border HeaderFill, Border Body, Border Chevron, ToggleButton Header)
        HeaderParts(Expander expander)
    {
        var header = expander.GetVisualDescendants().OfType<ToggleButton>().First();
        return (
            header.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ToggleButtonBackground"),
            expander.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ExpanderContent"),
            expander.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ExpandCollapseChevronBorder"),
            header);
    }

    private static Control? Focused(MainWindow window)
        => window.FocusManager?.GetFocusedElement() as Control;

    private static ListBoxItem RowItem(MainWindow window, string title)
        => window.GetVisualDescendants()
            .OfType<ListBoxItem>()
            .Single(item => item.IsEffectivelyVisible
                && item.DataContext is ResourceRowViewModel row && row.Title == title);

    private static bool RowIsRendered(MainWindow window, string title)
        => window.GetVisualDescendants()
            .OfType<ListBoxItem>()
            .Any(item => item.IsEffectivelyVisible
                && item.DataContext is ResourceRowViewModel row && row.Title == title);

    private static Expander GroupHeader(MainWindow window, string title)
        => window.GetVisualDescendants()
            .OfType<Expander>()
            .Single(expander => expander.IsEffectivelyVisible
                && AutomationProperties.GetName(expander) == $"Group {title}");

    private static TextBox TextBoxNamed(Visual root, string automationName)
        => root.GetVisualDescendants()
            .OfType<TextBox>()
            .Single(box => AutomationProperties.GetName(box) == automationName);

    // ---- the flat File, unchanged ------------------------------------------------

    /// <summary>
    /// A File with no groups is the list it has always been: no expander, no "loose in this
    /// File" heading, no Move affordance — the feature is invisible until used. Catches a
    /// loose heading bound to something that is true before any group exists, and group
    /// chrome rendered unconditionally.
    /// </summary>
    [AvaloniaFact]
    public void NoGroups_RendersTheFlatListWithNoGroupChrome()
    {
        var fixture = OpenSpanishFile();
        AddNote(fixture.File, "Vocab");
        AddNote(fixture.File, "Syllabus");
        fixture.Window.CaptureRenderedFrame();

        Assert.True(RowIsRendered(fixture.Window, "Vocab"));
        Assert.True(RowIsRendered(fixture.Window, "Syllabus"));
        Assert.False(ShowsText(fixture.Window, "loose in this File"));
        Assert.DoesNotContain(
            fixture.Window.GetVisualDescendants().OfType<Expander>(),
            expander => expander.IsEffectivelyVisible);
        Assert.Null(MaybeButtonNamed(fixture.Window, "Move Vocab"));
        Assert.False(ShowsText(fixture.Window, "Nothing collected yet."));
        fixture.Window.Close();
    }

    /// <summary>
    /// An empty group is a normal way to work, so its header renders with a count of 0 —
    /// and the File is no longer empty, so the empty state goes. Catches the empty state
    /// bound to <c>!HasResources</c>, which renders "Nothing collected yet." underneath a
    /// group header that plainly contradicts it.
    /// </summary>
    [AvaloniaFact]
    public void AnEmptyGroup_ReplacesTheEmptyState_WithItsHeaderAndZeroCount()
    {
        var fixture = OpenSpanishFile();
        Assert.True(ShowsText(fixture.Window, "Nothing collected yet."));

        AddGroup(fixture.File, "Unit 5");
        fixture.Window.CaptureRenderedFrame();

        Assert.True(GroupHeader(fixture.Window, "Unit 5").IsEffectivelyVisible);
        Assert.True(ShowsText(fixture.Window, "Unit 5"));
        Assert.True(ShowsText(fixture.Window, "0 items"));
        Assert.False(ShowsText(fixture.Window, "Nothing collected yet."));
        Assert.False(ShowsText(fixture.Window, "loose in this File"));
        fixture.Window.Close();
    }

    // ---- the New group affordance ------------------------------------------------

    /// <summary>
    /// The toolbar flyout is the only way in without a view-model call: type a title, press
    /// Create, and the header appears while the flyout closes and the box empties.
    /// </summary>
    [AvaloniaFact]
    public void TheToolbarFlyout_CreatesAGroup_AndClosesOnSuccess()
    {
        var fixture = OpenSpanishFile();

        var (flyout, content) = OpenFlyout(fixture.Window, "New resource group");
        TextBoxNamed(content, "New group title").Text = "Unit 3";
        Dispatcher.UIThread.RunJobs();
        ClickInPopup(ButtonNamed(content, "Create resource group"));
        fixture.Window.CaptureRenderedFrame();

        Assert.Equal("Unit 3", Assert.Single(fixture.File.Groups).Title);
        Assert.True(GroupHeader(fixture.Window, "Unit 3").IsEffectivelyVisible);
        Assert.False(flyout.IsOpen);
        Assert.Equal(string.Empty, fixture.File.NewGroupTitle);
        fixture.Window.Close();
    }

    /// <summary>
    /// A refused title leaves the form open with the text still in it and says why on the
    /// surface behind. Catches a handler that closes its flyout unconditionally, and a
    /// missing GroupNotice.
    /// </summary>
    [AvaloniaFact]
    public void ABlankGroupTitle_KeepsTheFlyoutOpen_AndShowsTheNotice()
    {
        var fixture = OpenSpanishFile();

        var (flyout, content) = OpenFlyout(fixture.Window, "New resource group");
        var box = TextBoxNamed(content, "New group title");
        box.Text = "   ";
        Dispatcher.UIThread.RunJobs();
        ClickInPopup(ButtonNamed(content, "Create resource group"));
        fixture.Window.CaptureRenderedFrame();

        Assert.Empty(fixture.File.Groups);
        Assert.True(flyout.IsOpen, "a refused title closed the form the user still has to fix");
        Assert.Equal("   ", box.Text);
        Assert.Equal("   ", fixture.File.NewGroupTitle);
        Assert.True(ShowsText(fixture.Window, "A group needs a title."));
        fixture.Window.Close();
    }

    // ---- the Move-to flyout ------------------------------------------------------

    /// <summary>
    /// The flyout offers every group but the one already holding the row, plus the File
    /// itself once the row is in a group. Catches a flyout bound to the File's groups
    /// rather than the row's own targets.
    /// </summary>
    [AvaloniaFact]
    public void TheMoveFlyout_ListsEveryTargetButTheCurrentContainer()
    {
        var fixture = OpenSpanishFile();
        AddNote(fixture.File, "Vocab");
        AddGroup(fixture.File, "Unit 3");
        AddGroup(fixture.File, "Unit 4");
        MoveByViewModel(fixture.File, "Vocab", "Unit 3");
        fixture.Window.CaptureRenderedFrame();

        var (_, content) = OpenFlyout(fixture.Window, "Move Vocab");

        var offered = content.GetVisualDescendants()
            .OfType<Button>()
            .Select(button => AutomationProperties.GetName(button))
            .ToList();
        Assert.Equal(["Move to Unit 4", "Move to loose in this File"], offered);
        var target = content.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "Move to Unit 4");
        Assert.IsType<ResourceMoveTargetViewModel>(target.DataContext);
        fixture.Window.Close();
    }

    /// <summary>
    /// Filing a row from its own flyout moves it, keeps it selected in the reading pane,
    /// and closes the flyout. This is the whole binding path — MoveTargets on the row, the
    /// click handler, and the refresh behind it — driven from the rendered button.
    ///
    /// Worth knowing about the close: the refresh replaces every row wholesale, so the
    /// button this flyout hangs off is detached mid-click and the flyout tears itself down
    /// before the handler's own CloseFlyout runs. The outcome asserted below is the one the
    /// user wants either way; the handler's close is what covers a mutation that does not
    /// refresh. A move that FAILS does not refresh, so its flyout stays open, which is the
    /// same contract the create and rename forms keep.
    /// </summary>
    [AvaloniaFact]
    public void MovingARow_FilesItIntoTheGroup_AndClosesTheFlyout()
    {
        var fixture = OpenSpanishFile();
        AddNote(fixture.File, "Vocab");
        AddNote(fixture.File, "Syllabus");
        AddGroup(fixture.File, "Unit 3");
        fixture.Window.CaptureRenderedFrame();

        var (flyout, content) = OpenFlyout(fixture.Window, "Move Vocab");
        ClickInPopup(content.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "Move to Unit 3"));
        fixture.Window.CaptureRenderedFrame();

        var group = Assert.Single(fixture.File.Groups);
        Assert.Equal("Vocab", Assert.Single(group.Resources).Title);
        Assert.Equal("Syllabus", Assert.Single(fixture.File.LooseResources).Title);
        Assert.False(flyout.IsOpen);
        Assert.Equal("1 item", group.CountText);
        Assert.True(ShowsText(fixture.Window, "1 item"));
        Assert.True(ShowsText(fixture.Window, "loose in this File"));
        Assert.Equal("Vocab", ReadingPaneTitle(fixture.Window));
        Assert.True(RowIsRendered(fixture.Window, "Vocab"));
        Assert.True(RowIsRendered(fixture.Window, "Syllabus"));
        fixture.Window.Close();
    }

    /// <summary>
    /// Into a group, on to another, and back out to loose — each hop through the rendered
    /// flyout of the row as it currently stands, because the row instance is replaced by
    /// every refresh and a stale one would file the wrong thing.
    /// </summary>
    [AvaloniaFact]
    public void MovingBetweenGroups_AndBackOutToLoose_FollowsTheRow()
    {
        var fixture = OpenSpanishFile();
        AddNote(fixture.File, "Vocab");
        AddGroup(fixture.File, "Unit 3");
        AddGroup(fixture.File, "Unit 4");
        fixture.Window.CaptureRenderedFrame();

        MoveThroughFlyout(fixture.Window, "Move Vocab", "Move to Unit 3");
        Assert.Equal("Vocab", Assert.Single(fixture.File.Groups.Single(g => g.Title == "Unit 3").Resources).Title);

        MoveThroughFlyout(fixture.Window, "Move Vocab", "Move to Unit 4");
        Assert.Empty(fixture.File.Groups.Single(g => g.Title == "Unit 3").Resources);
        Assert.Equal("Vocab", Assert.Single(fixture.File.Groups.Single(g => g.Title == "Unit 4").Resources).Title);
        Assert.False(ShowsText(fixture.Window, "loose in this File"));

        MoveThroughFlyout(fixture.Window, "Move Vocab", "Move to loose in this File");
        Assert.Empty(fixture.File.Groups.Single(g => g.Title == "Unit 4").Resources);
        Assert.Equal("Vocab", Assert.Single(fixture.File.LooseResources).Title);
        Assert.True(ShowsText(fixture.Window, "loose in this File"));
        Assert.Equal("Vocab", ReadingPaneTitle(fixture.Window));
        fixture.Window.Close();
    }

    private static void MoveThroughFlyout(MainWindow window, string rowButton, string targetButton)
    {
        var (flyout, content) = OpenFlyout(window, rowButton);
        ClickInPopup(content.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == targetButton));
        window.CaptureRenderedFrame();
        Assert.False(flyout.IsOpen);
    }

    // ---- one selection across several lists --------------------------------------

    /// <summary>
    /// Several ListBoxes bind to the one selection, and every list that does not hold it
    /// clears its own SelectedItem. Bound straight to <c>Selected</c>, whose setter is
    /// unguarded, that null write wipes the reading pane the instant another list renders
    /// or wins a click. The delegated properties refuse it. Nothing but a rendered ListBox
    /// exercises this, which is why the view-model tests cannot catch it.
    /// </summary>
    [AvaloniaFact]
    public void SelectingAcrossTheGroupAndLooseLists_KeepsTheReadingPane()
    {
        var fixture = OpenSpanishFile();
        AddNote(fixture.File, "Vocab");
        AddNote(fixture.File, "Syllabus");
        AddGroup(fixture.File, "Unit 3");
        MoveByViewModel(fixture.File, "Vocab", "Unit 3");
        fixture.Window.CaptureRenderedFrame();

        // Rendering alone is enough: the list that does not hold the selection has already
        // written its null back by now if it was allowed to.
        Assert.Equal("Vocab", ReadingPaneTitle(fixture.Window));

        Click(fixture.Window, RowItem(fixture.Window, "Syllabus"));
        Assert.Equal("Syllabus", fixture.File.Selected?.Title);
        Assert.Equal("Syllabus", ReadingPaneTitle(fixture.Window));

        Click(fixture.Window, RowItem(fixture.Window, "Vocab"));
        Assert.Equal("Vocab", fixture.File.Selected?.Title);
        Assert.Equal("Vocab", ReadingPaneTitle(fixture.Window));
        fixture.Window.Close();
    }

    // ---- the header ---------------------------------------------------------------

    /// <summary>Collapsing hides the group's rows and nothing else; expanding brings them back.</summary>
    [AvaloniaFact]
    public void CollapsingAGroup_HidesItsRows_AndExpandingShowsThemAgain()
    {
        var fixture = OpenSpanishFile();
        AddNote(fixture.File, "Vocab");
        AddNote(fixture.File, "Syllabus");
        AddGroup(fixture.File, "Unit 3");
        MoveByViewModel(fixture.File, "Vocab", "Unit 3");
        fixture.Window.CaptureRenderedFrame();

        var expander = GroupHeader(fixture.Window, "Unit 3");
        Assert.True(expander.IsExpanded);

        Click(fixture.Window, expander.GetVisualDescendants().OfType<ToggleButton>().First());
        Assert.False(expander.IsExpanded);
        Assert.False(fixture.File.Groups.Single().IsExpanded);
        Assert.False(RowIsRendered(fixture.Window, "Vocab"));
        Assert.True(RowIsRendered(fixture.Window, "Syllabus"));
        Assert.True(ShowsText(fixture.Window, "Unit 3"));

        Click(fixture.Window, expander.GetVisualDescendants().OfType<ToggleButton>().First());
        Assert.True(expander.IsExpanded);
        Assert.True(RowIsRendered(fixture.Window, "Vocab"));
        fixture.Window.Close();
    }

    /// <summary>
    /// The header's buttons live inside the expander's own toggle, so an unhandled press
    /// would collapse the group as a side effect of asking to rename it.
    /// </summary>
    [AvaloniaFact]
    public void AHeaderButton_DoesNotToggleTheGroup()
    {
        var fixture = OpenSpanishFile();
        AddNote(fixture.File, "Vocab");
        AddGroup(fixture.File, "Unit 3");
        MoveByViewModel(fixture.File, "Vocab", "Unit 3");
        fixture.Window.CaptureRenderedFrame();

        var expander = GroupHeader(fixture.Window, "Unit 3");
        ClickByName(fixture.Window, "Rename group Unit 3");

        Assert.True(expander.IsExpanded);
        Assert.True(RowIsRendered(fixture.Window, "Vocab"));
        fixture.Window.Close();
    }

    /// <summary>
    /// Rename opens on the current title (the seeding click), commits from the flyout, and
    /// the header shows the new name with its members untouched.
    /// </summary>
    [AvaloniaFact]
    public void RenamingAGroup_UpdatesItsHeader_AndKeepsItsRows()
    {
        var fixture = OpenSpanishFile();
        AddNote(fixture.File, "Vocab");
        AddGroup(fixture.File, "Unit 3");
        MoveByViewModel(fixture.File, "Vocab", "Unit 3");
        fixture.Window.CaptureRenderedFrame();

        var (flyout, content) = OpenFlyout(fixture.Window, "Rename group Unit 3");
        var box = TextBoxNamed(content, "Group title");
        Assert.Equal("Unit 3", box.Text); // seeded on the way open
        box.Text = "Unit 3 — Federalism";
        Dispatcher.UIThread.RunJobs();
        ClickInPopup(ButtonNamed(content, "Save group title"));
        fixture.Window.CaptureRenderedFrame();

        Assert.False(flyout.IsOpen);
        Assert.Equal("Unit 3 — Federalism", Assert.Single(fixture.File.Groups).Title);
        Assert.True(GroupHeader(fixture.Window, "Unit 3 — Federalism").IsEffectivelyVisible);
        Assert.False(ShowsText(fixture.Window, "Unit 3"));
        Assert.True(RowIsRendered(fixture.Window, "Vocab"));
        Assert.True(ShowsText(fixture.Window, "1 item"));
        fixture.Window.Close();
    }

    /// <summary>A blank rename is refused: the form stays open, the text stays, the notice says why.</summary>
    [AvaloniaFact]
    public void ABlankGroupRename_KeepsTheFlyoutOpen_AndShowsTheNotice()
    {
        var fixture = OpenSpanishFile();
        AddGroup(fixture.File, "Unit 3");
        fixture.Window.CaptureRenderedFrame();

        var (flyout, content) = OpenFlyout(fixture.Window, "Rename group Unit 3");
        var box = TextBoxNamed(content, "Group title");
        box.Text = "  ";
        Dispatcher.UIThread.RunJobs();
        ClickInPopup(ButtonNamed(content, "Save group title"));
        fixture.Window.CaptureRenderedFrame();

        Assert.True(flyout.IsOpen);
        Assert.Equal("  ", box.Text);
        Assert.Equal("Unit 3", Assert.Single(fixture.File.Groups).Title);
        Assert.True(ShowsText(fixture.Window, "A group needs a title."));
        fixture.Window.Close();
    }

    /// <summary>
    /// Ungroup destroys nothing, so it asks nothing: the rows drop to loose, the group's
    /// chrome goes with it, and every unrelated row is still there.
    /// </summary>
    [AvaloniaFact]
    public void Ungrouping_AsksNothing_AndLeavesEveryRowLoose()
    {
        var fixture = OpenSpanishFile();
        AddNote(fixture.File, "Vocab");
        AddNote(fixture.File, "Syllabus");
        AddGroup(fixture.File, "Unit 3");
        MoveByViewModel(fixture.File, "Vocab", "Unit 3");
        fixture.Window.CaptureRenderedFrame();

        ClickByName(fixture.Window, "Ungroup Unit 3");

        Assert.Null(fixture.File.Confirmation);
        Assert.Empty(fixture.File.Groups);
        Assert.Equal(2, fixture.File.LooseResources.Count);
        Assert.True(RowIsRendered(fixture.Window, "Vocab"));
        Assert.True(RowIsRendered(fixture.Window, "Syllabus"));
        Assert.False(ShowsText(fixture.Window, "loose in this File"));
        Assert.DoesNotContain(
            fixture.Window.GetVisualDescendants().OfType<Expander>(),
            expander => expander.IsEffectivelyVisible);
        fixture.Window.Close();
    }

    /// <summary>
    /// Delete group destroys the documents in it, so it goes through the File surface's own
    /// two-step prompt. Keep leaves everything standing; the confirm takes the group and its
    /// members and nothing else.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeletingAGroup_PromptsFirst_AndTakesOnlyItsMembers(bool confirm)
    {
        var fixture = OpenSpanishFile();
        AddNote(fixture.File, "Vocab");
        AddNote(fixture.File, "Syllabus");
        AddGroup(fixture.File, "Unit 3");
        MoveByViewModel(fixture.File, "Vocab", "Unit 3");
        fixture.Window.CaptureRenderedFrame();

        ClickByName(fixture.Window, "Delete group Unit 3");

        Assert.NotNull(fixture.File.Confirmation);
        Assert.True(ShowsText(
            fixture.Window,
            "Delete 'Unit 3'? Its 1 resource and any stored files are deleted too."));

        if (confirm)
        {
            ClickByName(fixture.Window, "Confirm: Delete group");
            Assert.Empty(fixture.File.Groups);
            Assert.Equal("Syllabus", Assert.Single(fixture.File.Resources).Title);
            Assert.False(RowIsRendered(fixture.Window, "Vocab"));
            Assert.True(RowIsRendered(fixture.Window, "Syllabus"));
        }
        else
        {
            Click(
                fixture.Window,
                fixture.Window.GetVisualDescendants()
                    .OfType<Button>()
                    .Single(button => button.Name == "FilePromptKeepButton" && button.IsEffectivelyVisible));
            Assert.Equal("Unit 3", Assert.Single(fixture.File.Groups).Title);
            Assert.Equal(2, fixture.File.Resources.Count);
            Assert.True(RowIsRendered(fixture.Window, "Vocab"));
            Assert.True(RowIsRendered(fixture.Window, "Syllabus"));
        }

        Assert.Null(fixture.File.Confirmation);
        fixture.Window.Close();
    }

    /// <summary>
    /// At the narrowest width the app supports, a header's actions still sit inside the
    /// group they belong to rather than being pushed out of the pane and clipped away.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 800)]
    [InlineData(1440, 960)]
    public void AtTheNarrowWidth_TheHeaderControlsStayInsideThePane(double width, double height)
    {
        var fixture = OpenSpanishFile(width, height);
        AddGroup(fixture.File, "Unit 3 — Federalism");
        fixture.Window.CaptureRenderedFrame();

        var expander = GroupHeader(fixture.Window, "Unit 3 — Federalism");
        foreach (var name in new[]
        {
            "Rename group Unit 3 — Federalism",
            "Ungroup Unit 3 — Federalism",
            "Delete group Unit 3 — Federalism",
        })
        {
            var button = ButtonNamed(fixture.Window, name);
            Assert.True(button.Bounds.Width > 0, $"{name} rendered with no width");
            var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), expander)!.Value.X;
            Assert.True(
                right <= expander.Bounds.Width + 1,
                $"{name} ends at {right} inside a {expander.Bounds.Width}-wide group header");
        }

        Assert.True(ShowsText(fixture.Window, "Unit 3 — Federalism"));
        fixture.Window.Close();
    }

    // ---- the header's chrome -------------------------------------------------------

    /// <summary>
    /// Fluent paints an expander's header and its body from two unrelated resource sets:
    /// the header from ExpanderHeader*, the body from the control's own Background and
    /// BorderBrush. A repaint applied only to the Expander therefore lands on half the
    /// card — a stock grey header with a #33000000 edge above a body with the workbench's
    /// own, two borders meeting mid-card. Every group header in the app wears this, so it
    /// is asserted rather than eyeballed.
    ///
    /// Property assertions, not pixels: headless renders, but reading back what the parts
    /// were told to paint is what catches a repaint that never reached them.
    /// </summary>
    [AvaloniaFact]
    public void TheGroupHeader_WearsTheWorkbenchPalette_InEveryState()
    {
        var fixture = OpenSpanishFile();
        AddGroup(fixture.File, "Unit 3");
        fixture.Window.CaptureRenderedFrame();

        var (headerFill, body, chevron, header) = HeaderParts(GroupHeader(fixture.Window, "Unit 3"));

        Assert.Equal(Token("BrushPaperWhite"), Painted(headerFill.Background));
        Assert.Equal(Token("BrushRuleLight"), Painted(headerFill.BorderBrush));
        // One card, so one edge colour top to bottom.
        Assert.Equal(Painted(body.BorderBrush), Painted(headerFill.BorderBrush));
        Assert.Equal(Token("BrushPencilGray"), Painted(headerFill.GetVisualDescendants()
            .OfType<Avalonia.Controls.Shapes.Path>()
            .Single(path => path.Name == "ExpandCollapseChevron").Stroke
            ?? headerFill.GetVisualDescendants()
                .OfType<Avalonia.Controls.Shapes.Path>()
                .Single(path => path.Name == "ExpandCollapseChevron").Fill));

        // Hover. Fluent drops a solid black square behind the chevron and leaves the
        // header edge unchanged; this app darkens the edge and never fills the chevron.
        Hover(fixture.Window, header);
        Assert.Contains(":pointerover", header.Classes);
        Assert.NotEqual(Colors.Black, Painted(chevron.Background));
        Assert.Equal(Token("BrushGraphite"), Painted(headerFill.BorderBrush));

        fixture.Window.Close();
    }

    /// <summary>
    /// Fluent's expander header shows nothing at all on keyboard focus — not a different
    /// ring from the app's, none. A header that can be tabbed to and collapsed with Space
    /// has to say so, so the focused header takes the lime wash the app uses for exactly
    /// this everywhere else.
    /// </summary>
    [AvaloniaFact]
    public void AFocusedGroupHeader_ShowsThatItHasFocus()
    {
        var fixture = OpenSpanishFile();
        AddGroup(fixture.File, "Unit 3");
        fixture.Window.CaptureRenderedFrame();

        var (headerFill, _, _, header) = HeaderParts(GroupHeader(fixture.Window, "Unit 3"));
        var resting = Painted(headerFill.Background);

        header.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        fixture.Window.CaptureRenderedFrame();

        Assert.Contains(":focus-visible", header.Classes);
        Assert.NotEqual(resting, Painted(headerFill.Background));
        Assert.Equal(Token("BrushLimeWash"), Painted(headerFill.Background));
        Assert.Equal(Token("BrushGraphite"), Painted(headerFill.BorderBrush));
        fixture.Window.Close();
    }

    // ---- focus survives the rebuild --------------------------------------------------

    /// <summary>
    /// The refresh behind a move destroys the row and the Move button that had focus with
    /// it, and Avalonia focuses nothing in its place — so before this, filing a resource
    /// from the keyboard dropped the user out of the tab order entirely and a File of
    /// twelve loose resources cost twelve traversals from the top of the window. The
    /// spec calls this flyout the keyboard-accessible path, so focus follows the resource
    /// to wherever it landed.
    /// </summary>
    [AvaloniaFact]
    public void AfterAMove_FocusFollowsTheRowToItsNewHome()
    {
        var fixture = OpenSpanishFile();
        AddNote(fixture.File, "Vocab");
        AddNote(fixture.File, "Syllabus");
        AddGroup(fixture.File, "Unit 3");
        fixture.Window.CaptureRenderedFrame();

        var (_, content) = OpenFlyout(fixture.Window, "Move Vocab");
        ClickInPopup(content.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "Move to Unit 3"));
        Dispatcher.UIThread.RunJobs();
        fixture.Window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();

        var moved = Assert.Single(fixture.File.Groups.Single().Resources);
        var focused = Focused(fixture.Window);
        Assert.True(focused is not null, "nothing holds focus after a move");
        Assert.Same(moved, Assert.IsType<ListBoxItem>(focused).DataContext);
        fixture.Window.Close();
    }

    /// <summary>
    /// Delete group raises its prompt at the top of the pane while the button that raised
    /// it sits deep in the list, so without this a keyboard user has to Shift+Tab back
    /// past every group and every row to answer it. The same card serves the inherited
    /// File and resource deletions, which gain the same behaviour.
    /// </summary>
    [AvaloniaFact]
    public void RaisingTheDeleteGroupPrompt_FocusesItsConfirmButton()
    {
        var fixture = OpenSpanishFile();
        AddNote(fixture.File, "Vocab");
        AddGroup(fixture.File, "Unit 3");
        MoveByViewModel(fixture.File, "Vocab", "Unit 3");
        fixture.Window.CaptureRenderedFrame();

        ClickByName(fixture.Window, "Delete group Unit 3");
        Dispatcher.UIThread.RunJobs();
        fixture.Window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();

        var focused = Focused(fixture.Window);
        Assert.True(focused is not null, "the prompt was raised with nothing focused");
        Assert.Equal("FilePromptConfirmButton", focused!.Name);
        Assert.True(focused.IsEffectivelyVisible);
        fixture.Window.Close();
    }

    // ---- keyboard ------------------------------------------------------------------

    /// <summary>
    /// The flyout is the keyboard-accessible path to filing a resource, so it has to work
    /// with no pointer at all: focus the row's Move button, open it with Enter, Tab from
    /// the first target to the second, and commit with Enter. Landing in Unit 4 rather
    /// than Unit 3 is what proves the Tab moved the thing Enter then pressed.
    ///
    /// One limitation, deliberately recorded rather than papered over: the headless
    /// platform renders popups in the window's own overlay layer, so the "popup" here is
    /// the MainWindow and this exercises the flyout's focus handling inside one focus
    /// scope. A real platform popup window is its own top level; Task 8's running-app
    /// check covers that half.
    /// </summary>
    [AvaloniaFact]
    public void TheMoveFlyout_OpensAndCommitsFromTheKeyboard()
    {
        var fixture = OpenSpanishFile();
        AddNote(fixture.File, "Vocab");
        AddNote(fixture.File, "Syllabus");
        AddGroup(fixture.File, "Unit 3");
        AddGroup(fixture.File, "Unit 4");
        fixture.Window.CaptureRenderedFrame();

        var move = ButtonNamed(fixture.Window, "Move Vocab");
        move.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(move, fixture.Window.FocusManager?.GetFocusedElement());

        fixture.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, keySymbol: null);
        Dispatcher.UIThread.RunJobs();
        fixture.Window.CaptureRenderedFrame();
        var flyout = Assert.IsType<Flyout>(move.Flyout);
        Assert.True(flyout.IsOpen, "Enter on the Move button did not open its flyout");

        var content = Assert.IsAssignableFrom<Control>(flyout.Content);
        var popup = TopLevel.GetTopLevel(content)!;
        popup.CaptureRenderedFrame();
        var first = content.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "Move to Unit 3");
        var second = content.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "Move to Unit 4");

        // Opening hands focus straight to the first target — no pointer, no hunting.
        Assert.Same(first, popup.FocusManager?.GetFocusedElement());
        popup.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, keySymbol: null);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(second, popup.FocusManager?.GetFocusedElement());

        popup.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, keySymbol: null);
        Dispatcher.UIThread.RunJobs();
        fixture.Window.CaptureRenderedFrame();

        Assert.Empty(fixture.File.Groups.Single(group => group.Title == "Unit 3").Resources);
        Assert.Equal(
            "Vocab",
            Assert.Single(fixture.File.Groups.Single(group => group.Title == "Unit 4").Resources).Title);
        Assert.Equal("Syllabus", Assert.Single(fixture.File.LooseResources).Title);
        Assert.False(flyout.IsOpen);
        Assert.Equal("Vocab", ReadingPaneTitle(fixture.Window));
        fixture.Window.Close();
    }
}
