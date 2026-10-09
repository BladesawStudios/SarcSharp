# SarcSharp

A C# reader and writer for **SARC**, the archive format recent Nintendo titles pack almost
everything into.

```csharp
SarcFile pack = SarcFile.FromBinary(decompressedBytes);

foreach (SarcEntry entry in pack.Entries)
    Console.WriteLine($"{entry.Name} ({entry.Data.Length} bytes)");

pack.Entries.Add(new SarcEntry("Component/ModelInfo/Thing.bgyml", bytes));
byte[] rebuilt = pack.Write();
```

Each entry carries the alignment its data sits at (`SarcEntry.Alignment`). Reading takes it from where the data is, and
writing places it there again, so an archive that is read and written without changes comes out as the same bytes.
(Checked on every `.pack.zs` in the Tears of the Kingdom romfs.) New entries default to 8; give a texture 0x1000.

## Build

```bash
dotnet build SarcSharp.sln -c Release
dotnet test SarcSharp.sln -c Release   # TOTK_ROMFS adds the whole-romfs check
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
