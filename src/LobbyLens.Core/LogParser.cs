using System.Text.RegularExpressions;

namespace LobbyLens.Core;

public enum LogEventKind
{
    LobbySubscribed,
    LobbyUnsubscribed,
    ReadyUpStatus,
    ReadyUpSent,
    LobbyRun,
    MatchmakingFlag,
    CustomMarker,
}

/// <summary>A console.log line that matters for lobby detection.</summary>
public sealed record LogEvent(LogEventKind Kind, ulong? LobbyId = null, int? Size = null, ulong? MatchId = null, bool? Matchmaking = null);

/// <summary>
/// Parses Dota 2 console.log lines (launch options: -console -condebug).
/// Known formats (old and new clients, optional "MM/DD HH:MM:SS " prefix):
///   [SOCache] CGCSOCacheSubscribedJob(owner=&lt;Lobby:26694969711146938&gt;) [in cache]
///   [SOCache] CGCSOCacheUnsubscribedJob(owner=Lobby:28646322118781118)
///   [GCClient] Recv msg 7170 (k_EMsgGCReadyUpStatus), 22 bytes
///   [GCClient] Send msg 7070 (k_EMsgGCReadyUp), 19 bytes
///   [GCClient] Sending 7070 (k_EMsgGCReadyUp) 19 bytes
///   LOBBY STATE RUN: lobby 2669...: Player AccountID 9 connecting to MatchID 5451564520 at ServerID ...
///   [SteamNetSockets] Received Steam datagram ticket for server ... match_id=4083586116 mm=1 flg=
/// </summary>
public static class LogParser
{
    static readonly Regex Subscribed = new(@"CGCSOCacheSubscribedJob\(owner=<?Lobby:(\d+)>?\)", RegexOptions.Compiled);
    static readonly Regex Unsubscribed = new(@"CGCSOCacheUnsubscribedJob\(owner=<?Lobby:(\d+)>?\)", RegexOptions.Compiled);
    static readonly Regex ReadyStatus = new(@"Recv msg 7170 \(k_EMsgGCReadyUpStatus\),?\s*(\d+) bytes", RegexOptions.Compiled);
    static readonly Regex ReadySent = new(@"(?:Send msg|Sending) 7070 \(k_EMsgGCReadyUp\)", RegexOptions.Compiled);
    static readonly Regex Run = new(@"LOBBY STATE RUN: lobby (\d+).*?MatchID (\d+)", RegexOptions.Compiled);
    static readonly Regex Mm = new(@"match_id=\d+ mm=(\d)", RegexOptions.Compiled);
    static readonly Regex Custom = new(@"k_EMsgGCQuickJoinCustomLobby\b|k_EMsgGCPracticeLobby(?:Join|Create|Launch)\b|k_EMsgGCPracticeLobbyJoinResponse", RegexOptions.Compiled);

    public static LogEvent? Parse(string line)
    {
        if (line.Length < 10) return null;
        Match m;
        if (line.Contains("Lobby:", StringComparison.Ordinal))
        {
            if ((m = Subscribed.Match(line)).Success && ulong.TryParse(m.Groups[1].Value, out var s))
                return new LogEvent(LogEventKind.LobbySubscribed, LobbyId: s);
            if ((m = Unsubscribed.Match(line)).Success && ulong.TryParse(m.Groups[1].Value, out var u))
                return new LogEvent(LogEventKind.LobbyUnsubscribed, LobbyId: u);
        }
        if (line.Contains("7170", StringComparison.Ordinal) && (m = ReadyStatus.Match(line)).Success)
            return new LogEvent(LogEventKind.ReadyUpStatus, Size: int.Parse(m.Groups[1].Value));
        if (line.Contains("7070", StringComparison.Ordinal) && ReadySent.IsMatch(line))
            return new LogEvent(LogEventKind.ReadyUpSent);
        if (line.Contains("LOBBY STATE RUN", StringComparison.Ordinal) && (m = Run.Match(line)).Success
            && ulong.TryParse(m.Groups[1].Value, out var rl) && ulong.TryParse(m.Groups[2].Value, out var mid))
            return new LogEvent(LogEventKind.LobbyRun, LobbyId: rl, MatchId: mid);
        if (line.Contains(" mm=", StringComparison.Ordinal) && (m = Mm.Match(line)).Success)
            return new LogEvent(LogEventKind.MatchmakingFlag, Matchmaking: m.Groups[1].Value == "1");
        if (line.Contains("Lobby", StringComparison.Ordinal) && Custom.IsMatch(line))
            return new LogEvent(LogEventKind.CustomMarker);
        return null;
    }
}
