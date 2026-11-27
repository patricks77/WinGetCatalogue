using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Windows.Management.Deployment;

namespace WinGetCatalogue;

internal sealed record RunningInstalledProcess(int Id, DateTime StartedUtc, string Executable);

internal static class RunningInstalledApplication
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const int ProcessQueryLimitedInformation = 0x1000;
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    internal static async Task<RunningInstalledProcess[]> FindAsync(InstalledSoftware item)
    {
        // Match registered applications by display name and installed version before inspecting processes.
        var packaged = await FindPackagedInstallationAsync(item).ConfigureAwait(false);
        var (directory, executable) = packaged is null ? FindDesktopInstallation(item) : (null, null);
        if (packaged is { Directory: null } || (packaged is null && directory is null && executable is null))
            return [];

        using var current = Process.GetCurrentProcess();
        var matches = new List<RunningInstalledProcess>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == current.Id || process.SessionId != current.SessionId ||
                        process.MainModule is not { } module)
                        continue;

                    string path = module.FileName;
                    bool matched = packaged is not null
                        ? IsWithin(path, packaged.Directory) && HasPackageIdentity(process.Id, packaged.FullName)
                        : PathComparer.Equals(path, executable) || (IsWithin(path, directory) &&
                            NameEquals(module.FileVersionInfo.ProductName, item.Name));
                    if (matched)
                        matches.Add(new RunningInstalledProcess(process.Id, process.StartTime.ToUniversalTime(), path));
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Processes can exit or be inaccessible while the current session is being inspected.
                }
            }
        }

        DiagnosticLog.Write($"Upgrade preflight {item.Source}/{item.Id}: {matches.Count} running processes matched");
        return matches.ToArray();
    }

    internal static async Task<bool> ForceCloseAsync(RunningInstalledProcess[] matches)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return (await Task.WhenAll(matches.Select(match => ForceCloseAsync(match, timeout.Token)))).All(closed => closed);
    }

    private static async Task<bool> ForceCloseAsync(RunningInstalledProcess match, CancellationToken token)
    {
        if (match.Id == Environment.ProcessId) return false;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(match.Id);
            // Revalidate the original process because Windows can reuse a PID after it exits.
            if (process.HasExited) return true;
            _ = process.SafeHandle;
            if (process.HasExited || process.StartTime.ToUniversalTime() != match.StartedUtc ||
                !PathComparer.Equals(process.MainModule?.FileName, match.Executable))
                return true;

            // Close the matched process only. Its children can belong to another application.
            process.Kill();
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            return true;
        }
        catch (ArgumentException)
        {
            return true; // The application exited before the user confirmed.
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            if (HasExited(process)) return true;
            DiagnosticLog.Write($"Could not close upgrade process {match.Id}", exception);
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static bool HasExited(Process? process)
    {
        try { return process?.HasExited == true; }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { return false; }
    }

    private sealed record PackagedInstallation(string FullName, string? Directory);

    private static async Task<PackagedInstallation?> FindPackagedInstallationAsync(InstalledSoftware item)
    {
        PackagedInstallation? installation = null;
        try
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                var version = package.Id.Version;
                if (package.IsFramework || package.IsResourcePackage ||
                    !VersionEquals($"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}", item.InstalledVersion))
                    continue;

                var entries = await package.GetAppListEntriesAsync().AsTask().ConfigureAwait(false);
                if (!entries.Any(entry => NameEquals(entry.DisplayInfo.DisplayName, item.Name))) continue;
                if (installation is not null) return new PackagedInstallation("", null);
                installation = new PackagedInstallation(package.Id.FullName, NormalizePath(package.InstalledLocation.Path));
            }
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or InvalidOperationException
            or IOException)
        {
            DiagnosticLog.Write($"Unable to read registered Windows apps for {item.Id}", exception);
            return installation is null ? null : new PackagedInstallation("", null);
        }
        return installation;
    }

    // An MSIX update can be blocked by helpers after the main window closes. Require both
    // the package's installation directory and the process's actual Windows package identity.
    private static bool HasPackageIdentity(int pid, string fullName)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid) return false;
        uint length = 256;
        var name = new StringBuilder((int)length);
        return GetPackageFullName(handle, ref length, name) == 0 && PathComparer.Equals(fullName, name.ToString());
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeProcessHandle OpenProcess(int desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetPackageFullName(SafeProcessHandle process, ref uint length, StringBuilder name);

    private static (string? Directory, string? Executable) FindDesktopInstallation(InstalledSoftware item)
    {
        var directories = new HashSet<string>(PathComparer);
        var executables = new HashSet<string>(PathComparer);
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallKey);
                if (uninstall is null) continue;

                foreach (string name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (!NameEquals(key?.GetValue("DisplayName") as string, item.Name) ||
                        !VersionEquals(key?.GetValue("DisplayVersion") as string, item.InstalledVersion))
                        continue;

                    string? directory = NormalizePath(key!.GetValue("InstallLocation") as string);
                    if (directory is not null && Directory.Exists(directory))
                        directories.Add(directory);

                    string? icon = key.GetValue("DisplayIcon") as string;
                    // A display icon can include an icon index after the executable path.
                    string? executable = NormalizePath(icon?.Split(',')[0]);
                    if (executable is not null && executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(executable) &&
                        NameEquals(FileVersionInfo.GetVersionInfo(executable).ProductName, item.Name))
                        executables.Add(executable);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                DiagnosticLog.Write($"Unable to read installed program registration in {hive}/{view}", exception);
            }
        }

        return directories.Count <= 1 && executables.Count <= 1
            ? (directories.SingleOrDefault(), executables.SingleOrDefault())
            : (null, null);
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            string path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(path)) return null;
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return PathComparer.Equals(path, Path.GetPathRoot(path)) ? null : path;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    internal static bool IsWithin(string path, string? directory) => directory is not null &&
        path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool NameEquals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && string.Equals(left.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool VersionEquals(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase) ||
            (Version.TryParse(left, out var a) && Version.TryParse(right, out var b) &&
             a.Major == b.Major && a.Minor == b.Minor &&
             Math.Max(a.Build, 0) == Math.Max(b.Build, 0) &&
             Math.Max(a.Revision, 0) == Math.Max(b.Revision, 0));
    }
}
