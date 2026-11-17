namespace LGMods.MapDoctor.Core.Model;

// Described from the player's point of view.
public enum Severity
{
    Problem = 0, // players will probably notice
    Warning = 1, // might look odd, worth a quick look
    Info = 2,
}

public enum FindingKind
{
    ClashingFiles,
    AlreadyFixed,
    IdenticalCopies,
    DuplicateResourceName,
    MissingDependency,
    WontStart,
    ScenarioOverrides,
    SameFileTwice,
    SameSpot,
    MissingSetupFile,
    MissingMapSetting,
    LargeFiles,
    MapsSwitchedOff,
    StartListMentionsMissing,
    NoStartList,
}

public sealed class Finding
{
    public required FindingKind Kind { get; init; }
    public required Severity Severity { get; init; }

    // What players notice, e.g. "Players may fall through floors".
    public required string Title { get; init; }
    public required string Where { get; init; }
    public required string WhatsWrong { get; init; }
    public required IReadOnlyList<string> WhatToDo { get; init; }
    public IReadOnlyList<string> Details { get; init; } = [];
    public IReadOnlyList<ResourceInfo> Resources { get; init; } = [];

    public string ShortTitle { get => _shortTitle ?? Title; init => _shortTitle = value; }
    private readonly string? _shortTitle;

    // Clashes only.
    public ResourceInfo? Winner { get; init; }
    public IReadOnlyList<FileClash> Files { get; init; } = [];

    // Stable across scans, used to remember "looks fine" decisions. Clashes include their file names,
    // so if a map update adds a new clashing file, a clash marked as fine comes back for another look.
    public string Key => $"{Kind}:{string.Join("|", Resources.Select(r => r.Name.ToLowerInvariant()).Order())}" +
                         (Files.Count == 0 ? "" : ":" + string.Join("|", Files.Select(f => f.FileName.ToLowerInvariant()).Order()));
}

public sealed record FileClash(
    string FileName,
    string Extension,
    string Meaning,
    IReadOnlyList<StreamFile> Copies,
    ResourceInfo Winner);

public sealed record StreamFile(
    ResourceInfo Resource,
    string FullPath,
    string RelativePath,
    string FileName,
    string Extension,
    long Size);

public sealed class ManifestInfo
{
    public static readonly ManifestInfo Empty = new();

    public bool ThisIsAMap { get; init; }
    public IReadOnlyList<(string Type, string Path)> DataFiles { get; init; } = [];
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyList<string> Files { get; init; } = [];

    // "gta5", "rdr3", "common"; null if the manifest couldn't be read.
    public IReadOnlyList<string>? Games { get; init; }

    // Resource names this one stands in for (provide 'mysql-async').
    public IReadOnlyList<string> Provides { get; init; } = [];
}

// Why the server tries to start a resource but FiveM refuses.
public enum StartProblemKind
{
    NotForGta5, // often because every line of fxmanifest.lua is commented out
    MissingNeeds,
    NeedsBrokenResource,
    NeedsNewerGame,
}

public sealed record StartProblem(StartProblemKind Kind, IReadOnlyList<string> Names);

public sealed class ResourceInfo
{
    public required string Name { get; init; }
    public required string Directory { get; init; }

    // The [category] folders it sits in, e.g. ["[CR_MLO]"].
    public required IReadOnlyList<string> Categories { get; init; }

    public required string ManifestPath { get; init; }
    public ManifestInfo Manifest { get; set; } = ManifestInfo.Empty;

    // Protected by FiveM Escrow (has .fxap files).
    public bool IsLocked { get; set; }

    public List<StreamFile> StreamFiles { get; } = [];

    // Null when the server never starts it.
    public int? StartOrder { get; set; }

    public bool IsStarted => StartOrder is not null;

    public StartProblem? WontStart { get; set; }

    // Set when another folder has the same name. FiveM only uses the first one it finds, so this copy never runs.
    public ResourceInfo? UsedInstead { get; set; }

    public bool IsFxManifest => Path.GetFileName(ManifestPath).Equals("fxmanifest.lua", StringComparison.OrdinalIgnoreCase);

    // fxmanifest.lua must say "game 'gta5'" or "game 'common'"; the old __resource.lua format is assumed
    // to be for GTA V. An unreadable manifest gets the benefit of the doubt.
    public bool IsForGta5 => Manifest.Games switch
    {
        null => true,
        { Count: 0 } => !IsFxManifest,
        var games => games.Any(g => g.Equals("gta5", StringComparison.OrdinalIgnoreCase) || g.Equals("common", StringComparison.OrdinalIgnoreCase)),
    };

    public bool ChangesTheWorld => StreamFiles.Any(f => FileTypes.IsWorldFile(f.Extension));

    public string CategoryLabel => Categories.Count == 0 ? "" : string.Join(" / ", Categories);

    public override string ToString() => Name;
}

public sealed class ScanResult
{
    public required string ResourcesFolder { get; init; }
    public string? StartListFile { get; init; }
    public required IReadOnlyList<ResourceInfo> Resources { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required TimeSpan Duration { get; init; }
    public DateTime ScannedAt { get; init; } = DateTime.Now;

    public int ProblemCount => Findings.Count(f => f.Severity == Severity.Problem);
    public int WarningCount => Findings.Count(f => f.Severity == Severity.Warning);
    public int InfoCount => Findings.Count(f => f.Severity == Severity.Info);
    public int MapCount => Resources.Count(r => r.ChangesTheWorld);
    public int StartedMapCount => Resources.Count(r => r.ChangesTheWorld && r.IsStarted);
    public int StreamFileCount => Resources.Sum(r => r.StreamFiles.Count);
}

public sealed record ScanProgress(string Message, double Fraction);
