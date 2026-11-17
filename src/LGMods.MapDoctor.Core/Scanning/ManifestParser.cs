using System.Text.RegularExpressions;
using LGMods.MapDoctor.Core.Model;

namespace LGMods.MapDoctor.Core.Scanning;

// Manifests are Lua, but the bits we need are simple enough for patterns, and a manifest we can't fully
// understand must never crash a scan.
public static partial class ManifestParser
{
    [GeneratedRegex(@"--\[(=*)\[.*?\]\1\]", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"--[^\r\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"\bthis_is_a_map\s*\(?\s*['""]yes['""]", RegexOptions.IgnoreCase)]
    private static partial Regex ThisIsAMap();

    [GeneratedRegex(@"\bdata_file\s*\(?\s*['""]([^'""]+)['""]\s*\)?\s*\(?\s*['""]([^'""]+)['""]")]
    private static partial Regex DataFile();

    [GeneratedRegex(@"\bdependency\s*\(?\s*['""]([^'""]+)['""]")]
    private static partial Regex Dependency();

    [GeneratedRegex(@"\bdependencies\s*\(?\s*\{([^}]*)\}")]
    private static partial Regex Dependencies();

    [GeneratedRegex(@"\bfiles\s*\(?\s*\{([^}]*)\}")]
    private static partial Regex FilesBlock();

    [GeneratedRegex(@"\bfile\s*\(?\s*['""]([^'""]+)['""]")]
    private static partial Regex SingleFile();

    [GeneratedRegex(@"\bgames?\s*\(?\s*(\{[^}]*\}|['""][^'""]*['""])")]
    private static partial Regex Games();

    [GeneratedRegex(@"\bprovides?\s*\(?\s*(\{[^}]*\}|['""][^'""]*['""])")]
    private static partial Regex Provides();

    [GeneratedRegex(@"['""]([^'""]+)['""]")]
    private static partial Regex QuotedString();

    public static ManifestInfo Parse(string text)
    {
        var code = LineComment().Replace(BlockComment().Replace(text, ""), "");

        var dataFiles = DataFile().Matches(code)
            .Select(m => (m.Groups[1].Value.Trim(), NormalisePath(m.Groups[2].Value)))
            .ToList();

        var dependencies = Dependency().Matches(code).Select(m => m.Groups[1].Value)
            .Concat(Dependencies().Matches(code).SelectMany(m => Strings(m.Groups[1].Value)))
            .Select(d => d.Trim())
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var files = FilesBlock().Matches(code).SelectMany(m => Strings(m.Groups[1].Value))
            .Concat(SingleFile().Matches(code).Select(m => m.Groups[1].Value))
            .Select(NormalisePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var games = Games().Matches(code).SelectMany(m => Strings(m.Groups[1].Value)).Select(g => g.Trim()).ToList();
        var provides = Provides().Matches(code).SelectMany(m => Strings(m.Groups[1].Value))
            .Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new ManifestInfo
        {
            Games = games,
            Provides = provides,
            ThisIsAMap = ThisIsAMap().IsMatch(code),
            DataFiles = dataFiles,
            Dependencies = dependencies,
            Files = files,
        };
    }

    private static IEnumerable<string> Strings(string block) =>
        QuotedString().Matches(block).Select(m => m.Groups[1].Value);

    private static string NormalisePath(string path) => path.Trim().Replace('\\', '/');
}
