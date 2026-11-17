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
