using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ConsoleAppFramework;

ConsoleApp.Run(args, Root);

partial class Program
{
    /// <param name="output">-o</param>
    internal static void Root([Argument]string filePath, string? output = null)
    {
        output ??= Path.Join(Path.GetDirectoryName(filePath), $"{Path.GetFileNameWithoutExtension(filePath)}_2{Path.GetExtension(filePath)}");

        using var fs = File.Open(filePath, FileMode.Open);

        using var gz = new GZipStream(fs, CompressionMode.Decompress, true);
        using var tar = new TarReader(gz, leaveOpen: true);

        Dictionary<Guid, Guid> guidMap = new();
        Span<byte> headerBuffer = stackalloc byte[10];
        Span<Range> splitRanges = stackalloc Range[4];

        Dictionary<TarEntry, (Guid Original, Guid Replaced)> entries = [];

        {
            TarEntry? entry;
            while ((entry = tar.GetNextEntry(copyData: true)) != null)
            {
                var name = entry.Name.AsSpan();
                name.Split(splitRanges, "/");

                var guid = Guid.Parse(name[splitRanges[0]]);
                if (!guidMap.TryGetValue(guid, out var newGuid))
                {
                    newGuid = Guid.NewGuid();
                    guidMap.Add(guid, newGuid);

                    Console.WriteLine($"{guid} => {newGuid}");
                }

                entries.Add(entry, (guid, newGuid));
            }
        }

        using TarWriter unitypackage = new(new GZipStream(File.Create(output), CompressionMode.Compress));

        foreach (var (source, guids) in entries)
        {
            if (source.EntryType is TarEntryType.Directory)
            {
                continue;
            }

            var name = source.Name.AsSpan();
            name.Split(splitRanges, "/");

            var guid = guids.Replaced;

            var fileName = name[splitRanges[1]];
            var entry2 = new UstarTarEntry(TarEntryType.RegularFile, $"{guid:N}/{fileName}");
            var data = source.DataStream;
            if (data != null)
            {
                var stream = new MemoryStream();
                if (fileName is "asset.meta")
                {
                    using var sr = new StreamReader(data, Encoding.UTF8, leaveOpen: true);
                    using var sw = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
                    ReplaceAllGuid(sr, sw);
                }
                else if (fileName is "asset")
                {
                    var len = data.Read(headerBuffer);
                    var header = headerBuffer[..len];
                    if (!header.SequenceEqual("%YAML 1.1\n"u8))
                    {
                        stream.Write(header);
                        data.CopyTo(stream);
                    }
                    else
                    {
                        using var sr = new StreamReader(data, Encoding.UTF8, leaveOpen: true);
                        using var sw = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
                        sw.WriteLine("%YAML 1.1");
                        ReplaceAllGuid(sr, sw);
                    }
                }
                else
                {
                    data.CopyTo(stream);
                }
                stream.Position = 0;
                entry2.DataStream = stream;
            }
            unitypackage.WriteEntry(entry2);

            void ReplaceAllGuid(StreamReader sr, StreamWriter sw)
            {
                var regex = RegexPatterns.GUIDPattern();

                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    var match = regex.Match(line);
                    if (match.Success)
                    {
                        var g = Guid.Parse(match.ValueSpan);
                        if (guidMap.TryGetValue(g, out var g2))
                        {
                            Console.WriteLine($"in {guids.Original}: Replace Guid {g} => {g2}");
                            line = regex.Replace(line, g2.ToString("N"));
                        }
                    }
                    sw.WriteLine(line);
                }
            }
        }
    }
}

partial class RegexPatterns
{
    [GeneratedRegex(@"(?<=guid: )[0-9a-zA-Z]+")]
    public static partial Regex GUIDPattern();
}