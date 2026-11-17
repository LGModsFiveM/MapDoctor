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

    // A pointer to one item, laid out like an array field's pointer. Lists of mixed items (a map's entities)
    // are arrays of these. Null if it doesn't point at a whole item.
    public (int At, uint Type)? Pointer(int at, int size)
    {
        var pointer = U32(at);
        var index = (int)(pointer & 0xFFF) - 1;
        if (index < 0 || index >= _blocks.Count) return null;
        var start = (int)((pointer >> 12) & 0xFFFFF);
        if (start + size > _blocks[index].Length) return null;
        return (_blocks[index].Offset + start, _blocks[index].Type);
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

    public List<(uint Type, byte[] Data)> CopyBlocks() => _blocks.Select(b => (b.Type, Bytes(b.Offset, b.Length))).ToList();

    // Which block an address is in, and how far into it.
    public (int Index, int Offset) BlockOf(int at)
    {
        var index = _blocks.FindIndex(b => at >= b.Offset && at < b.Offset + b.Length);
        if (index < 0) throw new InvalidDataException("Address outside every block.");
        return (index, at - _blocks[index].Offset);
    }

    public static uint PointerTo(int block, int offset) => (uint)(block + 1) | (uint)offset << 12;

    public static void WriteArray(byte[] owner, int field, int block, int offset, int count)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(owner.AsSpan(field), count == 0 ? 0 : PointerTo(block, offset));
        BinaryPrimitives.WriteUInt32LittleEndian(owner.AsSpan(field + 4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(owner.AsSpan(field + 8), (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(owner.AsSpan(field + 10), (ushort)count);
    }

    // Lays the whole file out again, for when blocks change size or new ones are added (at the end, so every block
    // keeps its number). Blocks point at each other by number and offset, so only the header pieces, which use
    // addresses, need moving. Type descriptions this file lacks are taken from the others, for added block types.
    public byte[] Save(IReadOnlyList<(uint Type, byte[] Data)> blocks, params MetaFile[] others)
    {
        var structs = Infos(this, 0x20, 0x48, 32, 0x10, 0x1E, 16)
            .Concat(others.SelectMany(o => Infos(o, 0x20, 0x48, 32, 0x10, 0x1E, 16)))
            .DistinctBy(s => BinaryPrimitives.ReadUInt32LittleEndian(s.Info)).ToList();
        var enums = Infos(this, 0x28, 0x4A, 24, 0x08, 0x10, 8)
            .Concat(others.SelectMany(o => Infos(o, 0x28, 0x4A, 24, 0x08, 0x10, 8)))
            .DistinctBy(e => BinaryPrimitives.ReadUInt32LittleEndian(e.Info)).ToList();

        // GTA fills in the page list when it loads the file; 128 entries is room for the most pages a file can have.
        const int pageListSize = 16 + 8 * 128;
        var chunks = new List<byte[]>
        {
            Bytes(0, 0x70),
            new byte[pageListSize],
            new byte[32 * structs.Count],
            new byte[24 * enums.Count],
            new byte[16 * blocks.Count],
        };
        chunks.AddRange(structs.Select(s => s.Entries));
        chunks.AddRange(enums.Select(e => e.Entries));
        var name = U64(0x38) == 0 ? null : NameAt(Offset(U64(0x38)));
        if (name is not null) chunks.Add(name);
        var firstBlock = chunks.Count;
        chunks.AddRange(blocks.Select(b => b.Data));

        var (at, pageSize, pages, lastPage) = Lay(chunks.Select(c => c.Length).ToList());
        var data = new byte[(pages - 1) * pageSize + lastPage];
        for (var i = 0; i < chunks.Count; i++) chunks[i].CopyTo(data, at[i]);

        if (blocks.Any(b => b.Data.Length == 0)) throw new InvalidDataException("Empty block.");
        ulong Address(int chunk) => 0x50000000UL + (ulong)at[chunk];
        void Pointer(int where, ulong address) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(where), address);

        Pointer(0x08, Address(1));
        data[at[1] + 8] = (byte)pages;
        Pointer(0x20, structs.Count == 0 ? 0 : Address(2));
        Pointer(0x28, enums.Count == 0 ? 0 : Address(3));
        Pointer(0x30, Address(4));
        Pointer(0x38, name is null ? 0 : Address(firstBlock - 1));
        Pointer(0x40, 0); // the "useless data" some tools add: nothing reads it, so it isn't carried over
        data[0x16] = 0;
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x48), (short)structs.Count);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x4A), (short)enums.Count);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x4C), (short)blocks.Count);
        for (var i = 0; i < structs.Count; i++)
        {
            structs[i].Info.CopyTo(data, at[2] + 32 * i);
            Pointer(at[2] + 32 * i + 0x10, structs[i].Entries.Length == 0 ? 0 : Address(5 + i));
        }
        for (var i = 0; i < enums.Count; i++)
        {
            enums[i].Info.CopyTo(data, at[3] + 24 * i);
            Pointer(at[3] + 24 * i + 0x08, enums[i].Entries.Length == 0 ? 0 : Address(5 + structs.Count + i));
        }
        for (var i = 0; i < blocks.Count; i++)
        {
            var entry = at[4] + 16 * i;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entry), blocks[i].Type);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(entry + 4), blocks[i].Data.Length);
            Pointer(entry + 8, Address(firstBlock + i));
        }

        var header = (byte[])_header.Clone();
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) & 0xF0000000;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), flags | PageFlags(pageSize, pages, lastPage));
        using var output = new MemoryStream();
        output.Write(header);
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(data);
        return output.ToArray();
    }

    // Structure and enum descriptions: a fixed-size info each, pointing at a list of entries.
    private static IEnumerable<(byte[] Info, byte[] Entries)> Infos(MetaFile file, int pointerField, int countField,
        int infoSize, int entriesField, int entriesCountField, int entrySize)
    {
        var count = BinaryPrimitives.ReadInt16LittleEndian(file.Span(countField, 2));
        if (count <= 0) yield break;
        var start = file.Offset(file.U64(pointerField));
        for (var i = 0; i < count; i++)
        {
            var info = file.Bytes(start + infoSize * i, infoSize);
            var entries = entriesCountField == 0x1E
                ? BinaryPrimitives.ReadInt16LittleEndian(info.AsSpan(entriesCountField))
                : BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(entriesCountField));
            var pointer = BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(entriesField));
            yield return (info, entries <= 0 || pointer == 0 ? [] : file.Bytes(file.Offset(pointer), entries * entrySize));
        }
    }

    private byte[] NameAt(int at)
    {
        var end = System.Array.IndexOf(_data, (byte)0, at);
        if (end < 0) throw new InvalidDataException("Unfinished name.");
        return Bytes(at, end - at + 1);
    }

    // Data is split into pages GTA loads separately, so nothing may cross from one page into the next. Pages are
    // a power of two in size: here all one size, apart from the last, which is shrunk to fit. The file header says
    // how many there are of each size, as a power-of-two base size (at least 0x200) and counts of base x 16
    // (up to 127) and base x 8, 4, 2 and 1 (one each at most).
    private static (int[] At, int PageSize, int Pages, int LastPage) Lay(List<int> sizes)
    {
        static int Align(int n) => (n + 15) & ~15;
        var largest = sizes.Max();
        // The header first, as everything else is found from there; then biggest first, so they pack well.
        var order = Enumerable.Range(1, sizes.Count - 1).OrderByDescending(i => sizes[i]).Prepend(0).ToList();
        for (var pageSize = 0x2000; ; pageSize *= 2)
        {
            if (pageSize < largest) continue;
            var used = new List<int>();
            var at = new int[sizes.Count];
            foreach (var i in order)
            {
                var page = used.FindIndex(u => Align(u) + sizes[i] <= pageSize);
                if (page < 0) { used.Add(0); page = used.Count - 1; }
                at[i] = page * pageSize + Align(used[page]);
                used[page] = Align(used[page]) + sizes[i];
            }
            if (used.Count > 127) continue;
            var last = pageSize;
            while (last > pageSize / 16 && used[^1] <= last / 2) last /= 2;
            return (at, pageSize, used.Count, last);
        }
    }

    private static uint PageFlags(int pageSize, int pages, int lastPage)
    {
        var shift = (uint)BitOperations.Log2((uint)(pageSize / 16 / 0x200));
        var full = (uint)(lastPage == pageSize ? pages : pages - 1);
        var flags = shift | full << 17;
        if (lastPage < pageSize) flags |= 1u << (23 + BitOperations.Log2((uint)(pageSize / lastPage)));
        return flags;
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
