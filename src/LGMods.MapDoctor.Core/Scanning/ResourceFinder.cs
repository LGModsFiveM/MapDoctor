using LGMods.MapDoctor.Core.Model;

namespace LGMods.MapDoctor.Core.Scanning;

// Same rules as FiveM: a folder with a manifest is a resource, a folder named like [this] is a category to
// look inside, anything else is ignored.
public static class ResourceFinder
{
    private static readonly string[] ManifestNames = ["fxmanifest.lua", "__resource.lua"];

    public static List<ResourceInfo> Find(string resourcesFolder, CancellationToken cancel = default)
    {
        var found = new List<ResourceInfo>();
        var root = new DirectoryInfo(resourcesFolder);
        Walk(root, [], found, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { RealPath(root) }, cancel);
        return found;
    }

    // Follows links and junctions; a broken link falls back to its own path.
    private static string RealPath(DirectoryInfo dir)
    {
        try { return Path.TrimEndingDirectorySeparator((dir.ResolveLinkTarget(returnFinalTarget: true) ?? dir).FullName); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return dir.FullName; }
    }

    private static void Walk(DirectoryInfo dir, List<string> categories, List<ResourceInfo> found, HashSet<string> visited, CancellationToken cancel)
    {
        IEnumerable<DirectoryInfo> subfolders;
        try { subfolders = dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        foreach (var sub in subfolders)
        {
            cancel.ThrowIfCancellationRequested();
            var name = sub.Name;

            if (name.StartsWith('[') && name.EndsWith(']'))
            {
                // A link that points back to a folder we're already in would go round forever, so each real
                // folder is only looked inside once.
                if (visited.Add(RealPath(sub))) Walk(sub, [.. categories, name], found, visited, cancel);
                continue;
            }

            var manifest = ManifestNames.Select(m => Path.Combine(sub.FullName, m)).FirstOrDefault(File.Exists);
            if (manifest is null) continue;

            found.Add(new ResourceInfo
            {
                Name = name,
                Directory = sub.FullName,
                Categories = categories,
                ManifestPath = manifest,
            });
        }
    }

    public static void Inspect(ResourceInfo resource)
    {
        try
        {
            resource.Manifest = ManifestParser.Parse(File.ReadAllText(resource.ManifestPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            resource.Manifest = ManifestInfo.Empty;
        }

        try
        {
            resource.IsLocked = Directory.EnumerateFiles(resource.Directory, "*.fxap", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // can't tell: treat as unlocked

        var stream = Path.Combine(resource.Directory, "stream");
        if (!Directory.Exists(stream)) return;

        IEnumerable<string> files;
        // Skip folders we can't read instead of giving up on the whole resource, and don't follow links
        // (a link back to a parent folder would go round forever).
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try { files = Directory.EnumerateFiles(stream, "*", options).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        foreach (var path in files)
        {
            var extension = Path.GetExtension(path);
            if (!FileTypes.IsStreamed(extension)) continue;

            long size;
            try { size = new FileInfo(path).Length; }
            catch (IOException) { continue; } // unreadable or gone since listing: skip it

            resource.StreamFiles.Add(new StreamFile(
                resource,
                path,
                Path.GetRelativePath(resource.Directory, path).Replace('\\', '/'),
                Path.GetFileName(path),
                extension.ToLowerInvariant(),
                size));
        }
    }
}
