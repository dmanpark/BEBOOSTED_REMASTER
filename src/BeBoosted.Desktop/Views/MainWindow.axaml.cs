using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using BeBoosted.Desktop.ViewModels;

namespace BeBoosted.Desktop.Views;

public partial class MainWindow : Window
{
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
}
