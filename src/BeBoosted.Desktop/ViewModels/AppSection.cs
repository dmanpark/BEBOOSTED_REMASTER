namespace BeBoosted.Desktop.ViewModels;

/// <summary>Primary destinations plus the Settings utility destination.
/// The Inbox is deliberately not a section: it opens as a drawer over the calendar.</summary>
public enum AppSection
{
    Calendar,
    Projects,
    Settings,
}
