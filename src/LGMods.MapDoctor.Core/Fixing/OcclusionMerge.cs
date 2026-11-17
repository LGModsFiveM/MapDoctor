using System.Buffers.Binary;
using LGMods.MapDoctor.Core.Model;
using LGMods.MapDoctor.Core.Reading;

namespace LGMods.MapDoctor.Core.Fixing;

public sealed record MergedFile(byte[] Bytes, int KeptBoxes, int TotalBoxes, int KeptModels, int TotalModels)
{
    public int Kept => KeptBoxes + KeptModels;
    public int Total => TotalBoxes + TotalModels;
}

// Combines clashing "_occl" .ymap files (which tell GTA what it can skip drawing because something is in front)
// so every map works, not just the one that loads.
//
// When a map adds a building, its maker removes the occluders around it, otherwise GTA thinks the building is
// hidden and stops drawing it. Two maps in the same area each ship their own copy with their own occluders
// removed, and only one copy loads. The combined file keeps only the occluders every copy still has, so nothing
// any map removed comes back. Leaving out an occluder can never hide anything; at worst GTA draws a little more.
//
// The result is the loading copy with occluders taken out, so its layout stays exactly as GTA expects:
// nothing is added, moved or resized.
public static class OcclusionMerge
{
    private static readonly uint MapData = MetaFile.Hash("CMapData");
    private static readonly uint Box = MetaFile.Hash("BoxOccluder");
    private static readonly uint Model = MetaFile.Hash("OccludeModel");

    // The map header, occluders, and raw data/text. Anything else means the file holds more than occlusion.
    private static readonly HashSet<uint> Allowed = [MapData, Box, Model, 0x10, 0x11];

    private const int MapDataSize = 512, BoxesField = 128, ModelsField = 144;
    private const int BoxSize = 16, ModelSize = 64;

    // Two copies of an occluder this close are the same one, saved again by a different tool.
    private const int BoxTolerance = 2;      // in GTA's units (a quarter of a metre for positions)
    private const float ModelTolerance = 0.25f; // metres

    public static bool CanMerge(IReadOnlyList<StreamFile> copies) =>
        copies.Count > 1
        && copies.All(c => c.Extension.Equals(".ymap", StringComparison.OrdinalIgnoreCase))
        && copies.All(c => Open(c.FullPath) is not null);

    // basePath is the copy that loads now.
    public static MergedFile Merge(string basePath, IReadOnlyList<string> otherPaths)
    {
        var main = Open(basePath) ?? throw new InvalidDataException($"{Path.GetFileName(basePath)} can't be combined.");
        var others = otherPaths.Select(p => Open(p) ?? throw new InvalidDataException($"{Path.GetFileName(p)} can't be combined.")).ToList();

        var data = main.File.CopyData();
        var mainBoxes = Boxes(main);
        var otherBoxes = others.Select(Boxes).ToList();
        var keptBoxes = mainBoxes.Where(b => otherBoxes.All(list => list.Any(x => SameBox(b.Value, x.Value)))).ToList();
        var mainModels = Models(main);
        var otherModels = others.Select(Models).ToList();
        var keptModels = mainModels.Where(m => otherModels.All(list => list.Any(x => SameModel(m.Value, x.Value)))).ToList();

        Rewrite(data, main.Header + BoxesField, main.File, keptBoxes.Select(b => b.At).ToList(), BoxSize);
        Rewrite(data, main.Header + ModelsField, main.File, keptModels.Select(m => m.At).ToList(), ModelSize);

        var bytes = main.File.Save(data);
        // Read it back the way the scanner does, so a file we can't read never reaches a server.
        if (MetaFile.Parse(bytes) is not { } check || Open(check, mustHaveOccluders: false) is null)
            throw new InvalidDataException("The combined file didn't check out.");
        return new MergedFile(bytes, keptBoxes.Count, mainBoxes.Count, keptModels.Count, mainModels.Count);
    }

    private sealed record Occlusion(MetaFile File, int Header);

    private static Occlusion? Open(string path) => MetaFile.Read(path) is { } file ? Open(file) : null;

    // Null unless the file holds nothing but occluders.
    private static Occlusion? Open(MetaFile file, bool mustHaveOccluders = true)
    {
        try
        {
            if (file.Blocks.Any(b => !Allowed.Contains(b.Type))) return null;
            var headers = file.Items(MapData, MapDataSize).ToList();
            if (headers.Count != 1) return null;
            var occlusion = new Occlusion(file, headers[0]);
            // Both lists must point where they should (or be empty), and there must be something to combine.
            var boxes = file.ArrayAt(headers[0] + BoxesField);
            var models = file.ArrayAt(headers[0] + ModelsField);
            if (boxes is { } b && b.Type != Box) return null;
            if (models is { } m && m.Type != Model) return null;
            if (mustHaveOccluders && boxes is null && models is null) return null;
            // Read every occluder now so a list running past its block is rejected here, not mid-merge.
            Boxes(occlusion);
            Models(occlusion);
            return occlusion;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    // Eight shorts per box: position, size, turn.
    private static List<(int At, short[] Value)> Boxes(Occlusion o)
    {
        if (o.File.ArrayAt(o.Header + BoxesField) is not { } array) return [];
        return Enumerable.Range(0, array.Count).Select(i =>
        {
            var at = array.Start + i * BoxSize;
            var raw = o.File.Bytes(at, BoxSize);
            var value = new short[8];
            for (var k = 0; k < 8; k++) value[k] = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(k * 2, 2));
            return (at, value);
        }).ToList();
    }

    private static List<(int At, (float[] Bounds, uint Triangles) Value)> Models(Occlusion o)
    {
        if (o.File.ArrayAt(o.Header + ModelsField) is not { } array) return [];
        return Enumerable.Range(0, array.Count).Select(i =>
        {
            var at = array.Start + i * ModelSize;
            float[] bounds = [o.File.F32(at), o.File.F32(at + 4), o.File.F32(at + 8), o.File.F32(at + 16), o.File.F32(at + 20), o.File.F32(at + 24)];
            var triangles = o.File.U32(at + 48) >> 16;
            return (at, (bounds, triangles));
        }).ToList();
    }

    private static bool SameBox(short[] a, short[] b)
    {
        for (var i = 0; i < 8; i++)
            if (Math.Abs(a[i] - b[i]) > BoxTolerance) return false;
        return true;
    }

    private static bool SameModel((float[] Bounds, uint Triangles) a, (float[] Bounds, uint Triangles) b) =>
        a.Triangles == b.Triangles && a.Bounds.Zip(b.Bounds).All(p => Math.Abs(p.First - p.Second) <= ModelTolerance);

    // Packs the kept items at the start of the list, in their original order, and zeroes the rest.
    // An empty list is written the way GTA's own files write one (no address, no count).
    private static void Rewrite(byte[] data, int field, MetaFile file, List<int> keep, int size)
    {
        if (file.ArrayAt(field) is not { } array) return;
        var items = keep.Select(at => file.Bytes(at, size)).ToList();
        Array.Clear(data, array.Start, array.Count * size);
        for (var i = 0; i < items.Count; i++) items[i].CopyTo(data, array.Start + i * size);

        if (items.Count == 0)
        {
            Array.Clear(data, field, 16);
            return;
        }
        // Count, then capacity.
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(field + 8, 2), (ushort)items.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(field + 10, 2), (ushort)items.Count);
    }
}
