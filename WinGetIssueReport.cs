using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

// Builds a reviewable GitHub issue draft from a failed installation and its logs.
namespace WinGetCatalogue;

internal sealed record WinGetInstallationFailure(CatalogEntry Package, int ExitCode, int ProcessId, DateTime StartedUtc)
{
    internal string Code => $"0x{ExitCode:X8}";
}

internal sealed record IssueField(string Name, string Value);

internal sealed record WinGetIssueDraft(string Title, Uri FormUri, IssueField[] Fields)
{
    internal string GetClipboardText() =>
        $"{Title}\n\n" + string.Join("\n\n", Fields.Select(field => $"### {field.Name}\n\n{field.Value}"));
}

internal static class WinGetIssueReport
{
    private const uint HashMismatch = 0x8A150011;
    private const string FormUrl = "https://github.com/microsoft/winget-pkgs/issues/new?template=package_issue.yml";

    internal static bool IsReportable(int exitCode) => WinGetCommand.GetSourceFailureMessage(exitCode) is null &&
        unchecked((uint)exitCode) is not
        (0 or 1223 or 1602 or 0x800704C7 or 0x80070642 or 0xC000013A or 0x8A150014 or
         0x8A150005 or 0x8A15006A or 0x8A150077 or 0x8A150109 or 0x8A15010A or
         0x8A15010B or 0x8A15010C or 0x8A15010D or 0x8A15010E);

    // Prepare a draft for user review; never submit an issue automatically.
    internal static WinGetIssueDraft Prepare(WinGetInstallationFailure failure)
    {
        string diagnostics = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "LocalState", "DiagOutputDir");
        string[] log = ReadMatchingLog(failure, diagnostics);
        string? attemptedVersion = Extract(log, @"\[CLI \] Manifest fields: Name \[.*\], Version \[([\w.+~-]{1,100})\]");
        string? wingetVersion = Extract(log, @"\[CORE\] WinGet, version \[([\d.]+)\]");
        string? windowsVersion = Extract(log, @"\[CORE\] OS: (Windows\.Desktop v[\d.]+)");
        string? appInstallerVersion = Extract(log, @"\[CORE\] Package: Microsoft\.DesktopAppInstaller v([\d.]+)");
        string? mediaType = Extract(log, @"\[CLI \] Package hash verification failed\..*content type \[([\w.+-]+/[\w.+-]+)(?:;[^\]]*)?\]");
        string? byteCount = Extract(log, @"\[CLI \] Package hash verification failed\..*file size \[(\d+)\]");

        bool hashMismatch = unchecked((uint)failure.ExitCode) == HashMismatch;
        string version = attemptedVersion is not null ? $" version {attemptedVersion}" : "";
        string title = $"[Package Issue]: {failure.Package.Id}{version} fails with {failure.Code}";
        string summary = $"Installing `{failure.Package.Id}`{version} from the WinGet community source fails with `{failure.Code}`.";
        if (attemptedVersion is null && !string.IsNullOrWhiteSpace(failure.Package.Version))
        {
            summary += $" The catalogue listed version {failure.Package.Version}.";
        }

        string actualBehavior = hashMismatch
            ? $"WinGet rejected the download because its SHA-256 did not match the package manifest. Exit code `{failure.Code}`."
            : $"WinGet did not finish the installation successfully. Exit code `{failure.Code}`.";
        if (mediaType is not null)
        {
            actualBehavior += byteCount is null
                ? $" The download was reported as `{mediaType}`."
                : $" The download was reported as `{mediaType}` ({byteCount} bytes).";
        }

        var environment = new List<string>
        {
            $"Windows: {windowsVersion ?? $"Windows.Desktop v{Environment.OSVersion.Version}"}",
            $"System Architecture: {RuntimeInformation.OSArchitecture}",
        };
        if (wingetVersion is not null)
        {
            environment.Insert(0, $"Windows Package Manager v{wingetVersion}");
        }

        if (appInstallerVersion is not null)
        {
            environment.Add($"Package: Microsoft.DesktopAppInstaller v{appInstallerVersion}");
        }

        var fields = new IssueField[]
        {
            new("Category of the issue", hashMismatch ? "Installer hash mismatch." : "Installation issue."),
            new("Brief description of your issue", summary),
            new("Steps to reproduce", $"`{WinGetCommand.GetCommand(failure.Package.Id)}`"),
            new("Actual behavior", actualBehavior),
            new("Expected behavior", "WinGet should download a valid installer and complete the installation."),
            new("Environment", string.Join("\n", environment)),
        };
        return new WinGetIssueDraft(title, new Uri($"{FormUrl}&title={Uri.EscapeDataString(title)}"), fields);
    }

    private static string[] ReadMatchingLog(WinGetInstallationFailure failure, string directory)
    {
        try
        {
            var logs = new DirectoryInfo(directory).EnumerateFiles("WinGet-*.log")
                .Where(log => log.LastWriteTimeUtc >= failure.StartedUtc.AddSeconds(-2))
                .OrderByDescending(log => log.LastWriteTimeUtc);
            string processMarker = $"[CORE] Process: winget.exe[{failure.ProcessId}]";
            foreach (var file in logs)
            {
                try
                {
                    using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    var header = new List<string>();
                    while (header.Count < 12 && reader.ReadLine() is { } line)
                    {
                        header.Add(line);
                    }

                    if (header.Any(line => line.Contains(processMarker, StringComparison.OrdinalIgnoreCase)))
                    {
                        while (reader.ReadLine() is { } line)
                        {
                            header.Add(line);
                        }

                        return header.ToArray();
                    }
                }
                catch (IOException)
                {
                    // WinGet can rotate an unrelated log while the directory is being inspected.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The failure can still be reported using the process exit code and catalogue data.
        }

        return [];
    }

    private static string? Extract(IEnumerable<string> lines, string pattern)
    {
        foreach (var line in lines)
        {
            var match = Regex.Match(line, pattern, RegexOptions.CultureInvariant);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }
}
