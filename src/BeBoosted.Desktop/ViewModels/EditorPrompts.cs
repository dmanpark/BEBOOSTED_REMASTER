namespace BeBoosted.Desktop.ViewModels;

/// <summary>
/// One two-step inline confirmation: the message names the exact scope, the
/// confirm button names the action, and task deletion gets the umber treatment.
/// </summary>
public sealed record ConfirmationPrompt(string Message, string ConfirmLabel, bool IsTaskDeletion);

/// <summary>
/// The save-or-discard gate. The sub-line is fixed frame copy ("Save or discard
/// before continuing.") and the other two actions are always "Discard changes
/// and continue" and "Keep editing" — rendered by the views, not stored here.
/// </summary>
public sealed record GatePrompt(string Title, string SaveLabel);
