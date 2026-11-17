namespace LGMods.MapDoctor.Core.Scanning;

public enum StartAction { Start, Stop }

public sealed record StartCommand(StartAction Action, string Target, string SourceFile, int LineNumber)
{
    public bool IsCategory => Target.StartsWith('[') && Target.EndsWith(']');
}

public sealed class StartList
{
    public required string MainFile { get; init; }
    public required IReadOnlyList<StartCommand> Commands { get; init; }

    // sv_enforceGameBuild, if the config sets one.
    public int? GameBuild { get; init; }
}

// Reads server.cfg, and any files it pulls in with "exec".
public static class StartListReader
{
    private static readonly string[] ConfigNames = ["server.cfg", "resources.cfg"];

    // In the picked folder or its parent.
    public static string? FindConfig(string pickedFolder)
    {
        var candidates = new List<string> { pickedFolder };
        var parent = Directory.GetParent(pickedFolder);
        if (parent is not null) candidates.Add(parent.FullName);

        foreach (var dir in candidates)
            foreach (var name in ConfigNames)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) return path;
            }

        return null;
    }

    public static StartList Read(string configPath, Func<string, string?>? resolveResourceFolder = null)
    {
        var commands = new List<StartCommand>();
        int? gameBuild = null;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var serverRoot = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        ReadFile(Path.GetFullPath(configPath), serverRoot, commands, visited, resolveResourceFolder, ref gameBuild, depth: 0);
        return new StartList { MainFile = configPath, Commands = commands, GameBuild = gameBuild };
    }

    private static void ReadFile(
        string path,
        string serverRoot,
        List<StartCommand> commands,
        HashSet<string> visited,
        Func<string, string?>? resolveResourceFolder,
        ref int? gameBuild,
        int depth)
    {
        if (depth > 16 || !visited.Add(path) || !File.Exists(path)) return;

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // unreadable: skip it

        for (var i = 0; i < lines.Length; i++)
        {
            var parts = Tokenise(StripComment(lines[i]));
            if (parts.Count < 2) continue;

            var verb = parts[0].ToLowerInvariant();
            var target = parts[1];

            // "set sv_enforceGameBuild 3258" (or setr/sets, or without "set").
            var setting = verb is "set" or "setr" or "sets" && parts.Count >= 3 ? (parts[1], parts[2]) : (parts[0], parts[1]);
            if (setting.Item1.Equals("sv_enforceGameBuild", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(setting.Item2, out var build))
                gameBuild = build;

            switch (verb)
            {
                case "ensure":
                case "start":
                case "restart":
                    commands.Add(new StartCommand(StartAction.Start, target, path, i + 1));
                    break;
                case "stop":
                    commands.Add(new StartCommand(StartAction.Stop, target, path, i + 1));
                    break;
                case "exec":
                    var included = ResolveExec(target, path, serverRoot, resolveResourceFolder);
                    if (included is not null)
                        ReadFile(included, serverRoot, commands, visited, resolveResourceFolder, ref gameBuild, depth + 1);
                    break;
            }
        }
    }

    private static string? ResolveExec(string target, string fromFile, string serverRoot, Func<string, string?>? resolveResourceFolder)
    {
        target = target.Replace('\\', '/');
        if (target.StartsWith('@'))
        {
            var slash = target.IndexOf('/');
            if (slash < 0 || resolveResourceFolder is null) return null;
            var folder = resolveResourceFolder(target[1..slash]);
            return folder is null ? null : Path.GetFullPath(Path.Combine(folder, target[(slash + 1)..]));
        }

        if (Path.IsPathRooted(target)) return target;

        // FXServer resolves exec paths from its working folder, which is normally where server.cfg lives.
        var fromRoot = Path.GetFullPath(Path.Combine(serverRoot, target));
        if (File.Exists(fromRoot)) return fromRoot;
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fromFile)!, target));
    }

    private static string StripComment(string line)
    {
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') inQuote = !inQuote;
            if (inQuote) continue;
            if (c == '#') return line[..i];
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') return line[..i];
        }
        return line;
    }

    private static List<string> Tokenise(string line)
    {
        var parts = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;
            if (line[i] == '"')
            {
                var end = line.IndexOf('"', i + 1);
                if (end < 0) end = line.Length;
                parts.Add(line[(i + 1)..end]);
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                parts.Add(line[start..i]);
            }
        }
        return parts;
    }
}
