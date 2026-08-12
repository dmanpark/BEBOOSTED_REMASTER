using Avalonia.Controls;
using Avalonia.Threading;

namespace BeBoosted.Desktop.Views;

public partial class PrioritySortView : UserControl
{
    public PrioritySortView()
    {
        InitializeComponent();

        // Focus the left card when the overlay opens so keyboard choices work immediately.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && e.NewValue is true)
            {
                Dispatcher.UIThread.Post(() => LeftCardButton.Focus());
            }
        };
    }
}
