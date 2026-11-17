using System.Numerics;
using LGMods.MapDoctor.Core.Model;

namespace LGMods.MapDoctor.Core.Fixing;

public sealed record MergedFile(byte[] Bytes)
{
    // Occlusion files: hiding spots kept, out of the loading copy's total.
    public int Kept { get; init; }
    public int Total { get; init; }

    // Object and distant-light files: what each copy (by path) brought to the combined one.
    public bool Objects { get; init; }
    public bool Lights { get; init; }
    public bool Occlusion => !Objects && !Lights;
    public IReadOnlyDictionary<string, MapChanges> Changes { get; init; } = new Dictionary<string, MapChanges>();

    // Placements saved again less than a metre apart, and objects maps changed in different ways (the loading
    // copy's version is kept for those).
    public int Nudged { get; init; }
    public int Conflicts { get; init; }
}

// What one map changed, as far as combining could tell.
public sealed class MapChanges
{
    public int Hidden { get; private set; }
    public int Added { get; internal set; }
    public int Changed { get; private set; }

    // Parked cars and lighting zones.
    public int Extras { get; internal set; }

    // Where its object changes are, to tell whose a parked car is.
    internal List<Vector3> Points { get; } = [];

    internal void Hide(Vector3 at) { Hidden++; Points.Add(at); }
    internal void Add(Vector3 at) { Added++; Points.Add(at); }
    internal void Change(Vector3 at) { Changed++; Points.Add(at); }
}

// Looks inside clashing copies of a file and combines them into one that works for every map, when that's safe.
// When it isn't, the exception says why in plain English, so it can be shown as it is.
public static class FileMerge
{
    // basePath is the copy that loads now.
    public static MergedFile Merge(string basePath, IReadOnlyList<string> otherPaths)
    {
        var extension = Path.GetExtension(basePath);
        if (!extension.Equals(".ymap", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"This is a {FileTypes.Describe(extension)} file, and only map placement files can be combined.");
        if (LightsMerge.IsLights(basePath)) return LightsMerge.Merge(basePath, otherPaths);
        return otherPaths.Prepend(basePath).All(OcclusionMerge.IsOcclusion)
            ? OcclusionMerge.Merge(basePath, otherPaths)
            : ObjectMerge.Merge(basePath, otherPaths);
    }
}
