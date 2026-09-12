# SarcSharp

A C# reader and writer for **SARC**, the archive format recent Nintendo titles pack almost
everything into — actor definitions, parameter sets, the zstd dictionaries themselves.

```csharp
SarcFile pack = SarcFile.FromBinary(decompressedBytes);

foreach (SarcEntry entry in pack.Entries)
    Console.WriteLine($"{entry.Name} ({entry.Data.Length} bytes)");

pack.Entries.Add(new SarcEntry("Component/ModelInfo/Thing.bgyml", bytes));
byte[] rebuilt = pack.Write();
```

No dependencies. Input must already be decompressed — shipped archives are zstd, usually
against a dictionary, which is a romfs concern rather than an archive one.

## The format

A header, then three parts. **SFAT** lists the files, sorted by a hash of the name so the game
can binary-search them. **SFNT** holds the names. Then the data. A node points at its name by
a word index into SFNT and at its bytes by offsets from where the data begins.

The hash is plain: each character multiplies the running value by the archive's key and adds
itself, `h = h * key + c`. The key is stored per archive; every one seen uses `0x65`.

## Alignment is not recoverable by reading

Files inside an archive can need their offset to be a multiple of something, for content the
game maps in place rather than copies. That requirement is **not stored** — all a reader can
see is what an offset happens to divide by, which is mostly luck.

Measured across 113 packs: every contained type with more than a handful of instances sits at
8, and the larger figures all come from single samples. So reading reports 8, writing uses 8,
and `SarcEntry.Alignment` is there to say otherwise when you are adding something that needs
more. Inferring it from offsets instead makes an archive grow a little on every rewrite.

## Checked against

413 archives from *Tears of the Kingdom* — actor packs and the packs beside them, 21,048
entries — read, write and read back identically, and all 413 come out **byte-for-byte
identical to the original**. An archive built from scratch reads back with its names,
contents and alignments intact.

## Build

```bash
dotnet build SarcSharp.sln -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
