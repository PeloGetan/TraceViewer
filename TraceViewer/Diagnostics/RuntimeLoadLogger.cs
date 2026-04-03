using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace TraceViewer.Diagnostics;

internal static class RuntimeLoadLogger
{
    private const string RuntimeLogEnvVar = "TRACEVIEWER_RUNTIME_LOG";
    private static readonly object SyncRoot = new();

    private static long _sessionStartTimestamp;
    private static int _sessionId;
    private static string? _sessionTracePath;

    public static bool IsEnabled => !string.IsNullOrWhiteSpace(ResolveLogPath());

    public static void BeginSession(string tracePath, string source)
    {
        var logPath = ResolveLogPath();
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        lock (SyncRoot)
        {
            _sessionId++;
            _sessionStartTimestamp = Stopwatch.GetTimestamp();
            _sessionTracePath = tracePath;

            AppendLine(
                logPath,
                BuildLine(
                    "session-begin",
                    0.0,
                    source,
                    $"session={_sessionId};pid={Environment.ProcessId};trace={tracePath}"));
        }
    }

    public static void EnsureSession(string tracePath, string source)
    {
        var logPath = ResolveLogPath();
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_sessionStartTimestamp != 0 &&
                string.Equals(_sessionTracePath, tracePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _sessionId++;
            _sessionStartTimestamp = Stopwatch.GetTimestamp();
            _sessionTracePath = tracePath;

            AppendLine(
                logPath,
                BuildLine(
                    "session-begin",
                    0.0,
                    source,
                    $"session={_sessionId};pid={Environment.ProcessId};trace={tracePath}"));
        }
    }

    public static void Log(string eventName, string? detail = null)
    {
        var logPath = ResolveLogPath();
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        lock (SyncRoot)
        {
            var elapsedMilliseconds = _sessionStartTimestamp == 0
                ? 0.0
                : Stopwatch.GetElapsedTime(_sessionStartTimestamp).TotalMilliseconds;

            AppendLine(logPath, BuildLine(eventName, elapsedMilliseconds, null, detail));
        }
    }

    private static string? ResolveLogPath()
    {
        var rawValue = Environment.GetEnvironmentVariable(RuntimeLogEnvVar);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        if (string.Equals(rawValue, "1", StringComparison.Ordinal))
        {
            return Path.Combine(Path.GetTempPath(), "TraceViewerRuntimeLoad.log");
        }

        return rawValue;
    }

    private static string BuildLine(string eventName, double elapsedMilliseconds, string? source, string? detail)
    {
        return string.Join(
            "\t",
            DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            $"elapsedMs={elapsedMilliseconds:F3}",
            $"event={Sanitize(eventName)}",
            $"source={Sanitize(source)}",
            $"detail={Sanitize(detail)}");
    }

    private static void AppendLine(string logPath, string line)
    {
        var directory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.AppendAllText(logPath, line + Environment.NewLine);
    }

    private static string Sanitize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }
}
