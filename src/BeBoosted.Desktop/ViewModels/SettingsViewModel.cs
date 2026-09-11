using System.Reflection;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

public sealed partial class SettingsViewModel(
    IAppDataPaths paths,
    AiPermissionSettings aiPermissions,
    CaptureModelSettings captureModel,
    ISecretProtector protector) : ViewModelBase
{
    public string DataDirectory => paths.DataDirectory;

    public string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    // ---- AI permissions (each is separate; capture never grants calendar access) ----

    public bool IsTaskCaptureReview
    {
        get => aiPermissions.TaskCapture == TaskCapturePermission.ReviewBeforeAdding;
        set
        {
            if (value)
            {
                aiPermissions.TaskCapture = TaskCapturePermission.ReviewBeforeAdding;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTaskCaptureAuto));
            }
        }
    }

    public bool IsTaskCaptureAuto
    {
        get => aiPermissions.TaskCapture == TaskCapturePermission.AddAutomatically;
        set
        {
            if (value)
            {
                aiPermissions.TaskCapture = TaskCapturePermission.AddAutomatically;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTaskCaptureReview));
            }
        }
    }

    public bool IsPlanningReview
    {
        get => aiPermissions.CalendarPlanning == CalendarPlanningPermission.ReviewEveryPlan;
        set
        {
            if (value)
            {
                aiPermissions.CalendarPlanning = CalendarPlanningPermission.ReviewEveryPlan;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPlanningAuto));
            }
        }
    }

    public bool IsPlanningAuto
    {
        get => aiPermissions.CalendarPlanning == CalendarPlanningPermission.ApplyAutomatically;
        set
        {
            if (value)
            {
                aiPermissions.CalendarPlanning = CalendarPlanningPermission.ApplyAutomatically;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPlanningReview));
            }
        }
    }

    // ---- Capture model (which parser turns a captured message into task drafts) ----

    /// <summary>False where no protector exists, which is what disables the Claude option.
    /// Without a protector there is nowhere safe to keep the key the choice would need,
    /// so the platform gets Ollama and the built-in parser but not cloud capture.</summary>
    public bool CanUseClaude => protector.IsAvailable;

    public string UnavailableKeyNotice => "Saving an API key isn't supported on this platform yet.";

    public bool IsCaptureHeuristic
    {
        get => captureModel.Source == CaptureModelSource.Heuristic;
        set => SetSource(value, CaptureModelSource.Heuristic);
    }

    public bool IsCaptureOllama
    {
        get => captureModel.Source == CaptureModelSource.Ollama;
        set => SetSource(value, CaptureModelSource.Ollama);
    }

    public bool IsCaptureClaude
    {
        get => captureModel.Source == CaptureModelSource.Claude;
        set
        {
            // Refused rather than half-applied: without a protector there is nowhere
            // safe to keep the key the choice would need. Still raise the change
            // notification so a bound RadioButton snaps back to the actual selection
            // instead of showing a checked state the stored source never reached.
            if (value && !CanUseClaude)
            {
                OnPropertyChanged();
                return;
            }

            SetSource(value, CaptureModelSource.Claude);
        }
    }

    private void SetSource(bool selected, CaptureModelSource source)
    {
        if (!selected || captureModel.Source == source)
        {
            return;
        }

        captureModel.Source = source;
        OnPropertyChanged(nameof(IsCaptureHeuristic));
        OnPropertyChanged(nameof(IsCaptureOllama));
        OnPropertyChanged(nameof(IsCaptureClaude));
        OnPropertyChanged(nameof(ConsentText));
    }

    /// <summary>States plainly what leaves the machine and when. This app's design
    /// principles rule out required cloud sync, so routing a captured message through
    /// Claude is a real departure from that — the card has to say so, not bury it.</summary>
    public string ConsentText => captureModel.Source switch
    {
        CaptureModelSource.Claude =>
            "The message you type, your project names, and today's date leave your computer — "
            + "only when you press send.",
        CaptureModelSource.Ollama => OllamaConsentText,
        _ => "Nothing is sent anywhere. Task capture uses built-in rules.",
    };

    /// <summary>
    /// What the Ollama choice actually sends, which depends on the address below it.
    /// The previous wording promised "Nothing leaves it" unconditionally — but the
    /// endpoint is an editable text box, so pointing it at another machine made that
    /// sentence false while it was still on screen. A consent line the app cannot keep
    /// is worse than no consent line, so the promise follows the address.
    /// </summary>
    public string OllamaConsentText
        => IsLocalAddress(captureModel.OllamaEndpoint)
            ? "Your message goes to Ollama on this computer. Nothing leaves this computer."
            : $"Your message goes to the Ollama server at {DisplayHost(captureModel.OllamaEndpoint)}. "
                + "It leaves this computer.";

    /// <summary>
    /// Loopback only. Anything unparseable is treated as remote on purpose: when the
    /// app cannot tell where a message is going, the honest answer is not a promise
    /// that it stays put.
    /// </summary>
    private static bool IsLocalAddress(string endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && (uri.IsLoopback
                || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    private static string DisplayHost(string endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Host
            : "the address below";

    public string OllamaEndpoint
    {
        get => captureModel.OllamaEndpoint;
        set
        {
            captureModel.OllamaEndpoint = value;
            OnPropertyChanged();
            // The consent line is derived from this address, so it is stale the moment
            // the address changes.
            OnPropertyChanged(nameof(OllamaConsentText));
            OnPropertyChanged(nameof(ConsentText));
        }
    }

    public string OllamaModel
    {
        get => captureModel.OllamaModel;
        set { captureModel.OllamaModel = value; OnPropertyChanged(); }
    }

    public string ClaudeModel
    {
        get => captureModel.ClaudeModel;
        set { captureModel.ClaudeModel = value; OnPropertyChanged(); }
    }

    /// <summary>The entry box only. The saved key is write-only by design — once protected
    /// it is never read back into this or any other property, so there is nothing on the
    /// view model that could redisplay or log it.</summary>
    [ObservableProperty]
    public partial string ApiKeyEntry { get; set; } = string.Empty;

    public bool HasSavedKey => captureModel.ProtectedClaudeKey is not null;

    [RelayCommand]
    private void SaveApiKey()
    {
        if (!protector.IsAvailable || string.IsNullOrWhiteSpace(ApiKeyEntry))
        {
            return;
        }

        captureModel.ProtectedClaudeKey = protector.Protect(ApiKeyEntry.Trim());
        ApiKeyEntry = string.Empty;
        OnPropertyChanged(nameof(HasSavedKey));
    }

    [RelayCommand]
    private void RemoveApiKey()
    {
        captureModel.ProtectedClaudeKey = null;
        OnPropertyChanged(nameof(HasSavedKey));
    }
}
