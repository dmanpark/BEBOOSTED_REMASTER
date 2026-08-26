using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;

namespace BeBoosted.Desktop.Views;

/// <summary>
/// Focus behavior for the whole-task editor: Tab cycles inside the card (or the
/// active prompt), prompts take focus on open and return it to their trigger on
/// dismissal, and returning from a pushed session editor refocuses its row.
/// </summary>
public partial class WholeTaskEditorView : UserControl
{
    private IInputElement? _promptReturnFocus;
    private Control? _lastBodyFocus;
    private WholeTaskEditorViewModel? _viewModel;

    public WholeTaskEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = DataContext as WholeTaskEditorViewModel;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }
        };
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        // Disabling the body clears focus before the prompt notification arrives,
        // so the return target is tracked as focus moves, not read at open time.
        AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.Source is Control control && !IsInPromptLayer(control))
            {
                _lastBodyFocus = control;
            }
        });
        // A prompt armed before the view existed still takes focus on appearance.
        Loaded += (_, _) =>
        {
            if (_viewModel is { } vm && (vm.Confirmation is not null || vm.Gate is not null))
            {
                var firstAction = vm.Gate is not null
                    ? this.FindControl<Button>("WholeTaskGateSaveButton")
                    : this.FindControl<Button>("WholeTaskPromptKeepButton");
                Dispatcher.UIThread.Post(() => firstAction?.Focus());
            }
        };
    }

    private static bool IsInPromptLayer(Control control)
        => control.GetVisualAncestors().OfType<Border>()
            .Any(border => border.Name is "WholeTaskGateCard" or "WholeTaskConfirmationCard");

    /// <summary>Focuses the Edit button of one schedule row (the return-from-push target).</summary>
    internal void FocusRow(CalendarBlockId? rowId)
    {
        Control? target = null;
        if (rowId is { } id)
        {
            target = this.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                button.Name == "RowEditButton"
                && button.DataContext is SessionRowViewModel row
                && row.Data.Id == id);
        }

        target ??= this.FindControl<TextBox>("TaskTitleBox");
        Dispatcher.UIThread.Post(() => target?.Focus());
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(WholeTaskEditorViewModel.Confirmation)
            or nameof(WholeTaskEditorViewModel.Gate)))
        {
            return;
        }

        var viewModel = _viewModel!;
        if (viewModel.Confirmation is not null || viewModel.Gate is not null)
        {
            _promptReturnFocus = _lastBodyFocus;
            var firstAction = viewModel.Gate is not null
                ? this.FindControl<Button>("WholeTaskGateSaveButton")
                : this.FindControl<Button>("WholeTaskPromptKeepButton");
            Dispatcher.UIThread.Post(() => firstAction?.Focus());
        }
        else if (_promptReturnFocus is Control { IsLoaded: true } trigger)
        {
            _promptReturnFocus = null;
            // Synchronous first: the visibility bindings (and their focus clear)
            // ran before this handler, so an immediate Focus lands last and wins.
            if (!trigger.Focus())
            {
                Dispatcher.UIThread.Post(() => FocusWhenReady(trigger));
            }
        }
        else
        {
            // No body control ever held focus (a pre-armed prompt): fall back to
            // the title field so focus never vanishes.
            _promptReturnFocus = null;
            Dispatcher.UIThread.Post(() =>
            {
                if (IsLoaded && this.FindControl<Control>("TaskTitleBox") is { } target)
                {
                    FocusWhenReady(target);
                }
            });
        }
    }

    /// <summary>
    /// The body's IsEffectivelyEnabled propagates on the next layout pass, so a
    /// failed immediate focus retries once after layout.
    /// </summary>
    private static void FocusWhenReady(Control trigger)
    {
        if (trigger.Focus())
        {
            return;
        }

        EventHandler? onLayout = null;
        onLayout = (_, _) =>
        {
            trigger.LayoutUpdated -= onLayout;
            trigger.Focus();
        };
        trigger.LayoutUpdated += onLayout;
    }

    /// <summary>
    /// The specification's explicit Tab order (spec "Keyboard traversal
    /// order"): Title → Project → Deadline → Estimate → completion checkbox →
    /// each row's Edit then Remove → Add session (with create mode's inline
    /// first-session group in its place) → Unschedule all → Delete task →
    /// Cancel → Save task, with Close as the final extra stop. A prompt or
    /// gate cycles over exactly its own actions.
    /// </summary>
    private IReadOnlyList<InputElement> TabOrder()
    {
        var scope = ActiveFocusScope();
        if (ReferenceEquals(scope, this.FindControl<Border>("WholeTaskGateCard")))
        {
            return EditorTabCycle.Focusables(
                this.FindControl<Button>("WholeTaskGateSaveButton"),
                this.FindControl<Button>("WholeTaskGateDiscardButton"),
                this.FindControl<Button>("WholeTaskGateKeepButton"));
        }

        if (ReferenceEquals(scope, this.FindControl<Border>("WholeTaskConfirmationCard")))
        {
            return EditorTabCycle.Focusables(
                this.FindControl<Button>("WholeTaskPromptKeepButton"),
                this.FindControl<Button>("WholeTaskPromptConfirmButton"));
        }

        var order = new List<InputElement?>
        {
            this.FindControl<TextBox>("TaskTitleBox"),
            this.FindControl<ComboBox>("TaskProjectSelector"),
            this.FindControl<DatePicker>("TaskDeadlinePicker"),
            this.FindControl<NumericUpDown>("TaskEstimateBox"),
            this.FindControl<CheckBox>("TaskCompletedBox"),
        };
        EditorTabCycle.AddItemButtons(
            order, this.FindControl<ItemsControl>("ScheduleRows"),
            "RowEditButton", "RowRemoveButton");
        order.Add(this.FindControl<Button>("AddSessionButton"));
        order.Add(this.FindControl<Button>("AddSessionEmptyButton"));
        order.Add(this.FindControl<DatePicker>("InlineDatePicker"));
        order.Add(this.FindControl<TimePicker>("InlineStartPicker"));
        order.Add(this.FindControl<TimePicker>("InlineEndPicker"));
        order.Add(this.FindControl<CheckBox>("InlineRepeatsBox"));
        EditorTabCycle.AddItemToggles(order, this.FindControl<ItemsControl>("InlineDaysList"));
        order.Add(this.FindControl<Button>("InlineClearButton"));
        order.Add(this.FindControl<Button>("UnscheduleAllButton"));
        order.Add(this.FindControl<Button>("WholeTaskDeleteButton"));
        order.Add(this.FindControl<Button>("WholeTaskCancelButton"));
        order.Add(this.FindControl<Button>("WholeTaskSaveButton"));
        order.Add(this.FindControl<Button>("WholeTaskCloseButton"));
        return EditorTabCycle.Focusables([.. order]);
    }

    /// <summary>Tab is trapped: the maintained semantic cycle, prompts first.</summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            EditorTabCycle.Handle(this, TabOrder(), e);
        }
    }

    private Visual ActiveFocusScope()
    {
        if (this.FindControl<Border>("WholeTaskGateCard") is { IsVisible: true } gate)
        {
            return gate;
        }

        if (this.FindControl<Border>("WholeTaskConfirmationCard") is { IsVisible: true } confirmation)
        {
            return confirmation;
        }

        return this.FindControl<Border>("WholeTaskEditorCard")!;
    }
}

/// <summary>
/// Shared Tab-cycle mechanics for the editor cards: a maintained ordered list
/// of top-level semantic controls, never arbitrary visual descendants or
/// template internals. Composite controls focus their first inner part, and
/// focus anywhere inside an entry maps back to that entry for the next step.
/// </summary>
internal static class EditorTabCycle
{
    public static IReadOnlyList<InputElement> Focusables(params InputElement?[] entries)
        => [.. entries.Where(entry =>
            entry is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true })
            .Cast<InputElement>()];

    /// <summary>Named buttons of every realized row, in item order.</summary>
    public static void AddItemButtons(
        List<InputElement?> order, ItemsControl? list, params string[] buttonNames)
    {
        if (list is null)
        {
            return;
        }

        for (var i = 0; i < list.ItemCount; i++)
        {
            if (list.ContainerFromIndex(i) is not Visual container)
            {
                continue;
            }

            foreach (var name in buttonNames)
            {
                order.Add(container.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(b => b.Name == name));
            }
        }
    }

    /// <summary>The weekday chips, Sunday-first (item order).</summary>
    public static void AddItemToggles(List<InputElement?> order, ItemsControl? list)
    {
        if (list is null || !list.IsEffectivelyVisible)
        {
            return;
        }

        for (var i = 0; i < list.ItemCount; i++)
        {
            if (list.ContainerFromIndex(i) is Visual container)
            {
                order.Add(container.GetVisualDescendants()
                    .OfType<Avalonia.Controls.Primitives.ToggleButton>().FirstOrDefault());
            }
        }
    }

    public static void Handle(Control host, IReadOnlyList<InputElement> order, KeyEventArgs e)
    {
        if (order.Count == 0)
        {
            return;
        }

        var focused = TopLevel.GetTopLevel(host)?.FocusManager?.GetFocusedElement() as Visual;
        var index = -1;
        for (var i = 0; i < order.Count; i++)
        {
            if (ReferenceEquals(order[i], focused)
                || (focused is not null && order[i] is Visual entry
                    && focused.GetVisualAncestors().Contains(entry)))
            {
                index = i;
                break;
            }
        }

        var backwards = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var next = index < 0
            ? (backwards ? order.Count - 1 : 0)
            : backwards ? (index - 1 + order.Count) % order.Count
            : (index + 1) % order.Count;
        FocusEntry(order[next]);
        e.Handled = true;
    }

    /// <summary>A composite entry (picker, numeric field) focuses its inner part.</summary>
    public static void FocusEntry(InputElement entry)
    {
        if (entry.Focusable && entry.Focus())
        {
            return;
        }

        entry.GetVisualDescendants().OfType<InputElement>()
            .FirstOrDefault(i => i.Focusable && i.IsEffectivelyVisible && i.IsEffectivelyEnabled)
            ?.Focus();
    }
}
