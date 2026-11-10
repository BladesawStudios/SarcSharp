using System.Buffers.Binary;
using System.Text;
using System.Diagnostics.CodeAnalysis;

namespace SarcSharp;

public sealed record SarcEntry(string Name, byte[] Data, int Alignment = SarcFile.DefaultAlignment);

public sealed class SarcFile
{
    public const int DefaultAlignment = 8;

    public const uint DefaultHashKey = 0x65;

    private const ushort Bom = 0xFEFF;
    private const ushort Version = 0x0100;
    private const int SarcHeaderSize = 0x14;
    private const int SfatHeaderSize = 0x0C;
    private const int SfntHeaderSize = 0x08;
    private const int NodeSize = 0x10;

    public List<SarcEntry> Entries { get; } = [];

    public uint HashKey { get; set; } = DefaultHashKey;

    public bool BigEndian { get; set; }

    private Dictionary<string, int>? _index;

    public int Count => Entries.Count;

    public SarcEntry? this[string name]
    {
        get
        {
            int at = IndexOf(name);
            return at >= 0 ? Entries[at] : null;
        }
    }

    public bool Contains(string name) => IndexOf(name) >= 0;

    public bool TryGet(string name, [NotNullWhen(true)] out SarcEntry? entry)
    {
        entry = this[name];
        return entry is not null;
    }

    public void Set(string name, byte[] data)
    {
        int at = IndexOf(name);
        int alignment = AlignmentFor(name, data);

        if (at >= 0)
        {
            Entries[at] = new SarcEntry(name, data, Math.Max(alignment, Entries[at].Alignment));
            return;
        }

        Entries.Add(new SarcEntry(name, data, alignment));

        if (_index is not null)
        {
            _index[name] = Entries.Count - 1;
        }
    }

    public bool Remove(string name)
    {
        int at = IndexOf(name);

        if (at < 0)
        {
            return false;
        }

        Entries.RemoveAt(at);
        _index = null;
        return true;
    }

    public Stream OpenWrite(string name) => new EntryStream(this, name);

    public int IndexOf(string name)
    {
        _index ??= BuildIndex();

        if (_index.TryGetValue(name, out int at) && at < Entries.Count && Entries[at].Name == name)
        {
            return at;
        }

        at = Entries.FindIndex(e => e.Name == name);

        if (at >= 0)
        {
            _index = BuildIndex();
        }

        return at;
    }

    private Dictionary<string, int> BuildIndex()
    {
        Dictionary<string, int> index = new(Entries.Count, StringComparer.Ordinal);

        for (int i = 0; i < Entries.Count; i++)
        {
            index.TryAdd(Entries[i].Name, i);
        }

        return index;
    }

    // ---- reading ----------------------------------------------------------
    public static SarcFile FromFile(string path) => FromBinary(File.ReadAllBytes(path));

    public static SarcFile FromBinary(ReadOnlySpan<byte> data)
    {
        if (data.Length < SarcHeaderSize || !data[..4].SequenceEqual("SARC"u8))
            throw new InvalidDataException("Not a SARC archive.");

        bool big = BinaryPrimitives.ReadUInt16LittleEndian(data[0x06..]) != Bom;
        Reader r = new(data, big);

        if (r.U16(0x06) != Bom)
            throw new InvalidDataException("SARC byte-order mark is neither FEFF nor FFFE.");

        int dataOffset = (int)r.U32(0x0C);
        int sfat = r.U16(0x04);

        if (!data.Slice(sfat, 4).SequenceEqual("SFAT"u8))
            throw new InvalidDataException("SARC is missing its SFAT table.");

        int count = r.U16(sfat + 0x06);
        uint hashKey = r.U32(sfat + 0x08);

        int nodes = sfat + SfatHeaderSize;
        int sfnt = nodes + count * NodeSize;

        if (!data.Slice(sfnt, 4).SequenceEqual("SFNT"u8))
            throw new InvalidDataException("SARC is missing its SFNT name table.");

        int names = sfnt + SfntHeaderSize;

        SarcFile archive = new() { HashKey = hashKey, BigEndian = big };

        for (int i = 0; i < count; i++)
        {
            int node = nodes + i * NodeSize;

            uint attributes = r.U32(node + 0x04);
            int start = dataOffset + (int)r.U32(node + 0x08);
            int end = dataOffset + (int)r.U32(node + 0x0C);

            if (start < 0 || end > data.Length || end < start)
                throw new InvalidDataException($"SARC entry {i} runs outside the archive.");

            string name = (attributes >> 24) != 0
                ? r.StringAt(names + (int)(attributes & 0xFFFFFF) * 4)
                : $"0x{r.U32(node):X8}";

            archive.Entries.Add(new SarcEntry(name, data[start..end].ToArray(), ObservedAlignment(start)));
        }

        return archive;
    }

    public static bool TryFind(ReadOnlySpan<byte> archive, string name, out Range range)
    {
        range = default;

        if (archive.Length < SarcHeaderSize || !archive[..4].SequenceEqual("SARC"u8))
        {
            throw new InvalidDataException("Not a SARC archive.");
        }

        bool big = BinaryPrimitives.ReadUInt16LittleEndian(archive[0x06..]) != Bom;
        Reader r = new(archive, big);

        int dataOffset = (int)r.U32(0x0C);
        int sfat = r.U16(0x04);
        int count = r.U16(sfat + 0x06);
        uint hashKey = r.U32(sfat + 0x08);
        int nodes = sfat + SfatHeaderSize;
        int names = nodes + count * NodeSize + SfntHeaderSize;

        uint hash = Hash(name, hashKey);

        int low = 0;
        int high = count;
        while (low < high)
        {
            int middle = (low + high) / 2;

            if (r.U32(nodes + middle * NodeSize) < hash)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        for (int i = low; i < count && r.U32(nodes + i * NodeSize) == hash; i++)
        {
            int node = nodes + i * NodeSize;
            uint attributes = r.U32(node + 0x04);

            if ((attributes >> 24) != 0 && r.StringAt(names + (int)(attributes & 0xFFFFFF) * 4) != name)
            {
                continue;
            }

            int start = dataOffset + (int)r.U32(node + 0x08);
            int end = dataOffset + (int)r.U32(node + 0x0C);
            range = start..end;
            return true;
        }

        return false;
    }

    // ---- writing ----------------------------------------------------------

    public void WriteTo(string path) => File.WriteAllBytes(path, Write());
    public void Write(Stream output) => output.Write(Write());

    public byte[] Write()
    {
        List<SarcEntry> sorted = [.. Entries
            .OrderBy(e => Hash(e.Name, HashKey))
            .ThenBy(e => e.Name, StringComparer.Ordinal)];

        int sfat = SarcHeaderSize;
        int nodes = sfat + SfatHeaderSize;
        int sfnt = nodes + sorted.Count * NodeSize;
        int names = sfnt + SfntHeaderSize;

        List<int> nameOffsets = new(sorted.Count);
        int nameLength = 0;
        foreach (SarcEntry entry in sorted)
        {
            nameOffsets.Add(nameLength);
            nameLength = Align(nameLength + Encoding.UTF8.GetByteCount(entry.Name) + 1, 4);
        }

        int dataStart = Align(names + nameLength, sorted.Count > 0 ? Math.Max(sorted[0].Alignment, MinimumAlignment) : MinimumAlignment);

        List<(int Start, int End)> spans = new(sorted.Count);
        int at = dataStart;
        foreach (SarcEntry entry in sorted)
        {
            at = Align(at, Math.Max(entry.Alignment, MinimumAlignment));
            spans.Add((at, at + entry.Data.Length));
            at += entry.Data.Length;
        }

        byte[] output = new byte[at];
        Writer w = new(output, BigEndian);

        "SARC"u8.CopyTo(output);
        w.U16(0x04, SarcHeaderSize);
        w.U16(0x06, Bom);
        w.U32(0x08, (uint)output.Length);
        w.U32(0x0C, (uint)dataStart);
        w.U16(0x10, Version);
        w.U16(0x12, 0);

        "SFAT"u8.CopyTo(output.AsSpan(sfat));
        w.U16(sfat + 0x04, SfatHeaderSize);
        w.U16(sfat + 0x06, (ushort)sorted.Count);
        w.U32(sfat + 0x08, HashKey);

        for (int i = 0; i < sorted.Count; i++)
        {
            int node = nodes + i * NodeSize;
            w.U32(node + 0x00, Hash(sorted[i].Name, HashKey));
            w.U32(node + 0x04, 0x01000000u | (uint)(nameOffsets[i] / 4));
            w.U32(node + 0x08, (uint)(spans[i].Start - dataStart));
            w.U32(node + 0x0C, (uint)(spans[i].End - dataStart));
        }

        "SFNT"u8.CopyTo(output.AsSpan(sfnt));
        w.U16(sfnt + 0x04, SfntHeaderSize);
        w.U16(sfnt + 0x06, 0);

        for (int i = 0; i < sorted.Count; i++)
            Encoding.UTF8.GetBytes(sorted[i].Name).CopyTo(output.AsSpan(names + nameOffsets[i]));

        for (int i = 0; i < sorted.Count; i++)
            sorted[i].Data.CopyTo(output.AsSpan(spans[i].Start));

        return output;
    }

    private int MinimumAlignment => BigEndian ? 4 : DefaultAlignment;

    public int AlignmentFor(string name, ReadOnlySpan<byte> data)
    {
        int alignment = MinimumAlignment;
        string extension = Path.GetExtension(name);

        if (extension == ".bffnt")
        {
            alignment = Math.Max(alignment, BigEndian ? 0x2000 : 0x1000);
        }
        else if (extension is ".sharc" or ".sharcb")
        {
            alignment = Math.Max(alignment, 0x1000);
        }

        return Math.Max(alignment, HeaderAlignment(data));
    }

    private static int HeaderAlignment(ReadOnlySpan<byte> data)
    {
        if (data.Length <= 0x20 || data[0x0E] > 16)
        {
            return 1;
        }

        bool big = BinaryPrimitives.ReadUInt16LittleEndian(data[0x0C..]) != Bom;
        Reader r = new(data, big);

        if (r.U32(0x1C) != data.Length)
        {
            return 1;
        }

        return 1 << data[0x0E];
    }

    private static int ObservedAlignment(int start)
    {
        const int Limit = 0x2000;
        return start == 0 ? Limit : Math.Min(start & -start, Limit);
    }

    private static int Align(int value, int to) => (value + to - 1) / to * to;

    public static uint Hash(string name, uint key = DefaultHashKey)
    {
        uint hash = 0;
        foreach (char c in name) hash = hash * key + c;

        return hash;
    }

    private readonly ref struct Reader(ReadOnlySpan<byte> data, bool big)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private readonly bool _big = big;

        public ushort U16(int at) => _big
            ? BinaryPrimitives.ReadUInt16BigEndian(_data[at..])
            : BinaryPrimitives.ReadUInt16LittleEndian(_data[at..]);

        public uint U32(int at) => _big
            ? BinaryPrimitives.ReadUInt32BigEndian(_data[at..])
            : BinaryPrimitives.ReadUInt32LittleEndian(_data[at..]);

        public string StringAt(int at)
        {
            ReadOnlySpan<byte> rest = _data[at..];
            int end = rest.IndexOf((byte)0);

            return Encoding.UTF8.GetString(end < 0 ? rest : rest[..end]);
        }
    }

    private readonly ref struct Writer(Span<byte> data, bool big)
    {
        private readonly Span<byte> _data = data;
        private readonly bool _big = big;

        public void U16(int at, ushort value)
        {
            if (_big) BinaryPrimitives.WriteUInt16BigEndian(_data[at..], value);
            else BinaryPrimitives.WriteUInt16LittleEndian(_data[at..], value);
        }

        public void U32(int at, uint value)
        {
            if (_big) BinaryPrimitives.WriteUInt32BigEndian(_data[at..], value);
            else BinaryPrimitives.WriteUInt32LittleEndian(_data[at..], value);
        }
    }

    private sealed class EntryStream(SarcFile owner, string name) : MemoryStream
    {
        private bool _stored;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_stored)
            {
                _stored = true;
                owner.Set(name, ToArray());
            }

            base.Dispose(disposing);
        }
    }
}
