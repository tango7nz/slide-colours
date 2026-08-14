using System.IO;
using System.Text;

namespace SlideColours.Services;

/// <summary>
/// Appends unexpected failures to a file next to the settings, so a fault that happened mid-service
/// can be looked at afterwards. Deliberately the only thing an unhandled exception does: a message
/// box on stage would be worse than the fault it's reporting.
///
/// Nothing here may throw. It runs from the last-chance handlers in <see cref="App"/>, where a
/// second failure has nowhere left to go.
/// </summary>
public static class ErrorLog
{
    /// <summary>Trimmed back to the newest half once it passes this, so it can't grow forever.</summary>
    private const long MaxBytes = 256 * 1024;

    private static readonly object Sync = new();
    private static int _count;

    /// <summary>
    /// Raised after an entry is written, so the pill can show that something went wrong rather than
    /// leaving it to be discovered in the log afterwards. Fires on whichever thread failed, so a
    /// subscriber has to marshal to its own.
    /// </summary>
    public static event Action? Written;

    /// <summary>Entries written this run, so a window built later can still catch up.</summary>
    public static int Count => Volatile.Read(ref _count);

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlideColours", "error.log");

    public static void Write(string context, Exception? ex)
    {
        try
        {
            var entry = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append("  [").Append(context).Append("]  ")
                .AppendLine(ex?.ToString() ?? "(no exception)")
                .AppendLine();

            lock (Sync)
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Trim(path);
                File.AppendAllText(path, entry.ToString());
            }
        }
        catch
        {
            // No disk, no permission, no profile — there is nowhere else to report this to.
        }

        // Outside the write: the pill should light up even if the log itself couldn't be saved,
        // and a subscriber that throws must not become a second failure on top of the first.
        Interlocked.Increment(ref _count);
        try
        {
            Written?.Invoke();
        }
        catch
        {
        }
    }

    private static void Trim(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < MaxBytes)
                return;

            var lines = File.ReadAllLines(path);
            File.WriteAllLines(path, lines.Skip(lines.Length / 2));
        }
        catch
        {
            // Locked or unreadable — appending to an oversized log still beats losing the entry.
        }
    }
}
