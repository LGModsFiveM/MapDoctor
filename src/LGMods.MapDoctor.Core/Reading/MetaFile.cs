using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace LGMods.MapDoctor.Core.Reading;

// Reads GTA's "meta" files (.ymap, .ytyp) without CodeWalker. They're RSC7 resources: a 16-byte header,
// then deflate-compressed data holding typed blocks of fixed-size items. Escrow leaves these readable,
// so locked maps work too. Every read is bounds-checked; anything that doesn't look right is rejected.
public sealed class MetaFile
{
    private const int MaxSize = 64 * 1024 * 1024;
    private readonly byte[] _data;
    private readonly byte[] _header;
    private readonly List<(uint Type, int Offset, int Length)> _blocks = [];

    private MetaFile(byte[] header, byte[] data)
    {
        _header = header;
        _data = data;
        var blocks = Offset(U64(0x30));
        var count = BinaryPrimitives.ReadInt16LittleEndian(Span(0x4C, 2));
        for (var i = 0; i < count; i++)
        {
            var at = blocks + 16 * i;
            var type = U32(at);
            var length = BinaryPrimitives.ReadInt32LittleEndian(Span(at + 4, 4));
            var offset = Offset(U64(at + 8));
            if (length < 0 || (long)offset + length > _data.Length) throw new InvalidDataException("Block outside the file.");
            _blocks.Add((type, offset, length));
        }
    }

    // Null if it isn't a readable meta file (locked, damaged or a different kind).
    public static MetaFile? Read(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return Read(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static MetaFile? Parse(byte[] file)
    {
        using var stream = new MemoryStream(file, writable: false);
        return Read(stream);
    }

    private static MetaFile? Read(Stream file)
    {
        try
        {
            var header = new byte[16];
            if (file.ReadAtLeast(header, 16, throwOnEndOfStream: false) != 16 || !header.AsSpan(0, 4).SequenceEqual("RSC7"u8)) return null;
            using var inflate = new DeflateStream(file, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = inflate.Read(buffer)) > 0)
            {
                if (output.Length + read > MaxSize) return null;
                output.Write(buffer, 0, read);
            }
            return new MetaFile(header, output.ToArray());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // GTA's name hash (Jenkins one-at-a-time), case-sensitive as meta type names are.
    public static uint Hash(string name)
    {
        uint h = 0;
        foreach (var c in Encoding.UTF8.GetBytes(name))
        {
            h += c;
            h += h << 10;
            h ^= h >> 6;
        }
        h += h << 3;
        h ^= h >> 11;
        h += h << 15;
        return h;
    }

    public IEnumerable<int> Items(uint type, int size)
    {
        foreach (var block in _blocks.Where(b => b.Type == type))
            for (var i = 0; i + size <= block.Length; i += size)
                yield return block.Offset + i;
    }

    // Array and text fields are a pointer (block index + 1 in the low 12 bits, offset in the next 20)
    // followed by a count or length at +8.
    public IEnumerable<int> Array(int field, uint type, int size)
    {
        var pointer = U32(field);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(Span(field + 8, 2));
        var index = (int)(pointer & 0xFFF) - 1;
        if (count == 0 || index < 0 || index >= _blocks.Count || _blocks[index].Type != type) yield break;
        var start = (int)((pointer >> 12) & 0xFFFFF);
        var block = _blocks[index];
        for (var i = 0; i < count && start + (i + 1) * size <= block.Length; i++)
            yield return block.Offset + start + i * size;
    }

    public string Text(int field, uint textType)
    {
        var pointer = U32(field);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(Span(field + 8, 2));
        var index = (int)(pointer & 0xFFF) - 1;
        if (length == 0 || index < 0 || index >= _blocks.Count || _blocks[index].Type != textType) return "";
        var start = (int)((pointer >> 12) & 0xFFFFF);
        if (start + length > _blocks[index].Length) return "";
        return Encoding.UTF8.GetString(Span(_blocks[index].Offset + start, length)).TrimEnd('\0');
    }

    public IReadOnlyList<(uint Type, int Offset, int Length)> Blocks => _blocks;

    public (int Start, int Count, uint Type)? ArrayAt(int field)
    {
        var pointer = U32(field);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(Span(field + 8, 2));
        var index = (int)(pointer & 0xFFF) - 1;
        if (count == 0 || index < 0 || index >= _blocks.Count) return null;
        var start = (int)((pointer >> 12) & 0xFFFFF);
        return (_blocks[index].Offset + start, count, _blocks[index].Type);
    }

    public byte[] Bytes(int at, int length) => Span(at, length).ToArray();

    // Same layout, so every address in it stays valid as long as nothing grows.
    public byte[] CopyData() => (byte[])_data.Clone();

    // Reuses this file's RSC7 header, which is only valid if the data is the same size.
    public byte[] Save(byte[] data)
    {
        if (data.Length != _data.Length) throw new InvalidDataException("The data changed size.");
        using var output = new MemoryStream();
        output.Write(_header);
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(data);
        return output.ToArray();
    }

    public uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(Span(at, 4));

    public float F32(int at) => BinaryPrimitives.ReadSingleLittleEndian(Span(at, 4));

    public Vector3 Vector(int at) => new(F32(at), F32(at + 4), F32(at + 8));

    public Quaternion Rotation(int at) => new(F32(at), F32(at + 4), F32(at + 8), F32(at + 12));

    private ulong U64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(Span(at, 8));

    private ReadOnlySpan<byte> Span(int at, int length)
    {
        if (at < 0 || at + length > _data.Length) throw new InvalidDataException("Read outside the file.");
        return _data.AsSpan(at, length);
    }

    // In-game addresses 0x5xxxxxxx point into the main data.
    private int Offset(ulong address)
    {
        if (address >> 28 != 5 || (address & 0x0FFFFFFF) >= (ulong)_data.Length) throw new InvalidDataException("Bad address.");
        return (int)(address & 0x0FFFFFFF);
    }
}
