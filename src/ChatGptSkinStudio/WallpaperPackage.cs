using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace ChatGptSkinStudio;

// A read-only archive index. Entry names never become output paths.
internal sealed class WallpaperPackage : IDisposable
{
    internal sealed record Entry(string Name, long Offset, int Length);
    private readonly FileStream input;
    internal Dictionary<string, Entry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal const int MaximumPayload = 256 * 1024 * 1024;

    internal WallpaperPackage(string path)
    {
        input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using var reader = new BinaryReader(input, Encoding.UTF8, true);
            string magic = ReadSizedText(reader, 32);
            if (!magic.StartsWith("PKGV", StringComparison.Ordinal)) throw new InvalidDataException("不是支持的 Wallpaper 场景包。");
            int count = reader.ReadInt32();
            if (count < 1 || count > 100000) throw new InvalidDataException("场景包文件数超出限制。");
            var entries = new List<Entry>(count);
            for (int i = 0; i < count; i++)
            {
                string name = Normalize(ReadSizedText(reader, 2048));
                long offset = reader.ReadUInt32(), length = reader.ReadUInt32();
                if (length > int.MaxValue || input.Position > 64 * 1024 * 1024) throw new InvalidDataException("场景包索引超出限制。");
                entries.Add(new Entry(name, offset, (int)length));
            }
            long start = input.Position;
            foreach (var entry in entries)
            {
                long offset = checked(start + entry.Offset);
                if (offset < start || offset > input.Length || entry.Length > input.Length - offset || !Entries.TryAdd(entry.Name, entry with { Offset = offset }))
                    throw new InvalidDataException("场景包包含重复或越界条目。");
            }
        }
        catch { input.Dispose(); throw; }
    }
    internal byte[] Read(string name, CancellationToken token, int maximum = MaximumPayload)
    {
        if (!Entries.TryGetValue(Normalize(name), out var entry)) throw new FileNotFoundException("场景素材不存在。", name);
        if (entry.Length > maximum) throw new InvalidDataException("单个场景素材超过读取限制。");
        input.Position = entry.Offset;
        byte[] result = new byte[entry.Length];
        int read = 0;
        while (read < result.Length)
        {
            token.ThrowIfCancellationRequested();
            int count = input.Read(result, read, Math.Min(128 * 1024, result.Length - read));
            if (count == 0) throw new EndOfStreamException("场景包数据不完整。");
            read += count;
        }
        return result;
    }
    internal byte[] ReadPrefix(string name, int maximum)
    {
        if (!Entries.TryGetValue(Normalize(name), out var entry)) throw new FileNotFoundException("场景素材不存在。", name);
        if (maximum < 1 || maximum > 65536) throw new ArgumentOutOfRangeException(nameof(maximum));
        input.Position = entry.Offset; byte[] bytes = new byte[Math.Min(maximum, entry.Length)]; input.ReadExactly(bytes); return bytes;
    }
    internal static string Normalize(string name)
    {
        string value = name.Replace('\\', '/').TrimEnd('\0');
        if (value.Length == 0 || value.StartsWith('/') || value.Contains(':') || value.Contains('\0')) throw new InvalidDataException("非法场景素材路径。");
        foreach (string part in value.Split('/')) if (part is "" or "." or "..") throw new InvalidDataException("非法场景素材路径。");
        return value;
    }
    private static string ReadSizedText(BinaryReader reader, int maximum)
    {
        int count = reader.ReadInt32();
        if (count < 1 || count > maximum || count > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("场景包字符串长度无效。");
        return Encoding.UTF8.GetString(reader.ReadBytes(count)).TrimEnd('\0');
    }
    public void Dispose() => input.Dispose();
}
