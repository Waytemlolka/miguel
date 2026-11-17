using System.Text;

namespace LobbyLens.Core;

/// <summary>
/// Follows a growing text file (Dota's console.log). Reads the existing content
/// first (replay=true) so the current lobby is known even if the app started late,
/// then follows new lines. Handles the file being deleted/recreated/truncated
/// (Dota recreates console.log on every launch).
/// </summary>
public sealed class LogTailer : IDisposable
{
    readonly Func<string?> _pathProvider;
    readonly CancellationTokenSource _cts = new();
    Task? _task;

    /// <summary>(line, isReplay)</summary>
    public event Action<string, bool>? Line;
    /// <summary>A new/recreated file was opened (path). Consumers should reset state.</summary>
    public event Action<string>? FileOpened;
    /// <summary>File missing or unreadable (message).</summary>
    public event Action<string>? Problem;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    public LogTailer(Func<string?> pathProvider) => _pathProvider = pathProvider;

    public void Start() => _task ??= Task.Run(() => Loop(_cts.Token));

    async Task Loop(CancellationToken ct)
    {
        string? lastProblem = null;
        void Report(string msg)
        {
            if (msg == lastProblem) return;
            lastProblem = msg;
            Problem?.Invoke(msg);
        }

        while (!ct.IsCancellationRequested)
        {
            var path = _pathProvider();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Report(string.IsNullOrEmpty(path) ? "Путь к console.log не задан" : "console.log не найден: " + path);
                await Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            FileStream fs;
            DateTime created;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                created = File.GetCreationTimeUtc(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report("Не удаётся открыть console.log: " + ex.Message);
                await Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            lastProblem = null;
            var head = ReadHead(fs);
            FileOpened?.Invoke(path);
            using (fs)
            {
                var pending = new StringBuilder();
                var buf = new byte[64 * 1024];
                var decoder = Encoding.UTF8.GetDecoder();
                var chars = new char[Encoding.UTF8.GetMaxCharCount(buf.Length)];
                bool replay = true;

                while (!ct.IsCancellationRequested)
                {
                    int n;
                    try { n = fs.Read(buf, 0, buf.Length); }
                    catch (IOException) { break; }

                    if (n > 0)
                    {
                        int cn = decoder.GetChars(buf, 0, n, chars, 0);
                        for (int i = 0; i < cn; i++)
                        {
                            char ch = chars[i];
                            if (ch == '\n')
                            {
                                var line = pending.ToString().TrimEnd('\r');
                                pending.Clear();
                                if (line.Length > 0) Line?.Invoke(line, replay);
                            }
                            else pending.Append(ch);
                        }
                        continue; // keep reading while there is data
                    }

                    replay = false;
                    await Delay(PollInterval, ct);

                    // Recreated or truncated?
                    try
                    {
                        if (!File.Exists(path)) break;
                        var len = new FileInfo(path).Length;
                        if (len < fs.Position) break;
                        if (OperatingSystem.IsWindows() && File.GetCreationTimeUtc(path) != created) break;
                        if (!HeadMatches(path, head)) break;
                    }
                    catch (IOException) { break; }
                    catch (UnauthorizedAccessException) { break; }
                    if (_pathProvider() != path) break;
                }
            }
        }
    }

    const int HeadSize = 512;

    /// <summary>First bytes of the file: if they change, the file was recreated.</summary>
    static byte[] ReadHead(FileStream fs)
    {
        var buf = new byte[HeadSize];
        int n = fs.Read(buf, 0, buf.Length);
        fs.Position = 0;
        return buf[..n];
    }

    static bool HeadMatches(string path, byte[] head)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[head.Length];
        int n = f.Read(buf, 0, buf.Length);
        // The head may have been shorter than HeadSize at open time; compare what we had.
        return n >= head.Length && buf.AsSpan(0, head.Length).SequenceEqual(head);
    }

    static async Task Delay(TimeSpan t, CancellationToken ct)
    {
        try { await Task.Delay(t, ct); } catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _task?.Wait(2000); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
