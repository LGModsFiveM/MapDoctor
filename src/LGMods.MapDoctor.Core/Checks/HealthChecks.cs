using LGMods.MapDoctor.Core.Fixing;
using LGMods.MapDoctor.Core.Model;
using LGMods.MapDoctor.Core.Text;

namespace LGMods.MapDoctor.Core.Checks;

public static class HealthChecks
{
    // FiveM warns about streamed files above this size; they can fail to load for players.
    public const long LargeFileLimit = 16L * 1024 * 1024;

    public static IEnumerable<Finding> DuplicateResourceNames(IReadOnlyList<ResourceInfo> resources)
    {
        foreach (var group in resources.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var copies = group.ToList();
            var used = copies.FirstOrDefault(c => c.UsedInstead is null) ?? copies[0];
            string Place(ResourceInfo c) => string.IsNullOrEmpty(c.CategoryLabel) ? "your main resources folder" : c.CategoryLabel;
            yield return new Finding
            {
                Kind = FindingKind.DuplicateResourceName,
                Severity = Severity.Problem,
                Title = $"You have {copies.Count} folders called \"{group.Key}\"",
                Where = Plain.JoinAndShort(copies.Select(Place).Distinct().ToList()),
                WhatsWrong =
                    "FiveM can only run one resource with each name. It uses the first folder it finds, which here is " +
                    $"the one in {Place(used)}, and ignores the {(copies.Count == 2 ? "other" : "others")}. " +
                    "If that isn't the version you meant to run, players are getting the wrong one.",
                WhatToDo =
                [
                    "Decide which copy you want to keep (usually the newest one).",
                    "Delete or move the other copies out of your resources folder.",
                ],
                Details = copies.Select(c => (c == used ? "Used: " : "Ignored: ") + c.Directory).ToList(),
                Resources = copies,
            };
        }
    }

    public static IEnumerable<Finding> WontStart(IReadOnlyList<ResourceInfo> resources)
    {
        var refused = resources.Where(r => r.WontStart is not null).ToList();

        // Missing or broken dependencies: one finding per resource, because each needs its own download.
        foreach (var resource in refused.Where(r => r.WontStart!.Kind is StartProblemKind.MissingNeeds or StartProblemKind.NeedsBrokenResource))
        {
            var names = resource.WontStart!.Names;
            var isMap = resource.StreamFiles.Count > 0;
            var missing = resource.WontStart.Kind == StartProblemKind.MissingNeeds;
            yield return new Finding
            {
                Kind = FindingKind.MissingDependency,
                Severity = isMap ? Severity.Problem : Severity.Warning,
                Title = $"{resource.Name} won't start because something it needs is {(missing ? "missing" : "broken")}",
                Where = resource.Name,
                WhatsWrong = missing
                    ? $"{resource.Name} needs {Plain.JoinAnd(names.Select(m => $"\"{m}\""))} to work, " +
                      $"but {(names.Count == 1 ? "it isn't" : "they aren't")} in your resources folder. FiveM won't start it without " +
                      (names.Count == 1 ? "it." : "them.")
                    : $"{resource.Name} needs {Plain.JoinAnd(names)}, but {(names.Count == 1 ? "that doesn't" : "those don't")} start " +
                      $"either, so FiveM won't start {resource.Name}. Fix {Plain.JoinAnd(names)} first (it's listed separately).",
                WhatToDo = missing
                    ?
                    [
                        $"Download {Plain.JoinAnd(names)} from wherever you got {resource.Name} (it's usually listed on the product page).",
                        "Put it in your resources folder, then restart your server.",
                    ]
                    :
                    [
                        $"Sort out {Plain.JoinAnd(names)} first. Once {(names.Count == 1 ? "it starts" : "they start")}, {resource.Name} will too.",
                    ],
                Details = names.Select(m => $"Needed by {Path.GetFileName(resource.ManifestPath)}: {m}").ToList(),
                Resources = [resource],
            };
        }

        var notForGta = refused.Where(r => r.WontStart!.Kind == StartProblemKind.NotForGta5)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (notForGta.Count > 0)
        {
            var maps = notForGta.Count(r => r.StreamFiles.Count > 0);
            yield return new Finding
            {
                Kind = FindingKind.WontStart,
                Severity = Severity.Warning,
                Title = $"{Plain.Count(notForGta.Count, "resource is", "resources are")} in your start-up list but FiveM refuses to start {(notForGta.Count == 1 ? "it" : "them")}",
                Where = Plain.JoinAndMore(notForGta.Select(r => r.Name).ToList()),
                WhatsWrong =
                    "Their fxmanifest.lua doesn't say they're for GTA V (the game 'gta5' line is missing, or the whole file " +
                    "is commented out), so FiveM skips them and players never see them." +
                    (maps > 0 ? " We've left them out of the clash checks, because they never load." : ""),
                WhatToDo =
                [
                    "If you switched them off on purpose, that's fine. To keep things tidy, move them out of your resources folder.",
                    "If you didn't, open fxmanifest.lua in Notepad, make sure the lines don't start with --, and that it has a line: game 'gta5'",
                ],
                Details = notForGta.Select(r => r.ManifestPath).ToList(),
                Resources = notForGta,
            };
        }

        foreach (var group in refused.Where(r => r.WontStart!.Kind == StartProblemKind.NeedsNewerGame).GroupBy(r => r.WontStart!.Names[0]))
        {
            var list = group.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            yield return new Finding
            {
                Kind = FindingKind.WontStart,
                Severity = list.Any(r => r.StreamFiles.Count > 0) ? Severity.Problem : Severity.Warning,
                Title = $"{Plain.Count(list.Count, "resource needs", "resources need")} a newer game version",
                Where = Plain.JoinAndMore(list.Select(r => r.Name).ToList()),
                WhatsWrong =
                    $"{(list.Count == 1 ? "It needs" : "They need")} game build {group.Key}, but your server.cfg sets an older one " +
                    "(sv_enforceGameBuild), so FiveM won't start " + (list.Count == 1 ? "it." : "them."),
                WhatToDo =
                [
                    $"In server.cfg, change the sv_enforceGameBuild line to {group.Key} or higher, then restart your server.",
                ],
                Details = list.Select(r => r.ManifestPath).ToList(),
                Resources = list,
            };
        }
    }

    public static IEnumerable<Finding> MissingSetupFiles(IReadOnlyList<ResourceInfo> resources)
    {
        // Model lists (ytyp) are found by name wherever they're streamed, so collect every streamed name.
        var streamedNames = new HashSet<string>(
            resources.Where(r => r.IsStarted).SelectMany(r => r.StreamFiles).Select(f => f.FileName),
            StringComparer.OrdinalIgnoreCase);

        var affected = new List<(ResourceInfo Resource, List<string> Missing)>();
        foreach (var resource in resources.Where(r => r.IsStarted && r.StreamFiles.Count > 0))
        {
            var missing = resource.Manifest.DataFiles
                .Where(d => IsMissingModelList(resource, d.Type, d.Path, streamedNames))
                .Select(d => d.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (missing.Count > 0) affected.Add((resource, missing));
        }
        if (affected.Count == 0) yield break;

        var names = affected.Select(a => a.Resource.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        yield return new Finding
        {
            Kind = FindingKind.MissingSetupFile,
            Severity = Severity.Warning,
            Title = $"Some objects may be invisible in {Plain.Count(affected.Count, "resource")}",
            Where = Plain.JoinAndMore(names),
            WhatsWrong =
                "These resources ask the game to load a model list (the file that tells GTA about their objects), " +
                "but that file isn't anywhere on your server. Objects that rely on it may not appear.",
            WhatToDo =
            [
                "Re-download the resource and replace your copy. A file may have been missed when it was uploaded.",
                "If it still happens, let the maker know which file is missing (it's listed under \"Show details\").",
            ],
            Details = affected
                .OrderBy(a => a.Resource.Name, StringComparer.OrdinalIgnoreCase)
                .SelectMany(a => a.Missing.Select(m => $"{a.Resource.Name}: {m}"))
                .ToList(),
            Resources = affected.Select(a => a.Resource).ToList(),
        };
    }

    // Only reports a model list FiveM definitely can't find. FiveM looks these up by name wherever they're streamed,
    // and ".ityp" or paths without "stream/" usually mean GTA's own built-in lists.
    private static bool IsMissingModelList(ResourceInfo resource, string type, string path, HashSet<string> streamedNames)
    {
        if (!type.Equals("DLC_ITYP_REQUEST", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Contains('*') || path.StartsWith('@')) return false;
        if (!path.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase)) return false;
        if (!path.StartsWith("stream/", StringComparison.OrdinalIgnoreCase)) return false;
        if (streamedNames.Contains(Path.GetFileName(path))) return false;
        return !File.Exists(Path.Combine(resource.Directory, path));
    }

    public static IEnumerable<Finding> MissingMapSetting(IReadOnlyList<ResourceInfo> resources)
    {
        var affected = resources
            .Where(r => r.IsStarted && !r.Manifest.ThisIsAMap && r.StreamFiles.Any(f => f.Extension == ".ymap"))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (affected.Count == 0) yield break;

        yield return new Finding
        {
            Kind = FindingKind.MissingMapSetting,
            Severity = Severity.Warning,
            Title = $"{Plain.Count(affected.Count, "map")} may not show up properly after a restart",
            Where = Plain.JoinAndMore(affected.Select(r => r.Name).ToList()),
            WhatsWrong =
                "These resources place things in the world but don't tell FiveM they're maps. " +
                "That can stop them loading properly, especially after restarting them while the server is running.",
            WhatToDo =
            [
                "Open each one's fxmanifest.lua file in Notepad.",
                "Add this line on its own: this_is_a_map 'yes'",
                "Save the file and restart your server.",
            ],
            Details = affected.Select(r => r.ManifestPath).ToList(),
            Resources = affected,
        };
    }

    public static IEnumerable<Finding> LargeFiles(IReadOnlyList<ResourceInfo> resources)
    {
        var large = resources.Where(r => r.IsStarted)
            .SelectMany(r => r.StreamFiles)
            .Where(f => f.Size > LargeFileLimit)
            .OrderByDescending(f => f.Size)
            .ToList();
        if (large.Count == 0) yield break;

        var owners = large.Select(f => f.Resource).Distinct().ToList();
        yield return new Finding
        {
            Kind = FindingKind.LargeFiles,
            Severity = Severity.Warning,
            Title = $"{Plain.Count(large.Count, "file is", "files are")} very big and may load slowly for players",
            Where = Plain.JoinAndMore(owners.Select(r => r.Name).ToList()),
            WhatsWrong =
                "FiveM struggles with single files over 16 MB. Players can see blurry textures, missing models or " +
                "objects popping in late, especially on slower connections.",
            WhatToDo =
            [
                "Check whether the maker has an optimised version of these resources.",
                "If you made them yourself, shrink the textures (e.g. 4K down to 2K) or split the file up.",
            ],
            Details = large.Select(f => $"{f.Resource.Name}/{f.RelativePath}: {Plain.Size(f.Size)}").ToList(),
            Resources = owners,
        };
    }

    public static IEnumerable<Finding> MapsSwitchedOff(IReadOnlyList<ResourceInfo> resources)
    {
        // Leave out maps FiveM refuses to start and ignored duplicate folders; they have their own findings.
        var off = resources.Where(r => !r.IsStarted && r.ChangesTheWorld && r.WontStart is null && r.UsedInstead is null)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        if (off.FirstOrDefault(r => r.Name.Equals(Resolver.FixResourceName, StringComparison.OrdinalIgnoreCase)) is { } ours)
        {
            off.Remove(ours);
            yield return new Finding
            {
                Kind = FindingKind.MapsSwitchedOff,
                Severity = Severity.Warning,
                Title = "The files Map Doctor combined aren't switched on",
                Where = ours.Name,
                WhatsWrong =
                    $"Map Doctor put combined versions of clashing files in {ours.Name}, but your server never starts it, " +
                    "so players still get the clashes.",
                WhatToDo =
                [
                    $"Add \"{Resolver.StartLine}\" as the last line of your server.cfg, then restart your server.",
                    "Or, if you don't want the combined files any more, undo that fix in \"Undo fixes\".",
                ],
                Details = [ours.Directory],
                Resources = [ours],
            };
        }
        if (off.Count == 0) yield break;

        yield return new Finding
        {
            Kind = FindingKind.MapsSwitchedOff,
            Severity = Severity.Info,
            Title = $"{Plain.Count(off.Count, "map is", "maps are")} in your folder but switched off",
            Where = Plain.JoinAndMore(off.Select(r => r.Name).ToList()),
            WhatsWrong =
                "Your server's start-up list never starts these, so players won't see them. " +
                "That's fine if you turned them off on purpose. We haven't checked them for clashes.",
            WhatToDo =
            [
                "If you want one of them, add a line like \"ensure its-name\" to your server.cfg.",
            ],
            Details = off.Select(r => r.Directory).ToList(),
            Resources = off,
        };
    }
}
