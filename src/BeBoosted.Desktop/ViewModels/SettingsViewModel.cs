using System.Reflection;
using BeBoosted.Application.Abstractions;

namespace BeBoosted.Desktop.ViewModels;

public sealed class SettingsViewModel(IAppDataPaths paths) : ViewModelBase
{
    public string DataDirectory => paths.DataDirectory;

    public string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
}
