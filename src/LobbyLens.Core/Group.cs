using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LobbyLens.Core;

/// <summary>
/// Group code = shared secret. Everything sent to the relay (MQTT broker) is
/// AES-GCM encrypted with a key derived from the code, and the lobby ID itself
/// is never sent: only an HMAC "tag" of it, so equal lobbies give equal tags.
/// </summary>
public sealed class GroupKeys
{
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I

    public string Code { get; }
    public string TopicRoot { get; }
    readonly byte[] _encKey;
    readonly byte[] _tagKey;

    GroupKeys(string code)
    {
        Code = code;
        TopicRoot = "lobbylens/v1/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("topic|" + code)))[..24].ToLowerInvariant();
        _encKey = SHA256.HashData(Encoding.UTF8.GetBytes("enc|" + code));
        _tagKey = SHA256.HashData(Encoding.UTF8.GetBytes("tag|" + code));
    }

    public static string NewCode()
    {
        Span<byte> rnd = stackalloc byte[10];
        RandomNumberGenerator.Fill(rnd);
        var sb = new StringBuilder(11);
        for (int i = 0; i < 10; i++)
        {
            if (i == 5) sb.Append('-');
            sb.Append(Alphabet[rnd[i] % Alphabet.Length]);
        }
        return sb.ToString();
    }

    /// <summary>Normalizes user input ("abcde fghjk" → "ABCDE-FGHJK"); null if invalid.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var chars = input.ToUpperInvariant().Where(ch => !char.IsWhiteSpace(ch) && ch != '-').ToArray();
        if (chars.Length != 10 || chars.Any(ch => !Alphabet.Contains(ch))) return null;
        return new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
    }

    public static GroupKeys? FromCode(string? input) => Normalize(input) is { } c ? new GroupKeys(c) : null;

    public string LobbyTag(ulong lobbyId)
    {
        using var h = new HMACSHA256(_tagKey);
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(lobbyId.ToString())))[..20];
    }

    public byte[] Encrypt(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using var aes = new AesGcm(_encKey, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    public byte[]? Decrypt(ReadOnlySpan<byte> data)
    {
        if (data.Length < 28) return null;
        try
        {
            var plain = new byte[data.Length - 28];
            using var aes = new AesGcm(_encKey, 16);
            aes.Decrypt(data[..12], data[28..], data[12..28], plain);
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>What each member publishes about itself.</summary>
public sealed record MemberState
{
    [JsonPropertyName("v")] public int Version { get; init; } = 1;
    [JsonPropertyName("id")] public string MemberId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("phase")] public Phase Phase { get; init; }
    [JsonPropertyName("tag")] public string? LobbyTag { get; init; }
    [JsonPropertyName("before")] public bool? LobbyIdBeforeAccept { get; init; }
    [JsonPropertyName("ts")] public long UnixMs { get; init; }

    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    public static MemberState? FromBytes(byte[] data)
    {
        try { return JsonSerializer.Deserialize<MemberState>(data, Json); }
        catch (JsonException) { return null; }
    }
}

public enum MemberRelation
{
    /// <summary>Same lobby as me.</summary>
    Same,
    /// <summary>Both lobby IDs known and different.</summary>
    Different,
    /// <summary>Not searching / not in a lobby.</summary>
    Idle,
    /// <summary>Their or my lobby ID is unknown, or their data is stale.</summary>
    Unknown,
}

public sealed record MemberView(MemberState State, MemberRelation Relation, bool Stale);

public sealed record MatchResult(string Headline, IReadOnlyList<MemberView> Members);

public static class MatchEvaluator
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(45);

    static bool Active(Phase p) => p is Phase.ReadyCheck or Phase.Accepted or Phase.InGame or Phase.InLobby;

    public static MatchResult Evaluate(MemberState me, IEnumerable<MemberState> others, DateTimeOffset now)
    {
        var views = new List<MemberView>();
        foreach (var o in others.Where(o => o.MemberId != me.MemberId).OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var stale = now - DateTimeOffset.FromUnixTimeMilliseconds(o.UnixMs) > StaleAfter;
            MemberRelation rel;
            if (stale) rel = MemberRelation.Unknown;
            else if (!Active(o.Phase)) rel = MemberRelation.Idle;
            else if (me.LobbyTag is null || o.LobbyTag is null) rel = MemberRelation.Unknown;
            else rel = me.LobbyTag == o.LobbyTag ? MemberRelation.Same : MemberRelation.Different;
            views.Add(new MemberView(o, rel, stale));
        }

        string headline;
        if (!Active(me.Phase))
            headline = "Нет найденного матча";
        else if (me.LobbyTag is null)
            headline = "Неизвестно";
        else
        {
            var same = views.Where(v => v.Relation == MemberRelation.Same).Select(v => v.State.Name).ToList();
            var unknown = views.Count(v => v.Relation == MemberRelation.Unknown);
            headline = same.Count > 0
                ? $"В одном матче с: {string.Join(", ", same)}  ({same.Count + 1}/{views.Count + 1})"
                : unknown > 0 ? "Совпадений нет, по остальным — неизвестно" : "Ни с кем из группы";
        }
        return new MatchResult(headline, views);
    }
}
