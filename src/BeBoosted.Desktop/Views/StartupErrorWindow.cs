using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BeBoosted.Desktop.Views;

/// <summary>Shown instead of the shell when local storage cannot be opened or migrated.</summary>
public sealed class StartupErrorWindow : Window
{
    public StartupErrorWindow(string title, string detail)
    {
        Title = "BeBoosted";
        Width = 560;
        Height = 320;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#F2EEDB"));

        var closeButton = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        closeButton.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(32),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                },
                new TextBlock
                {
                    Text = detail,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#73776E")),
                },
                closeButton,
            },
        };
    }
}
