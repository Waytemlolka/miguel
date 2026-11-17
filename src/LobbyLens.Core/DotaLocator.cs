using System.Text.RegularExpressions;

namespace LobbyLens.Core;

/// <summary>Finds dota 2 beta/game/dota/console.log in all Steam libraries.</summary>
public static class DotaLocator
{
    public const string DotaSubdir = @"steamapps/common/dota 2 beta/game/dota";

    public sealed record Result(string? LogPath, string? DotaDir, bool LogExists);

    public static Result Find(IEnumerable<string> steamRoots)
    {
        string? firstDotaDir = null;
        foreach (var lib in LibraryPaths(steamRoots))
        {
            var dir = Path.Combine(lib, "steamapps", "common", "dota 2 beta", "game", "dota");
            if (!Directory.Exists(dir)) continue;
            var log = Path.Combine(dir, "console.log");
            if (File.Exists(log)) return new Result(log, dir, true);
            firstDotaDir ??= dir;
        }
        return firstDotaDir is null
            ? new Result(null, null, false)
            : new Result(Path.Combine(firstDotaDir, "console.log"), firstDotaDir, false);
    }

    public static List<string> LibraryPaths(IEnumerable<string> steamRoots)
    {
        var libs = new List<string>();
        foreach (var root in steamRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            Add(libs, root);
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            string text;
            try { text = File.ReadAllText(vdf); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var p in ParseLibraryFolders(text))
                if (Directory.Exists(p)) Add(libs, p);
        }
        return libs;
    }

    public static IEnumerable<string> ParseLibraryFolders(string vdfText) =>
        Regex.Matches(vdfText, "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\"));

    static void Add(List<string> libs, string p)
    {
        var full = Path.GetFullPath(p).TrimEnd('\\', '/');
        if (!libs.Contains(full, StringComparer.OrdinalIgnoreCase)) libs.Add(full);
    }
}
