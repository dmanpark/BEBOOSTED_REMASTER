using System.Collections.ObjectModel;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>The universal Inbox: one capture queue for unscheduled work.</summary>
public sealed partial class InboxViewModel : ViewModelBase
{
    private readonly TaskService _service;
    private readonly IClock _clock;

    private readonly InboxQueryService _query;

    public InboxViewModel(TaskService service, InboxQueryService query, IClock clock)
    {
        _service = service;
        _query = query;
        _clock = clock;
        Reload();

        Tasks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(OpenCount));
            OnPropertyChanged(nameof(HasTasks));
        };
    }

    /// <summary>Rebuilds the queue from storage — called after scheduling or outcomes change it.</summary>
    public void Reload()
    {
        Tasks.Clear();
        foreach (var task in _query.GetInboxTasks())
        {
            Tasks.Add(new TaskRowViewModel(task, _service, _clock, RemoveRow));
        }
    }

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];

    public int OpenCount => Tasks.Count;

    public bool HasTasks => Tasks.Count > 0;

    [ObservableProperty]
    public partial string CaptureText { get; set; } = string.Empty;

    [RelayCommand]
    private void Capture()
    {
        var title = CaptureText.Trim();
        if (title.Length == 0)
        {
            return;
        }

        var task = _service.Capture(title);
        Tasks.Add(new TaskRowViewModel(task, _service, _clock, RemoveRow));
        CaptureText = string.Empty;
    }

    private void RemoveRow(TaskRowViewModel row) => Tasks.Remove(row);
}
