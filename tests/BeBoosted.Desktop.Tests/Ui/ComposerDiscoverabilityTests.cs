using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// The composer is the app's headline feature and its least discoverable surface. A
/// dogfooding session found no way to tell that it accepts natural language, and no
/// way to submit except pressing Enter — the only visible hint, "Ctrl+J", says how to
/// jump *to* the box, not how to send *from* it.
/// </summary>
public sealed class ComposerDiscoverabilityTests
{
    private static MainWindow Show(out ShellViewModel shell)
    {
        shell = TestShell.Create();
        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 800 };
        window.Show();
        window.CaptureRenderedFrame();
        return window;
    }

    /// <summary>
    /// The expanded chat has always had a send button; the collapsed composer — the one
    /// actually on screen all day — had none. Submission was Enter-only and unhinted.
    /// </summary>
    [AvaloniaFact]
    public void TheCollapsedComposer_HasAVisibleSendButton_BoundToSubmit()
    {
        var window = Show(out var shell);
        Assert.False(shell.Chat.IsExpanded, "this test is about the collapsed composer");

        var sendButtons = window.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Send")
            .ToList();

        var send = Assert.Single(sendButtons);
        Assert.Same(shell.Chat.SubmitCommand, send.Command);
    }

    /// <summary>
    /// The placeholder is the only guidance the composer gets, so it has to say that a
    /// whole thought is welcome — not just name the app.
    /// </summary>
    [AvaloniaFact]
    public void TheComposerPlaceholder_InvitesAWholeThought()
    {
        Show(out var shell);

        // Through SetScope, not just the field initializer: the shell resets the scope
        // on startup and on every project change, so a default that only lives in the
        // initializer is overwritten before anyone sees it.
        shell.Chat.SetScope(null);

        var placeholder = shell.Chat.Placeholder;
        Assert.Contains("task", placeholder, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            placeholder.Length > 30,
            $"a bare label does not teach the feature: \"{placeholder}\"");
    }
}
