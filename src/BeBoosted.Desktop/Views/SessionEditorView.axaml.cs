using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BeBoosted.Desktop.ViewModels;

namespace BeBoosted.Desktop.Views;

/// <summary>
/// Focus behavior for the session editor: Tab cycles inside the card (or the
/// active prompt), and prompts take focus on open, returning it to their
/// trigger on dismissal.
/// </summary>
public partial class SessionEditorView : UserControl
{
    private IInputElement? _promptReturnFocus;
    private Control? _lastBodyFocus;
    private SessionEditorViewModel? _viewModel;

    public SessionEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = DataContext as SessionEditorViewModel;
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
        // A prompt armed before the view existed (Delete-key entry) still takes
        // focus the moment the view appears.
        Loaded += (_, _) =>
        {
            if (_viewModel is { } vm && (vm.Confirmation is not null || vm.Gate is not null))
            {
                var firstAction = vm.Gate is not null
                    ? this.FindControl<Button>("SessionGateSaveButton")
                    : this.FindControl<Button>("SessionPromptKeepButton");
                Dispatcher.UIThread.Post(() => firstAction?.Focus());
            }
        };
    }

    private static bool IsInPromptLayer(Control control)
        => control.GetVisualAncestors().OfType<Border>()
            .Any(border => border.Name is "SessionGateCard" or "SessionConfirmationCard");

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SessionEditorViewModel.Confirmation)
            or nameof(SessionEditorViewModel.Gate)))
        {
            return;
        }

        var viewModel = _viewModel!;
        if (viewModel.Confirmation is not null || viewModel.Gate is not null)
        {
            _promptReturnFocus = _lastBodyFocus;
            var firstAction = viewModel.Gate is not null
                ? this.FindControl<Button>("SessionGateSaveButton")
                : this.FindControl<Button>("SessionPromptKeepButton");
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
            // the editor's default field so focus never vanishes.
            _promptReturnFocus = null;
            Dispatcher.UIThread.Post(() =>
            {
                if (IsLoaded && DefaultBodyFocus() is { } target)
                {
                    FocusWhenReady(target);
                }
            });
        }
    }

    private Control? DefaultBodyFocus()
        => _viewModel?.Mode == SessionEditorMode.Repeating
            ? this.FindControl<Control>("SessionStartPicker")
            : this.FindControl<Control>("SessionDatePicker");

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
    /// order"): schedule fields in visual order → occurrence checkbox
    /// (repeating) → Repeats toggle → weekday chips → Remove this session /
    /// Remove schedule → Edit whole task → Cancel → Save, with Close as the
    /// final extra stop. A prompt or gate cycles over exactly its own actions.
    /// </summary>
    private IReadOnlyList<InputElement> TabOrder()
    {
        var scope = ActiveFocusScope();
        if (ReferenceEquals(scope, this.FindControl<Border>("SessionGateCard")))
        {
            return EditorTabCycle.Focusables(
                this.FindControl<Button>("SessionGateSaveButton"),
                this.FindControl<Button>("SessionGateDiscardButton"),
                this.FindControl<Button>("SessionGateKeepButton"));
        }

        if (ReferenceEquals(scope, this.FindControl<Border>("SessionConfirmationCard")))
        {
            return EditorTabCycle.Focusables(
                this.FindControl<Button>("SessionPromptKeepButton"),
                this.FindControl<Button>("SessionPromptConfirmButton"));
        }

        var order = new List<InputElement?>
        {
            this.FindControl<DatePicker>("SessionDatePicker"),
            this.FindControl<TimePicker>("SessionStartPicker"),
            this.FindControl<TimePicker>("SessionEndPicker"),
            this.FindControl<CheckBox>("SessionOccurrenceBox"),
            this.FindControl<CheckBox>("SessionRepeatsBox"),
        };
        EditorTabCycle.AddItemToggles(order, this.FindControl<ItemsControl>("SessionDaysList"));
        order.Add(this.FindControl<Button>("SessionRemoveButton"));
        order.Add(this.FindControl<Button>("EditWholeTaskLink"));
        order.Add(this.FindControl<Button>("SessionCancelButton"));
        order.Add(this.FindControl<Button>("SessionSaveButton"));
        order.Add(this.FindControl<Button>("SessionCloseButton"));
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
        if (this.FindControl<Border>("SessionGateCard") is { IsVisible: true } gate)
        {
            return gate;
        }

        if (this.FindControl<Border>("SessionConfirmationCard") is { IsVisible: true } confirmation)
        {
            return confirmation;
        }

        return this.FindControl<Border>("SessionEditorCard")!;
    }
}
