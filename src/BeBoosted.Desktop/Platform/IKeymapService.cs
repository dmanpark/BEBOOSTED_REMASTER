using Avalonia.Input;

namespace BeBoosted.Desktop.Platform;

/// <summary>
/// Platform keyboard conventions: Ctrl-based gestures on Windows/Linux, Cmd-based on macOS.
/// </summary>
public interface IKeymapService
{
    KeyModifiers PrimaryModifier { get; }

    /// <summary>Gesture that focuses/expands the chatbot composer.</summary>
    KeyGesture ComposerGesture { get; }

    /// <summary>Human-readable form for shortcut chips, e.g. "Ctrl+J" or "⌘J".</summary>
    string DisplayString(KeyGesture gesture);
}
