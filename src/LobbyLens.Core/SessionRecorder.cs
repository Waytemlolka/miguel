using System.Text.Json;

namespace LobbyLens.Core;

/// <summary>
/// Writes every relevant console.log line with a precise local timestamp to a JSONL
/// file, so experiment results can be shared (lobby IDs stay on this PC unless sent).
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    readonly StreamWriter _w;
    readonly object _lock = new();
    public string FilePath { get; }

    public SessionRecorder(string dir)
    {
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
        _w = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public void Write(string type, object data)
    {
        var line = JsonSerializer.Serialize(new { t = DateTimeOffset.Now.ToString("O"), type, data });
        lock (_lock) _w.WriteLine(line);
    }

    public void Dispose()
    {
        lock (_lock) _w.Dispose();
    }
}
