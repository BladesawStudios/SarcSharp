using System.Collections.Concurrent;
using ZsDicSharp;

namespace SarcSharp.Tests;

public class SarcTests
{
    private static byte[] Bytes(int length, byte fill) => Enumerable.Repeat(fill, length).ToArray();

    [Fact]
    public void AnArchiveWritesAndReadsBack()
    {
        SarcFile sarc = new();
        sarc.Entries.Add(new SarcEntry("Actor/a.bgyml", [1, 2, 3]));
        sarc.Entries.Add(new SarcEntry("Actor/b.bgyml", Bytes(100, 7)));

        SarcFile back = SarcFile.FromBinary(sarc.Write());

        Assert.Equal(["Actor/a.bgyml", "Actor/b.bgyml"], back.Entries.Select(e => e.Name).Order());
        Assert.Equal([1, 2, 3], back["Actor/a.bgyml"]!.Data);
        Assert.Equal(Bytes(100, 7), back["Actor/b.bgyml"]!.Data);
    }

    [Fact]
    public void AnArchiveReadAndWrittenAgainIsTheSameBytes()
    {
        SarcFile sarc = new();
        sarc.Entries.Add(new SarcEntry("a.bin", Bytes(13, 1)));
        sarc.Entries.Add(new SarcEntry("Tex/b.bntx", Bytes(300, 2), 0x1000));
        sarc.Entries.Add(new SarcEntry("c.bin", Bytes(5, 3), 16));
        byte[] first = sarc.Write();

        byte[] second = SarcFile.FromBinary(first).Write();

        Assert.Equal(first, second);
    }

    [Fact]
    public void AFileKeepsTheAlignmentItWasWrittenWith()
    {
        SarcFile sarc = new();
        sarc.Entries.Add(new SarcEntry("small.bin", Bytes(3, 1)));
        sarc.Entries.Add(new SarcEntry("Tex/big.bntx", Bytes(10, 2), 0x1000));

        SarcFile back = SarcFile.FromBinary(sarc.Write());

        Assert.True(back["Tex/big.bntx"]!.Alignment >= 0x1000);
        Assert.Equal(0, (BitConverter.ToInt32(back.Write().AsSpan(0x0C)) + 0) % 8);
    }

    [Fact]
    public void ChangingAFileChangesOnlyThatFilesData()
    {
        SarcFile sarc = new();
        sarc.Entries.Add(new SarcEntry("a.bin", Bytes(9, 1)));
        sarc.Entries.Add(new SarcEntry("b.bin", Bytes(9, 2), 32));

        SarcFile back = SarcFile.FromBinary(sarc.Write());
        int at = back.Entries.FindIndex(e => e.Name == "a.bin");
        back.Entries[at] = back.Entries[at] with { Data = Bytes(40, 9) };
        SarcFile again = SarcFile.FromBinary(back.Write());

        Assert.Equal(Bytes(40, 9), again["a.bin"]!.Data);
        Assert.Equal(Bytes(9, 2), again["b.bin"]!.Data);
        Assert.True(again["b.bin"]!.Alignment >= 32);
    }

    [Fact]
    public void ABigEndianArchiveKeepsItsOrderAndFourByteAlignment()
    {
        SarcFile sarc = new() { BigEndian = true };
        sarc.Entries.Add(new SarcEntry("a.bin", Bytes(3, 1), 4));

        byte[] bytes = sarc.Write();
        SarcFile back = SarcFile.FromBinary(bytes);

        Assert.True(back.BigEndian);
        Assert.Equal(bytes, back.Write());
    }

    // Runs over every .pack.zs in a Tears of the Kingdom romfs when TOTK_ROMFS points at one.
    [Fact]
    public void EveryPackInTheRomfsIsWrittenBackUnchanged()
    {
        if (Environment.GetEnvironmentVariable("TOTK_ROMFS") is not { } root || !File.Exists(Path.Combine(root, "Pack", "ZsDic.pack.zs"))) return;

        using ZsDic zs = ZsDic.FromRomfs(root);
        ConcurrentBag<string> failures = [];
        int files = 0;

        Parallel.ForEach(Directory.EnumerateFiles(root, "*.pack.zs", SearchOption.AllDirectories), path =>
        {
            byte[] raw = zs.DecompressFile(path);
            if (raw.Length < 4 || !raw.AsSpan(0, 4).SequenceEqual("SARC"u8)) return;
            Interlocked.Increment(ref files);

            if (!SarcFile.FromBinary(raw).Write().AsSpan().SequenceEqual(raw)) failures.Add(Path.GetFileName(path));
        });

        Assert.True(files > 10000, $"Expected the full set of packs, found {files}.");
        Assert.True(failures.IsEmpty, $"{failures.Count} packs differ: {string.Join(", ", failures.Order().Take(10))}");
    }

    [Fact]
    public void SetReplacesAndRemoveDrops()
    {
        SarcFile sarc = new();
        sarc.Entries.Add(new SarcEntry("a.bin", [1], 32));
        sarc.Entries.Add(new SarcEntry("b.bin", [2]));

        sarc.Set("a.bin", [9, 9]);
        Assert.True(sarc.Remove("b.bin"));
        Assert.False(sarc.Remove("b.bin"));

        SarcFile back = SarcFile.FromBinary(sarc.Write());

        Assert.Equal(1, back.Count);
        Assert.Equal([9, 9], back["a.bin"]!.Data);
        Assert.True(back["a.bin"]!.Alignment >= 32);
    }

    [Fact]
    public void LookupsSeeEditsMadeThroughEntries()
    {
        SarcFile sarc = new();
        sarc.Entries.Add(new SarcEntry("a.bin", [1]));
        sarc.Entries.Add(new SarcEntry("b.bin", [2]));
        Assert.True(sarc.Contains("a.bin"));

        sarc.Entries.Add(new SarcEntry("c.bin", [3]));
        sarc.Entries[0] = sarc.Entries[0] with { Name = "renamed.bin" };
        sarc.Entries.RemoveAt(1);

        Assert.Null(sarc["a.bin"]);
        Assert.Null(sarc["b.bin"]);
        Assert.Equal([1], sarc["renamed.bin"]!.Data);
        Assert.Equal([3], sarc["c.bin"]!.Data);
    }

    [Fact]
    public void OpenWriteStoresTheDataWhenDisposed()
    {
        SarcFile sarc = new();

        using (Stream stream = sarc.OpenWrite("a.bin"))
        {
            stream.Write([4, 5, 6]);
            Assert.False(sarc.Contains("a.bin"));
        }

        Assert.Equal([4, 5, 6], sarc["a.bin"]!.Data);
    }

    [Fact]
    public void ANewFileGetsTheAlignmentItsHeaderAsksFor()
    {
        byte[] data = new byte[0x40];
        "BNTX"u8.CopyTo(data);
        data[0x0C] = 0xFF;
        data[0x0D] = 0xFE;
        data[0x0E] = 12;
        BitConverter.TryWriteBytes(data.AsSpan(0x1C), data.Length);

        SarcFile sarc = new();
        sarc.Set("Tex/a.bntx", data);
        sarc.Set("b.bin", [1, 2, 3]);

        Assert.Equal(0x1000, sarc["Tex/a.bntx"]!.Alignment);
        Assert.Equal(SarcFile.DefaultAlignment, sarc["b.bin"]!.Alignment);
    }

    [Fact]
    public void TryFindPointsAtEachFilesBytes()
    {
        SarcFile sarc = new();
        for (int i = 0; i < 50; i++)
        {
            sarc.Entries.Add(new SarcEntry($"Dir/file{i}.bin", Bytes(i + 1, (byte)i)));
        }

        byte[] archive = sarc.Write();

        for (int i = 0; i < 50; i++)
        {
            Assert.True(SarcFile.TryFind(archive, $"Dir/file{i}.bin", out Range range));
            Assert.Equal(Bytes(i + 1, (byte)i), archive[range]);
        }

        Assert.False(SarcFile.TryFind(archive, "missing.bin", out _));
    }
}
