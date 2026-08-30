using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BeBoosted.Application.Settings;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// Rendered proof of the scope-led editors: the approved widths and fixed
/// header/footer, every scope label, prompt focus depth, the editor-local
/// focus ring, hit areas, and the 1100×720 minimum window — frames 3a/3b/4a–4p.
/// </summary>
public sealed class TaskEditorScopeUiTests
{
    private static readonly DateOnly Date = TestShell.DesignDate;

    private sealed record Scene(
        MainWindow Window,
        ShellViewModel Shell,
        InMemoryTaskRepository Tasks,
        InMemoryCalendarBlockRepository Blocks,
        FakeClock Clock);

    private static Scene Show(double width = 1280, double height = 800)
    {
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        var window = new MainWindow { DataContext = shell, Width = width, Height = height };
        window.Show();
        window.CaptureRenderedFrame();
        return new Scene(window, shell, tasks, blocks, new FakeClock(Date));
    }

    private static TaskItem AddTask(Scene scene, string title)
    {
        var task = TaskItem.Create(title, scene.Clock.Now);
        scene.Tasks.Add(task);
        return task;
    }

    private static CalendarBlock AddSession(
        Scene scene, TaskItem task, DateOnly date, TimeOnly start, TimeOnly end,
        RecurrenceRule? recurrence = null)
    {
        var session = CalendarBlock.CreateTaskSession(
            task.Id, date, start, end, scene.Clock.Now, recurrence);
        scene.Blocks.Add(session);
        return session;
    }

    private static TaskItem AddEightSessionTask(Scene scene)
    {
        var task = AddTask(scene, "Prepare regional qualifier written event binder and role-play scenario cards");
        for (var i = 0; i < 8; i++)
        {
            AddSession(scene, task, new DateOnly(2026, 9, 30).AddDays(i), new TimeOnly(23, 30), new TimeOnly(23, 59));
        }

        return task;
    }

    private static T Find<T>(MainWindow window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static bool HasText(MainWindow window, string text)
        => window.GetVisualDescendants().OfType<TextBlock>()
            .Any(t => t.IsEffectivelyVisible && t.Text == text);

    private static void Render(Scene scene)
    {
        Dispatcher.UIThread.RunJobs();
        scene.Window.CaptureRenderedFrame();
    }

    // ---- Widths, fixed header/footer, minimum window ----

    [AvaloniaFact]
    public void WholeTaskCard_Is480Wide_WithFixedHeaderAndFooter()
    {
        var scene = Show();
        var task = AddEightSessionTask(scene);
        scene.Shell.Calendar.OpenWholeTaskEditor(task.Id);
        Render(scene);

        var card = Find<Border>(scene.Window, "WholeTaskEditorCard");
        Assert.Equal(480, card.Bounds.Width, 3);
        var body = Find<ScrollViewer>(scene.Window, "WholeTaskBody");
        Assert.True(body.Extent.Height > body.Viewport.Height, "the 8-session body must scroll");
        Assert.True(HasText(scene.Window, "WHOLE TASK"));
        Assert.True(Find<Button>(scene.Window, "WholeTaskSaveButton").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void SessionCards_Are408Wide()
    {
        var scene = Show();
        var task = AddTask(scene, "Practice DECA role-play");
        var oneOff = AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var series = AddSession(
            scene, task, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));

        scene.Shell.Calendar.OpenSessionEditorForBlock(oneOff.Id, Date);
        Render(scene);
        Assert.Equal(408, Find<Border>(scene.Window, "SessionEditorCard").Bounds.Width, 3);

        scene.Shell.Calendar.OpenSessionEditorForBlock(series.Id, Date);
        Render(scene);
        Assert.Equal(408, Find<Border>(scene.Window, "SessionEditorCard").Bounds.Width, 3);

        var parent = scene.Shell.Calendar.OpenWholeTaskEditor(task.Id)!;
        parent.AddSessionCommand.Execute(null);
        Render(scene);
        Assert.Equal(408, Find<Border>(scene.Window, "SessionEditorCard").Bounds.Width, 3);
    }

    [AvaloniaFact]
    public void MinimumWindow_1100x720_NoHorizontalExtent_HeaderFooterVisible()
    {
        var scene = Show(1100, 720);
        var task = AddEightSessionTask(scene);
        scene.Shell.Calendar.OpenWholeTaskEditor(task.Id);
        Render(scene);

        var card = Find<Border>(scene.Window, "WholeTaskEditorCard");
        // The editor's own scroller never scrolls horizontally (a TextBox's
        // internal caret scroller is not editor-level scrolling).
        var body = Find<ScrollViewer>(scene.Window, "WholeTaskBody");
        Assert.True(
            body.Extent.Width <= body.Viewport.Width + 0.5,
            "the editor body must not scroll horizontally");
        Assert.True(card.Bounds.Width <= 1100, "the card must fit the minimum window");

        Assert.True(HasText(scene.Window, "WHOLE TASK"));
        var save = Find<Button>(scene.Window, "WholeTaskSaveButton");
        Assert.True(save.IsEffectivelyVisible);
        var savePoint = save.TranslatePoint(new Point(0, save.Bounds.Height), scene.Window)!.Value;
        Assert.True(savePoint.Y <= 720, "the footer must stay inside the minimum window");
    }

    [AvaloniaFact]
    public void LongRows_Wrap_WithEditAndRemoveStillVisible()
    {
        var scene = Show(1100, 720);
        var task = AddEightSessionTask(scene);
        scene.Shell.Calendar.OpenWholeTaskEditor(task.Id);
        Render(scene);

        var card = Find<Border>(scene.Window, "WholeTaskEditorCard");
        var editButtons = card.GetVisualDescendants().OfType<Button>()
            .Where(b => AutomationProperties.GetName(b)?.StartsWith("Edit session") == true)
            .ToList();
        Assert.NotEmpty(editButtons);
        foreach (var button in editButtons.Take(2))
        {
            Assert.True(button.IsEffectivelyVisible);
            var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), card)!.Value.X;
            Assert.True(right <= card.Bounds.Width + 0.5, "row actions stay inside the card");
        }
    }

    // ---- Scope labels, sections, states ----

    [AvaloniaFact]
    public void ScopeLabels_Render_ForEveryMode()
    {
        var scene = Show();
        var task = AddTask(scene, "Practice DECA role-play");
        AddSession(scene, task, new DateOnly(2026, 8, 10), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var second = AddSession(scene, task, Date, new TimeOnly(15, 30), new TimeOnly(17, 0));
        AddSession(scene, task, new DateOnly(2026, 8, 15), new TimeOnly(16, 0), new TimeOnly(17, 0));
        var seriesTask = AddTask(scene, "Morning reading");
        var series = AddSession(
            scene, seriesTask, new DateOnly(2026, 8, 4), new TimeOnly(7, 0), new TimeOnly(7, 30),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));

        scene.Shell.Calendar.OpenWholeTaskEditor(task.Id);
        Render(scene);
        Assert.True(HasText(scene.Window, "WHOLE TASK"));

        scene.Shell.Calendar.OpenSessionEditorForBlock(second.Id, Date);
        Render(scene);
        Assert.True(HasText(scene.Window, "THIS SESSION · 2 OF 3"));

        scene.Shell.Calendar.OpenSessionEditorForBlock(series.Id, Date);
        Render(scene);
        Assert.True(HasText(scene.Window, "REPEATING SCHEDULE"));

        var parent = scene.Shell.Calendar.OpenWholeTaskEditor(task.Id)!;
        parent.AddSessionCommand.Execute(null);
        Render(scene);
        Assert.True(HasText(scene.Window, "NEW SESSION"));
    }

    [AvaloniaFact]
    public void RepeatingEditor_RendersBothSectionLabels_AndTheSeriesSentence()
    {
        var scene = Show();
        var task = AddTask(scene, "Stats HW");
        var series = AddSession(
            scene, task, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        scene.Shell.Calendar.OpenSessionEditorForBlock(series.Id, Date);
        Render(scene);

        Assert.True(HasText(scene.Window, "THIS OCCURRENCE · TUE, AUG 11"));
        Assert.True(HasText(scene.Window, "Only Tue, Aug 11. Other occurrences aren't affected."));
        Assert.True(HasText(
            scene.Window, "Time and weekday changes apply to every occurrence of this schedule."));
        var occurrenceBox = scene.Window.GetVisualDescendants().OfType<CheckBox>()
            .Single(c => AutomationProperties.GetName(c) == "Mark this occurrence complete");
        Assert.True(occurrenceBox.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void ScheduleRows_ShowPositions_Chips_AndRepeatingWash()
    {
        var scene = Show();
        var task = AddTask(scene, "SAT vocabulary drill");
        var done = AddSession(scene, task, new DateOnly(2026, 8, 10), new TimeOnly(16, 0), new TimeOnly(16, 45));
        done.RecordOutcome(BlockOutcome.Done, scene.Clock.Now);
        AddSession(scene, task, new DateOnly(2026, 8, 30), new TimeOnly(10, 0), new TimeOnly(11, 0));
        AddSession(
            scene, task, new DateOnly(2026, 8, 5), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Wednesday));
        scene.Shell.Calendar.OpenWholeTaskEditor(task.Id);
        Render(scene);

        Assert.True(HasText(scene.Window, "SESSION 1 OF 2"));
        Assert.True(HasText(scene.Window, "SESSION 2 OF 2"));
        Assert.True(HasText(scene.Window, "REPEATING SCHEDULE"));
        Assert.True(HasText(scene.Window, "DONE"));
        Assert.True(HasText(
            scene.Window,
            "Session numbers count one-off sessions only; the repeating schedule has no number."));
    }

    [AvaloniaFact]
    public void EmptyState_AndCreateModeInlineSession_Render()
    {
        var scene = Show();
        var task = AddTask(scene, "Never scheduled");
        scene.Shell.Calendar.OpenWholeTaskEditor(task.Id);
        Render(scene);
        Assert.True(HasText(
            scene.Window, "No sessions scheduled. The task stays in your Inbox until you add one."));

        scene.Shell.Calendar.OpenNewWholeTaskEditor(
            Date, new TimeOnly(13, 0), new TimeOnly(14, 0), scheduled: true);
        Render(scene);
        Assert.True(HasText(scene.Window, "FIRST SESSION"));
        Assert.True(HasText(scene.Window, "saved together with the task"));
    }

    [AvaloniaFact]
    public void CompletedTask_ShowsDoneChips_AndDisabledAddSession_WithNote()
    {
        var scene = Show();
        var task = AddTask(scene, "Finished");
        var session = AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        session.RecordOutcome(BlockOutcome.Done, scene.Clock.Now);
        task.Complete(scene.Clock.Now);
        scene.Shell.Calendar.OpenWholeTaskEditor(task.Id);
        Render(scene);

        Assert.True(HasText(scene.Window, "DONE"));
        Assert.False(Find<Button>(scene.Window, "AddSessionButton").IsEnabled);
        Assert.True(HasText(scene.Window, "Task complete — reopen it to schedule more sessions."));
    }

    [AvaloniaFact]
    public void OneOffEditor_HasNoCompletionControl_RepeatingHasExactlyOne()
    {
        var scene = Show();
        var task = AddTask(scene, "Mixed");
        var oneOff = AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var series = AddSession(
            scene, task, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));

        scene.Shell.Calendar.OpenSessionEditorForBlock(oneOff.Id, Date);
        Render(scene);
        Assert.DoesNotContain(
            scene.Window.GetVisualDescendants().OfType<CheckBox>(),
            c => AutomationProperties.GetName(c) == "Mark this occurrence complete"
                && c.IsEffectivelyVisible);

        scene.Shell.Calendar.OpenSessionEditorForBlock(series.Id, Date);
        Render(scene);
        Assert.Single(
            scene.Window.GetVisualDescendants().OfType<CheckBox>(),
            c => AutomationProperties.GetName(c) == "Mark this occurrence complete"
                && c.IsEffectivelyVisible);
    }

    // ---- Prompts: dimming, focus depth, Escape ----

    [AvaloniaFact]
    public void Confirmations_DimTheBody_AndUseScopeCopy()
    {
        var scene = Show();
        var task = AddTask(scene, "Split");
        AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(scene, task, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = scene.Shell.Calendar.OpenWholeTaskEditor(task.Id)!;
        editor.RequestDeleteCommand.Execute(null);
        Render(scene);

        Assert.True(Find<Border>(scene.Window, "WholeTaskConfirmationCard").IsEffectivelyVisible);
        Assert.True(HasText(scene.Window, "Delete this task? Its 2 sessions go with it."));
        var bodyHost = Find<Border>(scene.Window, "WholeTaskBodyHost");
        Assert.False(bodyHost.IsEnabled);
        Assert.Equal(0.45, bodyHost.Opacity, 2);
    }

    [AvaloniaFact]
    public void GateCard_RendersItsThreeActions()
    {
        var scene = Show();
        var task = AddTask(scene, "Split");
        AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = scene.Shell.Calendar.OpenWholeTaskEditor(task.Id)!;
        editor.Title = "Split v2";
        editor.Sessions.Single().EditCommand.Execute(null);
        Render(scene);

        Assert.True(Find<Border>(scene.Window, "WholeTaskGateCard").IsEffectivelyVisible);
        Assert.True(HasText(scene.Window, "You have unsaved task changes."));
        Assert.True(HasText(scene.Window, "Save or discard before continuing."));
        Assert.True(HasText(scene.Window, "Save task and continue"));
        Assert.True(HasText(scene.Window, "Discard changes and continue"));
        Assert.True(HasText(scene.Window, "Keep editing"));
    }

    [AvaloniaFact]
    public void StaleAndFailureStates_Render()
    {
        var scene = Show();
        var task = AddTask(scene, "Solo");
        var only = AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = scene.Shell.Calendar.OpenSessionEditorForBlock(only.Id, Date)!;
        scene.Blocks.Delete(only.Id);
        editor.SaveCommand.Execute(null);
        Render(scene);

        Assert.True(HasText(
            scene.Window, "This session no longer exists — it was removed elsewhere. Cancel to go back."));
        Assert.False(Find<Border>(scene.Window, "SessionBodyHost").IsEnabled);
    }

    [AvaloniaFact]
    public void PromptOpens_FocusMovesToItsFirstAction()
    {
        var scene = Show();
        var task = AddTask(scene, "Split");
        AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(scene, task, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = scene.Shell.Calendar.OpenWholeTaskEditor(task.Id)!;
        Render(scene);

        editor.RequestUnscheduleAllCommand.Execute(null);
        Render(scene);
        var focused = TopLevel.GetTopLevel(scene.Window)!.FocusManager!.GetFocusedElement();
        Assert.Equal("WholeTaskPromptKeepButton", (focused as Control)?.Name);

        editor.KeepPromptCommand.Execute(null);
        editor.Title = "Split v2";
        editor.Sessions.First().EditCommand.Execute(null);
        Render(scene);
        focused = TopLevel.GetTopLevel(scene.Window)!.FocusManager!.GetFocusedElement();
        Assert.Equal("WholeTaskGateSaveButton", (focused as Control)?.Name);
    }

    [AvaloniaFact]
    public void Tab_IsTrappedInsideTheActivePrompt()
    {
        var scene = Show();
        var task = AddTask(scene, "Split");
        AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(scene, task, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = scene.Shell.Calendar.OpenWholeTaskEditor(task.Id)!;
        Render(scene);   // the editor must be rendered before a user can open its prompt
        editor.RequestUnscheduleAllCommand.Execute(null);
        Render(scene);

        var prompt = Find<Border>(scene.Window, "WholeTaskConfirmationCard");
        for (var i = 0; i < 5; i++)
        {
            scene.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Dispatcher.UIThread.RunJobs();
            var focused = TopLevel.GetTopLevel(scene.Window)!.FocusManager!.GetFocusedElement() as Visual;
            Assert.True(
                focused is not null && prompt.GetVisualDescendants().Contains(focused),
                $"Tab must stay inside the active prompt (iteration {i}, focused: "
                + $"{(focused as Control)?.Name ?? focused?.GetType().Name ?? "null"})");
        }
    }

    [AvaloniaFact]
    public void DismissingThePrompt_RestoresFocus_ToTheTriggerControl()
    {
        var scene = Show();
        var task = AddTask(scene, "Split");
        AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(scene, task, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        scene.Shell.Calendar.OpenWholeTaskEditor(task.Id);
        Render(scene);
        var trigger = scene.Window.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b)?.StartsWith("Remove session") == true);
        trigger.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.True(trigger.IsFocused, "precondition: the trigger holds focus");
        trigger.Command!.Execute(null);   // opens the remove confirmation
        Render(scene);
        Assert.Equal(
            "WholeTaskPromptKeepButton",
            (TopLevel.GetTopLevel(scene.Window)!.FocusManager!.GetFocusedElement() as Control)?.Name);
        scene.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        scene.Window.CaptureRenderedFrame();

        var focused = TopLevel.GetTopLevel(scene.Window)!.FocusManager!.GetFocusedElement();
        Assert.True(
            ReferenceEquals(trigger, focused),
            "focus must return to the trigger, but was "
            + ((focused as Control)?.Name ?? focused?.GetType().Name ?? "null"));
    }

    [AvaloniaFact]
    public void Escape_DismissesConfirmationThenGateThenNavigates()
    {
        var scene = Show();
        var task = AddTask(scene, "Split");
        AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(scene, task, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = scene.Shell.Calendar.OpenWholeTaskEditor(task.Id)!;
        Render(scene);

        editor.RequestUnscheduleAllCommand.Execute(null);
        scene.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Null(editor.Confirmation);
        Assert.Same(editor, scene.Shell.Calendar.ActiveTaskEditor);

        editor.Sessions.First().EditCommand.Execute(null);
        Render(scene);
        scene.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Same(editor, scene.Shell.Calendar.ActiveTaskEditor);

        scene.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Null(scene.Shell.Calendar.ActiveTaskEditor);
    }

    // ---- Frame 4n: field-pinned end-before-start validation ----

    [AvaloniaFact]
    public void EndBeforeStart_PinsTheErrorToTheEndField_AndDisablesSave()
    {
        var scene = Show();
        var task = AddTask(scene, "Solo");
        var session = AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = scene.Shell.Calendar.OpenSessionEditorForBlock(session.Id, Date)!;
        Render(scene);

        editor.Schedule.End = new TimeSpan(8, 0, 0); // before the 9:00 start
        Render(scene);

        var endField = Find<TimePicker>(scene.Window, "SessionEndPicker");
        Assert.Contains("invalid", endField.Classes);
        var message = Find<TextBlock>(scene.Window, "SessionEndFieldError");
        Assert.True(message.IsEffectivelyVisible);
        Assert.Equal("A block must end after it starts.", message.Text);
        // Pinned beside/below the END field — inside the body, never the footer.
        var fieldTop = endField.TranslatePoint(default, scene.Window)!.Value.Y;
        var messageTop = message.TranslatePoint(default, scene.Window)!.Value.Y;
        var saveButton = Find<Button>(scene.Window, "SessionSaveButton");
        var saveTop = saveButton.TranslatePoint(default, scene.Window)!.Value.Y;
        Assert.True(messageTop >= fieldTop, "the message must sit beside/below the END field");
        Assert.True(messageTop < saveTop, "the pinned message must not be the footer line");
        Assert.False(saveButton.IsEffectivelyEnabled);
        Assert.Null(editor.Error); // the generic persistence line is a separate slot

        // Correcting the end recovers the field, the message, and Save.
        editor.Schedule.End = new TimeSpan(10, 30, 0);
        Render(scene);
        Assert.DoesNotContain("invalid", endField.Classes);
        Assert.False(Find<TextBlock>(scene.Window, "SessionEndFieldError").IsEffectivelyVisible);
        Assert.True(Find<Button>(scene.Window, "SessionSaveButton").IsEffectivelyEnabled);

        // The create-mode inline first session pins the same way.
        scene.Shell.Calendar.OpenNewTaskEditorAt(Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var create = (WholeTaskEditorViewModel)scene.Shell.Calendar.ActiveTaskEditor!;
        create.Title = "New";
        create.InlineSchedule.End = new TimeSpan(8, 0, 0);
        Render(scene);
        var inlineEnd = Find<TimePicker>(scene.Window, "InlineEndPicker");
        Assert.Contains("invalid", inlineEnd.Classes);
        Assert.True(Find<TextBlock>(scene.Window, "InlineEndFieldError").IsEffectivelyVisible);
        Assert.False(Find<Button>(scene.Window, "WholeTaskSaveButton").IsEffectivelyEnabled);
        create.InlineSchedule.End = new TimeSpan(10, 0, 0);
        Render(scene);
        Assert.True(Find<Button>(scene.Window, "WholeTaskSaveButton").IsEffectivelyEnabled);
    }

    /// <summary>
    /// A disabled primary action is immediately distinguishable: the vivid lime
    /// gives way to a muted fill in every editor card, create mode included.
    /// </summary>
    [AvaloniaFact]
    public void DisabledPrimaryActions_AreVisiblyMuted()
    {
        var scene = Show();
        var task = AddTask(scene, "Solo");
        var session = AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = scene.Shell.Calendar.OpenSessionEditorForBlock(session.Id, Date)!;
        Render(scene);

        var save = Find<Button>(scene.Window, "SessionSaveButton");
        var enabledColor = ((ISolidColorBrush)save.Background!).Color;

        editor.Schedule.End = new TimeSpan(8, 0, 0);
        Render(scene);
        Assert.False(save.IsEffectivelyEnabled);
        var disabledColor = ((ISolidColorBrush)save.Background!).Color;
        Assert.NotEqual(enabledColor, disabledColor);

        // The gate's save-and-continue mutes the same way.
        editor.EditWholeTaskCommand.Execute(null);
        Render(scene);
        var gateSave = Find<Button>(scene.Window, "SessionGateSaveButton");
        Assert.False(gateSave.IsEffectivelyEnabled);
        Assert.Equal(disabledColor, ((ISolidColorBrush)gateSave.Background!).Color);
        editor.GateKeepEditingCommand.Execute(null);
        scene.Shell.Calendar.EscapeTaskEditor();

        // Create mode's Save task mutes as well.
        scene.Shell.Calendar.OpenNewTaskEditorAt(Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var create = (WholeTaskEditorViewModel)scene.Shell.Calendar.ActiveTaskEditor!;
        create.InlineSchedule.End = new TimeSpan(8, 0, 0);
        Render(scene);
        var taskSave = Find<Button>(scene.Window, "WholeTaskSaveButton");
        Assert.False(taskSave.IsEffectivelyEnabled);
        Assert.Equal(disabledColor, ((ISolidColorBrush)taskSave.Background!).Color);
    }

    /// <summary>An invalid END disables the gate's save-and-continue as well.</summary>
    [AvaloniaFact]
    public void InvalidEnd_DisablesTheGateSaveAndContinue()
    {
        var scene = Show();
        var task = AddTask(scene, "Solo");
        var session = AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = scene.Shell.Calendar.OpenSessionEditorForBlock(session.Id, Date)!;
        Render(scene);

        editor.Schedule.End = new TimeSpan(8, 0, 0); // dirty and invalid
        editor.EditWholeTaskCommand.Execute(null);   // dirty draft → the gate
        Render(scene);

        Assert.NotNull(editor.Gate);
        Assert.False(Find<Button>(scene.Window, "SessionGateSaveButton").IsEffectivelyEnabled);
        // The pinned field error stays with the END field under the dimmed body.
        Assert.True(Find<TextBlock>(scene.Window, "SessionEndFieldError").IsEffectivelyVisible);
        Assert.Null(editor.Error);
    }

    // ---- Save-success and pre-armed-prompt focus behavior ----

    /// <summary>
    /// A successful save rebuilds the invoking surface, so the captured Control
    /// instance is unloaded — focus must land on the replacement semantic
    /// invoker, not vanish with the old visual.
    /// </summary>
    [AvaloniaFact]
    public void SaveSuccess_RestoresFocus_ToTheReplacementInvokers()
    {
        var scene = Show();
        var task = AddTask(scene, "Essay");
        var session = AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        scene.Shell.Calendar.Reload();
        Render(scene);
        var focusManager = TopLevel.GetTopLevel(scene.Window)!.FocusManager!;

        // Daily pencil → whole-task editor → Save: the Daily rows rebuild.
        var pencil = scene.Window.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible
                && AutomationProperties.GetName(b) == "Edit task Essay");
        pencil.Focus();
        Dispatcher.UIThread.RunJobs();
        pencil.Command!.Execute(pencil.CommandParameter);
        Render(scene);
        ((WholeTaskEditorViewModel)scene.Shell.Calendar.ActiveTaskEditor!)
            .SaveCommand.Execute(null);
        Render(scene);
        Render(scene);
        var focused = focusManager.GetFocusedElement() as Control;
        Assert.True(focused is not null, "focus vanished after the Daily-row save");
        Assert.Equal("Edit task Essay", AutomationProperties.GetName(focused));
        Assert.True(focused.IsEffectivelyVisible);

        // Week block → session editor → Save: the calendar blocks rebuild.
        scene.Shell.Calendar.ViewKind = CalendarViewKind.Week;
        Render(scene);
        var blockView = scene.Window.GetVisualDescendants().OfType<CalendarBlockView>()
            .First(v => (v.DataContext as CalendarBlockViewModel)?.Id == session.Id);
        blockView.Focus();
        Dispatcher.UIThread.RunJobs();
        ((CalendarBlockViewModel)blockView.DataContext!).Edit();
        Render(scene);
        ((SessionEditorViewModel)scene.Shell.Calendar.ActiveTaskEditor!)
            .SaveCommand.Execute(null);
        Render(scene);
        Render(scene);
        var focusedBlock = focusManager.GetFocusedElement() as CalendarBlockView;
        Assert.True(focusedBlock is not null, "focus vanished after the block save");
        Assert.Equal(session.Id, ((CalendarBlockViewModel)focusedBlock.DataContext!).Id);
    }

    /// <summary>
    /// Focus restore is keyed on domain identity, not accessible names: with two
    /// tasks sharing a title, the exact originating row's pencil regains focus.
    /// </summary>
    [AvaloniaFact]
    public void SaveRestore_WithDuplicateTitles_FocusesTheExactOriginatingRow()
    {
        var scene = Show();
        var first = AddTask(scene, "Essay");
        var second = AddTask(scene, "Essay"); // duplicate title, different task
        AddSession(scene, first, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(scene, second, Date, new TimeOnly(11, 0), new TimeOnly(12, 0));
        scene.Shell.Calendar.Reload();
        Render(scene);

        var pencil = scene.Window.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible
                && AutomationProperties.GetName(b) == "Edit task Essay"
                && (b.DataContext as DailyRowViewModel)?.TaskId == second.Id);
        pencil.Focus();
        Dispatcher.UIThread.RunJobs();
        pencil.Command!.Execute(pencil.CommandParameter);
        Render(scene);
        ((WholeTaskEditorViewModel)scene.Shell.Calendar.ActiveTaskEditor!)
            .SaveCommand.Execute(null);
        Render(scene);
        Render(scene);

        var focused = TopLevel.GetTopLevel(scene.Window)!.FocusManager!
            .GetFocusedElement() as Control;
        Assert.True(focused is not null, "focus vanished after the save");
        var row = Assert.IsType<DailyRowViewModel>(focused.DataContext);
        Assert.Equal(second.Id, row.TaskId); // the ORIGINATING task, not a name twin
    }

    /// <summary>
    /// The same title on two surfaces (Inbox drawer over the Daily list): the
    /// restore lands back on the Inbox pencil that opened the editor.
    /// </summary>
    [AvaloniaFact]
    public void SaveRestore_AcrossSurfaces_FocusesTheOriginatingSurfaceControl()
    {
        var scene = Show();
        var unscheduled = AddTask(scene, "Essay"); // in the Inbox drawer
        var scheduled = AddTask(scene, "Essay");   // in the Daily list
        AddSession(scene, scheduled, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        scene.Shell.Calendar.Reload();
        scene.Shell.Inbox.Reload();
        scene.Shell.IsInboxOpen = true;
        Render(scene);

        var inboxPencil = scene.Window.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible
                && AutomationProperties.GetName(b) == "Edit task Essay"
                && (b.DataContext as TaskRowViewModel)?.Task.Id == unscheduled.Id);
        inboxPencil.Focus();
        Dispatcher.UIThread.RunJobs();
        inboxPencil.Command!.Execute(inboxPencil.CommandParameter);
        Render(scene);
        ((WholeTaskEditorViewModel)scene.Shell.Calendar.ActiveTaskEditor!)
            .SaveCommand.Execute(null);
        Render(scene);
        Render(scene);

        var focused = TopLevel.GetTopLevel(scene.Window)!.FocusManager!
            .GetFocusedElement() as Control;
        Assert.True(focused is not null, "focus vanished after the save");
        var row = Assert.IsType<TaskRowViewModel>(focused.DataContext);
        Assert.Equal(unscheduled.Id, row.Task.Id); // the Inbox pencil, not the Daily twin
    }

    private sealed record ProjectScene(Scene Scene, Domain.Tasks.TaskItem First, Domain.Tasks.TaskItem Second);

    /// <summary>A "Schoolwork" project holding two tasks with the same title.</summary>
    private static ProjectScene ShowProjectWithDuplicateTitles(bool scheduled)
    {
        var scene = Show();
        scene.Shell.NavigateCommand.Execute(AppSection.Projects);
        scene.Shell.Projects.NewProjectName = "Schoolwork";
        Assert.True(scene.Shell.Projects.TryCreateProject());
        var projectId = scene.Shell.Projects.Detail!.Project.Id;
        var first = Domain.Tasks.TaskItem.Create("Essay", scene.Clock.Now, projectId: projectId);
        scene.Tasks.Add(first);
        var second = Domain.Tasks.TaskItem.Create("Essay", scene.Clock.Now, projectId: projectId);
        scene.Tasks.Add(second);
        if (scheduled)
        {
            AddSession(scene, first, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
            AddSession(scene, second, Date, new TimeOnly(11, 0), new TimeOnly(12, 0));
        }

        scene.Shell.Projects.Detail!.Refresh();
        Render(scene);
        return new ProjectScene(scene, first, second);
    }

    /// <summary>
    /// A Projects task row is a restorable invoker too: with duplicate titles,
    /// the save lands focus back on the exact originating row's pencil.
    /// </summary>
    [AvaloniaFact]
    public void SaveRestore_FromAProjectTaskRow_FocusesTheExactRow()
    {
        var fixture = ShowProjectWithDuplicateTitles(scheduled: false);
        var scene = fixture.Scene;
        var focusManager = TopLevel.GetTopLevel(scene.Window)!.FocusManager!;

        var secondRow = scene.Shell.Projects.Detail!.OpenTasks[1];
        var pencil = scene.Window.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible
                && ReferenceEquals(b.DataContext, secondRow)
                && AutomationProperties.GetName(b)?.StartsWith("Edit task") == true);
        pencil.Focus();
        Dispatcher.UIThread.RunJobs();
        pencil.Command!.Execute(pencil.CommandParameter);
        Render(scene);
        ((WholeTaskEditorViewModel)scene.Shell.Calendar.ActiveTaskEditor!)
            .SaveCommand.Execute(null);
        Render(scene);
        Render(scene);

        var focused = focusManager.GetFocusedElement() as Control;
        Assert.True(focused is not null, "focus vanished after the Projects task-row save");
        var replacement = Assert.IsType<ProjectTaskRowViewModel>(focused.DataContext);
        Assert.Same(scene.Shell.Projects.Detail!.OpenTasks[1], replacement);
        Assert.StartsWith("Edit task", AutomationProperties.GetName(focused)!);
    }

    /// <summary>Same guarantee for a Projects scheduled-session row.</summary>
    [AvaloniaFact]
    public void SaveRestore_FromAProjectScheduledRow_FocusesTheExactRow()
    {
        var fixture = ShowProjectWithDuplicateTitles(scheduled: true);
        var scene = fixture.Scene;
        var focusManager = TopLevel.GetTopLevel(scene.Window)!.FocusManager!;

        var secondRow = scene.Shell.Projects.Detail!.ScheduledBlocks
            .First(r => r.Start == new TimeOnly(11, 0));
        var pencil = scene.Window.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible
                && ReferenceEquals(b.DataContext, secondRow)
                && AutomationProperties.GetName(b)?.StartsWith("Edit session") == true);
        pencil.Focus();
        Dispatcher.UIThread.RunJobs();
        pencil.Command!.Execute(pencil.CommandParameter);
        Render(scene);
        ((WholeTaskEditorViewModel)scene.Shell.Calendar.ActiveTaskEditor!)
            .SaveCommand.Execute(null);
        Render(scene);
        Render(scene);

        var focused = focusManager.GetFocusedElement() as Control;
        Assert.True(focused is not null, "focus vanished after the Projects scheduled-row save");
        var replacement = Assert.IsType<ScheduledBlockRowViewModel>(focused.DataContext);
        Assert.Equal(new TimeOnly(11, 0), replacement.Start);
        Assert.Equal(Date, replacement.Date);
    }

    /// <summary>
    /// The Delete key on a repeating block opens the session editor with the
    /// Remove-schedule confirmation already active: the prompt takes focus on
    /// appearance, traps Tab, and Escape steps back out to the block.
    /// </summary>
    [AvaloniaFact]
    public void DeleteKeyOnARepeatingBlock_PreArmedPrompt_FocusesTrapsAndReturns()
    {
        var scene = Show();
        var task = AddTask(scene, "Stats HW");
        var session = AddSession(
            scene, task, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        scene.Shell.Calendar.ViewKind = CalendarViewKind.Week;
        scene.Shell.Calendar.Reload();
        Render(scene);
        var focusManager = TopLevel.GetTopLevel(scene.Window)!.FocusManager!;

        var blockView = scene.Window.GetVisualDescendants().OfType<CalendarBlockView>()
            .First(v => (v.DataContext as CalendarBlockViewModel)?.Id == session.Id);
        blockView.Focus();
        Dispatcher.UIThread.RunJobs();
        scene.Window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Render(scene);

        var editor = Assert.IsType<SessionEditorViewModel>(scene.Shell.Calendar.ActiveTaskEditor);
        Assert.NotNull(editor.Confirmation);
        Assert.Equal(
            "SessionPromptKeepButton",
            (focusManager.GetFocusedElement() as Control)?.Name);

        // Tab stays trapped inside the confirmation card.
        var card = Find<Border>(scene.Window, "SessionConfirmationCard");
        for (var i = 0; i < 3; i++)
        {
            scene.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            var inPrompt = (focusManager.GetFocusedElement() as Visual)?
                .GetVisualAncestors().Contains(card) == true;
            Assert.True(inPrompt, $"Tab {i} escaped the pre-armed prompt");
        }

        // Escape dismisses the prompt; focus lands on a real, enabled control.
        scene.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Render(scene);
        Assert.Null(editor.Confirmation);
        Assert.Same(editor, scene.Shell.Calendar.ActiveTaskEditor);
        var afterDismiss = focusManager.GetFocusedElement() as Control;
        Assert.True(afterDismiss is not null, "focus vanished after dismissing the prompt");
        Assert.True(afterDismiss.IsEffectivelyEnabled);

        // Escape again closes the editor and returns to the originating block.
        scene.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Render(scene);
        Render(scene);
        Assert.Null(scene.Shell.Calendar.ActiveTaskEditor);
        var back = Assert.IsType<CalendarBlockView>(focusManager.GetFocusedElement());
        Assert.Equal(session.Id, ((CalendarBlockViewModel)back.DataContext!).Id);
    }

    // ---- Icon treatment: stroked Path glyphs, never filled PathIcon ----

    private static Button VisibleButton(MainWindow window, Func<string?, bool> nameMatches)
        => window.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible && nameMatches(AutomationProperties.GetName(b)));

    /// <summary>The glyph itself must carry BeBoosted's stroked treatment.</summary>
    private static void AssertStrokedGlyph(Visual host)
    {
        Assert.Empty(host.GetVisualDescendants().OfType<PathIcon>()); // never a filled icon
        var glyph = host.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
            .FirstOrDefault(p => p.IsEffectivelyVisible && p.Stroke is not null);
        Assert.True(glyph is not null, "no stroked Path glyph rendered inside the control");
        AssertStrokedPath(glyph);
    }

    private static void AssertStrokedPath(Avalonia.Controls.Shapes.Path glyph)
    {
        var stroke = Assert.IsAssignableFrom<ISolidColorBrush>(glyph.Stroke);
        Assert.Equal(Color.Parse("#20231F"), stroke.Color); // BrushGraphite
        Assert.True(glyph.StrokeThickness > 0, "the glyph stroke must be visible");
        Assert.Null(glyph.Fill);
        Assert.True(glyph.Bounds.Width > 0 && glyph.Bounds.Height > 0, "the glyph must lay out");
    }

    [AvaloniaFact]
    public void EditorGlyphs_UseTheStrokedIconTreatment()
    {
        var scene = Show();
        var task = AddTask(scene, "Split");
        var oneOff = AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(
            scene, task, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        scene.Shell.Calendar.OpenWholeTaskEditor(task.Id);
        Render(scene);

        AssertStrokedGlyph(VisibleButton(scene.Window, n => n == "Close"));
        AssertStrokedGlyph(VisibleButton(scene.Window, n => n?.StartsWith("Edit session") == true));
        AssertStrokedGlyph(VisibleButton(scene.Window, n => n?.StartsWith("Remove") == true));
        AssertStrokedGlyph(VisibleButton(scene.Window, n => n == "Add session")); // list footer
        var repeatGlyph = scene.Window.GetVisualDescendants()
            .OfType<Avalonia.Controls.Shapes.Path>()
            .FirstOrDefault(p => p.Name == "RowRepeatGlyph" && p.IsEffectivelyVisible);
        Assert.True(repeatGlyph is not null, "the repeating row must show its stroked repeat glyph");
        AssertStrokedPath(repeatGlyph);

        // The empty state's Add-session variant carries the same treatment.
        var bare = AddTask(scene, "Bare");
        scene.Shell.Calendar.OpenWholeTaskEditor(bare.Id);
        Render(scene);
        AssertStrokedGlyph(VisibleButton(scene.Window, n => n == "Add session"));

        // And the session editor's Close.
        scene.Shell.Calendar.OpenSessionEditorForBlock(oneOff.Id, Date);
        Render(scene);
        AssertStrokedGlyph(VisibleButton(scene.Window, n => n == "Close"));
    }

    // ---- Focus visuals, hit areas, initial focus, automation names ----

    /// <summary>Focuses the control and proves its ring shows graphite + halo.</summary>
    private static void AssertFocusRing(Scene scene, Control control)
    {
        var label = control.Name
            ?? AutomationProperties.GetName(control)
            ?? control.GetType().Name;
        var ring = control.GetVisualAncestors().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("focusRing"));
        Assert.True(ring is not null, $"no focusRing wraps {label}");
        Assert.Equal(new Thickness(2), ring.BorderThickness);

        control.Focus();
        Dispatcher.UIThread.RunJobs();
        var focusManager = TopLevel.GetTopLevel(scene.Window)!.FocusManager!;
        if ((focusManager.GetFocusedElement() as Visual)?.GetVisualAncestors()
                .Contains(ring) != true
            && focusManager.GetFocusedElement() != ring.Child)
        {
            // A composite control (picker, numeric field) takes focus on an
            // inner part — exactly what keyboard navigation does.
            control.GetVisualDescendants().OfType<InputElement>()
                .FirstOrDefault(i => i.Focusable && i.IsEffectivelyVisible && i.IsEffectivelyEnabled)
                ?.Focus();
            Dispatcher.UIThread.RunJobs();
        }

        scene.Window.CaptureRenderedFrame();
        var graphite = ring.BorderBrush as ISolidColorBrush;
        Assert.True(
            graphite is not null && graphite.Color == Color.Parse("#20231F"),
            $"{label}: ring border is not graphite when focused");
        Assert.True(
            ring.BoxShadow.Count > 0 && ring.BoxShadow[0].Color == Color.Parse("#8CC8F24A"),
            $"{label}: ring halo missing when focused");
    }

    // ---- The specification's exact Tab order (maintained semantic lists) ----

    /// <summary>
    /// The authored control owning the focus (never a template part), reported
    /// by its accessible name so per-row identity is verified exactly.
    /// </summary>
    private static string FocusStop(MainWindow window)
    {
        var focused = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() as Visual;
        for (var visual = focused; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Control { TemplatedParent: null } control
                && (AutomationProperties.GetName(control) is not null || control.Name is not null))
            {
                return AutomationProperties.GetName(control) ?? control.Name!;
            }
        }

        return "null";
    }

    private static List<string> TabStops(Scene scene, int count, bool backwards = false)
    {
        var stops = new List<string>();
        for (var i = 0; i < count; i++)
        {
            scene.Window.KeyPress(
                Key.Tab,
                backwards ? RawInputModifiers.Shift : RawInputModifiers.None,
                PhysicalKey.Tab, null);
            Dispatcher.UIThread.RunJobs();
            stops.Add(FocusStop(scene.Window));
        }

        return stops;
    }

    [AvaloniaFact]
    public void TabOrder_WholeTaskEditor_MatchesTheSemanticOrder_BothDirections()
    {
        var scene = Show();
        var task = AddTask(scene, "Split");
        AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(scene, task, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = scene.Shell.Calendar.OpenWholeTaskEditor(task.Id)!;
        Render(scene);

        // The specification's explicit order (spec lines 377-379): Title →
        // Project → Deadline → Estimate → completion checkbox → each row's
        // Edit then Remove → Add session → Unschedule all → Delete task →
        // Cancel → Save task; Close stays reachable as the final extra stop.
        var rows = editor.Sessions.Select(r =>
            (Edit: r.Data.EditControlName, Remove: r.Data.RemoveControlName)).ToList();
        var completion = editor.CompletionCheckboxText;
        var expected = new List<string>
        {
            "Project", "Deadline", "Estimated minutes", completion,
            rows[0].Edit, rows[0].Remove, rows[1].Edit, rows[1].Remove,
            "Add session", "Unschedule all",
            "Delete task", "WholeTaskCancelButton", "WholeTaskSaveButton",
            "Close", "Task title", // wraps
        };

        Find<TextBox>(scene.Window, "TaskTitleBox").Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(expected, TabStops(scene, expected.Count));

        // Reverse retraces the same order exactly.
        var reversed = new List<string>
        {
            "Close", "WholeTaskSaveButton", "WholeTaskCancelButton",
            "Delete task", "Unschedule all", "Add session",
            rows[1].Remove, rows[1].Edit, rows[0].Remove, rows[0].Edit,
            completion, "Estimated minutes", "Deadline",
            "Project", "Task title",
        };
        Assert.Equal(reversed, TabStops(scene, reversed.Count, backwards: true));
    }

    [AvaloniaFact]
    public void TabOrder_RepeatingSessionEditor_MatchesTheSemanticOrder_BothDirections()
    {
        var scene = Show();
        var task = AddTask(scene, "Stats HW");
        var series = AddSession(
            scene, task, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        scene.Shell.Calendar.OpenSessionEditorForBlock(series.Id, Date);
        Render(scene);

        // The specification's explicit order (spec lines 380-382): schedule
        // fields in visual order → occurrence checkbox → Repeats → weekday
        // chips → Remove schedule → Edit whole task → Cancel → Save; Close
        // stays reachable as the final extra stop.
        var editor = (SessionEditorViewModel)scene.Shell.Calendar.ActiveTaskEditor!;
        var occurrence = editor.OccurrenceCheckboxText;
        var expected = new List<string>
        {
            "End time", occurrence, "Repeats",
            "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday",
            "Remove schedule", "Edit whole task",
            "SessionCancelButton", "SessionSaveButton",
            "Close", "Start time", // wraps to the repeating initial focus
        };

        Find<TimePicker>(scene.Window, "SessionStartPicker").Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(expected, TabStops(scene, expected.Count));

        var reversed = new List<string>
        {
            "Close", "SessionSaveButton", "SessionCancelButton",
            "Edit whole task", "Remove schedule",
            "Saturday", "Friday", "Thursday", "Wednesday", "Tuesday", "Monday", "Sunday",
            "Repeats", occurrence, "End time", "Start time",
        };
        Assert.Equal(reversed, TabStops(scene, reversed.Count, backwards: true));
    }

    /// <summary>
    /// Create mode holds at most ONE prospective first session: once the inline
    /// form is visible (prefilled slot or Add session), no schedule-list or
    /// empty-state Add session action may render anywhere.
    /// </summary>
    [AvaloniaFact]
    public void PrefilledCreateMode_ShowsOnlyTheInlineFirstSession()
    {
        var scene = Show();
        scene.Shell.Calendar.OpenNewTaskEditorAt(Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        Render(scene);

        Assert.True(Find<TimePicker>(scene.Window, "InlineStartPicker").IsEffectivelyVisible);
        Assert.Contains(
            scene.Window.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible
                && AutomationProperties.GetName(b) == "Remove first session");
        Assert.DoesNotContain(
            scene.Window.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Add session");

        // The unscheduled path first shows the empty state with exactly one
        // Add session action and no inline fields…
        scene.Shell.Calendar.EscapeTaskEditor();
        scene.Shell.Calendar.OpenNewUnscheduledTaskEditor(Date);
        Render(scene);
        var editor = (WholeTaskEditorViewModel)scene.Shell.Calendar.ActiveTaskEditor!;
        Assert.True(HasText(scene.Window, editor.EmptyStateText));
        Assert.Single(
            scene.Window.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible
                && AutomationProperties.GetName(b) == "Add session");
        Assert.False(Find<TimePicker>(scene.Window, "InlineStartPicker").IsEffectivelyVisible);

        // …and Add session swaps to the inline-only presentation.
        editor.AddSessionCommand.Execute(null);
        Render(scene);
        Assert.True(Find<TimePicker>(scene.Window, "InlineStartPicker").IsEffectivelyVisible);
        Assert.DoesNotContain(
            scene.Window.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Add session");
    }

    [AvaloniaFact]
    public void TabOrder_EmptyCreateMode_HasExactlyOneAddSessionStop()
    {
        var scene = Show();
        scene.Shell.Calendar.OpenNewUnscheduledTaskEditor(Date);
        Render(scene);

        var expected = new List<string>
        {
            "Project", "Deadline", "Estimated minutes",
            "Add session",
            "WholeTaskCancelButton", "WholeTaskSaveButton",
            "Close", "Task title", // wraps
        };
        Find<TextBox>(scene.Window, "TaskTitleBox").Focus();
        Dispatcher.UIThread.RunJobs();
        var stops = TabStops(scene, expected.Count);
        Assert.Equal(expected, stops);
        Assert.Equal(1, stops.Count(s => s == "Add session"));

        var reversed = new List<string>
        {
            "Close", "WholeTaskSaveButton", "WholeTaskCancelButton",
            "Add session", "Estimated minutes", "Deadline", "Project", "Task title",
        };
        Assert.Equal(reversed, TabStops(scene, reversed.Count, backwards: true));
    }

    [AvaloniaFact]
    public void TabOrder_InlineCreateMode_HasNoAddSessionStop()
    {
        var scene = Show();
        scene.Shell.Calendar.OpenNewTaskEditorAt(Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        Render(scene);

        var expected = new List<string>
        {
            "Project", "Deadline", "Estimated minutes",
            "First session date", "First session start", "First session end",
            "Repeats", "Remove first session",
            "WholeTaskCancelButton", "WholeTaskSaveButton",
            "Close", "Task title", // wraps
        };
        Find<TextBox>(scene.Window, "TaskTitleBox").Focus();
        Dispatcher.UIThread.RunJobs();
        var stops = TabStops(scene, expected.Count);
        Assert.Equal(expected, stops);
        Assert.DoesNotContain("Add session", stops);

        var reversed = new List<string>
        {
            "Close", "WholeTaskSaveButton", "WholeTaskCancelButton",
            "Remove first session", "Repeats",
            "First session end", "First session start", "First session date",
            "Estimated minutes", "Deadline", "Project", "Task title",
        };
        Assert.Equal(reversed, TabStops(scene, reversed.Count, backwards: true));
    }

    /// <summary>Top-level authored semantic controls, never template internals.</summary>
    private static IEnumerable<Control> SemanticControls(Visual root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is Button or Avalonia.Controls.Primitives.ToggleButton or TextBox
                or ComboBox or DatePicker or TimePicker or NumericUpDown)
            {
                // Template parts (scrollbar buttons, picker internals) carry a
                // TemplatedParent; authored controls do not.
                if (((Control)child).TemplatedParent is null)
                {
                    yield return (Control)child;
                }

                continue; // never descend into a control's own subtree
            }

            foreach (var nested in SemanticControls(child))
            {
                yield return nested;
            }
        }
    }

    private static void AssertEveryControlRings(Scene scene, Visual root, int atLeast)
    {
        var controls = SemanticControls(root)
            .Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled)
            .ToList();
        Assert.True(
            controls.Count >= atLeast,
            $"expected at least {atLeast} enabled controls, found {controls.Count}");
        foreach (var control in controls)
        {
            AssertFocusRing(scene, control);
        }
    }

    /// <summary>
    /// The approved treatment holds on EVERY enabled semantic focus target in
    /// both editors — fields, links, checkboxes, pickers, footer, prompt, and
    /// gate actions alike — proven by enumeration, not representatives.
    /// </summary>
    [AvaloniaFact]
    public void EveryEnabledEditorControl_ShowsTheFocusTreatment()
    {
        var scene = Show();
        var task = AddTask(scene, "Split");
        AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(scene, task, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var seriesTask = AddTask(scene, "Series");
        var series = AddSession(
            scene, seriesTask, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));

        // Whole-task edit mode: title, project, deadline, estimate, completion,
        // Unschedule all, row icons, Add session, footer, Close.
        var editor = scene.Shell.Calendar.OpenWholeTaskEditor(task.Id)!;
        Render(scene);
        AssertEveryControlRings(scene, Find<Border>(scene.Window, "WholeTaskEditorCard"), 14);

        // Its confirmation prompt actions.
        editor.RequestUnscheduleAllCommand.Execute(null);
        Render(scene);
        AssertEveryControlRings(scene, Find<Border>(scene.Window, "WholeTaskConfirmationCard"), 2);
        editor.KeepPromptCommand.Execute(null);
        Render(scene);

        // Its gate actions.
        editor.Title = "Split renamed";
        editor.Sessions.First().EditCommand.Execute(null);
        Render(scene);
        AssertEveryControlRings(scene, Find<Border>(scene.Window, "WholeTaskGateCard"), 3);
        editor.GateKeepEditingCommand.Execute(null);

        // The repeating session editor: Edit whole task, occurrence checkbox,
        // START/END, Repeats, weekday chips, footer, Close.
        var sessionEditor = scene.Shell.Calendar.OpenSessionEditorForBlock(series.Id, Date)!;
        Render(scene);
        AssertEveryControlRings(scene, Find<Border>(scene.Window, "SessionEditorCard"), 14);

        // Its confirmation and gate actions.
        sessionEditor.RequestRemoveCommand.Execute(null);
        Render(scene);
        AssertEveryControlRings(scene, Find<Border>(scene.Window, "SessionConfirmationCard"), 2);
        sessionEditor.KeepPromptCommand.Execute(null);
        Render(scene);
        sessionEditor.Schedule.Start = new TimeSpan(15, 0, 0);
        sessionEditor.EditWholeTaskCommand.Execute(null);
        Render(scene);
        AssertEveryControlRings(scene, Find<Border>(scene.Window, "SessionGateCard"), 3);
        sessionEditor.GateKeepEditingCommand.Execute(null);
        Render(scene);

        // Create mode with the inline first session and its weekday chips.
        scene.Shell.Calendar.OpenNewTaskEditorAt(Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var createEditor = (WholeTaskEditorViewModel)scene.Shell.Calendar.ActiveTaskEditor!;
        createEditor.InlineSchedule.RepeatsWeekly = true;
        Render(scene);
        AssertEveryControlRings(scene, Find<Border>(scene.Window, "WholeTaskEditorCard"), 16);
    }

    [AvaloniaFact]
    public void EveryIconControl_HasAtLeast32x32HitArea()
    {
        var scene = Show();
        var task = AddTask(scene, "Split");
        AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(scene, task, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        scene.Shell.Calendar.OpenWholeTaskEditor(task.Id);
        Render(scene);

        var card = Find<Border>(scene.Window, "WholeTaskEditorCard");
        var iconButtons = card.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("icon")).ToList();
        Assert.NotEmpty(iconButtons);
        foreach (var button in iconButtons)
        {
            Assert.True(button.Bounds.Width >= 32 && button.Bounds.Height >= 32,
                $"icon control below 32×32: {AutomationProperties.GetName(button)}");
        }
    }

    [AvaloniaFact]
    public void InitialFocus_PerEditorType()
    {
        var scene = Show();
        var task = AddTask(scene, "Solo");
        var oneOff = AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var seriesTask = AddTask(scene, "Series");
        var series = AddSession(
            scene, seriesTask, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));

        scene.Shell.Calendar.OpenWholeTaskEditor(task.Id);
        Render(scene);
        Assert.Equal(
            "TaskTitleBox",
            (TopLevel.GetTopLevel(scene.Window)!.FocusManager!.GetFocusedElement() as Control)?.Name);

        scene.Shell.Calendar.CloseActiveEditor();
        scene.Shell.Calendar.OpenSessionEditorForBlock(oneOff.Id, Date);
        Render(scene);
        Assert.Equal(
            "SessionDatePicker",
            (TopLevel.GetTopLevel(scene.Window)!.FocusManager!.GetFocusedElement() as Control)?.Name);

        scene.Shell.Calendar.CloseActiveEditor();
        scene.Shell.Calendar.OpenSessionEditorForBlock(series.Id, Date);
        Render(scene);
        Assert.Equal(
            "SessionStartPicker",
            (TopLevel.GetTopLevel(scene.Window)!.FocusManager!.GetFocusedElement() as Control)?.Name);
    }

    [AvaloniaFact]
    public void AutomationNames_MatchTheSpec()
    {
        var scene = Show();
        var task = AddTask(scene, "Practice DECA role-play");
        AddSession(scene, task, new DateOnly(2026, 8, 10), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var second = AddSession(scene, task, Date, new TimeOnly(15, 30), new TimeOnly(17, 0));
        AddSession(scene, task, new DateOnly(2026, 8, 15), new TimeOnly(16, 0), new TimeOnly(17, 0));

        scene.Shell.Calendar.OpenWholeTaskEditor(task.Id);
        Render(scene);
        var card = Find<Border>(scene.Window, "WholeTaskEditorCard");
        Assert.Equal("Whole task — Practice DECA role-play", AutomationProperties.GetName(card));

        scene.Shell.Calendar.OpenSessionEditorForBlock(second.Id, Date);
        Render(scene);
        var sessionCard = Find<Border>(scene.Window, "SessionEditorCard");
        Assert.Equal(
            "Session 2 of 3 — Practice DECA role-play", AutomationProperties.GetName(sessionCard));
    }

    // ---- Source markers and the hosting bridge ----

    [AvaloniaFact]
    public void SourceRow_ShowsEditingChip_AndBlockShowsHalo_BehindTheScrim()
    {
        var scene = Show();
        var task = AddTask(scene, "Practice DECA role-play");
        var session = AddSession(scene, task, Date, new TimeOnly(15, 30), new TimeOnly(17, 0));
        scene.Shell.Calendar.Reload();
        Render(scene);

        scene.Shell.Calendar.OpenWholeTaskEditor(task.Id);
        Render(scene);
        Assert.True(HasText(scene.Window, "Editing"));

        scene.Shell.Calendar.ViewKind = CalendarViewKind.Week;
        Render(scene);
        scene.Shell.Calendar.OpenSessionEditorForBlock(session.Id, Date);
        Render(scene);
        var blockVm = scene.Shell.Calendar.Days
            .SelectMany(day => day.Blocks)
            .First(block => block.Id == session.Id);
        Assert.True(blockVm.IsBeingEdited);
    }

    // ---- Real entry points, rendered ----

    [AvaloniaFact]
    public void DailySessionRowEdit_Rendered_OpensTheWholeTaskEditor()
    {
        var scene = Show();
        var task = AddTask(scene, "Essay");
        AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        scene.Shell.Calendar.Reload();
        Render(scene);

        var edit = scene.Window.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible
                && AutomationProperties.GetName(b) == "Edit task Essay");
        var point = edit.TranslatePoint(
            new Point(edit.Bounds.Width / 2, edit.Bounds.Height / 2), scene.Window)!.Value;
        scene.Window.MouseDown(point, MouseButton.Left);
        scene.Window.MouseUp(point, MouseButton.Left);
        Render(scene);

        var editor = Assert.IsType<WholeTaskEditorViewModel>(scene.Shell.Calendar.ActiveTaskEditor);
        Assert.Equal(task.Id, editor.TaskId);
    }

    [AvaloniaFact]
    public void CloseRestoresFocus_ToTheInvokingRowOrBlock()
    {
        var scene = Show();
        var task = AddTask(scene, "Essay");
        var session = AddSession(scene, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        scene.Shell.Calendar.Reload();
        Render(scene);

        // Daily row pencil: focus it, open, Escape — the pencil regains focus.
        var edit = scene.Window.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible
                && AutomationProperties.GetName(b) == "Edit task Essay");
        edit.Focus();
        Dispatcher.UIThread.RunJobs();
        edit.Command!.Execute(edit.CommandParameter);
        Render(scene);
        Assert.NotNull(scene.Shell.Calendar.ActiveTaskEditor);
        scene.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Render(scene);
        Assert.Null(scene.Shell.Calendar.ActiveTaskEditor);
        Assert.Same(edit,
            TopLevel.GetTopLevel(scene.Window)!.FocusManager!.GetFocusedElement());

        // Week block: focus it, open, Escape — the block regains focus.
        scene.Shell.Calendar.ViewKind = CalendarViewKind.Week;
        Render(scene);
        var blockView = scene.Window.GetVisualDescendants().OfType<CalendarBlockView>()
            .First(v => (v.DataContext as CalendarBlockViewModel)?.Id == session.Id);
        blockView.Focus();
        Dispatcher.UIThread.RunJobs();
        ((CalendarBlockViewModel)blockView.DataContext!).Edit();
        Render(scene);
        Assert.NotNull(scene.Shell.Calendar.ActiveTaskEditor);
        scene.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Render(scene);
        Assert.Null(scene.Shell.Calendar.ActiveTaskEditor);
        Assert.Same(blockView,
            TopLevel.GetTopLevel(scene.Window)!.FocusManager!.GetFocusedElement());
    }
}
