using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace LobbyLens.App;

public sealed class Settings
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LobbyLens");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public string Name { get; set; } = Environment.UserName;
    public string MemberId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    /// <summary>Manual console.log path; null = find automatically.</summary>
    public string? LogPath { get; set; }
    public string? GroupCode { get; set; }
    public string BrokerHost { get; set; } = "broker.emqx.io";
    public int BrokerPort { get; set; } = 1883;
    public bool OverlayAlways { get; set; }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Steam install roots from the registry plus default locations.</summary>
    public static IEnumerable<string> SteamRoots()
    {
        var roots = new List<string>();
        void Add(object? v)
        {
            if (v is string s && !string.IsNullOrWhiteSpace(s)) roots.Add(s.Replace('/', '\\'));
        }
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) Add(k?.GetValue("SteamPath"));
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")) Add(k?.GetValue("InstallPath"));
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam")) Add(k?.GetValue("InstallPath"));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        roots.Add(@"C:\Program Files (x86)\Steam");
        roots.Add(@"C:\Program Files\Steam");
        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
