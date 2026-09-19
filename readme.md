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

## Build

```bash
dotnet build SarcSharp.sln -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
