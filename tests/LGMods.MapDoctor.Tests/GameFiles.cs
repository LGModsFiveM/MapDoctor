using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using LGMods.MapDoctor.Core.Reading;

namespace LGMods.MapDoctor.Tests;

// Tiny but real-format .ymap/.ytyp files (RSC7, compressed, typed blocks).
public static class GameFiles
{
    private sealed class Builder
    {
        private readonly List<(uint Type, byte[] Data)> _blocks = [];

        public int Add(uint type, byte[] data)
        {
            _blocks.Add((type, data));
            return _blocks.Count; // 1-based block id, as GTA stores it
        }

        public byte[] Build()
        {
            const int table = 0x100;
            var position = table + 16 * _blocks.Count;
            var offsets = new List<int>();
            foreach (var (_, data) in _blocks)
            {
                position = (position + 15) & ~15;
                offsets.Add(position);
                position += data.Length;
            }

            var raw = new byte[position];
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(0x30), 0x50000000UL + table);
            BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(0x4C), (short)_blocks.Count);
            for (var i = 0; i < _blocks.Count; i++)
            {
                var at = table + 16 * i;
                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(at), _blocks[i].Type);
                BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(at + 4), _blocks[i].Data.Length);
                BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(at + 8), 0x50000000UL + (ulong)offsets[i]);
                _blocks[i].Data.CopyTo(raw, offsets[i]);
            }

            using var output = new MemoryStream();
            output.Write("RSC7"u8);
            output.Write(new byte[12]);
            using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(raw);
            return output.ToArray();
        }
    }

    private static void Vector(byte[] data, int at, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at), v.X);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at + 4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at + 8), v.Z);
    }

    public static byte[] Ymap(string interior, Vector3 position, float turnDegrees = 0)
    {
        var item = new byte[160];
        BinaryPrimitives.WriteUInt32LittleEndian(item.AsSpan(8), MetaFile.Hash(interior));
        Vector(item, 32, position);
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turnDegrees * MathF.PI / 180);
        Vector(item, 48, new Vector3(q.X, q.Y, q.Z));
        BinaryPrimitives.WriteSingleLittleEndian(item.AsSpan(60), q.W);

        var builder = new Builder();
        builder.Add(MetaFile.Hash("CMloInstanceDef"), item);
        return builder.Build();
    }

    // Like GTA's "_occl" files: nothing but box occluders, each eight shorts
    // (centre x, y, z, turn, length, width, height, turn).
    public static byte[] Occlusion(params short[][] boxes)
    {
        var header = new byte[512];
        var data = new byte[16 * boxes.Length];
        for (var i = 0; i < boxes.Length; i++)
            for (var k = 0; k < 8; k++)
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(16 * i + 2 * k), boxes[i][k]);

        var builder = new Builder();
        builder.Add(MetaFile.Hash("CMapData"), header);     // block 1
        builder.Add(MetaFile.Hash("BoxOccluder"), data);    // block 2
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(128), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(136), (ushort)boxes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(138), (ushort)boxes.Length);
        return builder.Build();
    }

    // Like GTA's road and prop files: a header, the placed objects, and the list of pointers to them.
    // Object i is "prop{i}" with id i + 1, so copies saved from the same file line up.
    public static byte[] Objects(params Vector3[] positions) => Objects(positions.Select((p, i) => ($"prop{i}", p)).ToArray());

    // Each object's name is its model, and its guid comes from the name, like the game's own files.
    public static byte[] Objects(params (string Name, Vector3 At)[] objects)
    {
        var positions = objects.Select(o => o.At).ToArray();
        var header = new byte[512];
        var items = new byte[128 * positions.Length];
        var pointers = new byte[8 * positions.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(items.AsSpan(128 * i + 8), MetaFile.Hash(objects[i].Name));
            BinaryPrimitives.WriteUInt32LittleEndian(items.AsSpan(128 * i + 16), MetaFile.Hash(objects[i].Name + "guid"));
            Vector(items, 128 * i + 32, positions[i]);
            BinaryPrimitives.WriteSingleLittleEndian(items.AsSpan(128 * i + 60), 1); // facing the default way
            BinaryPrimitives.WriteSingleLittleEndian(items.AsSpan(128 * i + 64), 1);
            BinaryPrimitives.WriteSingleLittleEndian(items.AsSpan(128 * i + 68), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(pointers.AsSpan(8 * i), 2u | (uint)(128 * i) << 12);
        }
        var min = positions.Aggregate(Vector3.Min);
        var max = positions.Aggregate(Vector3.Max);
        Vector(header, 0x20, min);
        Vector(header, 0x30, max);
        Vector(header, 0x40, min);
        Vector(header, 0x50, max);

        var builder = new Builder();
        builder.Add(MetaFile.Hash("CMapData"), header);     // block 1
        builder.Add(MetaFile.Hash("CEntityDef"), items);    // block 2
        builder.Add(7, pointers);                           // block 3: the entity list points at each object
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x60), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x68), (ushort)positions.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x6A), (ushort)positions.Length);
        return builder.Build();
    }

    // The same file as another tool saves it: new guids, the rotation written as its negative (the same turn),
    // rounding in the last digit and filler in the unused slot.
    public static byte[] Resaved(byte[] file)
    {
        var meta = MetaFile.Parse(file)!;
        var data = meta.CopyData();
        foreach (var at in meta.Items(MetaFile.Hash("CEntityDef"), 128))
        {
            var span = data.AsSpan(at);
            BinaryPrimitives.WriteUInt32LittleEndian(span[16..], BinaryPrimitives.ReadUInt32LittleEndian(span[16..]) ^ 0x5A5A5A5A);
            BinaryPrimitives.WriteUInt32LittleEndian(span[44..], 0x7F800001);
            for (var n = 0; n < 4; n++)
                BinaryPrimitives.WriteSingleLittleEndian(span[(48 + 4 * n)..], -BinaryPrimitives.ReadSingleLittleEndian(span[(48 + 4 * n)..]));
            BinaryPrimitives.WriteSingleLittleEndian(span[64..], 0.99999994f);
        }
        return meta.Save(data);
    }

    // One of a distant-light pair: distlodlights holds where each light is, lodlights how it shines (here, a hash
    // made from where it is, so tests can check both files list the lights in the same order).
    public static byte[] Lights(bool distant, params Vector3[] lights)
    {
        var header = new byte[512];
        var builder = new Builder();
        builder.Add(MetaFile.Hash("CMapData"), header);
        void List(int field, int size, Func<Vector3, byte[]> item)
        {
            var block = builder.Add(0x15, lights.SelectMany(item).ToArray());
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(field), (uint)block);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(field + 8), (ushort)lights.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(field + 10), (ushort)lights.Length);
        }
        byte[] Bytes(int size, uint value) => BitConverter.GetBytes(value).Concat(new byte[12]).Take(size).ToArray();
        if (distant)
        {
            List(0x190, 12, l => { var b = new byte[12]; Vector(b, 0, l); return b; });
            List(0x1A0, 4, _ => Bytes(4, 0xFFFFFFFF));
        }
        else
            foreach (var (field, size) in new[] { (0x108, 12), (0x118, 4), (0x128, 4), (0x138, 4), (0x148, 4), (0x158, 1), (0x168, 1), (0x178, 1) })
                List(field, size, l => Bytes(size, field == 0x148 ? LightHash(l) : 0));
        var min = lights.Aggregate(Vector3.Min);
        var max = lights.Aggregate(Vector3.Max);
        Vector(header, 0x20, min);
        Vector(header, 0x30, max);
        Vector(header, 0x40, min);
        Vector(header, 0x50, max);
        return builder.Build();
    }

    public static uint LightHash(Vector3 at) => MetaFile.Hash($"{at.X},{at.Y},{at.Z}");

    public static List<Vector3> LightPositions(byte[] file) =>
        LightList(file, 0x190, 12, (m, at) => m.Vector(at)).ToList();

    public static List<uint> LightHashes(byte[] file) =>
        LightList(file, 0x148, 4, (m, at) => m.U32(at)).ToList();

    private static IEnumerable<T> LightList<T>(byte[] file, int field, int size, Func<MetaFile, int, T> read)
    {
        var meta = MetaFile.Parse(file)!;
        var header = meta.Items(MetaFile.Hash("CMapData"), 512).Single();
        return meta.Array(header + field, meta.ArrayAt(header + field)!.Value.Type, size).Select(at => read(meta, at));
    }

    public static List<Vector3> Positions(byte[] file)
    {
        var meta = MetaFile.Parse(file) ?? throw new InvalidDataException("Not readable");
        var header = meta.Items(MetaFile.Hash("CMapData"), 512).Single();
        return meta.Array(header + 0x60, meta.ArrayAt(header + 0x60)!.Value.Type, 8)
            .Select(pointer => meta.Vector(meta.Pointer(pointer, 128)!.Value.At + 32)).ToList();
    }

    public static (Vector3 Min, Vector3 Max) Extents(byte[] file)
    {
        var meta = MetaFile.Parse(file) ?? throw new InvalidDataException("Not readable");
        var header = meta.Items(MetaFile.Hash("CMapData"), 512).Single();
        return (meta.Vector(header + 0x20), meta.Vector(header + 0x30));
    }

    public static int BoxCount(byte[] file)
    {
        var meta = MetaFile.Parse(file) ?? throw new InvalidDataException("Not readable");
        var header = meta.Items(MetaFile.Hash("CMapData"), 512).Single();
        return meta.ArrayAt(header + 128)?.Count ?? 0;
    }

    // One interior made of the given rooms, plus a "limbo" room like real ones have.
    public static byte[] Ytyp(string interior, params (Vector3 Min, Vector3 Max)[] rooms)
    {
        var builder = new Builder();
        var names = new List<string> { "limbo" };
        names.AddRange(rooms.Select((_, i) => $"room{i}"));
        var text = Encoding.UTF8.GetBytes(string.Join("\0", names) + "\0");
        var archetype = new byte[240];
        var roomData = new byte[112 * names.Count];

        builder.Add(MetaFile.Hash("CMloArchetypeDef"), archetype);       // block 1
        builder.Add(MetaFile.Hash("CMloRoomDef"), roomData);             // block 2
        builder.Add(0x10, text);                                         // block 3

        BinaryPrimitives.WriteUInt32LittleEndian(archetype.AsSpan(88), MetaFile.Hash(interior));
        BinaryPrimitives.WriteUInt32LittleEndian(archetype.AsSpan(168), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(archetype.AsSpan(176), (ushort)names.Count);

        var textAt = 0;
        for (var i = 0; i < names.Count; i++)
        {
            var at = 112 * i;
            BinaryPrimitives.WriteUInt32LittleEndian(roomData.AsSpan(at + 8), 3u | ((uint)textAt << 12));
            BinaryPrimitives.WriteUInt16LittleEndian(roomData.AsSpan(at + 16), (ushort)names[i].Length);
            var (min, max) = i == 0 ? (new Vector3(-500), new Vector3(500)) : rooms[i - 1]; // limbo is huge, and ignored
            Vector(roomData, at + 32, min);
            Vector(roomData, at + 48, max);
            textAt += names[i].Length + 1;
        }
        return builder.Build();
    }
}
