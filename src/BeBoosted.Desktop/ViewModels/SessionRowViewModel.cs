using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>
/// One row of the whole-task editor's Schedule section. The row is display data
/// plus two quiet actions; every flow (gate, confirmation, persistence) lives
/// on the owning whole-task editor.
/// </summary>
public sealed partial class SessionRowViewModel(WholeTaskEditorViewModel owner, SessionRowData data)
    : ViewModelBase
{
    public SessionRowData Data { get; } = data;

    [RelayCommand]
    private void Edit() => owner.EditRow(this);

    [RelayCommand]
    private void Remove() => owner.RequestRemoveRow(this);
}
