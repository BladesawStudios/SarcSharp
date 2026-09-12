using System.Buffers.Binary;
using System.Text;

namespace SarcSharp;

/// <summary>One file inside an archive.</summary>
/// <param name="Name">The path within the archive, with forward slashes.</param>
/// <param name="Data">The file's bytes.</param>
/// <param name="Alignment">
/// What the file's offset must be a multiple of, for content the game maps in place rather
/// than copies.
///
/// This is not recovered when reading, because it cannot be: all a read can see is what the
/// offset happens to divide by, which is mostly luck. Measured across a hundred and thirteen
/// packs, every contained type with more than a handful of instances sits at 8 - the larger
/// figures all come from single samples. Set it when writing something that needs more.
/// </param>
public sealed record SarcEntry(string Name, byte[] Data, int Alignment = SarcFile.DefaultAlignment);

/// <summary>
/// A SARC archive: a flat set of named files.
/// </summary>
/// <remarks>
/// Three parts after the header. SFAT lists the files, sorted by a hash of the name so the
/// game can binary-search them; SFNT holds the names themselves; then the data. A node points
/// at its name by an index into SFNT and at its bytes by offsets from where the data starts.
/// </remarks>
public sealed class SarcFile
{
    public const int DefaultAlignment = 8;

    /// <summary>The multiplier the name hash uses. Every archive seen uses this.</summary>
    public const uint DefaultHashKey = 0x65;

    private const ushort Bom = 0xFEFF;
    private const ushort Version = 0x0100;
    private const int SarcHeaderSize = 0x14;
    private const int SfatHeaderSize = 0x0C;
    private const int SfntHeaderSize = 0x08;
    private const int NodeSize = 0x10;

    public List<SarcEntry> Entries { get; } = [];

    /// <summary>The hash multiplier, kept so an archive written back keeps its own.</summary>
    public uint HashKey { get; set; } = DefaultHashKey;

    /// <summary>Whether the archive is stored big endian, which its byte-order mark says.</summary>
    public bool BigEndian { get; set; }

    public SarcEntry? this[string name]
        => Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));

    public bool Contains(string name) => this[name] is not null;

    // ---- reading ----------------------------------------------------------

    public static SarcFile FromFile(string path) => FromBinary(File.ReadAllBytes(path));

    /// <exception cref="InvalidDataException">The data is not a SARC archive.</exception>
    public static SarcFile FromBinary(ReadOnlySpan<byte> data)
    {
        if (data.Length < SarcHeaderSize || !data[..4].SequenceEqual("SARC"u8))
            throw new InvalidDataException("Not a SARC archive.");

        // The byte-order mark is the field that says how to read the rest of them.
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

            // The top byte flags that the name is present; the rest is its offset in words.
            string name = (attributes >> 24) != 0
                ? r.StringAt(names + (int)(attributes & 0xFFFFFF) * 4)
                : $"0x{r.U32(node):X8}";

            archive.Entries.Add(new SarcEntry(name, data[start..end].ToArray()));
        }

        return archive;
    }

    // ---- writing ----------------------------------------------------------

    public void WriteTo(string path) => File.WriteAllBytes(path, Write());

    public byte[] Write()
    {
        // Sorted by hash: the game binary-searches the table, so the order is not cosmetic.
        // Names break ties so that the same set always writes the same way.
        List<SarcEntry> sorted = [.. Entries
            .OrderBy(e => Hash(e.Name, HashKey))
            .ThenBy(e => e.Name, StringComparer.Ordinal)];

        int sfat = SarcHeaderSize;
        int nodes = sfat + SfatHeaderSize;
        int sfnt = nodes + sorted.Count * NodeSize;
        int names = sfnt + SfntHeaderSize;

        // Name offsets are in words, so each name starts on a multiple of four.
        List<int> nameOffsets = new(sorted.Count);
        int nameLength = 0;
        foreach (SarcEntry entry in sorted)
        {
            nameOffsets.Add(nameLength);
            nameLength = Align(nameLength + Encoding.UTF8.GetByteCount(entry.Name) + 1, 4);
        }

        int dataStart = Align(names + nameLength, LargestAlignment(sorted));

        // Lay the files out before writing anything, so the nodes can name their offsets.
        List<(int Start, int End)> spans = new(sorted.Count);
        int at = dataStart;
        foreach (SarcEntry entry in sorted)
        {
            at = Align(at, Math.Max(entry.Alignment, DefaultAlignment));
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

    private static int LargestAlignment(List<SarcEntry> entries)
    {
        int largest = DefaultAlignment;
        foreach (SarcEntry entry in entries) largest = Math.Max(largest, entry.Alignment);

        return largest;
    }

    private static int Align(int value, int to) => (value + to - 1) / to * to;

    /// <summary>
    /// The hash a name is filed under.
    /// </summary>
    /// <remarks>
    /// Each character multiplies the running value by the archive's key and adds itself. The
    /// key is stored per archive, though every one seen uses 0x65.
    /// </remarks>
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
}
