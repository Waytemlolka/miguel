using LobbyLens.Core;
using Xunit;

namespace LobbyLens.Tests;

public class ParserTests
{
    [Theory]
    [InlineData("06/03 16:47:24 [SOCache] CGCSOCacheSubscribedJob(owner=<Lobby:26694969711146938>) [in cache]", LogEventKind.LobbySubscribed, 26694969711146938UL)]
    [InlineData("[SOCache] CGCSOCacheUnsubscribedJob(owner=Lobby:28646322118781118)", LogEventKind.LobbyUnsubscribed, 28646322118781118UL)]
    [InlineData("LOBBY STATE RUN: lobby 26694969711146938: Player AccountID 9 connecting to MatchID 5451564520 at ServerID [A:1:1:1] =[A:1:1:1]", LogEventKind.LobbyRun, 26694969711146938UL)]
    public void ParsesLobbyLines(string line, LogEventKind kind, ulong lobby)
    {
        var e = LogParser.Parse(line);
        Assert.NotNull(e);
        Assert.Equal(kind, e!.Kind);
        Assert.Equal(lobby, e.LobbyId);
    }

    [Theory]
    [InlineData("[GCClient] Send msg 7070 (k_EMsgGCReadyUp), 44 bytes")]
    [InlineData("[GCClient] Sending 7070 (k_EMsgGCReadyUp) 19 bytes")]
    public void ParsesAccept(string line) => Assert.Equal(LogEventKind.ReadyUpSent, LogParser.Parse(line)!.Kind);

    [Fact]
    public void ParsesReadyStatusSize()
    {
        var e = LogParser.Parse("[GCClient] Recv msg 7170 (k_EMsgGCReadyUpStatus), 33 bytes");
        Assert.Equal(LogEventKind.ReadyUpStatus, e!.Kind);
        Assert.Equal(33, e.Size);
    }

    [Fact]
    public void ParsesMmFlag()
    {
        var e = LogParser.Parse("[SteamNetSockets] Received Steam datagram ticket for server [A:1:5:1] vport 0. match_id=4083586116 mm=1 flg=rsauto");
        Assert.Equal(true, e!.Matchmaking);
    }

    [Fact]
    public void IgnoresNoise()
    {
        Assert.Null(LogParser.Parse("[GCClient] Recv msg 3001 (k_EMsgGCPingRequest), 8 bytes"));
        Assert.Null(LogParser.Parse("[SOCache] CGCSOCacheSubscribedJob(owner=<Party:25758356520108019>) [in cache]"));
        Assert.Null(LogParser.Parse("Ignoring custom lobby 26694969709653690, cannot estimate ping to ''"));
    }
}

public class TrackerTests
{
    static LobbyTracker Feed(params string[] lines) => LogAnalyzer.Run(lines);

    [Fact]
    public void LobbyBeforeAccept_IsDetected()
    {
        var t = Feed(
            "[GCClient] Recv msg 24 (k_ESOMsg_CacheSubscribed), 527 bytes",
            "[SOCache] CGCSOCacheSubscribedJob(owner=<Lobby:111>) [in cache]",
            "[GCClient] Recv msg 7170 (k_EMsgGCReadyUpStatus), 22 bytes");
        var s = t.Snapshot();
        Assert.Equal(Phase.ReadyCheck, s.Phase);
        Assert.Equal(111UL, s.LobbyId);
        Assert.Null(s.LobbyIdBeforeAccept); // not accepted yet

        t.Feed(LogParser.Parse("[GCClient] Send msg 7070 (k_EMsgGCReadyUp), 19 bytes")!, DateTimeOffset.Now);
        t.Feed(LogParser.Parse("[SteamNetSockets] Received Steam datagram ticket for server x vport 0. match_id=5 mm=1 flg=")!, DateTimeOffset.Now);
        t.Feed(LogParser.Parse("LOBBY STATE RUN: lobby 111: Player AccountID 1 connecting to MatchID 5 at ServerID x")!, DateTimeOffset.Now);
        var c = t.Current!;
        Assert.True(c.LobbyIdBeforeAccept);
        Assert.True(c.RunMatchesSubscribed);
        Assert.Equal(true, c.Matchmaking);
        Assert.Equal(Phase.InGame, c.Phase);
    }

    [Fact]
    public void ReadyCheckBeforeSubscription_AttachesLobby()
    {
        var t = Feed(
            "[GCClient] Recv msg 7170 (k_EMsgGCReadyUpStatus), 22 bytes",
            "[SOCache] CGCSOCacheSubscribedJob(owner=Lobby:222)",
            "[GCClient] Send msg 7070 (k_EMsgGCReadyUp), 19 bytes");
        Assert.Equal(222UL, t.Current!.LobbyId);
        Assert.True(t.Current.LobbyIdBeforeAccept);
    }

    [Fact]
    public void LobbyOnlyAfterAccept_IsReportedAsNo()
    {
        // Like Dota2-Gameplay#15333: no subscription line before the accept.
        var t = Feed(
            "[GCClient] Recv msg 26 (k_ESOMsg_UpdateMultiple), 21541 bytes",
            "[GCClient] Send msg 7070 (k_EMsgGCReadyUp), 44 bytes",
            "[GCClient] Recv msg 7170 (k_EMsgGCReadyUpStatus), 22 bytes",
            "LOBBY STATE RUN: lobby 333: Player AccountID 1 connecting to MatchID 7 at ServerID x");
        var c = t.Current!;
        Assert.Equal(333UL, c.LobbyId);
        Assert.Equal("run", c.LobbySource);
        Assert.False(c.LobbyIdBeforeAccept);
    }

    [Fact]
    public void Unsubscribe_ClearsCurrent_NoStaleId()
    {
        var t = Feed(
            "[SOCache] CGCSOCacheSubscribedJob(owner=<Lobby:444>) [in cache]",
            "[SOCache] CGCSOCacheUnsubscribedJob(owner=<Lobby:444>)",
            "[GCClient] Recv msg 7170 (k_EMsgGCReadyUpStatus), 22 bytes");
        var s = t.Snapshot();
        Assert.Equal(Phase.ReadyCheck, s.Phase);
        Assert.Null(s.LobbyId);
        Assert.Single(t.History);
    }

    [Fact]
    public void CustomLobby_IsMarked()
    {
        var t = Feed(
            "06/03 16:47:23 [GCClient] Send msg 7470 (k_EMsgGCQuickJoinCustomLobby), 526 bytes",
            "06/03 16:47:24 [SOCache] CGCSOCacheSubscribedJob(owner=<Lobby:555>) [in cache]",
            "06/03 16:47:27 [GCClient] Send msg 7070 (k_EMsgGCReadyUp), 19 bytes");
        Assert.True(t.Current!.CustomMarkers);
        Assert.Contains("кастом", LogAnalyzer.Report(t));
    }

    [Fact]
    public void NewLobby_ReplacesOld()
    {
        var t = Feed(
            "[SOCache] CGCSOCacheSubscribedJob(owner=<Lobby:1>) [in cache]",
            "[SOCache] CGCSOCacheSubscribedJob(owner=<Lobby:2>) [in cache]");
        Assert.Equal(2UL, t.Current!.LobbyId);
    }
}

public class GroupTests
{
    [Fact]
    public void CodeRoundTrip()
    {
        var code = GroupKeys.NewCode();
        Assert.Equal(code, GroupKeys.Normalize(code.ToLowerInvariant().Replace("-", " ")));
        Assert.Null(GroupKeys.Normalize("abc"));
        Assert.Null(GroupKeys.Normalize("OOOOO-11111"));
    }

    [Fact]
    public void SameCode_SameTagsAndDecrypt()
    {
        var code = GroupKeys.NewCode();
        var a = GroupKeys.FromCode(code)!;
        var b = GroupKeys.FromCode(code)!;
        Assert.Equal(a.TopicRoot, b.TopicRoot);
        Assert.Equal(a.LobbyTag(42), b.LobbyTag(42));
        Assert.NotEqual(a.LobbyTag(42), a.LobbyTag(43));

        var st = new MemberState { MemberId = "x", Name = "Вася", Phase = Phase.ReadyCheck, LobbyTag = a.LobbyTag(42), UnixMs = 1 };
        var back = MemberState.FromBytes(b.Decrypt(a.Encrypt(st.ToBytes()))!);
        Assert.Equal(st, back);

        var other = GroupKeys.FromCode(GroupKeys.NewCode())!;
        Assert.Null(other.Decrypt(a.Encrypt(st.ToBytes())));
    }

    [Fact]
    public void Evaluator()
    {
        var now = DateTimeOffset.Now;
        long ms = now.ToUnixTimeMilliseconds();
        var me = new MemberState { MemberId = "me", Name = "Я", Phase = Phase.ReadyCheck, LobbyTag = "A", UnixMs = ms };
        var others = new[]
        {
            new MemberState { MemberId = "1", Name = "Петя", Phase = Phase.ReadyCheck, LobbyTag = "A", UnixMs = ms },
            new MemberState { MemberId = "2", Name = "Коля", Phase = Phase.ReadyCheck, LobbyTag = "B", UnixMs = ms },
            new MemberState { MemberId = "3", Name = "Женя", Phase = Phase.ReadyCheck, LobbyTag = null, UnixMs = ms },
            new MemberState { MemberId = "4", Name = "Старый", Phase = Phase.ReadyCheck, LobbyTag = "A", UnixMs = ms - 120_000 },
            new MemberState { MemberId = "5", Name = "Афк", Phase = Phase.Idle, UnixMs = ms },
        };
        var r = MatchEvaluator.Evaluate(me, others, now);
        Assert.Contains("Петя", r.Headline);
        Assert.Equal(MemberRelation.Same, r.Members.Single(m => m.State.Name == "Петя").Relation);
        Assert.Equal(MemberRelation.Different, r.Members.Single(m => m.State.Name == "Коля").Relation);
        Assert.Equal(MemberRelation.Unknown, r.Members.Single(m => m.State.Name == "Женя").Relation);
        Assert.Equal(MemberRelation.Unknown, r.Members.Single(m => m.State.Name == "Старый").Relation);
        Assert.Equal(MemberRelation.Idle, r.Members.Single(m => m.State.Name == "Афк").Relation);

        var unknownMe = me with { LobbyTag = null };
        Assert.Equal("Неизвестно", MatchEvaluator.Evaluate(unknownMe, others, now).Headline);
    }
}

public class LocatorAndTailerTests
{
    [Fact]
    public void ParsesLibraryFolders()
    {
        var vdf = "\"libraryfolders\"\n{\n \"0\"\n {\n  \"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n }\n \"1\"\n {\n  \"path\"\t\t\"D:\\\\SteamLibrary\"\n }\n}";
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, DotaLocator.ParseLibraryFolders(vdf).ToArray());
    }

    [Fact]
    public void FindsConsoleLogInLibrary()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var lib = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "steamapps"));
            File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"), $"\"path\" \"{lib.Replace("\\", "\\\\")}\"");
            var dota = Path.Combine(lib, "steamapps", "common", "dota 2 beta", "game", "dota");
            Directory.CreateDirectory(dota);
            var r = DotaLocator.Find(new[] { root });
            Assert.False(r.LogExists);
            File.WriteAllText(Path.Combine(dota, "console.log"), "x");
            r = DotaLocator.Find(new[] { root });
            Assert.True(r.LogExists);
            Assert.EndsWith("console.log", r.LogPath);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(lib, true);
        }
    }

    [Fact]
    public async Task TailerFollowsAndSurvivesRecreate()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "console.log");
        File.WriteAllText(path, "old line\n");
        var lines = new System.Collections.Concurrent.ConcurrentQueue<(string, bool)>();
        int opened = 0;
        using (var t = new LogTailer(() => path) { PollInterval = TimeSpan.FromMilliseconds(30) })
        {
            t.Line += (l, r) => lines.Enqueue((l, r));
            t.FileOpened += _ => Interlocked.Increment(ref opened);
            t.Start();
            await WaitFor(() => lines.Count >= 1);
            File.AppendAllText(path, "new part");
            await Task.Delay(150);
            File.AppendAllText(path, " done\r\n");
            await WaitFor(() => lines.Count >= 2);

            // Dota starting again: the file is rewritten from scratch (shorter than before).
            File.WriteAllText(path, "fresh\n");
            await WaitFor(() => lines.Count >= 3);
        }
        var arr = lines.ToArray();
        Assert.Equal(("old line", true), arr[0]);
        Assert.Equal(("new part done", false), arr[1]);
        Assert.Equal("fresh", arr[2].Item1);
        Assert.True(opened >= 2);
        Directory.Delete(dir, true);
    }

    static async Task WaitFor(Func<bool> cond)
    {
        for (int i = 0; i < 100 && !cond(); i++) await Task.Delay(50);
        Assert.True(cond());
    }
}
