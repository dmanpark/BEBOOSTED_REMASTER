using System.Collections.Specialized;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BeBoosted.Desktop.ViewModels;

namespace BeBoosted.Desktop.Views;

public partial class MainWindow : Window
{
    private IInputElement? _taskEditorReturnFocus;

    public MainWindow()
    {
        InitializeComponent();

        // Focus the capture box whenever the Inbox drawer opens.
        InboxDrawer.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && e.NewValue is true)
            {
                Dispatcher.UIThread.Post(() => InboxDrawerContent.FocusCapture());
            }
        };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ShellViewModel shell)
            {
                return;
            }

            // Initial focus per editor type; closing returns focus to the invoker.
            shell.Calendar.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(CalendarViewModel.ActiveTaskEditor))
                {
                    OnEditorSlotsChanged(shell.Calendar);
                }
            };

            // Returning from a pushed session editor refocuses the launching row.
            shell.Calendar.EditorRowFocusRequested += rowId => Dispatcher.UIThread.Post(() =>
                this.GetVisualDescendants().OfType<WholeTaskEditorView>()
                    .FirstOrDefault()?.FocusRow(rowId));

            // Ctrl+J / ⌘J routes focus to whichever composer surface is showing.
            shell.ComposerFocusRequested += () => Dispatcher.UIThread.Post(() =>
            {
                if (shell.Chat.IsExpanded)
                {
                    ChatInput.Focus();
                }
                else
                {
                    ComposerInput.Focus();
                }
            });

            // Keep the newest exchange visible, and hand focus over on expand.
            shell.Chat.Items.CollectionChanged += (_, args) =>
            {
                if (args.Action is NotifyCollectionChangedAction.Add)
                {
                    Dispatcher.UIThread.Post(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);
                }
            };
            shell.Chat.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ChatViewModel.IsExpanded) && shell.Chat.IsExpanded)
                {
                    Dispatcher.UIThread.Post(() => ChatInput.Focus());
                }
            };
        };
    }

    /// <summary>Escape steps out one level: prompt, then a pushed editor, then close.</summary>
    private void OnTaskModalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ShellViewModel shell)
        {
            shell.Calendar.EscapeTaskEditor();
            e.Handled = true;
        }
    }

    private bool _editorOpen;
    private string? _taskEditorReturnIdentity;

    /// <summary>
    /// A stable invoker identity: originating surface, control role, and the
    /// row's domain id. Accessible names alone collide (duplicate task titles,
    /// one task on several surfaces), so they are never the key on their own.
    /// </summary>
    private static string? InvokerIdentity(Control control)
    {
        var domain = control.DataContext switch
        {
            DailyRowViewModel row =>
                $"daily-row:{row.TaskId}:{row.BlockId}:{row.Date:yyyy-MM-dd}:{row.Kind}",
            CalendarBlockViewModel block => $"block:{block.Id}:{block.Date:yyyy-MM-dd}",
            TaskRowViewModel task => $"inbox-task:{task.Task.Id}",
            ProjectTaskRowViewModel projectTask => $"project-task:{projectTask.TaskId}",
            ScheduledBlockRowViewModel projectBlock =>
                $"project-block:{projectBlock.BlockId}:{projectBlock.Date:yyyy-MM-dd}",
            _ => null,
        };
        if (domain is null)
        {
            return null;
        }

        var surface = control.FindAncestorOfType<UserControl>()?.GetType().Name ?? "window";
        var role = $"{control.GetType().Name}:{control.Name ?? AutomationProperties.GetName(control)}";
        return $"{surface}|{role}|{domain}";
    }

    /// <summary>
    /// The invoker's focus is captured once on closed→open and restored on close;
    /// in-modal transitions (push/return) only move the initial focus. A save
    /// may rebuild the invoking surface, so the capture also keeps the invoker's
    /// domain identity to find the exact replacement control.
    /// </summary>
    private void OnEditorSlotsChanged(CalendarViewModel calendar)
    {
        var open = calendar.ActiveTaskEditor is not null;
        if (open && !_editorOpen)
        {
            _taskEditorReturnFocus = FocusManager?.GetFocusedElement();
            _taskEditorReturnIdentity = _taskEditorReturnFocus is Control invokerControl
                ? InvokerIdentity(invokerControl)
                : null;
        }

        _editorOpen = open;
        if (open)
        {
            var focusName = calendar.ActiveTaskEditor switch
            {
                WholeTaskEditorViewModel => "TaskTitleBox",
                SessionEditorViewModel { Mode: SessionEditorMode.Repeating } => "SessionStartPicker",
                SessionEditorViewModel => "SessionDatePicker",
                _ => "TaskTitleBox",
            };
            Dispatcher.UIThread.Post(() =>
            {
                // A pre-armed prompt owns the initial focus; the editor views
                // send it to the prompt's first action instead.
                var promptActive = calendar.ActiveTaskEditor switch
                {
                    WholeTaskEditorViewModel wholeTask => wholeTask.HasActivePrompt,
                    SessionEditorViewModel session => session.HasActivePrompt,
                    _ => false,
                };
                if (!promptActive)
                {
                    this.GetVisualDescendants().OfType<Control>()
                        .FirstOrDefault(control => control.Name == focusName)
                        ?.Focus();
                }
            });
            return;
        }

        var invoker = _taskEditorReturnFocus as Control;
        var identity = _taskEditorReturnIdentity;
        _taskEditorReturnFocus = null;
        _taskEditorReturnIdentity = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (TryRestoreInvokerFocus(invoker, identity))
            {
                return;
            }

            // The replacement may not have materialized yet — retry after layout.
            EventHandler? onLayout = null;
            onLayout = (_, _) =>
            {
                LayoutUpdated -= onLayout;
                TryRestoreInvokerFocus(invoker, identity);
            };
            LayoutUpdated += onLayout;
        });
    }

    /// <summary>The original instance if it survived, else its exact identity match.</summary>
    private bool TryRestoreInvokerFocus(Control? invoker, string? identity)
    {
        if (invoker is { IsLoaded: true, IsEffectivelyVisible: true })
        {
            return invoker.Focus();
        }

        if (identity is null)
        {
            return invoker is null; // no identity to match: nothing safe to focus
        }

        var replacement = this.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => control.IsEffectivelyVisible
                && InvokerIdentity(control) == identity);
        return replacement?.Focus() == true;
    }
}
