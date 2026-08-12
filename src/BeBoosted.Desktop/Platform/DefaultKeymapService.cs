using Avalonia.Input;

namespace BeBoosted.Desktop.Platform;

public sealed class DefaultKeymapService : IKeymapService
{
    private readonly bool _isMac = OperatingSystem.IsMacOS();

    public KeyModifiers PrimaryModifier => _isMac ? KeyModifiers.Meta : KeyModifiers.Control;

    public KeyGesture ComposerGesture => new(Key.J, PrimaryModifier);

    public string DisplayString(KeyGesture gesture)
    {
        var key = gesture.Key.ToString();
        if (_isMac)
        {
            var mods = string.Concat(
                gesture.KeyModifiers.HasFlag(KeyModifiers.Control) ? "⌃" : string.Empty,
                gesture.KeyModifiers.HasFlag(KeyModifiers.Alt) ? "⌥" : string.Empty,
                gesture.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "⇧" : string.Empty,
                gesture.KeyModifiers.HasFlag(KeyModifiers.Meta) ? "⌘" : string.Empty);
            return mods + key;
        }

        var parts = new List<string>(4);
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(key);
        return string.Join("+", parts);
    }
}
