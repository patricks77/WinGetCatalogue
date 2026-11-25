using System.Diagnostics;

namespace WinGetCatalogue;

// Diagnostic output is local and never includes user-entered report text or credentials.
internal static class DiagnosticLog
{
    private static readonly object Sync = new();
    private static readonly string PathName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinGetCatalogue", "logs", "diagnostics.log");

    internal static void Write(string message, Exception? error = null)
    {
        string line = $"{DateTimeOffset.Now:O} {message}" + (error is null ? "" : $": {error}");
        Debug.WriteLine(line);
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
                File.AppendAllText(PathName, line + Environment.NewLine);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(exception);
        }
    }
}
