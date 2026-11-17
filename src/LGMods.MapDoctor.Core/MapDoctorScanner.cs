using System.Diagnostics;
using LGMods.MapDoctor.Core.Checks;
using LGMods.MapDoctor.Core.Model;
using LGMods.MapDoctor.Core.Scanning;
using LGMods.MapDoctor.Core.Text;

namespace LGMods.MapDoctor.Core;

// Read-only: a scan never changes any file.
public static class MapDoctorScanner
{
    // The user may pick the server folder (with server.cfg and resources inside) or the resources folder itself.
    public static string? FindResourcesFolder(string picked)
    {
        if (!Directory.Exists(picked)) return null;

        // Picked a [category] inside the resources folder? Check the whole resources folder instead: clashes are
        // between all maps on the server, and which copy wins depends on server.cfg, which is further up.
        var climbed = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(picked)));
        while (climbed.Name.StartsWith('[') && climbed.Name.EndsWith(']') && climbed.Parent is { } up) climbed = up;
        if (climbed.FullName != Path.TrimEndingDirectorySeparator(Path.GetFullPath(picked))) picked = climbed.FullName;

        var inside = Path.Combine(picked, "resources");
        if (Directory.Exists(inside)) return inside;
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(picked)).Equals("resources", StringComparison.OrdinalIgnoreCase))
            return picked;

        // A folder that holds resources or [categories] directly.
        try
        {
            var looksLikeResources = Directory.EnumerateDirectories(picked).Take(500).Any(d =>
                Path.GetFileName(d).StartsWith('[') ||
                File.Exists(Path.Combine(d, "fxmanifest.lua")) ||
                File.Exists(Path.Combine(d, "__resource.lua")));
            return looksLikeResources ? picked : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static ScanResult Scan(string pickedFolder, IProgress<ScanProgress>? progress = null, CancellationToken cancel = default)
    {
        var clock = Stopwatch.StartNew();
        var resourcesFolder = FindResourcesFolder(pickedFolder)
            ?? throw new DirectoryNotFoundException("We couldn't find any FiveM resources in that folder.");

        progress?.Report(new ScanProgress("Finding your resources…", 0.02));
        var resources = ResourceFinder.Find(resourcesFolder, cancel);

        for (var i = 0; i < resources.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            if (i % 10 == 0)
                progress?.Report(new ScanProgress($"Looking at {resources[i].Name}… ({i + 1:N0} of {resources.Count:N0})",
                    0.05 + 0.6 * i / Math.Max(1, resources.Count)));
            ResourceFinder.Inspect(resources[i]);
        }

        progress?.Report(new ScanProgress("Reading your server's start-up list…", 0.68));
        var configFile = StartListReader.FindConfig(pickedFolder) ?? StartListReader.FindConfig(resourcesFolder);
        var findings = new List<Finding>();
        ApplyStartOrder(resources, configFile, findings);

        progress?.Report(new ScanProgress("Checking for clashes between maps…", 0.75));
        findings.AddRange(ClashCheck.Run(resources, cancel));

        progress?.Report(new ScanProgress("Checking for interiors built in the same spot…", 0.85));
        findings.AddRange(SameSpotCheck.Run(resources, cancel));

        progress?.Report(new ScanProgress("Running health checks…", 0.92));
        findings.AddRange(HealthChecks.DuplicateResourceNames(resources));
        findings.AddRange(HealthChecks.WontStart(resources));
        findings.AddRange(HealthChecks.MissingSetupFiles(resources));
        findings.AddRange(HealthChecks.MissingMapSetting(resources));
        findings.AddRange(HealthChecks.LargeFiles(resources));
        findings.AddRange(HealthChecks.MapsSwitchedOff(resources));

        progress?.Report(new ScanProgress("Done", 1));
        return new ScanResult
        {
            ResourcesFolder = resourcesFolder,
            StartListFile = configFile,
            Resources = resources,
            Findings = findings.OrderBy(f => f.Severity).ThenBy(f => f.Kind).ToList(),
            Duration = clock.Elapsed,
        };
    }

    private static void ApplyStartOrder(List<ResourceInfo> resources, string? configFile, List<Finding> findings)
    {
        if (configFile is null)
        {
            // Without a start-up list, assume everything runs, in folder order.
            var everything = new StartOrder(resources, gameBuild: null);
            foreach (var r in everything.Used) everything.Start(r);
            findings.Add(new Finding
            {
                Kind = FindingKind.NoStartList,
                Severity = Severity.Info,
                Title = "We couldn't find your server's start-up list",
                Where = "server.cfg",
                WhatsWrong =
                    "We looked for server.cfg next to your resources folder but couldn't find it, so we've assumed " +
                    "every map is switched on. Some results may mention maps you don't actually use.",
                WhatToDo = ["Pick the folder that contains server.cfg and scan again for the most accurate results."],
            });
            return;
        }

        // Only used to find folders for "exec @resource/..." lines, before the game build is known.
        var folders = new StartOrder(resources, gameBuild: null);
        var list = StartListReader.Read(configFile, name => folders.Find(name)?.Directory);
        var starter = new StartOrder(resources, list.GameBuild);

        var mentionedButMissing = new List<StartCommand>();
        foreach (var command in list.Commands)
        {
            var targets = command.IsCategory
                ? starter.InCategory(command.Target).ToList()
                : starter.Find(command.Target) is { } single ? [single] : [];

            if (targets.Count == 0)
            {
                if (command.Action == StartAction.Start && !StartOrder.BuiltIn.Contains(command.Target))
                    mentionedButMissing.Add(command);
                continue;
            }

            foreach (var target in targets)
            {
                if (command.Action == StartAction.Stop) starter.Stop(target);
                else starter.Start(target);
            }
        }

        if (mentionedButMissing.Count > 0)
        {
            var names = mentionedButMissing.Select(c => c.Target).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            findings.Add(new Finding
            {
                Kind = FindingKind.StartListMentionsMissing,
                Severity = Severity.Info,
                Title = $"Your start-up list mentions {Plain.Count(names.Count, "thing")} that {(names.Count == 1 ? "isn't" : "aren't")} in your resources folder",
                Where = Path.GetFileName(configFile),
                WhatsWrong =
                    "Your server tries to start these but can't find them. Some may come built into FiveM, " +
                    "so this is only worth a look if something you expected is missing in game.",
                WhatToDo = ["If you removed these on purpose, delete their lines from your config to tidy up."],
                Details = mentionedButMissing
                    .Select(c => $"{Path.GetFileName(c.SourceFile)} line {c.LineNumber}: {c.Target}")
                    .ToList(),
            });
        }
    }
}
