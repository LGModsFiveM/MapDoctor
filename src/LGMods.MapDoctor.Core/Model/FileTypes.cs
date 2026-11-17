namespace LGMods.MapDoctor.Core.Model;

public static class FileTypes
{
    private static readonly HashSet<string> Streamed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ymap", ".ytyp", ".ybn", ".ydr", ".ydd", ".yft", ".ytd",
        ".ynv", ".ynd", ".ycd", ".ypt", ".ymf", ".yed", ".yld", ".ysc",
        ".ymt", ".gfx", ".mrf",
    };

    private static readonly HashSet<string> World = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ymap", ".ytyp", ".ybn",
    };

    public static bool IsStreamed(string extension) => Streamed.Contains(extension);

    public static bool IsWorldFile(string extension) => World.Contains(extension);

    // Lower = worse for players when two resources clash on it.
    public static int ClashRank(string extension) => extension.ToLowerInvariant() switch
    {
        ".ybn" => 0,
        ".ymap" => 1,
        ".ytyp" => 2,
        ".ynv" or ".ynd" => 3,
        ".ydr" or ".ydd" or ".yft" => 4,
        ".ytd" => 5,
        ".ymt" => 5,
        _ => 6,
    };

    public static string Things(string extension) => extension.ToLowerInvariant() switch
    {
        ".ydr" or ".ydd" or ".yft" => "models",
        ".ytd" => "textures",
        ".ycd" => "animations",
        ".ypt" => "effects",
        ".gfx" => "screen graphics",
        ".ymt" => "game data files",
        ".ynv" or ".ynd" => "path files",
        ".ymap" or ".ytyp" or ".ybn" => "map files",
        _ => "game files",
    };

    public static (Severity Severity, string Title) ClashSymptom(string extension) => extension.ToLowerInvariant() switch
    {
        ".ybn" => (Severity.Problem, "Players may fall through floors or walk through walls"),
        ".ymap" => (Severity.Problem, "Buildings or objects may be missing, in the wrong place or flickering"),
        ".ytyp" => (Severity.Problem, "Some objects may not appear, or show up as the wrong thing"),
        ".ynv" or ".ynd" => (Severity.Warning, "Locals or traffic may move strangely or get stuck"),
        ".ydr" or ".ydd" or ".yft" => (Severity.Warning, "Some models may look wrong"),
        ".ytd" => (Severity.Warning, "Some textures may look wrong"),
        ".ymt" => (Severity.Warning, "Locals may be missing or in odd places, or some game settings may be wrong"),
        ".ycd" => (Severity.Warning, "Some animations may look wrong"),
        ".ypt" => (Severity.Warning, "Some effects (smoke, fire, sparks) may look wrong"),
        ".gfx" => (Severity.Warning, "Some on-screen graphics may look wrong"),
        _ => (Severity.Warning, "Some game files may be the wrong version"),
    };

    // Some map files have a special job that shows in their name (occlusion, distant lights, grass),
    // which gives a more precise symptom. Lower rank = worse.
    public static (Severity Severity, int Rank, string Title, string ShortTitle) ClashSymptomFor(string fileName, string extension)
    {
        if (extension.Equals(".ymap", StringComparison.OrdinalIgnoreCase))
        {
            var name = fileName.ToLowerInvariant();
            if (name.Contains("_occl"))
                return (Severity.Problem, 1, "Buildings may vanish or flicker when seen from certain angles", "Buildings vanishing");
            if (name.Contains("lodlights"))
                return (Severity.Warning, 7, "Distant lights at night may be missing or in the wrong place", "Distant lights");
            if (name.Contains("grass"))
                return (Severity.Warning, 7, "Grass and plants may poke through floors or be missing", "Grass through floors");
        }

        var (severity, title) = ClashSymptom(extension);
        var rank = ClashRank(extension);
        var shortTitle = extension.ToLowerInvariant() switch
        {
            ".ybn" => "Falling through floors",
            ".ymap" => "Missing or misplaced objects",
            ".ytyp" => "Objects not appearing",
            ".ynv" or ".ynd" => "Locals and traffic",
            ".ydr" or ".ydd" or ".yft" => "Models look wrong",
            ".ytd" => "Textures look wrong",
            ".ymt" => "Game data files",
            ".ycd" => "Animations look wrong",
            ".ypt" => "Effects look wrong",
            ".gfx" => "Screen graphics",
            _ => "Clashing files",
        };
        return (severity, rank >= 1 ? rank + 1 : rank, title, shortTitle);
    }

    public static string Meaning(string fileName, string extension)
    {
        var name = fileName.ToLowerInvariant();
        if (extension.Equals(".ymap", StringComparison.OrdinalIgnoreCase))
        {
            if (name.Contains("_occl")) return "What the game hides when it's out of view";
            if (name.Contains("lodlights")) return "Distant lights at night";
            if (name.Contains("grass")) return "Grass and plants";
            if (name.Contains("_rd") || name.Contains("road")) return "Roads and pavements";
            if (name.Contains("_lod")) return "Far-away building shapes";
            if (name.Contains("strm") || name.Contains("long")) return "Objects placed in the world";
            return "Things placed in the world";
        }

        return extension.ToLowerInvariant() switch
        {
            ".ybn" => "Solid surfaces you walk on and bump into",
            ".ytyp" => "List of objects the map uses",
            ".ydr" or ".ydd" => "A 3D model",
            ".yft" => "A vehicle or breakable model",
            ".ytd" => "Textures (the images on models)",
            ".ynv" => "Where locals can walk",
            ".ynd" => "Where traffic drives",
            ".ycd" => "Animations",
            ".ypt" => "Particle effects",
            ".ymt" => "Game data (often where locals stand and what they do)",
            ".gfx" => "On-screen graphics",
            _ => "Game file",
        };
    }

    public static string Describe(string extension) => extension.ToLowerInvariant() switch
    {
        ".ybn" => "solid surfaces (collision)",
        ".ymap" => "map placements",
        ".ytyp" => "model list",
        ".ydr" => "3D model",
        ".ydd" => "3D model set",
        ".yft" => "vehicle or breakable model",
        ".ytd" => "textures",
        ".ynv" => "walking paths for locals",
        ".ynd" => "traffic paths",
        ".ycd" => "animations",
        ".ypt" => "particle effects",
        ".ymt" => "game data",
        _ => "game file",
    };
}
