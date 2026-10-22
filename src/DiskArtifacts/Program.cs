using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace DiskArtifacts;

public record Entry(string Path, bool Deleted, bool Directory, uint FirstCluster, uint Size, string? Modified);

public interface IFileSystemParser
{
    IReadOnlyList<Entry> Parse(Stream image);
}

public sealed class Fat32Parser : IFileSystemParser
{
    private Stream image = Stream.Null;
    private uint bytesPerSector, sectorsPerCluster, sectorsPerFat, firstDataSector, totalSectors, rootCluster;
    private ushort reserved, fats;
    private long fatOffset;
    private readonly List<Entry> entries = new();
    private readonly HashSet<uint> visitedDirectories = new();

    private byte[] Read(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > image.Length) throw new InvalidDataException("Image is truncated");
        var buffer = new byte[length];
        image.Position = offset;
        image.ReadExactly(buffer);
        return buffer;
    }

    private long ClusterOffset(uint cluster)
    {
        if (cluster < 2) throw new InvalidDataException("Invalid cluster number");
        ulong sector = (ulong)firstDataSector + ((ulong)cluster - 2) * sectorsPerCluster;
        if (sector >= totalSectors || sector + sectorsPerCluster > totalSectors) throw new InvalidDataException("Cluster outside volume");
        return checked((long)sector * bytesPerSector);
    }

    private uint NextCluster(uint cluster)
    {
        long address = fatOffset + cluster * 4L;
        if (address + 4 > fatOffset + sectorsPerFat * bytesPerSector) throw new InvalidDataException("FAT index outside table");
        return BinaryPrimitives.ReadUInt32LittleEndian(Read(address,4)) & 0x0FFFFFFF;
    }

    private static string? Timestamp(ReadOnlySpan<byte> entry)
    {
        ushort date = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(24,2));
        ushort time = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(22,2));
        if (date == 0) return null;
        try { return new DateTime(1980 + (date >> 9), (date >> 5) & 15, date & 31,
            (time >> 11) & 31, (time >> 5) & 63, (time & 31) * 2).ToString("yyyy-MM-ddTHH:mm:ss"); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private void Walk(uint start, string prefix, int depth)
    {
        if (depth > 64 || !visitedDirectories.Add(start)) throw new InvalidDataException("Cyclic or deeply nested directory");
        uint cluster = start;
        var chain = new HashSet<uint>();
        while (cluster >= 2 && cluster < 0x0FFFFFF8)
        {
            if (!chain.Add(cluster)) throw new InvalidDataException("Cyclic FAT chain");
            var data = Read(ClusterOffset(cluster), checked((int)(bytesPerSector * sectorsPerCluster)));
            for (int offset = 0; offset + 32 <= data.Length; offset += 32)
            {
                var record = data.AsSpan(offset, 32);
                if (record[0] == 0) return;
                byte attr = record[11];
                if (attr == 0x0F || (attr & 0x08) != 0) continue;
                bool deleted = record[0] == 0xE5;
                string stem = Encoding.ASCII.GetString(record.Slice(0,8)).TrimEnd();
                if (deleted) stem = "?" + stem[1..];
                string ext = Encoding.ASCII.GetString(record.Slice(8,3)).TrimEnd();
                string name = ext.Length > 0 ? stem + "." + ext : stem;
                if (name is "." or "..") continue;
                uint first = ((uint)BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(20,2)) << 16) |
                             BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(26,2));
                bool directory = (attr & 0x10) != 0;
                entries.Add(new Entry(prefix + name, deleted, directory, first,
                    BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(28,4)), Timestamp(record)));
                if (directory && !deleted && first >= 2) Walk(first, prefix + name + "/", depth + 1);
            }
            cluster = NextCluster(cluster);
            if (cluster == 0 || cluster == 0x0FFFFFF7) throw new InvalidDataException("Broken FAT chain");
        }
    }

    public IReadOnlyList<Entry> Parse(Stream source)
    {
        if (!source.CanRead || !source.CanSeek) throw new ArgumentException("Seekable stream required");
        image = source; entries.Clear(); visitedDirectories.Clear();
        var boot = Read(0,512);
        bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11,2));
        sectorsPerCluster = boot[13];
        reserved = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14,2));
        fats = boot[16];
        sectorsPerFat = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(36,4));
        totalSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32,4));
        rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(44,4));
        if (BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(510,2)) != 0xAA55 ||
            bytesPerSector is not (512 or 1024 or 2048 or 4096) || sectorsPerCluster == 0 ||
            (sectorsPerCluster & (sectorsPerCluster - 1)) != 0 || reserved == 0 || fats == 0 ||
            sectorsPerFat == 0 || totalSectors == 0 || rootCluster < 2)
            throw new InvalidDataException("Invalid FAT32 boot sector");
        firstDataSector = checked((uint)(reserved + fats * sectorsPerFat));
        fatOffset = (long)reserved * bytesPerSector;
        if (firstDataSector >= totalSectors || (long)totalSectors * bytesPerSector > image.Length)
            throw new InvalidDataException("Invalid volume size");
        Walk(rootCluster, "", 0);
        return entries.ToArray();
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length < 1) throw new ArgumentException("usage: DiskArtifacts IMAGE [--format json|csv] [--output FILE]");
            string format = "json"; string? output = null;
            for (int i=1; i<args.Length; i++)
            {
                if (args[i] == "--format" && i+1<args.Length) format = args[++i];
                else if (args[i] == "--output" && i+1<args.Length) output = args[++i];
                else throw new ArgumentException("Unknown option: " + args[i]);
            }
            if (format is not ("json" or "csv")) throw new ArgumentException("format must be json or csv");
            using var stream = File.Open(args[0], FileMode.Open, FileAccess.Read, FileShare.Read);
            var entries = new Fat32Parser().Parse(stream);
            string content = format == "json" ? JsonSerializer.Serialize(entries,new JsonSerializerOptions { WriteIndented=true }) :
                "Path,Deleted,Directory,FirstCluster,Size,Modified\n" + string.Join("", entries.Select(e =>
                    $"{Csv(e.Path)},{e.Deleted},{e.Directory},{e.FirstCluster},{e.Size},{Csv(e.Modified ?? "")}\n"));
            if (output is null) Console.WriteLine(content); else File.WriteAllText(output,content);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException)
        { Console.Error.WriteLine("Error: " + ex.Message); return 1; }
    }

    private static string Csv(string value) => "\"" + value.Replace("\"","\"\"") + "\"";
}

