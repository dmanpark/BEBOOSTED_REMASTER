using Avalonia.Controls;
using Avalonia.Threading;

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
    }
}
