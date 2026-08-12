using System.Diagnostics;

namespace BeBoosted.Desktop.Platform;

/// <summary>Platform file/URL opening conventions (Explorer vs Finder).</summary>
public interface IFileRevealService
{
    void OpenFile(string path);

    void RevealInFolder(string path);

    void OpenUrl(string url);
}

public sealed class DefaultFileRevealService : IFileRevealService
{
    public void OpenFile(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", $"\"{path}\"");
        }
        else
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    public void RevealInFolder(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", $"-R \"{path}\"");
        }
        else
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
    }

    public void OpenUrl(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
