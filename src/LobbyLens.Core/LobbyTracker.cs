namespace LobbyLens.Core;

public enum Phase
{
    /// <summary>Not in a lobby / nothing known.</summary>
    Idle,
    /// <summary>Subscribed to a lobby, no ready check seen (custom/practice lobby before launch).</summary>
    InLobby,
    /// <summary>Match found, ready check running, we have NOT accepted yet.</summary>
    ReadyCheck,
    /// <summary>We sent "Accept" (k_EMsgGCReadyUp).</summary>
    Accepted,
    /// <summary>Connecting to / playing on the game server.</summary>
    InGame,
}

/// <summary>One lobby lifetime as seen in console.log.</summary>
public sealed class LobbyCycle
{
    public ulong? LobbyId { get; internal set; }
    /// <summary>"subscribe" = from SOCache subscription line, "run" = learned only from LOBBY STATE RUN.</summary>
    public string? LobbySource { get; internal set; }
    public long? SubscribedSeq { get; internal set; }
    public DateTimeOffset? SubscribedAt { get; internal set; }
    public DateTimeOffset StartedAt { get; internal set; }
    public List<(DateTimeOffset At, long Seq, int Size)> ReadyStatuses { get; } = new();
    public long? AcceptSeq { get; internal set; }
    public DateTimeOffset? AcceptAt { get; internal set; }
    public ulong? RunLobbyId { get; internal set; }
    public ulong? MatchId { get; internal set; }
    public DateTimeOffset? RunAt { get; internal set; }
    public bool? Matchmaking { get; internal set; }
    public bool CustomMarkers { get; internal set; }
    public DateTimeOffset? EndedAt { get; internal set; }
    public Phase Phase { get; internal set; } = Phase.Idle;

    /// <summary>
    /// true  = lobby ID was in the log before we pressed Accept (the thing we need);
    /// false = lobby ID appeared only after Accept;
    /// null  = we have not accepted yet / unknown.
    /// </summary>
    public bool? LobbyIdBeforeAccept
    {
        get
        {
            if (AcceptSeq is null) return null;
            if (SubscribedSeq is null) return false;
            return SubscribedSeq < AcceptSeq;
        }
    }

    public bool RunMatchesSubscribed => RunLobbyId is not null && LobbyId == RunLobbyId;
}

public sealed record TrackerSnapshot(
    Phase Phase,
    ulong? LobbyId,
    string? LobbySource,
    DateTimeOffset? LobbyIdAt,
    bool? LobbyIdBeforeAccept,
    int ReadyStatusCount,
    int? LastReadyStatusSize,
    bool IsCustom,
    bool? Matchmaking,
    ulong? MatchId)
{
    public static readonly TrackerSnapshot Empty = new(Phase.Idle, null, null, null, null, 0, null, false, null, null);
}

/// <summary>
/// State machine over parsed console.log events. Not thread-safe: feed from one thread.
/// </summary>
public sealed class LobbyTracker
{
    static readonly TimeSpan CustomMarkerWindow = TimeSpan.FromSeconds(60);

    long _seq;
    DateTimeOffset? _lastCustomMarker;
    readonly List<LobbyCycle> _history = new();

    public LobbyCycle? Current { get; private set; }
    public IReadOnlyList<LobbyCycle> History => _history;

    public void Reset()
    {
        if (Current is not null) Close(DateTimeOffset.Now);
        _lastCustomMarker = null;
    }

    /// <summary>Returns true if the snapshot may have changed.</summary>
    public bool Feed(LogEvent e, DateTimeOffset at)
    {
        var seq = ++_seq;
        switch (e.Kind)
        {
            case LogEventKind.LobbySubscribed:
            {
                var id = e.LobbyId!.Value;
                if (Current?.LobbyId == id) return false;
                // A ready check may start before the lobby subscription line: attach to it.
                if (Current is { LobbyId: null, AcceptSeq: null } c && c.ReadyStatuses.Count > 0)
                {
                    c.LobbyId = id;
                    c.LobbySource = "subscribe";
                    c.SubscribedSeq = seq;
                    c.SubscribedAt = at;
                    return true;
                }
                if (Current is not null) Close(at);
                var cycle = NewCycle(at);
                cycle.LobbyId = id;
                cycle.LobbySource = "subscribe";
                cycle.SubscribedSeq = seq;
                cycle.SubscribedAt = at;
                cycle.Phase = Phase.InLobby;
                return true;
            }
            case LogEventKind.LobbyUnsubscribed:
            {
                if (Current is null) return false;
                if (Current.LobbyId == e.LobbyId || Current.RunLobbyId == e.LobbyId)
                {
                    Close(at);
                    return true;
                }
                return false;
            }
            case LogEventKind.ReadyUpStatus:
            {
                var c = Current ?? NewCycle(at);
                c.ReadyStatuses.Add((at, seq, e.Size ?? 0));
                if (c.Phase < Phase.ReadyCheck) c.Phase = Phase.ReadyCheck;
                return true;
            }
            case LogEventKind.ReadyUpSent:
            {
                var c = Current ?? NewCycle(at);
                if (c.AcceptSeq is null)
                {
                    c.AcceptSeq = seq;
                    c.AcceptAt = at;
                }
                if (c.Phase < Phase.Accepted) c.Phase = Phase.Accepted;
                return true;
            }
            case LogEventKind.LobbyRun:
            {
                var c = Current;
                if (c is null || (c.LobbyId is not null && c.LobbyId != e.LobbyId))
                {
                    if (c is not null) Close(at);
                    c = NewCycle(at);
                }
                c.RunLobbyId = e.LobbyId;
                c.MatchId = e.MatchId;
                c.RunAt = at;
                if (c.LobbyId is null)
                {
                    c.LobbyId = e.LobbyId;
                    c.LobbySource = "run";
                }
                c.Phase = Phase.InGame;
                return true;
            }
            case LogEventKind.MatchmakingFlag:
            {
                if (Current is null) return false;
                Current.Matchmaking = e.Matchmaking;
                return true;
            }
            case LogEventKind.CustomMarker:
            {
                _lastCustomMarker = at;
                if (Current is not null && Current.Phase != Phase.InGame) Current.CustomMarkers = true;
                return true;
            }
        }
        return false;
    }

    LobbyCycle NewCycle(DateTimeOffset at)
    {
        var c = new LobbyCycle { StartedAt = at, Phase = Phase.Idle };
        if (_lastCustomMarker is { } t && at - t <= CustomMarkerWindow) c.CustomMarkers = true;
        Current = c;
        return c;
    }

    void Close(DateTimeOffset at)
    {
        if (Current is null) return;
        Current.EndedAt = at;
        _history.Add(Current);
        Current = null;
    }

    public TrackerSnapshot Snapshot()
    {
        var c = Current;
        if (c is null) return TrackerSnapshot.Empty;
        return new TrackerSnapshot(
            c.Phase,
            c.LobbyId,
            c.LobbySource,
            c.LobbySource == "run" ? c.RunAt : c.SubscribedAt,
            c.LobbyIdBeforeAccept,
            c.ReadyStatuses.Count,
            c.ReadyStatuses.Count > 0 ? c.ReadyStatuses[^1].Size : null,
            c.CustomMarkers,
            c.Matchmaking,
            c.MatchId);
    }

    /// <summary>All cycles: finished ones plus the current one.</summary>
    public IEnumerable<LobbyCycle> AllCycles()
    {
        foreach (var c in _history) yield return c;
        if (Current is not null) yield return Current;
    }
}
