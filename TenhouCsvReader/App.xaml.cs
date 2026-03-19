using System.IO;
using System.Linq;
using System.Windows;

namespace TenhouCsvReader;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var startupCsvPath = TryResolveStartupCsvPath(e.Args);
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        if (startupCsvPath is null)
        {
            return;
        }

        _ = mainWindow.OpenCsvFromLaunchAsync(startupCsvPath);
    }

    private static string? TryResolveStartupCsvPath(string[] args)
    {
        var allArgs = args
            .Concat(Environment.GetCommandLineArgs().Skip(1))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var rawArg in allArgs)
        {
            if (!TryNormalizeLaunchPath(rawArg, out var candidatePath))
            {
                continue;
            }

            if (!string.Equals(Path.GetExtension(candidatePath), ".csv", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return candidatePath;
        }

        return null;
    }

    private static bool TryNormalizeLaunchPath(string? rawArg, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        var candidate = rawArg?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        while (candidate.Length >= 2 &&
               ((candidate[0] == '"' && candidate[^1] == '"') ||
                (candidate[0] == '\'' && candidate[^1] == '\'')))
        {
            candidate = candidate[1..^1].Trim();
        }

        if (string.IsNullOrWhiteSpace(candidate) ||
            string.Equals(candidate, "%1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, "%L", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, "%V", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            candidate = uri.LocalPath;
        }

        try
        {
            normalizedPath = Path.GetFullPath(candidate);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
