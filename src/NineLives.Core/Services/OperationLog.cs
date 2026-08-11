using System.IO;
using System.Text;

namespace Blackcat.NineLives.Services;

/// <summary>
/// A small append-only log on disk (#40).
///
/// The app had no logging of any kind, which for a tool that executes restores against production
/// databases means a bug report can contain nothing but a screenshot, and a server-state change -
/// creating or altering a credential - leaves no trace whatsoever.
///
/// Hand-rolled rather than a logging framework: the requirement is a few lines of text per
/// operation, and a self-contained single-file exe should not grow a dependency tree for that.
///
/// Two rules it must never break:
///   - it never throws, because a logging failure must not break a restore
///   - everything goes through LogRedactor, so a new call site cannot leak a token by forgetting
/// </summary>
public sealed class OperationLog
{
    /// <summary>What retention is when nobody has said otherwise.</summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>The floor. A log the app deletes the moment it writes it is not a log.</summary>
    public const int MinimumRetentionDays = 1;

    private int _retentionDays = DefaultRetentionDays;

    /// <summary>
    /// Files older than this are removed by <see cref="Prune"/>. Settable from the Settings screen
    /// (#117 item 2), and clamped: the value comes from config.json, which is hand-editable, and a
    /// 0 there would delete today's file - the one recording the restore currently running.
    /// </summary>
    public int RetentionDays
    {
        get => _retentionDays;
        set => _retentionDays = Math.Max(MinimumRetentionDays, value);
    }

    /// <summary>A single day's file is rolled once it passes this, so one runaway loop cannot fill the disk.</summary>
    private const long MaxFileBytes = 5 * 1024 * 1024;

    private readonly string _directory;
    // Plain object rather than System.Threading.Lock, which is .NET 9+. Swap it when the .NET 10
    // retarget lands (#34).
    private readonly object _gate = new();

    public OperationLog() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NineLives", "logs"))
    { }

    /// <summary>Lets the tests write somewhere disposable instead of the real profile.</summary>
    internal OperationLog(string directory) => _directory = directory;

    public string Directory => _directory;

    /// <summary>Today's log file. The name is stable within a day so it is easy to find and attach.</summary>
    public string CurrentFile =>
        Path.Combine(_directory, $"ninelives-{DateTime.Now:yyyyMMdd}.log");

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message) => Write("ERROR", message);

    public void Error(string message, Exception ex)
        => Write("ERROR", $"{message} | {ex.GetType().Name}: {ex.Message}");

    /// <summary>
    /// Records something the app changed on a server, as opposed to something it read. These are
    /// the lines that matter when someone asks why a credential on their instance changed.
    /// </summary>
    public void ServerChange(string serverName, string what)
        => Write("CHANGE", $"[{serverName}] {what}");

    private string? _ensuredDirectory;
    private int _writesSinceRollCheck;

    private void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {LogRedactor.Redact(message)}";

            lock (_gate)
            {
                // Creating the directory and stat-ing the file on EVERY line meant three
                // filesystem calls per message. A restore reporting progress every few percent
                // does that hundreds of times from the UI thread, which is a real part of why the
                // console stuttered. The directory is created once, and the size is only checked
                // periodically - a log overshooting the roll threshold by a few hundred lines
                // costs nothing, and the check is not free.
                if (_ensuredDirectory != _directory)
                {
                    System.IO.Directory.CreateDirectory(_directory);
                    _ensuredDirectory = _directory;
                }

                if (_writesSinceRollCheck++ % 200 == 0) RollIfTooBig();

                File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging is never worth failing an operation over. A locked file, a full disk or a
            // redirected profile all end up here and are all survivable. Forget the cached
            // directory so a transient failure is retried rather than assumed permanent.
            _ensuredDirectory = null;
        }
    }

    private void RollIfTooBig()
    {
        var current = CurrentFile;
        if (!File.Exists(current) || new FileInfo(current).Length < MaxFileBytes) return;

        for (var i = 1; i < 100; i++)
        {
            var rolled = Path.ChangeExtension(current, $".{i}.log");
            if (File.Exists(rolled)) continue;
            File.Move(current, rolled);
            return;
        }
    }

    /// <summary>
    /// Drops files past the retention window. Called once at startup rather than on every write -
    /// enumerating a directory to append one line would be silly.
    /// </summary>
    /// <summary>
    /// How many log files a given retention would delete, without deleting any of them (#370).
    ///
    /// Shortening the retention destroys evidence: the screen that offers the setting says in
    /// its own words that a restore's record lives in these files and a change ticket may need
    /// it later. Asking first is only reasonable if the question can say what is at stake, and
    /// asking at all is only reasonable when something actually goes.
    /// </summary>
    public int CountPrunable(int retentionDays)
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory)) return 0;

            var cutoff = DateTime.Now.AddDays(-Math.Max(1, retentionDays));
            return System.IO.Directory
                .EnumerateFiles(_directory, "ninelives-*.log")
                .Count(f => File.GetLastWriteTime(f) < cutoff);
        }
        catch
        {
            // Counting must not be able to fail the thing that asked.
            return 0;
        }
    }

    public void Prune()
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory)) return;

            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "ninelives-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // Same rule: never worth failing over.
        }
    }
}
