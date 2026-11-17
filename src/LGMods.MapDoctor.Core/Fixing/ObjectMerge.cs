using System.Buffers.Binary;
using System.Numerics;
using LGMods.MapDoctor.Core.Reading;

namespace LGMods.MapDoctor.Core.Fixing;

// Combines clashing .ymap files that place objects (roads, kerbs, props), keeping every map's changes.
//
// There's no original file to compare against, so each change has to be put down to a map. Objects are lined up
// across the copies (by model and id, or by model and spot when a tool renumbered the ids), then for each one:
//  - With three or more copies, what most of them have is taken as the original, and the others' changes are kept.
//  - Otherwise a change counts when it's clear who made it: an object one map sank out of sight (well below the
//    ground around it, or off to 0,0,0) or took out, or one a map added after the last object they all share.
//  - Saving again can shift an object a few centimetres; that's the same placement.
// Hiding wins, so every map's space stays clear. Where maps changed the same object in different ways there's no
// telling which is right, so the copy that loads now keeps its version, and that's counted so it can be pointed out.
// Parked cars and time-cycle zones are kept or left out the same way, put down to whichever map changed objects
// near them.
public static class ObjectMerge
{
    private static readonly uint MapData = MetaFile.Hash("CMapData");
    private static readonly uint Entity = MetaFile.Hash("CEntityDef");
    private static readonly uint MloInstance = MetaFile.Hash("CMloInstanceDef");
    private static readonly uint CarGen = MetaFile.Hash("CCarGen");
    private static readonly uint TimeCycle = MetaFile.Hash("CTimeCycleModifier");
    private const uint Pointers = 7, Hashes = 0x4A;

    private const int MapDataSize = 512, EntitySize = 128, PointerSize = 8, CarGenSize = 80, TimeCycleSize = 64;
    private const int NameField = 0x08, ParentField = 0x0C, ContentField = 0x14;
    // Four corners: where the file starts streaming in, and where its objects are (min, max of each).
    private const int ExtentsField = 0x20, ExtentsCount = 4;
    private const int EntitiesField = 0x60, PhysicsField = 0xA0, TimeCycleField = 0xE0, CarGensField = 0xF0;

    // The rest of the header has to match in every copy: settings, and lists this doesn't combine (field, item size,
    // what it is). Size 0: items hold pointers, so they can't be compared, and any at all means no.
    private static readonly (int At, int Size)[] Settings = [(0x10, 4), (0x18, 8), (0xB0, 16), (0x100, 8), (0x188, 8), (0x1B0, 8)];
    private static readonly (int Field, int Size, string What)[] Lists =
    [
        (0x70, 8, "interior links"), (0x80, 16, "hiding spots"), (0x90, 0, "hiding spots"), (0xC0, 0, "scattered props"),
        (0xD0, 0, "grass"), (0x108, 12, "distant lights"), (0x118, 4, "distant lights"), (0x128, 4, "distant lights"),
        (0x138, 4, "distant lights"), (0x148, 4, "distant lights"), (0x158, 1, "distant lights"), (0x168, 1, "distant lights"),
        (0x178, 1, "distant lights"), (0x190, 12, "distant lights"), (0x1A0, 4, "distant lights"),
    ];

    private const int ArchetypeField = 8, GuidField = 16, PositionField = 32, LodDistField = 76, ChildrenField = 88, ExtensionsField = 96;
    // Entity fields, for combining different maps' changes to different fields of one object.
    private static readonly (int At, int Size)[] Fields =
        [(0, 32), (32, 32), (64, 8), (72, 4), (76, 4), (80, 4), (84, 4), (88, 4), (92, 4), (104, 2), (112, 16)];

    private const float SamePlace = 1f, Sunk = 5f, Near = 100f; // metres

    private sealed class Copy(string path, MetaFile file, int header, List<int> entities)
    {
        public string Path { get; } = path;
        public MetaFile File { get; } = file;
        public int Header { get; } = header;
        public List<int> Entities { get; } = entities;

        // Everything but its guid, which some tools hand out afresh on every object they save, and where its extensions
        // (lights and effects) happen to be stored, which differs between saves.
        public List<byte[]> Content { get; } = entities.Select(at =>
        {
            var bytes = file.Bytes(at, EntitySize);
            bytes.AsSpan(GuidField, 4).Clear();
            bytes.AsSpan(ExtensionsField, 8).Clear();
            bytes.AsSpan(ExtensionsField + 10, 6).Clear();
            return bytes;
        }).ToList();

        public Vector3 Position(int i) => File.Vector(Entities[i] + PositionField);
        public uint Children(int i) => File.U32(Entities[i] + ChildrenField);
    }

    // basePath is the copy that loads now.
    public static MergedFile Merge(string basePath, IReadOnlyList<string> otherPaths)
    {
        var copies = otherPaths.Prepend(basePath).Select(Open).ToList();
        var main = copies[0];
        var named = CheckHeaders(copies);

        var rows = Align(copies);
        var tallies = copies.Select(_ => new MapChanges()).ToArray();
        var agreed = rows.Where(r => r.All(i => i >= 0) && Enumerable.Range(1, copies.Count - 1).All(k => Alike(copies[k].Content[r[k]], main.Content[r[0]])))
            .Select(r => main.Position(r[0])).ToList();
        var lastShared = rows.FindLastIndex(r => r.All(i => i >= 0));
        var kept = new List<(int[] Row, byte[] Entity)>();
        var nudged = 0;
        var conflicts = 0;

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var present = Enumerable.Range(0, copies.Count).Where(k => row[k] >= 0).ToList();
            var absent = Enumerable.Range(0, copies.Count).Where(k => row[k] < 0).ToList();
            var spot = copies[present[0]].Position(row[present[0]]);

            if (absent.Count > 0)
            {
                // Taken out by the maps without it, or added by the ones with it?
                var removed = copies.Count >= 3 && present.Count != absent.Count ? present.Count > absent.Count : r <= lastShared;
                if (removed)
                {
                    foreach (var k in absent) tallies[k].Hide(spot);
                    if (present.Any(k => copies[k].Children(row[k]) > 0)) throw Linked();
                    continue;
                }
                foreach (var k in present) tallies[k].Add(spot);
            }

            var content = Choose(copies, row, present, agreed, tallies, ref nudged, ref conflicts);
            kept.Add((row, Output(copies, row, content, ref conflicts)));
        }

        // Other map files point at objects in this one by their position in the list: none of those may move.
        for (var o = 0; o < kept.Count; o++)
            for (var k = 0; k < copies.Count; k++)
                if (kept[o].Row[k] is var i and >= 0 && i != o && copies[k].Children(i) > 0) throw Linked();

        GiveUniqueGuids(kept);
        return Build(copies, named, kept.Select(x => x.Entity).ToList(), tallies, nudged, conflicts);
    }

    private static InvalidDataException Linked() => new(
        "Other map files point into this one by position (for distance detail), and the maps add or take out objects in it, " +
        "so combining it would break those links.");

    // Which version of an object to keep, out of the copies that have it, and whose change that is.
    private static byte[] Choose(List<Copy> copies, int[] row, List<int> present, List<Vector3> agreed,
        MapChanges[] tallies, ref int nudged, ref int conflicts)
    {
        byte[] Of(List<int> version) => copies[version[0]].Content[row[version[0]]];
        var versions = new List<List<int>>();
        foreach (var k in present)
        {
            if (versions.FirstOrDefault(v => Alike(Of(v), copies[k].Content[row[k]])) is { } same) same.Add(k);
            else versions.Add([k]);
        }
        Vector3 Spot(List<int> version) => copies[version[0]].Position(row[version[0]]);
        // Maps hide an object by moving it to the middle of the map, sinking it, or making it draw only from a metre away.
        bool Hidden(List<int> version) => Spot(version).Length() < SamePlace || Spot(version).Z < GroundNear(agreed, Spot(version)) - Sunk ||
                                          Float(Of(version), LodDistField) is > 0 and <= SamePlace;
        var mine = versions.FirstOrDefault(v => v.Contains(0)) ?? versions[0];
        var spot = versions.Select(Spot).FirstOrDefault(s => s.Length() >= SamePlace);

        if (versions.Count == 1) return Of(versions[0]);

        var counts = versions.Select(v => v.Count).ToList();
        var original = copies.Count >= 3 && counts.Count(c => c == counts.Max()) == 1 ? versions[counts.IndexOf(counts.Max())] : null;
        if (original is not null)
        {
            var changed = versions.Where(v => v != original).ToList();
            if (changed.Count == 1 || changed.All(Hidden))
            {
                var pick = changed.FirstOrDefault(v => v.Contains(0)) ?? changed[0];
                foreach (var version in changed)
                    foreach (var k in version)
                        if (Hidden(version)) tallies[k].Hide(spot); else tallies[k].Change(spot);
                return Of(pick);
            }
            // Different maps changed different things about it: take each one's change.
            var result = (byte[])Of(original).Clone();
            foreach (var (at, size) in Fields)
            {
                var values = changed.Select(Of).Where(v => !Alike(v, Of(original), at, size)).ToList();
                if (values.Any(v => !Alike(v, values[0], at, size)))
                {
                    conflicts++;
                    return Of(mine);
                }
                if (values.Count > 0) values[0].AsSpan(at, size).CopyTo(result.AsSpan(at));
            }
            foreach (var k in changed.SelectMany(v => v)) tallies[k].Change(spot);
            return result;
        }

        var hidden = versions.Where(Hidden).ToList();
        if (hidden.Count == versions.Count) return Of(mine);
        if (hidden.Count > 0)
        {
            foreach (var k in hidden.SelectMany(v => v)) tallies[k].Hide(spot);
            return Of(hidden.FirstOrDefault(v => v.Contains(0)) ?? hidden[0]);
        }
        if (versions.All(v => SamePlacement(Of(v), Of(versions[0])))) nudged++;
        else conflicts++;
        return Of(mine);
    }

    // The entity as it goes in the combined file. Its extensions stay where the loading copy keeps them, so a version
    // from another copy is only taken if it has the same number of them.
    private static byte[] Output(List<Copy> copies, int[] row, byte[] content, ref int conflicts)
    {
        var main = copies[0];
        var extensions = BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(ExtensionsField + 8));
        if (row[0] >= 0)
        {
            var bytes = main.File.Bytes(main.Entities[row[0]], EntitySize);
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(ExtensionsField + 8)) != extensions)
            {
                conflicts++;
                return bytes;
            }
            content.AsSpan(0, GuidField).CopyTo(bytes);
            content.AsSpan(GuidField + 4, ExtensionsField - GuidField - 4).CopyTo(bytes.AsSpan(GuidField + 4));
            content.AsSpan(ExtensionsField + 16).CopyTo(bytes.AsSpan(ExtensionsField + 16));
            return bytes;
        }
        if (extensions > 0)
            throw new InvalidDataException("A map adds objects with lights or effects attached here, and Map Doctor can't copy those between files.");
        var added = (byte[])content.Clone();
        var from = Array.FindIndex(row, i => i >= 0);
        BinaryPrimitives.WriteUInt32LittleEndian(added.AsSpan(GuidField), copies[from].File.U32(copies[from].Entities[row[from]] + GuidField));
        return added;
    }

    // An added object can carry a guid the file already uses for another, since each map handed its own out.
    private static void GiveUniqueGuids(List<(int[] Row, byte[] Entity)> kept)
    {
        var used = new HashSet<uint>();
        foreach (var (row, entity) in kept.Where(x => x.Row[0] >= 0))
            used.Add(BinaryPrimitives.ReadUInt32LittleEndian(entity.AsSpan(GuidField)));
        foreach (var (row, entity) in kept.Where(x => x.Row[0] < 0))
        {
            var guid = BinaryPrimitives.ReadUInt32LittleEndian(entity.AsSpan(GuidField));
            while (guid == 0 || !used.Add(guid)) guid = guid * 2654435761u + 1;
            BinaryPrimitives.WriteUInt32LittleEndian(entity.AsSpan(GuidField), guid);
        }
    }

    private static MergedFile Build(List<Copy> copies, int named, List<byte[]> entities, MapChanges[] tallies, int nudged, int conflicts)
    {
        var main = copies[0];
        var blocks = main.File.CopyBlocks();
        var (headerBlock, h) = main.File.BlockOf(main.Header);
        var header = blocks[headerBlock].Data;

        copies[named].File.Bytes(copies[named].Header + NameField, 8).CopyTo(header, h + NameField);
        var content = copies.Aggregate(0u, (all, c) => all | c.File.U32(c.Header + ContentField));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(h + ContentField), content);
        // Each kept object comes from one copy and sits inside that copy's area, so together they cover them all.
        for (var k = 0; k < ExtentsCount; k++)
        {
            var corner = copies.Select(c => c.File.Vector(c.Header + ExtentsField + 16 * k)).Aggregate(k % 2 == 0 ? Vector3.Min : Vector3.Max);
            BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(h + ExtentsField + 16 * k), corner.X);
            BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(h + ExtentsField + 16 * k + 4), corner.Y);
            BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(h + ExtentsField + 16 * k + 8), corner.Z);
        }

        // Objects go back into the entity blocks there are, up to 128 a block as CodeWalker saves them, with more
        // blocks if needed. Any block left over keeps its old contents, which nothing points at any more.
        var slots = Enumerable.Range(0, blocks.Count).Where(b => blocks[b].Type == Entity).ToList();
        var pointers = new byte[PointerSize * entities.Count];
        for (int e = 0, s = 0; e < entities.Count; s++)
        {
            var index = s < slots.Count ? slots[s] : blocks.Count;
            var room = s < slots.Count ? Math.Max(blocks[index].Data.Length / EntitySize, 128) : 128;
            var take = Math.Min(room, entities.Count - e);
            var data = new byte[take * EntitySize];
            for (var i = 0; i < take; i++)
            {
                entities[e + i].CopyTo(data, i * EntitySize);
                BinaryPrimitives.WriteUInt32LittleEndian(pointers.AsSpan(PointerSize * (e + i)), MetaFile.PointerTo(index, i * EntitySize));
            }
            if (index == blocks.Count) blocks.Add((Entity, data)); else blocks[index] = (Entity, data);
            e += take;
        }
        SetList(main, blocks, header, h, EntitiesField, Pointers, PointerSize, pointers);

        var physics = copies.SelectMany(c => Items(c, PhysicsField, 4)).Distinct(ByteArrays.Instance).SelectMany(x => x).ToArray();
        SetList(main, blocks, header, h, PhysicsField, Hashes, 4, physics);
        SetList(main, blocks, header, h, CarGensField, CarGen, CarGenSize, MergeItems(copies, CarGensField, CarGenSize, 16, tallies, ref conflicts));
        SetList(main, blocks, header, h, TimeCycleField, TimeCycle, TimeCycleSize, MergeItems(copies, TimeCycleField, TimeCycleSize, 16, tallies, ref conflicts));

        var bytes = main.File.Save(blocks, copies.Skip(1).Select(c => c.File).ToArray());
        try
        {
            if (MetaFile.Parse(bytes) is not { } check || Read(check, "check").Entities.Count != entities.Count) throw new InvalidDataException();
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException("The combined file didn't check out.", e);
        }
        return new MergedFile(bytes)
        {
            Objects = true,
            Changes = copies.Select((c, k) => (c.Path, tallies[k])).ToDictionary(x => x.Path, x => x.Item2),
            Nudged = nudged,
            Conflicts = conflicts,
        };
    }

    // Replaces a list in the header. If the old one had a block to itself it's reused; otherwise (say it shares a block
    // with other lists) it goes in a new block and the old space is left unused.
    private static void SetList(Copy main, List<(uint Type, byte[] Data)> blocks, byte[] header, int h, int field, uint type, int size, byte[] items)
    {
        var count = items.Length / size;
        if (main.File.ArrayAt(main.Header + field) is { } old)
        {
            var (index, offset) = main.File.BlockOf(old.Start);
            var current = Items(main, field, size).SelectMany(x => x).ToArray();
            if (current.AsSpan().SequenceEqual(items)) return;
            if (offset == 0 && old.Count * size == main.File.Blocks[index].Length && count > 0)
            {
                blocks[index] = (blocks[index].Type, items);
                MetaFile.WriteArray(header, h + field, index, 0, count);
                return;
            }
            type = main.File.Blocks[index].Type;
        }
        if (count == 0)
        {
            MetaFile.WriteArray(header, h + field, 0, 0, 0);
            return;
        }
        blocks.Add((type, items));
        MetaFile.WriteArray(header, h + field, blocks.Count - 1, 0, count);
    }

    private static List<byte[]> Items(Copy c, int field, int size) =>
        c.File.ArrayAt(c.Header + field) is { } array
            ? c.File.Array(c.Header + field, array.Type, size).Select(at => c.File.Bytes(at, size)).ToList()
            : [];

    // Whole items such as parked cars: kept or left out one at a time. With three or more copies the majority is
    // the original; otherwise it's down to whichever map changed objects near it, or the loading copy if that's unclear.
    private static byte[] MergeItems(List<Copy> copies, int field, int size, int positionAt, MapChanges[] tallies, ref int conflicts)
    {
        var lists = copies.Select(c => Items(c, field, size)).ToList();
        var kept = new List<byte[]>();
        foreach (var item in lists.SelectMany(l => l).Distinct(ByteArrays.Instance))
        {
            var has = lists.Select(l => l.Any(x => Same(x, item))).ToList();
            var count = has.Count(x => x);
            bool keep;
            if (count == copies.Count) keep = true;
            else if (copies.Count >= 3 && count * 2 != copies.Count) keep = count * 2 < copies.Count;
            else
            {
                var at = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(item.AsSpan(positionAt)),
                    BinaryPrimitives.ReadSingleLittleEndian(item.AsSpan(positionAt + 4)), 0);
                var near = Enumerable.Range(0, copies.Count)
                    .Where(k => tallies[k].Points.Any(p => Vector2.Distance(new(p.X, p.Y), new(at.X, at.Y)) < Near)).ToList();
                if (near.Count == 1) keep = has[near[0]];
                else
                {
                    keep = has[0];
                    conflicts++;
                }
            }
            if (keep) kept.Add(item);
            if (has[0] != keep)
                for (var k = 1; k < copies.Count; k++)
                    if (has[k] == keep) tallies[k].Extras++;
        }
        return kept.SelectMany(x => x).ToArray();
    }

    // Lines the copies' objects up: each row holds, for each copy, the index of its version of that object or -1.
    // Rows follow the loading copy's order, with objects only other copies have slotted in where they appear.
    private static List<int[]> Align(List<Copy> copies)
    {
        var rows = Enumerable.Range(0, copies[0].Entities.Count).Select(i =>
        {
            var row = Enumerable.Repeat(-1, copies.Count).ToArray();
            row[0] = i;
            return row;
        }).ToList();

        for (var k = 1; k < copies.Count; k++)
        {
            var c = copies[k];
            bool Match(int[] row, int j)
            {
                var p = System.Array.FindIndex(row, i => i >= 0);
                var other = copies[p];
                var a = other.Entities[row[p]];
                var b = c.Entities[j];
                if (other.File.U32(a + ArchetypeField) != c.File.U32(b + ArchetypeField)) return false;
                var guid = c.File.U32(b + GuidField);
                if (guid != 0 && guid == other.File.U32(a + GuidField)) return true;
                var x = other.Position(row[p]);
                var y = c.Position(j);
                return Vector2.Distance(new(x.X, x.Y), new(y.X, y.Y)) < SamePlace;
            }

            // Longest common run of matching objects.
            var n = rows.Count;
            var m = c.Entities.Count;
            var match = new bool[n, m];
            var table = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    table[i, j] = (match[i, j] = Match(rows[i], j)) ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);

            var aligned = new List<int[]>();
            int ri = 0, ci = 0;
            while (ri < n || ci < m)
            {
                if (ri < n && ci < m && match[ri, ci] && table[ri, ci] == table[ri + 1, ci + 1] + 1)
                {
                    rows[ri][k] = ci++;
                    aligned.Add(rows[ri++]);
                }
                else if (ci == m || ri < n && table[ri + 1, ci] >= table[ri, ci + 1]) aligned.Add(rows[ri++]);
                else
                {
                    var row = Enumerable.Repeat(-1, copies.Count).ToArray();
                    row[k] = ci++;
                    aligned.Add(row);
                }
            }
            rows = aligned;
        }
        return rows;
    }

    // The ground around a spot: low, but not the lowest, of the objects every copy agrees on nearby.
    private static float GroundNear(List<Vector3> agreed, Vector3 at)
    {
        foreach (var radius in new[] { 150f, 500f, float.MaxValue })
        {
            var near = agreed.Where(p => Vector2.Distance(new(p.X, p.Y), new(at.X, at.Y)) <= radius).Select(p => p.Z).Order().ToList();
            if (near.Count >= 5 || radius == float.MaxValue && near.Count > 0) return near[near.Count / 10];
        }
        return float.MinValue; // nothing agreed on, so nothing counts as sunk
    }

    // Saved again a little differently: same object, same everything but a position within a metre.
    private static bool SamePlacement(byte[] a, byte[] b) =>
        Vector3.Distance(new(Float(a, PositionField), Float(a, PositionField + 4), Float(a, PositionField + 8)),
                         new(Float(b, PositionField), Float(b, PositionField + 4), Float(b, PositionField + 8))) <= SamePlace &&
        Alike(a, b, 0, PositionField) && Alike(a, b, PositionField + 12, EntitySize - PositionField - 12);

    // Saving a map again in another tool rounds its numbers a little differently and can write a rotation as its
    // negative (the same turn), so an object counts as unchanged if it matches to within a hair.
    private static readonly int[] Floats = [32, 36, 40, 64, 68, 76, 80];
    private const int RotationField = 48;

    private static bool Alike(byte[] a, byte[] b, int at = 0, int size = EntitySize)
    {
        // Numbers are compared below; the four bytes after the position are unused filler, which tools fill differently.
        bool Loose(int i) => i >= PositionField + 12 && i < RotationField + 16 || Floats.Any(f => i >= f && i < f + 4);
        for (var i = at; i < at + size; i++)
            if (!Loose(i) && a[i] != b[i]) return false;
        if (Floats.Any(f => f >= at && f < at + size && Math.Abs(Float(a, f) - Float(b, f)) > 0.001f)) return false;
        if (RotationField < at || RotationField >= at + size) return true;

        var x = new Quaternion(Float(a, 48), Float(a, 52), Float(a, 56), Float(a, 60));
        var y = new Quaternion(Float(b, 48), Float(b, 52), Float(b, 56), Float(b, 60));
        return Math.Abs(Quaternion.Dot(x, y)) >= 0.999999f * x.Length() * y.Length();
    }

    private static float Float(byte[] bytes, int at) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at));

    // Returns which copy's name to use. Copies saved by different tools can disagree on the name and parent inside;
    // the right one is named after the file.
    private static int CheckHeaders(List<Copy> copies)
    {
        var main = copies[0];
        foreach (var other in copies.Skip(1))
        {
            foreach (var (at, size) in Settings)
                if (!Same(other.File.Bytes(other.Header + at, size), main.File.Bytes(main.Header + at, size)))
                    throw new InvalidDataException("The maps set this file up differently, so they can't share one copy.");
            foreach (var (field, size, what) in Lists)
            {
                var mine = main.File.ArrayAt(main.Header + field)?.Count ?? 0;
                var theirs = other.File.ArrayAt(other.Header + field)?.Count ?? 0;
                var same = mine == theirs && (mine == 0 || size > 0 &&
                    Items(other, field, size).SelectMany(x => x).SequenceEqual(Items(main, field, size).SelectMany(x => x)));
                if (!same) throw new InvalidDataException($"The maps also change the {what} in this file, which Map Doctor can't combine.");
            }
        }

        var names = copies.Select(c => c.File.U32(c.Header + NameField) | (ulong)c.File.U32(c.Header + ParentField) << 32).ToList();
        if (names.All(n => n == names[0])) return 0;
        var expected = MetaFile.Hash(System.IO.Path.GetFileNameWithoutExtension(main.Path).ToLowerInvariant());
        var right = copies.FindIndex(c => c.File.U32(c.Header + NameField) == expected);
        return right >= 0 ? right
            : throw new InvalidDataException("The copies give different names inside for this file, so there's no telling which is right.");
    }

    private static Copy Open(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var file = MetaFile.Read(path) ?? throw new InvalidDataException($"Map Doctor can't read one of the copies of {name} (it may be locked).");
        return Read(file, path);
    }

    private static Copy Read(MetaFile file, string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var odd = new InvalidDataException($"{name} isn't laid out like a normal map file.");
        var headers = file.Items(MapData, MapDataSize).ToList();
        if (headers.Count != 1) throw odd;

        // The entity list is a list of pointers, each to an object or an interior.
        var array = file.ArrayAt(headers[0] + EntitiesField);
        var pointers = array is null ? [] : file.Array(headers[0] + EntitiesField, Pointers, PointerSize).ToList();
        if (pointers.Count != (array?.Count ?? 0)) throw odd;
        var entities = new List<int>();
        foreach (var pointer in pointers)
        {
            var target = file.Pointer(pointer, EntitySize);
            if (target?.Type == MloInstance) throw new InvalidDataException($"{name} places interiors, and two interiors can't share a spot.");
            if (target is not { } t || t.Type != Entity) throw odd;
            entities.Add(t.At);
        }
        return new Copy(path, file, headers[0], entities);
    }

    private static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

    private sealed class ByteArrays : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrays Instance = new();
        public bool Equals(byte[]? a, byte[]? b) => a is not null && b is not null && Same(a, b);
        public int GetHashCode(byte[] bytes)
        {
            var hash = new HashCode();
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }
    }
}
