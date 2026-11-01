using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChatGptSkinStudio;

internal sealed record OfflineMedia(string Kind, string Path, string Description, string SourceEntry = "", int Width = 0, int Height = 0);

internal static class OfflineWallpaper
{
    private static readonly SemaphoreSlim extracting = new(1, 1);
    private const int CacheVersion = 2;
    private sealed record Manifest(int Version, string File, string Kind, string Description, string SourceEntry, int Width, int Height, long Bytes, string SHA256);

    internal static string CheckAvailability(SkinAsset asset, CancellationToken token)
    {
        try
        {
            using var files = new ProjectFiles(asset.Path, token);
            if (asset.Kind == "scene" && SceneCandidates(files, token).Count == 0)
                return "此场景没有可定位的高清主图，可能只有小尺寸贴图或3D/程序生成画面；不会以模型贴图或封面代替。";
            if (asset.Kind == "web" && !files.Names.Any(n => Eligible(n, asset.Preview)))
                return "网页没有可提取的受支持图片或视频；本版不执行网页脚本。";
            return "";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        { LocalStore.Log("offline-availability", ex.Message); return "本地原素材暂不可提取：" + ex.Message; }
    }

    internal static async Task<OfflineMedia> ResolveAsync(SkinAsset asset, CancellationToken token)
    {
        if (!asset.Usable) throw new InvalidOperationException(asset.Problem);
        if (asset.Kind is not ("scene" or "web")) return new(asset.Kind, asset.Path, asset.Detail.Length > 0 ? asset.Detail : asset.Kind == "builtin" ? "内置实时图形" : asset.Kind == "video" ? "原始视频 · 无需 Wallpaper Engine" : "原始素材 · 无需 Wallpaper Engine");
        await extracting.WaitAsync(token);
        try { return await Task.Run(() => Extract(asset, token), token); }
        finally { extracting.Release(); }
    }
    private static OfflineMedia Extract(SkinAsset asset, CancellationToken token)
    {
        using var files = new ProjectFiles(asset.Path, token);
        string directory = System.IO.Path.Combine(LocalStore.Root, "extracted", files.Fingerprint);
        string manifestPath = System.IO.Path.Combine(directory, "source.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath));
                if (saved is not null && saved.Version == CacheVersion && saved.Kind is "image" or "video" &&
                    saved.File == "original" + System.IO.Path.GetExtension(saved.File) && saved.File.IndexOfAny(new[] { '/', '\\', ':' }) < 0)
                {
                    string path = System.IO.Path.Combine(directory, saved.File);
                    if (File.Exists(path) && new FileInfo(path).Length == saved.Bytes && Digest(path, token) == saved.SHA256)
                        return new(saved.Kind, path, saved.Description, saved.SourceEntry, saved.Width, saved.Height);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { LocalStore.Log("extract-cache", ex.Message); }
        }
        List<string> candidates = asset.Kind == "scene" ? SceneCandidates(files, token) : new();
        if (candidates.Count == 0 && asset.Kind == "web")
            candidates = files.Names.Where(n => Eligible(n, asset.Preview)).OrderByDescending(n => files.Length(n)).Take(64).ToList();
        string last = "没有找到可离线使用的高清原图或视频；此项目可能只有实时生成的几何、粒子或脚本。";
        foreach (string name in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
                byte[] data = files.Read(name, token);
                DecodedWallpaper decoded;
                if (extension == ".tex") decoded = WallpaperTexture.Decode(data, token);
                else if (extension is ".mp4" or ".m4v" or ".wmv") decoded = new(data, extension, 0, 0);
                else decoded = WallpaperTexture.EncodeImage(data);
                bool video = decoded.Extension is ".mp4" or ".m4v" or ".wmv";
                // Do not silently turn a particle sprite, icon, or preview into the wallpaper.
                if (!video && (decoded.Width < 640 || decoded.Height < 360 || (long)decoded.Width * decoded.Height < 500000)) continue;
                string kind = video ? "video" : "image";
                string note = video ? "离线提取原始视频 · 无需 Wallpaper Engine" : asset.Kind == "scene" ? "离线场景原图 · 静态，不含场景特效" : "离线网页原图 · 静态，不含网页脚本与特效";
                Directory.CreateDirectory(directory);
                string file = "original" + decoded.Extension;
                string output = System.IO.Path.Combine(directory, file);
                WriteAtomic(output, decoded.Bytes, token);
                var manifest = new Manifest(CacheVersion, file, kind, note, name, decoded.Width, decoded.Height, decoded.Bytes.LongLength, Convert.ToHexString(SHA256.HashData(decoded.Bytes)));
                WriteAtomic(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }), token);
                return new(kind, output, note, name, decoded.Width, decoded.Height);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or NotSupportedException or ArgumentException or OverflowException or System.Runtime.InteropServices.COMException)
            { last = ex.Message; LocalStore.Log("extract-entry", name + ": " + ex.Message); }
        }
        throw new InvalidOperationException("此壁纸暂未提取到可用原素材，不会使用封面或启动 Wallpaper Engine。" + last);
    }
    private static List<string> SceneCandidates(ProjectFiles files, CancellationToken token)
    {
        var ranked = new List<(string Name, double Area, int Order)>();
        string scene = files.MainFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? files.MainFile : "scene.json";
        try
        {
            using var document = JsonDocument.Parse(files.Read(scene, token, 16 * 1024 * 1024));
            if (!document.RootElement.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Array) return new();
            int order = 0;
            foreach (var item in objects.EnumerateArray())
            {
                token.ThrowIfCancellationRequested(); order++;
                if (item.TryGetProperty("visible", out var visible) && visible.ValueKind == JsonValueKind.False) continue;
                if (!item.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(image.GetString())) continue;
                try
                {
                    string modelName = image.GetString()!;
                    using var model = JsonDocument.Parse(files.Read(modelName, token, 2 * 1024 * 1024));
                    if (!model.RootElement.TryGetProperty("material", out var material) || material.ValueKind != JsonValueKind.String) continue;
                    using var definition = JsonDocument.Parse(files.Read(material.GetString()!, token, 2 * 1024 * 1024));
                    if (!definition.RootElement.TryGetProperty("passes", out var passes) || passes.ValueKind != JsonValueKind.Array || passes.GetArrayLength() == 0) continue;
                    if (!passes[0].TryGetProperty("textures", out var textures) || textures.ValueKind != JsonValueKind.Array || textures.GetArrayLength() == 0 || textures[0].ValueKind != JsonValueKind.String) continue;
                    string texture = textures[0].GetString()!;
                    if (!texture.StartsWith("materials/", StringComparison.OrdinalIgnoreCase)) texture = "materials/" + texture;
                    if (!texture.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) texture += ".tex";
                    if (!files.Contains(texture)) continue;
                    var dimensions = WallpaperTexture.PeekSize(files.ReadPrefix(texture));
                    if (dimensions.Width < 640 || dimensions.Height < 360 || (long)dimensions.Width * dimensions.Height < 500000) continue;
                    double[] size = Vector(item, "size", 1), scale = Vector(item, "scale", 1), origin = Vector(item, "origin", 0);
                    double w = Math.Abs(size[0] * scale[0]), h = Math.Abs(size[1] * scale[1]);
                    // Ignore layers positioned wholly outside the author's 0..width/height canvas.
                    if (origin[0] + w / 2 <= 0 || origin[1] + h / 2 <= 0) continue;
                    ranked.Add((texture, w * h, order));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException) { if (ex is not FileNotFoundException) LocalStore.Log("scene-reference", ex.Message); }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException) { LocalStore.Log("scene-index", ex.Message); }
        return ranked.OrderByDescending(r => r.Area).ThenBy(r => r.Order).Select(r => r.Name).ToList();
    }
    private static double[] Vector(JsonElement item, string key, double fallback)
    {
        double[] result = { fallback, fallback, fallback };
        if (!item.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) return result;
        var parts = value.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int n = 0; n < Math.Min(parts.Length, result.Length); n++) if (double.TryParse(parts[n], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)) result[n] = number;
        return result;
    }
    private static bool Eligible(string name, string preview)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(name);
        string extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return extension is ".tex" or ".png" or ".jpg" or ".jpeg" or ".bmp" or ".mp4" or ".m4v" or ".wmv" &&
            !stem.StartsWith("preview", StringComparison.OrdinalIgnoreCase) && !stem.StartsWith("thumb", StringComparison.OrdinalIgnoreCase) &&
            !System.IO.Path.GetFileName(preview).Equals(System.IO.Path.GetFileName(name), StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("/masks/", StringComparison.OrdinalIgnoreCase) && !name.Contains("/effects/", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("/particle", StringComparison.OrdinalIgnoreCase) && !stem.Contains("normal", StringComparison.OrdinalIgnoreCase) && !stem.Contains("logo", StringComparison.OrdinalIgnoreCase);
    }
    private static string Digest(string path, CancellationToken token)
    {
        using var file = File.OpenRead(path); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024]; int count;
        while ((count = file.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void WriteAtomic(string path, byte[] bytes, CancellationToken token)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                for (int offset = 0; offset < bytes.Length; offset += Math.Min(128 * 1024, bytes.Length - offset)) { token.ThrowIfCancellationRequested(); stream.Write(bytes, offset, Math.Min(128 * 1024, bytes.Length - offset)); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed class ProjectFiles : IDisposable
    {
        private readonly string root;
        private readonly WallpaperPackage? package;
        private readonly Dictionary<string, string> loose = new(StringComparer.OrdinalIgnoreCase);
        internal string MainFile { get; }
        internal string Fingerprint { get; }
        internal IEnumerable<string> Names => loose.Keys.Concat(package?.Entries.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase);
        internal ProjectFiles(string project, CancellationToken token)
        {
            root = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(project))!;
            if (!File.Exists(project) || new FileInfo(project).Length > 1000000) throw new InvalidDataException("壁纸项目描述不可用。");
            if ((File.GetAttributes(project) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不读取链接项目文件。");
            using var document = JsonDocument.Parse(File.ReadAllText(project));
            MainFile = WallpaperPackage.Normalize(document.RootElement.GetProperty("file").GetString() ?? "scene.json");
            string pkg = System.IO.Path.Combine(root, MainFile.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) ? MainFile : "scene.pkg");
            CheckLocal(pkg);
            package = File.Exists(pkg) ? new WallpaperPackage(pkg) : null;
            try
            {
                var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0)); int visited = 0;
                var stamp = new StringBuilder("offline-v" + CacheVersion + "|" + root);
                while (pending.Count > 0 && visited < 12000)
                {
                    token.ThrowIfCancellationRequested(); var folder = pending.Pop();
                    foreach (string path in Directory.EnumerateFileSystemEntries(folder.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        token.ThrowIfCancellationRequested(); if (++visited > 12000) throw new InvalidDataException("壁纸项目文件数超出离线读取限制。");
                        var info = new FileInfo(path); if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((info.Attributes & FileAttributes.Directory) != 0) { if (folder.Depth < 8) pending.Push((path, folder.Depth + 1)); continue; }
                        string name = WallpaperPackage.Normalize(System.IO.Path.GetRelativePath(root, path)); loose.TryAdd(name, path);
                        stamp.Append('|').Append(name).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
                    }
                }
                Fingerprint = Library.Hash(stamp.ToString());
            }
            catch { package?.Dispose(); throw; }
        }
        internal bool Contains(string name) => package?.Entries.ContainsKey(WallpaperPackage.Normalize(name)) == true || loose.ContainsKey(WallpaperPackage.Normalize(name));
        internal long Length(string name) => package?.Entries.TryGetValue(name, out var entry) == true ? entry.Length : loose.TryGetValue(name, out var file) ? new FileInfo(file).Length : 0;
        internal byte[] ReadPrefix(string name)
        {
            name = WallpaperPackage.Normalize(name);
            if (package?.Entries.ContainsKey(name) == true) return package.ReadPrefix(name, 64);
            if (!loose.TryGetValue(name, out var file)) throw new FileNotFoundException("没有找到场景原素材。", name);
            CheckLocal(file); using var stream = File.OpenRead(file); byte[] bytes = new byte[(int)Math.Min(64, stream.Length)]; stream.ReadExactly(bytes); return bytes;
        }
        internal byte[] Read(string name, CancellationToken token, int maximum = WallpaperPackage.MaximumPayload)
        {
            name = WallpaperPackage.Normalize(name);
            if (package?.Entries.ContainsKey(name) == true) return package.Read(name, token, maximum);
            if (!loose.TryGetValue(name, out var path)) throw new FileNotFoundException("没有找到场景原素材。", name);
            CheckLocal(path); var info = new FileInfo(path);
            if (!info.Exists || info.Length < 1 || info.Length > maximum) throw new InvalidDataException("壁纸原素材长度无效或过大。");
            using var stream = File.OpenRead(path); byte[] data = new byte[(int)info.Length]; int offset = 0;
            while (offset < data.Length) { token.ThrowIfCancellationRequested(); int count = stream.Read(data, offset, Math.Min(128 * 1024, data.Length - offset)); if (count == 0) throw new EndOfStreamException(); offset += count; }
            return data;
        }
        private void CheckLocal(string path)
        {
            path = System.IO.Path.GetFullPath(path);
            if (!path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("壁纸素材不能越出项目目录。");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不读取链接素材。");
            for (var parent = new DirectoryInfo(System.IO.Path.GetDirectoryName(path)!); parent is not null && parent.FullName.Length >= root.Length; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不读取链接目录。");
        }
        public void Dispose() => package?.Dispose();
    }
}
