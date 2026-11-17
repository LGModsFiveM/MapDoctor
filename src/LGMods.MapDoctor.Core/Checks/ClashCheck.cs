using System.Security.Cryptography;
using LGMods.MapDoctor.Core.Fixing;
using LGMods.MapDoctor.Core.Model;
using LGMods.MapDoctor.Core.Text;

namespace LGMods.MapDoctor.Core.Checks;

// Files with the same name streamed by more than one running resource. The game only ever uses one of them,
// so whatever the other resources changed is lost.
public static class ClashCheck
{
    // Resources whose whole job is to replace clashing files with combined versions.
    private static readonly string[] FixResourceNames = ["vertex-hub", "vertex_hub", "vertexhub", "lgmods_fixes", "lgmods-fixes", Resolver.FixResourceName];

    // Almost every map ships these under the same name, but FiveM loads them per resource, so a "clash" is a false alarm.
    private static readonly HashSet<string> PerResourceFiles = new(StringComparer.OrdinalIgnoreCase) { "_manifest.ymf" };

    public static bool IsFixResource(ResourceInfo r) =>
        FixResourceNames.Any(n => r.Name.Equals(n, StringComparison.OrdinalIgnoreCase));

    // fixes are Map Doctor's own fixes still in place, to tell when a map update has undone or outdated one.
    public static IEnumerable<Finding> Run(IReadOnlyList<ResourceInfo> resources, IReadOnlyList<FixRecord> fixes, CancellationToken cancel = default)
    {
        var byName = resources
            .Where(r => r.IsStarted)
            .SelectMany(r => r.StreamFiles)
            .Where(f => !PerResourceFiles.Contains(f.FileName))
            .GroupBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.GroupBy(f => f.Resource).Select(rg => rg.First()).ToList())
            .Where(copies => copies.Count > 1)
            .ToList();

        var hasher = new ContentComparer();
        var clashes = new List<Clash>();
        foreach (var copies in byName)
        {
            cancel.ThrowIfCancellationRequested();
            clashes.Add(new Clash(copies, hasher.Compare(copies)));
        }

        var findings = new List<Finding>();
        findings.AddRange(SameFileTwice(resources, hasher, cancel));
        findings.AddRange(ScenarioOverrides(resources, clashes));

        // 1. Clashes a fix resource (e.g. vertex-hub) already takes care of, because it starts last. Map Doctor's own
        // combined files are checked against the map copies they were made from, in case a map has changed since.
        var handled = clashes.Where(c => IsFixResource(c.Winner)).ToList();
        var outOfDate = handled.Select(c => (Clash: c, Why: WhyOutOfDate(c, fixes))).Where(x => x.Why is not null).ToList();
        foreach (var group in outOfDate.GroupBy(x => x.Clash.Key))
            findings.Add(OutOfDate(group.Select(x => (x.Clash, x.Why!)).ToList()));
        handled = handled.Except(outOfDate.Select(x => x.Clash)).ToList();
        foreach (var byFixer in handled.GroupBy(c => c.Winner))
            findings.Add(AlreadyFixed(byFixer.Key, byFixer.ToList()));

        var open = clashes.Where(c => !IsFixResource(c.Winner)).ToList();

        // 2. Real clashes: different contents, grouped by the set of resources involved.
        foreach (var group in open.Where(c => c.Same == Sameness.Different).GroupBy(c => c.Key))
            findings.Add(RealClash(group.ToList(), fixes));

        // 3. Identical copies are harmless; mention them once so nobody worries.
        var identical = open.Where(c => c.Same != Sameness.Different).ToList();
        if (identical.Count > 0)
            findings.Add(IdenticalCopies(identical));

        return findings;
    }

    // Why a file Map Doctor combined no longer matches the maps it was made from, or null if it still does.
    private static string? WhyOutOfDate(Clash clash, IReadOnlyList<FixRecord> fixes)
    {
        if (!Resolver.IsOurs(clash.Winner)) return null;
        // Newest fix first, so this is the combine that made the file there now.
        var combined = fixes.SelectMany(r => r.Combined).FirstOrDefault(c => c.FileName.Equals(clash.FileName, StringComparison.OrdinalIgnoreCase));
        if (combined is null) return null;

        var copies = clash.Copies.Where(f => !Resolver.IsOurs(f.Resource)).ToList();
        List<string> Names(IEnumerable<string> maps) => maps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Fixes made before Map Doctor kept each copy's fingerprint only know which maps were combined.
        var added = Names(copies.Where(f => combined.Sources.Count > 0
                ? !combined.Sources.Any(s => SamePath(s.Path, f.FullPath))
                : !combined.Maps.Contains(f.Resource.Name, StringComparer.OrdinalIgnoreCase))
            .Select(f => f.Resource.Name));
        var gone = Names(combined.Sources.Where(s => !copies.Any(f => SamePath(s.Path, f.FullPath))).Select(s => s.Map));
        var changed = Names(copies
            .Where(f => combined.Sources.FirstOrDefault(s => SamePath(s.Path, f.FullPath)) is { } s && Resolver.FingerprintOf(f.FullPath) != s.Fingerprint)
            .Select(f => f.Resource.Name)
            .Concat(added.Intersect(gone, StringComparer.OrdinalIgnoreCase))); // moved within the map by an update

        string Has(List<string> maps) => maps.Count == 1 ? "has" : "have";
        var reasons = new List<string>();
        if (changed.Count > 0) reasons.Add($"{Plain.JoinAnd(changed)} {Has(changed)} changed it since");
        added = added.Except(changed, StringComparer.OrdinalIgnoreCase).ToList();
        gone = gone.Except(changed, StringComparer.OrdinalIgnoreCase).ToList();
        if (added.Count > 0) reasons.Add($"{Plain.JoinAnd(added)} {Has(added)} it now too");
        if (gone.Count > 0) reasons.Add($"{Plain.JoinAnd(gone)} no longer {(gone.Count == 1 ? "loads" : "load")} it");
        return reasons.Count == 0 ? null : Plain.JoinAnd(reasons);
    }

    private static bool SamePath(string a, string b) =>
        Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static Finding OutOfDate(List<(Clash Clash, string Why)> stale)
    {
        var clashes = stale.Select(x => x.Clash).ToList();
        var maps = clashes[0].Resources.Where(r => !Resolver.IsOurs(r)).Select(r => r.Name).ToList();
        var whys = stale.Select(x => x.Why).Distinct().ToList();
        var one = stale.Count == 1;

        return new Finding
        {
            Kind = FindingKind.ClashingFiles,
            Severity = clashes.Min(c => FileTypes.ClashSymptomFor(c.FileName, c.Extension).Severity),
            Title = one ? $"Map Doctor's combined {clashes[0].FileName} is out of date" : $"{stale.Count} of Map Doctor's combined files are out of date",
            ShortTitle = one ? "Combined file out of date" : "Combined files out of date",
            Where = $"Around the {Plain.JoinAndShort(maps, noun: "map")}",
            WhatsWrong =
                $"Map Doctor combined {(one ? "this file" : "these files")} for {Plain.JoinAndShort(maps)} earlier, but " +
                $"{(whys.Count == 1 ? whys[0] : "some of the maps have changed since")}. Players still get the old combined " +
                $"version, so {(one ? "that change is" : "those changes are")} missing in game.",
            WhatToDo =
            [
                "Fix it to combine the maps' current versions. The old combined version goes to the backup, so you can still undo.",
                "Or pick one map's version instead.",
            ],
            Details = stale.Select(x => $"{x.Clash.FileName}: {x.Why}").ToList(),
            Resources = clashes[0].Resources,
            Winner = clashes[0].Winner,
            Files = FileList(clashes),
        };
    }

    private static List<FileClash> FileList(List<Clash> clashes) => clashes
        .OrderBy(c => FileTypes.ClashSymptomFor(c.FileName, c.Extension).Rank)
        .ThenBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
        .Select(c => new FileClash(c.FileName, c.Extension, FileTypes.Meaning(c.FileName, c.Extension), c.Copies, c.Winner))
        .ToList();

    private static Finding RealClash(List<Clash> clashes, IReadOnlyList<FixRecord> fixes)
    {
        var resources = clashes[0].Resources;
        var winner = clashes[0].Winner;
        var losers = resources.Where(r => r != winner).Select(r => r.Name).ToList();
        var (severity, _, title, shortTitle) = clashes
            .Select(c => FileTypes.ClashSymptomFor(c.FileName, c.Extension))
            .OrderBy(s => s.Severity).ThenBy(s => s.Rank)
            .First();
        var names = resources.Select(r => r.Name).ToList();
        var fileCount = Plain.Count(clashes.Count, "file");
        var them = clashes.Count == 1 ? "it" : "them";

        var whatsWrong =
            $"{(names.Count == 2 ? "Both maps" : $"All {names.Count} maps")} include {fileCount} with the same name. " +
            $"Only one version of each can load, so {winner.Name} wins because it starts later, " +
            $"and {(losers.Count == 1 ? $"{losers[0]}'s {(clashes.Count == 1 ? "version is" : "versions are")}" : $"the versions in {Plain.JoinAndShort(losers)} are")} ignored.";

        // A copy Map Doctor moved out in an earlier fix is back, which an update or reinstall does.
        var back = clashes.SelectMany(c => c.Copies)
            .Where(f => !Resolver.IsOurs(f.Resource) && fixes.Any(r => r.Moves.Any(m => SamePath(m.From, f.FullPath))))
            .Select(f => f.Resource.Name).Distinct().ToList();
        if (back.Count > 0)
            whatsWrong += $" Map Doctor moved {(back.Count == 1 ? $"{back[0]}'s copy" : $"the copies in {Plain.JoinAnd(back)}")} out in an " +
                          $"earlier fix, but {(back.Count == 1 ? "it's" : "they're")} back, most likely because " +
                          $"{(back.Count == 1 ? "that map was" : "those maps were")} updated or reinstalled.";

        var steps = new List<string>
        {
            $"Check in game around {Plain.JoinAndShort(losers)}. If it looks right, you can ignore this.",
            $"If it doesn't, ask the makers for versions that work together and send {them} the file list.",
            "Or, if you don't need all of these maps, turn one off.",
        };

        var files = FileList(clashes);

        var details = clashes
            .OrderBy(c => FileTypes.ClashRank(c.Extension)).ThenBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(c => $"{c.FileName} ({FileTypes.Describe(c.Extension)}): " +
                         string.Join(", ", c.Copies.Select(f => $"{f.Resource.Name} {f.RelativePath} [{Plain.Size(f.Size)}]")))
            .Prepend($"Start order: {string.Join(" → ", resources.OrderBy(r => r.StartOrder).Select(r => r.Name))} (the last one wins)")
            .ToList();

        return new Finding
        {
            Kind = FindingKind.ClashingFiles,
            Severity = severity,
            Title = title,
            ShortTitle = shortTitle,
            Where = $"Around the {Plain.JoinAndShort(names, noun: "map")}",
            WhatsWrong = whatsWrong,
            WhatToDo = steps,
            Details = details,
            Resources = resources,
            Winner = winner,
            Files = files,
        };
    }

    private static Finding AlreadyFixed(ResourceInfo fixer, List<Clash> clashes)
    {
        var others = clashes.SelectMany(c => c.Resources).Where(r => r != fixer).Distinct()
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var ours = fixer.Name.Equals(Resolver.FixResourceName, StringComparison.OrdinalIgnoreCase);

        return new Finding
        {
            Kind = FindingKind.AlreadyFixed,
            Severity = Severity.Info,
            Title = $"{fixer.Name} is already sorting out {Plain.Count(clashes.Count, "clashing file")}",
            Where = $"{Plain.Count(others.Count, "map")} it combines",
            WhatsWrong =
                $"{fixer.Name} is a fix-up resource{(ours ? " Map Doctor made" : "")}. It contains combined versions of files " +
                "that other maps clash on, and because it starts last, its versions are the ones players get. Nothing to do here.",
            WhatToDo =
            [
                $"Keep {fixer.Name} as the last thing in your start-up list.",
                ours
                    ? "If you update any of the maps it combines, scan again: Map Doctor spots the change and offers to combine them again."
                    : $"If you update any of the maps it combines, rebuild {fixer.Name} so it picks up the changes.",
            ],
            Details = clashes.OrderBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(c => $"{c.FileName}: {string.Join(", ", c.Resources.Where(r => r != fixer).Select(r => r.Name))}")
                .ToList(),
            Resources = [fixer, .. others],
        };
    }

    private static Finding IdenticalCopies(List<Clash> clashes)
    {
        var resources = clashes.SelectMany(c => c.Resources).Distinct()
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        return new Finding
        {
            Kind = FindingKind.IdenticalCopies,
            Severity = Severity.Info,
            Title = $"{Plain.Count(clashes.Count, "file")} {(clashes.Count == 1 ? "is" : "are")} included twice, but the copies match",
            Where = $"Across {Plain.Count(resources.Count, "resource")}",
            WhatsWrong =
                "Some resources ship exactly the same file. That's common with shared props, and it's harmless " +
                "because both copies are identical." +
                (clashes.Any(c => c.Same == Sameness.ProbablySame)
                    ? " Some are locked with Escrow, so we can't compare them exactly, but they're almost exactly the same size, " +
                      "so they're very likely the same model."
                    : ""),
            WhatToDo = ["Nothing to do."],
            Details = clashes.OrderBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(c => $"{c.FileName}: {string.Join(", ", c.Resources.Select(r => r.Name))}" +
                             (c.Same == Sameness.ProbablySame ? " (locked, sizes nearly match)" : ""))
                .ToList(),
            Resources = resources,
        };
    }

    // The same file name twice in one resource's stream folder: only one loads and there's no telling which.
    // Often a maker ships alternatives (say, with and without a car park) and only one is meant to be kept.
    private static IEnumerable<Finding> SameFileTwice(IReadOnlyList<ResourceInfo> resources, ContentComparer hasher, CancellationToken cancel)
    {
        foreach (var resource in resources.Where(r => r.IsStarted))
        {
            cancel.ThrowIfCancellationRequested();
            var twice = resource.StreamFiles
                .Where(f => !PerResourceFiles.Contains(f.FileName))
                .GroupBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.ToList())
                .Where(g => g.Count > 1 && hasher.Compare(g) == Sameness.Different)
                .OrderBy(g => FileTypes.ClashRank(g[0].Extension)).ThenBy(g => g[0].FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (twice.Count == 0) continue;

            var worst = twice.Select(g => FileTypes.ClashSymptomFor(g[0].FileName, g[0].Extension)).OrderBy(x => x.Rank).First();
            yield return new Finding
            {
                Kind = FindingKind.SameFileTwice,
                Severity = Severity.Warning,
                Title = twice.Count == 1
                    ? $"{resource.Name} has two different versions of {twice[0][0].FileName}"
                    : $"{resource.Name} has two different versions of {Plain.Count(twice.Count, "file")}",
                ShortTitle = "Two versions in one map",
                Where = resource.Name,
                WhatsWrong =
                    $"{(twice.Count == 1 ? "This file is" : "These files are")} in two places inside {resource.Name}'s stream folder, " +
                    "and the copies are different. Only one copy can load and there's no telling which. " +
                    $"{worst.Title}. Makers sometimes include two options " +
                    "(for example with or without an extra room) and you're meant to keep just one.",
                WhatToDo =
                [
                    $"Check {resource.Name}'s instructions or readme for which version to use.",
                    "Move the copy you don't want out of the stream folder, then restart your server.",
                    "Not sure? Ask the maker which one is right.",
                ],
                Details = twice.SelectMany(g => g.Select(f => $"{f.FileName}: {f.RelativePath}")).ToList(),
                Resources = [resource],
            };
        }
    }

    // Maps can replace GTA's scenario point lists (where locals stand and what they do). If two maps replace the
    // same list, only the one that starts last is used.
    private static IEnumerable<Finding> ScenarioOverrides(IReadOnlyList<ResourceInfo> resources, List<Clash> streamClashes)
    {
        var groups = resources.Where(r => r.IsStarted)
            .SelectMany(r => r.Manifest.DataFiles
                .Where(d => d.Type.StartsWith("SCENARIO_POINTS_OVERRIDE", StringComparison.OrdinalIgnoreCase))
                .Select(d => (Resource: r, File: Path.GetFileName(d.Path))))
            .GroupBy(x => x.File, StringComparer.OrdinalIgnoreCase)
            .Select(g => (File: g.Key, Resources: g.Select(x => x.Resource).Distinct().OrderBy(r => r.StartOrder).ToList()))
            .Where(g => g.Resources.Count > 1)
            // Already reported as a clash on the same streamed file between the same maps.
            .Where(g => !streamClashes.Any(c => c.FileName.Equals(g.File, StringComparison.OrdinalIgnoreCase)
                                               && g.Resources.All(c.Resources.Contains)))
            .ToList();

        foreach (var group in groups.GroupBy(g => string.Join("|", g.Resources.Select(r => r.Directory))))
        {
            var list = group.ToList();
            var maps = list[0].Resources;
            var winner = maps[^1];
            var losers = maps.Take(maps.Count - 1).Select(r => r.Name).ToList();
            var files = list.Select(g => g.File).Order(StringComparer.OrdinalIgnoreCase).ToList();
            yield return new Finding
            {
                Kind = FindingKind.ScenarioOverrides,
                Severity = Severity.Warning,
                Title = "Locals may be missing or standing in odd places",
                ShortTitle = "Where locals stand",
                Where = $"Around the {Plain.JoinAndShort(maps.Select(r => r.Name).ToList(), noun: "map")}",
                WhatsWrong =
                    $"{(maps.Count == 2 ? "Both maps" : $"All {maps.Count} maps")} replace the same list of where locals stand " +
                    $"and what they do ({Plain.JoinAnd(files)}). Only one list can be used, so {winner.Name}'s wins because it " +
                    $"starts later, and the locals {Plain.JoinAndShort(losers)} set up won't appear.",
                WhatToDo =
                [
                    $"Check in game around {Plain.JoinAndShort(losers)}. If it looks fine, you can ignore this.",
                    "If locals are missing or stuck in walls, ask the makers for a combined version of the file.",
                ],
                Details = maps.SelectMany(r => r.Manifest.DataFiles
                        .Where(d => d.Type.StartsWith("SCENARIO_POINTS_OVERRIDE", StringComparison.OrdinalIgnoreCase)
                                    && files.Contains(Path.GetFileName(d.Path), StringComparer.OrdinalIgnoreCase))
                        .Select(d => $"{r.Name}: {d.Path}"))
                    .Prepend($"Start order: {string.Join(" → ", maps.Select(r => r.Name))} (the last one wins)")
                    .ToList(),
                Resources = maps,
                Winner = winner,
            };
        }
    }

    private enum Sameness { Different, Same, ProbablySame }

    private sealed class Clash(List<StreamFile> copies, Sameness same)
    {
        public List<StreamFile> Copies { get; } = copies;
        public Sameness Same { get; } = same;
        public string FileName => Copies[0].FileName;
        public string Extension => Copies[0].Extension;

        public List<ResourceInfo> Resources { get; } =
            copies.Select(c => c.Resource).OrderBy(r => r.StartOrder ?? int.MaxValue).ToList();

        public ResourceInfo Winner => Resources[^1];

        public string Key => string.Join("|", Resources.Select(r => r.Directory));
    }

    // Checks sizes before reading anything.
    private sealed class ContentComparer
    {
        private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);

        public Sameness Compare(List<StreamFile> copies)
        {
            if (copies.Select(c => c.Size).Distinct().Count() > 1)
                return ProbablySameLocked(copies) ? Sameness.ProbablySame : Sameness.Different;
            var first = Hash(copies[0].FullPath);
            return first is not null && copies.Skip(1).All(c => Hash(c.FullPath) == first) ? Sameness.Same : Sameness.Different;
        }

        // Escrow encrypts models and textures, so two copies of the same model never match byte for byte.
        // When every copy is encrypted and the sizes are within 0.1%, it's almost certainly the same model.
        private static bool ProbablySameLocked(List<StreamFile> copies)
        {
            if (copies[0].Extension is not (".ydr" or ".ydd" or ".yft" or ".ytd")) return false;
            var min = copies.Min(c => c.Size);
            var max = copies.Max(c => c.Size);
            return min > 0 && (max - min) <= max / 1000 && copies.All(c => IsEncrypted(c.FullPath));
        }

        private static bool IsEncrypted(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                Span<byte> head = stackalloc byte[4];
                return stream.Read(head) == 4 && head.SequenceEqual("FXAP"u8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private string? Hash(string path)
        {
            if (_hashes.TryGetValue(path, out var known)) return known;
            try
            {
                using var stream = File.OpenRead(path);
                var hash = Convert.ToHexString(SHA256.HashData(stream));
                _hashes[path] = hash;
                return hash;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
