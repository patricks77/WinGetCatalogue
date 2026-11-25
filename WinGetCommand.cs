using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

// Validates package IDs before constructing or starting WinGet commands.
namespace WinGetCatalogue;

internal static partial class WinGetCommand
{
    [GeneratedRegex(@"\A[^\s\p{Cc}\\/:*?""<>|]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdPattern();

    private static readonly char[] PowerShellQuotes = ['\'', '‘', '’', '‚', '‛'];

    internal static string ExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WindowsApps", "winget.exe");

    // An execution alias can exist even when WinGet cannot start for this user.
    internal static async Task<bool> CheckAvailabilityAsync()
    {
        string path = ExecutablePath;
        if (!File.Exists(path))
        {
            DiagnosticLog.Write("WinGet dependency check: execution alias not found");
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                ArgumentList = { "--version" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }) ?? throw new InvalidOperationException("WinGet did not start.");

            // WinGet prints a short version line. Read both streams while it runs.
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(output, errors);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill();
                DiagnosticLog.Write("WinGet dependency check: version command timed out");
                return false;
            }

            DiagnosticLog.Write($"WinGet dependency check: version command exited with 0x{process.ExitCode:X8}");
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or IOException)
        {
            DiagnosticLog.Write("WinGet dependency check failed", exception);
            return false;
        }
    }

    internal static bool IsValidId(string? id) =>
        id is { Length: > 0 and <= 128 } && !id.StartsWith('-') && PackageIdPattern().IsMatch(id);

    internal static string GetCommand(string id)
    {
        ValidateId(id);
        string escapedId = string.Concat(id.Select(character =>
            PowerShellQuotes.Contains(character) ? new string(character, 2) : character.ToString()));
        return $"winget install --id '{escapedId}' --exact --source winget";
    }

    internal static Process StartInstallation(string id)
        => Start(id, "install");

    internal static Process StartUpgrade(string id, string source)
        => Start(id, "upgrade", source);

    internal static string? GetSourceFailureMessage(int exitCode) => unchecked((uint)exitCode) switch
    {
        0x8A150046 => "Open WinGet and accept the source agreements, then try again.",
        0x8A150012 or 0x8A150015 => "Check the configured sources with winget source list, then try again.",
        _ => null,
    };

    internal static string GetFailureMessage(int exitCode) => GetSourceFailureMessage(exitCode) ??
        $"WinGet exited with code 0x{exitCode:X8}.";

    private static Process Start(string id, string action, string source = "winget")
    {
        ValidateId(id);
        string wingetPath = ExecutablePath;
        if (!File.Exists(wingetPath))
        {
            throw new FileNotFoundException("WinGet was not found. Install or update Microsoft App Installer, then try again.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = wingetPath,
            UseShellExecute = true,
        };

        foreach (string argument in new[] { action, "--id", id, "--exact", "--source", source })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The WinGet process could not be started.");
    }

    // Reject IDs that could alter the intended command arguments.
    private static void ValidateId(string id)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException("L’identifiant WinGet est invalide.", nameof(id));
        }
    }
}
