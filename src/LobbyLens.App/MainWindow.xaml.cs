using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LobbyLens.Core;
using Microsoft.Win32;

namespace LobbyLens.App;

public partial class MainWindow : Window
{
    readonly Settings _settings = Settings.Load();
    readonly object _lock = new();
    readonly LobbyTracker _tracker = new();
    readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly OverlayWindow _overlay = new();
    readonly SessionRecorder? _recorder;

    LogTailer? _tailer;
    GroupClient? _group;
    string? _resolvedPath;
    DateTime _resolvedAt;
    string? _logProblem;
    bool _logOpen;
    TrackerSnapshot _lastPublished = TrackerSnapshot.Empty;
    DateTime _lastPublishAt;
    TrackerSnapshot _lastShown = TrackerSnapshot.Empty;

    static readonly string SessionsDir = Path.Combine(Settings.Dir, "sessions");

    public MainWindow()
    {
        InitializeComponent();
        try { _recorder = new SessionRecorder(SessionsDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _recorder = null; }

        NameBox.Text = _settings.Name;
        GroupCodeBox.Text = _settings.GroupCode ?? "";
        OverlayAlways.IsChecked = _settings.OverlayAlways;

        _uiTimer.Tick += (_, _) => Tick();
        Loaded += (_, _) => Start();
        StateChanged += (_, _) => UpdateOverlayVisibility();
        Closing += async (_, _) => await Shutdown();
    }

    // ───────────────────────── log ─────────────────────────

    void Start()
    {
        _tailer = new LogTailer(ResolvePath);
        _tailer.FileOpened += path =>
        {
            lock (_lock) { _tracker.Reset(); }
            _logOpen = true;
            _logProblem = null;
            _recorder?.Write("file_opened", new { path });
            AddEvent("Открыт " + path);
        };
        _tailer.Problem += msg =>
        {
            _logOpen = false;
            _logProblem = msg;
        };
        _tailer.Line += OnLine;
        _tailer.Start();
        _uiTimer.Start();

        if (GroupKeys.FromCode(_settings.GroupCode) is { } keys) JoinGroup(keys);
        UpdateOverlayVisibility();
    }

    string? ResolvePath()
    {
        if (!string.IsNullOrWhiteSpace(_settings.LogPath)) return _settings.LogPath;
        // Re-detect every few seconds while not found (Dota may start later).
        if (_resolvedPath is not null && File.Exists(_resolvedPath)) return _resolvedPath;
        if (DateTime.UtcNow - _resolvedAt < TimeSpan.FromSeconds(3)) return _resolvedPath;
        _resolvedAt = DateTime.UtcNow;
        var r = DotaLocator.Find(Settings.SteamRoots());
        _resolvedPath = r.LogPath;
        if (r.DotaDir is null) _logProblem = "Dota 2 не найдена в библиотеках Steam. Укажи console.log вручную (кнопка «Выбрать…»).";
        else if (!r.LogExists) _logProblem = "Dota найдена, но console.log нет. Добавь параметры запуска -console -condebug и запусти Dota.";
        return _resolvedPath;
    }

    void OnLine(string line, bool replay)
    {
        var e = LogParser.Parse(line);
        if (e is null) return;
        bool changed;
        lock (_lock) { changed = _tracker.Feed(e, DateTimeOffset.Now); }
        if (replay) return;
        _recorder?.Write("event", new { kind = e.Kind.ToString(), lobby = e.LobbyId?.ToString(), size = e.Size, match = e.MatchId?.ToString(), mm = e.Matchmaking, line });
        if (changed) AddEvent(Describe(e));
    }

    static string Describe(LogEvent e) => e.Kind switch
    {
        LogEventKind.LobbySubscribed => $"Лобби {e.LobbyId} (подписка)",
        LogEventKind.LobbyUnsubscribed => $"Лобби {e.LobbyId} закрыто",
        LogEventKind.ReadyUpStatus => $"7170 ReadyUpStatus, {e.Size} байт",
        LogEventKind.ReadyUpSent => "Ты нажал «Принять» (7070)",
        LogEventKind.LobbyRun => $"LOBBY STATE RUN: лобби {e.LobbyId}, матч {e.MatchId}",
        LogEventKind.MatchmakingFlag => e.Matchmaking == true ? "Подбор (mm=1)" : "Не подбор (mm=0)",
        LogEventKind.CustomMarker => "Кастомное/практическое лобби",
        _ => e.Kind.ToString(),
    };

    void AddEvent(string text)
    {
        Dispatcher.BeginInvoke(() =>
        {
            EventsList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {text}");
            while (EventsList.Items.Count > 300) EventsList.Items.RemoveAt(EventsList.Items.Count - 1);
        });
    }

    // ───────────────────────── UI tick ─────────────────────────

    void Tick()
    {
        TrackerSnapshot s;
        lock (_lock) { s = _tracker.Snapshot(); }

        LogPathBox.Text = ResolvePathForDisplay();
        LogStatus.Text = _logOpen ? "" : _logProblem ?? "";

        if (s != _lastShown)
        {
            _lastShown = s;
            ShowSnapshot(s);
        }

        // Group: publish on change + heartbeat.
        if (_group is not null && (s != _lastPublished || DateTime.UtcNow - _lastPublishAt > TimeSpan.FromSeconds(15)))
        {
            _lastPublished = s;
            _lastPublishAt = DateTime.UtcNow;
            _ = _group.PublishAsync(MyState(s));
        }

        UpdateGroupView(s);
    }

    string ResolvePathForDisplay() => !string.IsNullOrWhiteSpace(_settings.LogPath) ? _settings.LogPath! + "  (вручную)" : _resolvedPath ?? "не найден";

    static string PhaseText(Phase p) => p switch
    {
        Phase.Idle => "Нет найденного матча",
        Phase.InLobby => "В лобби",
        Phase.ReadyCheck => "МАТЧ НАЙДЕН — ждёт принятия",
        Phase.Accepted => "Матч принят",
        Phase.InGame => "Загрузка / игра",
        _ => p.ToString(),
    };

    static string Short(ulong id)
    {
        var s = id.ToString();
        return s.Length <= 6 ? s : s[^6..^3] + " " + s[^3..];
    }

    void ShowSnapshot(TrackerSnapshot s)
    {
        PhaseLabel.Text = PhaseText(s.Phase) + (s.IsCustom ? "  (кастом/практика)" : "");
        if (s.LobbyId is { } id)
        {
            LobbyIdBox.Text = id.ToString();
            ShortCode.Text = Short(id);
        }
        else
        {
            var unknown = s.Phase is Phase.ReadyCheck or Phase.Accepted ? "Неизвестно" : "—";
            LobbyIdBox.Text = unknown;
            ShortCode.Text = unknown;
        }

        var parts = new List<string>();
        if (s.LobbyId is not null)
            parts.Add(s.LobbySource == "subscribe" ? "ID из подписки на лобби" : "ID из LOBBY STATE RUN (уже после принятия)");
        parts.Add("ID до принятия: " + (s.LobbyIdBeforeAccept switch
        {
            true => "ДА",
            false => "НЕТ",
            null => s.Phase == Phase.ReadyCheck && s.LobbyId is not null ? "да (ты ещё не принял)" : "—",
        }));
        if (s.ReadyStatusCount > 0) parts.Add($"сообщений 7170: {s.ReadyStatusCount} (последнее {s.LastReadyStatusSize} байт)");
        if (s.Matchmaking is not null) parts.Add(s.Matchmaking == true ? "подбор Valve (mm=1)" : "mm=0");
        if (s.MatchId is not null) parts.Add($"матч {s.MatchId}");
        DetailsText.Text = string.Join("  •  ", parts);
    }

    MemberState MyState(TrackerSnapshot s) => new()
    {
        MemberId = _settings.MemberId,
        Name = string.IsNullOrWhiteSpace(_settings.Name) ? "Игрок" : _settings.Name,
        Phase = s.Phase,
        LobbyTag = s.LobbyId is { } id && _group is not null ? _group.Keys.LobbyTag(id) : null,
        LobbyIdBeforeAccept = s.LobbyIdBeforeAccept,
        UnixMs = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
    };

    void UpdateGroupView(TrackerSnapshot s)
    {
        string line1 = s.LobbyId is { } id ? "Лобби " + Short(id) : s.Phase is Phase.ReadyCheck or Phase.Accepted ? "Лобби: Неизвестно" : "LobbyLens: жду матч";
        string line2 = s.Phase == Phase.ReadyCheck ? "Матч найден" : "";
        var accent = s.LobbyId is not null ? Colors.White : s.Phase is Phase.ReadyCheck or Phase.Accepted ? Colors.Orange : Colors.Gray;

        if (_group is null)
        {
            MembersList.ItemsSource = null;
            MatchHeadline.Text = "";
        }
        else
        {
            var me = MyState(s);
            var r = MatchEvaluator.Evaluate(me, _group.Members, DateTimeOffset.Now);
            MatchHeadline.Text = r.Headline;
            MembersList.ItemsSource = r.Members.Select(m => $"{m.State.Name}: {Relation(m)}").ToList();
            line2 = r.Headline;
            if (r.Members.Any(m => m.Relation == MemberRelation.Same)) accent = Colors.LimeGreen;
            else if (r.Headline == "Неизвестно") accent = Colors.Orange;
            GroupStatus.Text = $"Группа {_group.Keys.Code} • {(_group.Connected ? "подключено" : "нет связи")} • участников: {r.Members.Count + 1}";
        }
        _overlay.SetText(line1, line2, accent);
    }

    static string Relation(MemberView m) => m.Relation switch
    {
        MemberRelation.Same => "ТОТ ЖЕ МАТЧ",
        MemberRelation.Different => "другой матч",
        MemberRelation.Idle => "нет матча",
        _ => m.Stale ? "нет связи" : "неизвестно",
    } + $"   [{PhaseText(m.State.Phase)}]";

    // ───────────────────────── group ─────────────────────────

    void JoinGroup(GroupKeys keys)
    {
        _ = LeaveGroupAsync();
        _settings.GroupCode = keys.Code;
        _settings.Save();
        GroupCodeBox.Text = keys.Code;
        var g = new GroupClient(keys, _settings.MemberId, _settings.BrokerHost, _settings.BrokerPort);
        g.Status += msg => Dispatcher.BeginInvoke(() => GroupStatus.Text = msg);
        _group = g;
        _lastPublishAt = DateTime.MinValue;
        g.Start();
    }

    async Task LeaveGroupAsync()
    {
        var g = _group;
        _group = null;
        if (g is not null) await g.DisposeAsync();
    }

    void OnCreateGroup(object sender, RoutedEventArgs e) => JoinGroup(GroupKeys.FromCode(GroupKeys.NewCode())!);

    void OnJoinGroup(object sender, RoutedEventArgs e)
    {
        var keys = GroupKeys.FromCode(GroupCodeBox.Text);
        if (keys is null)
        {
            MessageBox.Show("Код группы — 10 символов, например ABCDE-FGH23.", "LobbyLens");
            return;
        }
        JoinGroup(keys);
    }

    async void OnLeaveGroup(object sender, RoutedEventArgs e)
    {
        _settings.GroupCode = null;
        _settings.Save();
        await LeaveGroupAsync();
        GroupStatus.Text = "Не в группе";
    }

    void OnCopyCode(object sender, RoutedEventArgs e)
    {
        if (_group is not null) TrySetClipboard(_group.Keys.Code);
    }

    void OnNameChanged(object sender, RoutedEventArgs e)
    {
        _settings.Name = NameBox.Text.Trim();
        _settings.Save();
        _lastPublishAt = DateTime.MinValue;
    }

    // ───────────────────────── misc buttons ─────────────────────────

    void OnCopyLobby(object sender, RoutedEventArgs e)
    {
        if (ulong.TryParse(LobbyIdBox.Text, out _)) TrySetClipboard(LobbyIdBox.Text);
    }

    static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    void OnBrowsePath(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "console.log|console.log|Все файлы|*.*", Title = "Где лежит dota 2 beta\\game\\dota\\console.log" };
        if (dlg.ShowDialog(this) == true)
        {
            _settings.LogPath = dlg.FileName;
            _settings.Save();
        }
    }

    void OnAutoPath(object sender, RoutedEventArgs e)
    {
        _settings.LogPath = null;
        _settings.Save();
        _resolvedPath = null;
        _resolvedAt = DateTime.MinValue;
    }

    void OnOverlayToggle(object sender, RoutedEventArgs e)
    {
        _settings.OverlayAlways = OverlayAlways.IsChecked == true;
        _settings.Save();
        UpdateOverlayVisibility();
    }

    void UpdateOverlayVisibility()
    {
        bool show = _settings.OverlayAlways || WindowState == WindowState.Minimized;
        if (show && !_overlay.IsVisible) _overlay.Show();
        else if (!show && _overlay.IsVisible) _overlay.Hide();
    }

    void OnAnalyzeFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Логи|*.log;*.txt|Все файлы|*.*", Title = "Файл console.log для проверки" };
        if (dlg.ShowDialog(this) == true) Analyze(dlg.FileName);
    }

    void OnAnalyzeCurrent(object sender, RoutedEventArgs e)
    {
        var p = ResolvePath();
        if (p is null || !File.Exists(p)) MessageBox.Show("console.log не найден.", "LobbyLens");
        else Analyze(p);
    }

    void Analyze(string path)
    {
        string report;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var lines = new List<string>();
            while (sr.ReadLine() is { } l) lines.Add(l);
            report = LogAnalyzer.Report(LogAnalyzer.Run(lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report = "Не удалось прочитать файл: " + ex.Message;
        }
        var box = new System.Windows.Controls.TextBox
        {
            Text = path + "\n\n" + report,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
        };
        new Window { Title = "Проверка лога — LobbyLens", Width = 760, Height = 600, Content = box, Owner = this }.Show();
    }

    void OnOpenSessions(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SessionsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", SessionsDir) { UseShellExecute = true });
    }

    async Task Shutdown()
    {
        _uiTimer.Stop();
        _overlay.Close();
        _tailer?.Dispose();
        await LeaveGroupAsync();
        _recorder?.Dispose();
    }
}
