using System.Buffers.Binary;
using System.Numerics;
using LGMods.MapDoctor.Core.Reading;

namespace LGMods.MapDoctor.Core.Fixing;

// Combines GTA's distant-light files: how street lamps and lit windows look from far off. They come in pairs,
// x_distlodlights_y.ymap (where each light is and its colour) and x_lodlights_y.ymap (how it shines), listing the
// same lights in the same order. So both files of every copy are read together, and either file of the pair comes
// out with the same lights in the same order, street lights first as GTA expects.
//
// Every map's lights are kept. With three or more copies, a light most of them have but some don't was taken out by
// those, so it stays out.
public static class LightsMerge
{
    private static readonly uint MapData = MetaFile.Hash("CMapData");
    private const int MapDataSize = 512;
    private const int ExtentsField = 0x20, ExtentsCount = 4;
    private const int StreetLightsField = 0x1B0, CategoryField = 0x1B2;

    // (field, item size). The first distant field is where each light is, which is what lines lights up.
    private static readonly (int Field, int Size)[] Distant = [(0x190, 12), (0x1A0, 4)];
    private static readonly (int Field, int Size)[] Near = [(0x108, 12), (0x118, 4), (0x128, 4), (0x138, 4), (0x148, 4), (0x158, 1), (0x168, 1), (0x178, 1)];

    public static bool IsLights(string path) => Path.GetFileName(path).Contains("lodlights", StringComparison.OrdinalIgnoreCase);

    private static bool IsDistant(string path) => Path.GetFileName(path).Contains("distlodlights", StringComparison.OrdinalIgnoreCase);

    public static string Partner(string path)
    {
        var name = Path.GetFileName(path);
        var partner = IsDistant(path)
            ? name.Replace("distlodlights", "lodlights", StringComparison.OrdinalIgnoreCase)
            : name.Replace("lodlights", "distlodlights", StringComparison.OrdinalIgnoreCase);
        return Path.Combine(Path.GetDirectoryName(path) ?? "", partner);
    }

    private sealed record Copy(string Path, MetaFile Distant, int DistantHeader, MetaFile Near, int NearHeader, int Street);

    // basePath is the copy that loads now.
    public static MergedFile Merge(string basePath, IReadOnlyList<string> otherPaths)
    {
        // The others in a fixed order, so both files of a pair line their lights up the same way.
        var copies = otherPaths.Order(StringComparer.OrdinalIgnoreCase).Prepend(basePath).Select(Open).ToList();
        var main = copies[0];
        if (copies.Any(c => c.Distant.U32(c.DistantHeader + CategoryField) != main.Distant.U32(main.DistantHeader + CategoryField)))
            throw new InvalidDataException("The maps set this file up differently, so they can't share one copy.");

        // Every light, by where it is: main's first, then any only the others have. The same spot twice in one copy
        // is two lights, matched up in order.
        var lights = new List<(bool Street, int[] Index)>();
        var bySpot = new Dictionary<string, List<int>>();
        for (var k = 0; k < copies.Count; k++)
        {
            var c = copies[k];
            var seen = new Dictionary<string, int>();
            foreach (var (at, i) in Array(c.Distant, c.DistantHeader, Distant[0]).Select((at, i) => (at, i)))
            {
                var spot = Convert.ToHexString(c.Distant.Bytes(at, 12));
                var nth = seen[spot] = seen.GetValueOrDefault(spot) + 1;
                var same = bySpot.TryGetValue(spot, out var list) ? list : bySpot[spot] = [];
                if (same.Count < nth)
                {
                    lights.Add((i < c.Street, Enumerable.Repeat(-1, copies.Count).ToArray()));
                    same.Add(lights.Count - 1);
                }
                lights[same[nth - 1]].Index[k] = i;
            }
        }

        var tallies = copies.Select(_ => new MapChanges()).ToArray();
        var kept = new List<(int From, int Index, bool Street)>();
        foreach (var (street, index) in lights)
        {
            var have = Enumerable.Range(0, copies.Count).Where(k => index[k] >= 0).ToList();
            if (have.Count < copies.Count && copies.Count >= 3 && have.Count * 2 > copies.Count)
            {
                for (var k = 0; k < copies.Count; k++) if (index[k] < 0) tallies[k].Hide(default);
                continue;
            }
            if (index[0] < 0) foreach (var k in have) tallies[k].Added++;
            kept.Add((have[0], index[have[0]], street));
        }
        kept = kept.Where(l => l.Street).Concat(kept.Where(l => !l.Street)).ToList();

        var distant = IsDistant(basePath);
        var bytes = Build(copies, kept, distant);
        return new MergedFile(bytes) { Lights = true, Total = kept.Count, Changes = copies.Select((c, k) => (c.Path, tallies[k])).ToDictionary(x => x.Path, x => x.Item2) };
    }

    private static byte[] Build(List<Copy> copies, List<(int From, int Index, bool Street)> kept, bool distant)
    {
        var main = copies[0];
        var (file, header, fields) = distant ? (main.Distant, main.DistantHeader, Distant) : (main.Near, main.NearHeader, Near);
        var blocks = file.CopyBlocks();
        var (headerBlock, h) = file.BlockOf(header);
        var head = blocks[headerBlock].Data;

        // Each list rebuilt from whichever copy each light comes from.
        var lists = fields.Select(f => kept.SelectMany(l =>
        {
            var c = copies[l.From];
            var (source, at) = distant ? (c.Distant, c.DistantHeader) : (c.Near, c.NearHeader);
            return source.Bytes(Array(source, at, f).ElementAt(l.Index), f.Size);
        }).ToArray()).ToList();

        // The lists share blocks, one after another on 16-byte boundaries. Each block is written again holding the same
        // lists in the same order.
        var byBlock = fields.Select((f, i) => (Field: f.Field, List: i, Old: file.ArrayAt(header + f.Field)))
            .Where(x => x.Old is not null)
            .GroupBy(x => file.BlockOf(x.Old!.Value.Start).Index);
        foreach (var group in byBlock)
        {
            var ordered = group.OrderBy(x => x.Old!.Value.Start).ToList();
            var oldLength = ordered.Sum(x => Align(x.Old!.Value.Count * fields[x.List].Size));
            if (oldLength != Align(file.Blocks[group.Key].Length) || file.BlockOf(ordered[0].Old!.Value.Start).Offset != 0)
                throw new InvalidDataException($"{Path.GetFileName(main.Path)} isn't laid out like a normal distant-lights file.");
            var data = new byte[Math.Max(16, ordered.Sum(x => Align(lists[x.List].Length)))];
            var offset = 0;
            foreach (var x in ordered)
            {
                lists[x.List].CopyTo(data, offset);
                MetaFile.WriteArray(head, h + x.Field, group.Key, offset, kept.Count);
                offset += Align(lists[x.List].Length);
            }
            blocks[group.Key] = (blocks[group.Key].Type, data);
        }

        if (distant) BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(h + StreetLightsField), (ushort)kept.Count(l => l.Street));
        for (var k = 0; k < ExtentsCount; k++)
        {
            var corner = copies.Select(c => distant ? c.Distant.Vector(c.DistantHeader + ExtentsField + 16 * k) : c.Near.Vector(c.NearHeader + ExtentsField + 16 * k))
                .Aggregate(k % 2 == 0 ? Vector3.Min : Vector3.Max);
            BinaryPrimitives.WriteSingleLittleEndian(head.AsSpan(h + ExtentsField + 16 * k), corner.X);
            BinaryPrimitives.WriteSingleLittleEndian(head.AsSpan(h + ExtentsField + 16 * k + 4), corner.Y);
            BinaryPrimitives.WriteSingleLittleEndian(head.AsSpan(h + ExtentsField + 16 * k + 8), corner.Z);
        }
        return file.Save(blocks);
    }

    private static int Align(int n) => (n + 15) & ~15;

    private static IEnumerable<int> Array(MetaFile file, int header, (int Field, int Size) field) =>
        file.ArrayAt(header + field.Field) is { } array ? file.Array(header + field.Field, array.Type, field.Size) : [];

    private static Copy Open(string path)
    {
        var partner = Partner(path);
        var (distantPath, nearPath) = IsDistant(path) ? (path, partner) : (partner, path);
        var name = Path.GetFileName(path);
        if (!File.Exists(partner))
            throw new InvalidDataException($"{name} goes with {Path.GetFileName(partner)}, which isn't next to it in every map, so the lights can't be lined up.");
        var distant = MetaFile.Read(distantPath) ?? throw new InvalidDataException($"Map Doctor can't read one of the copies of {name} (it may be locked).");
        var near = MetaFile.Read(nearPath) ?? throw new InvalidDataException($"Map Doctor can't read one of the copies of {name} (it may be locked).");
        var distantHeader = distant.Items(MapData, MapDataSize).ToList();
        var nearHeader = near.Items(MapData, MapDataSize).ToList();
        if (distantHeader.Count != 1 || nearHeader.Count != 1)
            throw new InvalidDataException($"{name} isn't laid out like a normal distant-lights file.");

        var count = Array(distant, distantHeader[0], Distant[0]).Count();
        if (Distant.Any(f => Array(distant, distantHeader[0], f).Count() != count) || Near.Any(f => Array(near, nearHeader[0], f).Count() != count))
            throw new InvalidDataException("The two distant-light files in one of the maps don't list the same lights, so they can't be lined up.");
        var street = BinaryPrimitives.ReadUInt16LittleEndian(distant.Bytes(distantHeader[0] + StreetLightsField, 2));
        return new Copy(path, distant, distantHeader[0], near, nearHeader[0], street);
    }
}
