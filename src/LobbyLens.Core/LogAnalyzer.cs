using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LobbyLens.Core;

/// <summary>
/// Offline check of a whole console.log: for every lobby, was its ID in the log
/// before "Accept" was pressed? This is the experiment the project depends on.
/// </summary>
public static class LogAnalyzer
{
    static readonly Regex TimePrefix = new(@"^(\d\d)/(\d\d) (\d\d):(\d\d):(\d\d)", RegexOptions.Compiled);

    public static LobbyTracker Run(IEnumerable<string> lines)
    {
        var tracker = new LobbyTracker();
        var baseTime = DateTimeOffset.Now;
        long n = 0;
        foreach (var line in lines)
        {
            n++;
            var e = LogParser.Parse(line);
            if (e is null) continue;
            tracker.Feed(e, LineTime(line) ?? baseTime.AddMilliseconds(n));
        }
        return tracker;
    }

    /// <summary>Uses the "MM/DD HH:MM:SS" prefix newer clients write, if present.</summary>
    public static DateTimeOffset? LineTime(string line)
    {
        var m = TimePrefix.Match(line);
        if (!m.Success) return null;
        try
        {
            var now = DateTime.Now;
            var dt = new DateTime(now.Year, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), DateTimeKind.Local);
            return new DateTimeOffset(dt);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static string Report(LobbyTracker tracker)
    {
        var sb = new StringBuilder();
        var cycles = tracker.AllCycles().Where(c => c.LobbyId is not null || c.ReadyStatuses.Count > 0 || c.AcceptSeq is not null).ToList();
        if (cycles.Count == 0)
        {
            sb.AppendLine("В логе нет ни одного лобби. Проверь, что Dota запущена с параметрами -console -condebug и что был найден матч.");
            return sb.ToString();
        }

        int i = 0, mmYes = 0, mmTotal = 0;
        foreach (var c in cycles)
        {
            i++;
            var kind = c.CustomMarkers ? "кастом/практика"
                : c.Matchmaking == true ? "подбор (mm=1)"
                : c.Matchmaking == false ? "не подбор (mm=0)"
                : "неизвестно";
            sb.AppendLine($"#{i}  Лобби: {c.LobbyId?.ToString(CultureInfo.InvariantCulture) ?? "—"}  [{kind}]");
            sb.AppendLine($"    Источник ID: {(c.LobbySource == "subscribe" ? "подписка на лобби (SOCache)" : c.LobbySource == "run" ? "только LOBBY STATE RUN (после принятия)" : "—")}");
            sb.AppendLine($"    Сообщений 7170 (ReadyUpStatus): {c.ReadyStatuses.Count}{(c.ReadyStatuses.Count > 0 ? "  размеры: " + string.Join(",", c.ReadyStatuses.Select(r => r.Size)) : "")}");
            var before7170 = c.SubscribedSeq is not null && c.ReadyStatuses.Count > 0 ? (c.SubscribedSeq < c.ReadyStatuses[0].Seq ? "да" : "нет") : "—";
            sb.AppendLine($"    ID раньше первого 7170: {before7170}");
            sb.AppendLine($"    Принятие (7070) отправлено: {(c.AcceptSeq is null ? "нет" : "да")}");
            sb.AppendLine($"    ID ДО ПРИНЯТИЯ: {c.LobbyIdBeforeAccept switch { true => "ДА", false => "НЕТ", null => "—" }}");
            if (c.RunLobbyId is not null)
                sb.AppendLine($"    LOBBY STATE RUN: лобби {c.RunLobbyId}, матч {c.MatchId} — {(c.RunMatchesSubscribed ? "совпадает" : "НЕ совпадает")} с ID лобби");
            sb.AppendLine();

            if (!c.CustomMarkers && c.Matchmaking == true && c.AcceptSeq is not null)
            {
                mmTotal++;
                if (c.LobbyIdBeforeAccept == true && (c.RunLobbyId is null || c.RunMatchesSubscribed)) mmYes++;
            }
        }

        sb.AppendLine("ИТОГ по матчам подбора (mm=1), где было принятие:");
        sb.AppendLine(mmTotal == 0
            ? "    таких матчей в логе нет — нужен лог с рейтинговым/обычным подбором."
            : $"    ID лобби был в логе до принятия: {mmYes} из {mmTotal}");
        return sb.ToString();
    }
}
