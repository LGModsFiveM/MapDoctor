using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace LGMods.MapDoctor.App.Services;

public static class Links
{
    public const string Store = "https://store.lgmods.co.uk/";
    public const string Discord = "https://discord.gg/lgmods";
}

public static class Shell
{
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception) { MessageBox.Show($"We couldn't open:\n{target}", "LGMods Map Doctor"); }
    }

    public static void ShowFolder(string folder)
    {
        try { Process.Start("explorer.exe", $"\"{folder}\""); }
        catch (Exception) { MessageBox.Show($"We couldn't open:\n{folder}", "LGMods Map Doctor"); }
    }

    public static string AppVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.1.0";
}

public sealed class Settings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LGMods", "MapDoctor", "settings.json");

    public List<string> RecentFolders { get; set; } = [];

    // Per server folder: keys of findings the user checked in game and marked as fine.
    public Dictionary<string, List<string>> MarkedFine { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsFine(string folder, string key) =>
        MarkedFine.TryGetValue(folder, out var keys) && keys.Contains(key);

    public void SetFine(string folder, string key, bool fine)
    {
        if (!MarkedFine.TryGetValue(folder, out var keys)) MarkedFine[folder] = keys = [];
        keys.Remove(key);
        if (fine) keys.Add(key);
        Save();
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                loaded.MarkedFine = new(loaded.MarkedFine, StringComparer.OrdinalIgnoreCase);
                return loaded;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable settings file just means starting with none.
        }
        return new Settings();
    }

    public void Remember(string folder)
    {
        RecentFolders.RemoveAll(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, folder);
        if (RecentFolders.Count > 5) RecentFolders.RemoveRange(5, RecentFolders.Count - 5);
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to save them shouldn't interrupt anything.
        }
    }
}

public static class TitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Brand(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));        // immersive dark mode
        var caption = 0x00110F07;                                       // #070F11 as 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));     // caption colour (Windows 11)
    }
}
