using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ChatGptSkinStudio;

internal sealed record SkinAsset(string Id, string Title, string Kind, string Path, string Preview, string Origin, int Preset = 0, string Desktop = "", string Problem = "", string Detail = "")
{
    [JsonIgnore] public string TypeLabel => Kind switch { "builtin" => "内置动态", "image" => "原图", "video" => Origin == "本地生成" ? "离线视频" : "原始视频", "scene" => "场景 · 离线提取", "web" => "网页 · 提取原素材", _ => "不支持" };
    [JsonIgnore] public string ThumbnailGlyph => Kind == "video" ? "▶" : "◈";
    [JsonIgnore] public string Subtitle => TypeLabel + " · " + Origin + (Desktop.Length > 0 ? " · 桌面正在使用" : "") + (Detail.Length > 0 ? " · " + Detail : "");
    [JsonIgnore] public bool IsLocalCopy => Id.StartsWith("import-", StringComparison.Ordinal);
    [JsonIgnore] public bool Usable => Problem.Length == 0;
    [JsonIgnore] public BitmapImage? Thumbnail => Library.TryThumbnail(Preview);
    public override string ToString() => Title;
}

internal sealed class Preferences
{
    public string Fit { get; set; } = "glass-dark";
    public int FitRevision { get; set; } = 2;
    public double GlassOpacity { get; set; } = .86;
    public double GlassBlur { get; set; } = 40;
    public double Dim { get; set; } = .38;
    public double Surface { get; set; } = .72;
    public bool IntroEnabled { get; set; } = true;
    public double IntroSeconds { get; set; } = 1.2;
    public string IntroPath { get; set; } = "";
    public bool ResumeOnClient { get; set; } = true;
    public string SelectedId { get; set; } = "builtin-aurora";
    public string AppliedId { get; set; } = "";
    public bool AutoApply { get; set; }
    public string UiTheme { get; set; } = "dark";
    public bool LibraryCollapsed { get; set; }
    public bool PreviewExpanded { get; set; }
    public bool AutoScan { get; set; } = true;
    public string ClientTheme { get; set; } = "auto";
    public List<WallpaperGroup> Groups { get; set; } = new();
    public Dictionary<string, string> GroupAssignments { get; set; } = new();
    public List<string> HiddenIds { get; set; } = new();
    public int RecordSeconds { get; set; } = 20;
    public List<string> Directories { get; set; } = new();
    public List<SkinAsset> Imports { get; set; } = new();
    public void Normalize()
    {
        if (Fit == "extend") Fit = "glass-dark";
        if (FitRevision < 2) FitRevision = 2;
        if (Fit != "glass-dark" && Fit != "glass-light" && Fit != "contain" && Fit != "cover" && Fit != "stretch") Fit = "glass-dark";
        GlassOpacity = double.IsFinite(GlassOpacity) ? Math.Clamp(GlassOpacity, .65, .96) : .86;
        GlassBlur = double.IsFinite(GlassBlur) ? Math.Clamp(GlassBlur, 12, 80) : 40;
        Dim = double.IsFinite(Dim) ? Math.Clamp(Dim, 0, .85) : .38;
        Surface = double.IsFinite(Surface) ? Math.Clamp(Surface, 0, 1) : .72;
        IntroSeconds = double.IsFinite(IntroSeconds) ? Math.Clamp(IntroSeconds, .3, 5) : 1.2;
        Directories ??= new(); Imports ??= new();
        if (UiTheme is not ("light" or "dark" or "halloween")) UiTheme = "dark";
        if (ClientTheme is not ("auto" or "light" or "dark")) ClientTheme = "auto";
        Catalog.Normalize(this);
        RecordSeconds = Math.Clamp(RecordSeconds, 5, 60);
    }
}

internal static class LocalStore
{
    internal static string Root => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatGptSkinStudio");
    internal static string SettingsPath => System.IO.Path.Combine(Root, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    internal static Preferences Load()
    {
        Directory.CreateDirectory(Root);
        try
        {
            if (File.Exists(SettingsPath) && new FileInfo(SettingsPath).Length < 2_000_000)
            { var result = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsPath), Json); if (result is not null) { result.Normalize(); return result; } }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { Log("settings", ex.Message); }
        var created = new Preferences(); created.Normalize(); return created;
    }
    internal static void Save(Preferences preferences)
    {
        preferences.Normalize(); Directory.CreateDirectory(Root);
        string temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, Json), new UTF8Encoding(false));
        File.Move(temporary, SettingsPath, true);
    }
    internal static void Log(string scope, string message)
    {
        try
        {
            string path = System.IO.Path.Combine(Root, "diagnostic.log");
            Directory.CreateDirectory(Root);
            if (File.Exists(path) && new FileInfo(path).Length > 512_000) File.Move(path, path + ".previous", true);
            File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + scope + ": " + message + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    internal static async Task<SkinAsset> ImportAsync(string source, CancellationToken token)
    {
        string kind = Library.KindFor(source);
        if (kind.Length == 0 || !File.Exists(source)) throw new InvalidOperationException("请选择 PNG、JPG、BMP、GIF 或 MP4 视频。");
        string id = Guid.NewGuid().ToString("N");
        string directory = System.IO.Path.Combine(Root, "media", id); Directory.CreateDirectory(directory);
        string target = System.IO.Path.Combine(directory, "original" + System.IO.Path.GetExtension(source).ToLowerInvariant());
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, true))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, true))
                await input.CopyToAsync(output, token);
            return new SkinAsset("import-" + id, System.IO.Path.GetFileNameWithoutExtension(source), kind, target, kind == "image" ? target : "", "本地导入");
        }
        catch { if (File.Exists(target)) File.Delete(target); if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); throw; }
    }
    internal static void DeleteImport(SkinAsset item)
    {
        if (!item.Id.StartsWith("import-", StringComparison.Ordinal)) return;
        string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, "media")) + System.IO.Path.DirectorySeparatorChar;
        string path = System.IO.Path.GetFullPath(item.Path);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(path).StartsWith("original.", StringComparison.Ordinal)) return;
        var parent = Directory.GetParent(path);
        if (parent is null || parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
        if (File.Exists(path)) File.Delete(path);
        string thumb = System.IO.Path.Combine(parent.FullName, "preview.png"); if (File.Exists(thumb)) File.Delete(thumb);
        if (!Directory.EnumerateFileSystemEntries(parent.FullName).Any()) parent.Delete();
    }
}

internal sealed record LibraryScan(List<SkinAsset> Items, List<string> Roots, string Engine, List<string> Warnings);

internal static class Library
{
    private static readonly Dictionary<string, (long Modified, long Length, BitmapImage Bitmap)> ThumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    internal static string KindFor(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    { ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" => "image", ".mp4" or ".m4v" or ".wmv" => "video", _ => "" };
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())))[..24];
    internal static List<SkinAsset> Builtins() => new()
    {
        new("builtin-aurora", "极光 · Aurora", "builtin", "", "", "Skin Studio", 0),
        new("builtin-plum", "暮紫 · Plum", "builtin", "", "", "Skin Studio", 1),
        new("builtin-blue", "深海 · Deep Blue", "builtin", "", "", "Skin Studio", 2),
        new("builtin-halloween", "万圣夜庄园 · Lantern Manor", "builtin", System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "halloween-manor-final.png"), System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "halloween-manor-final.png"), "Skin Studio", 3, Detail: "原创场景插画 · 动态烛光、夜雾与光点")
    };
    internal static LibraryScan Scan(Preferences settings, CancellationToken token)
    {
        var items = Builtins(); items.AddRange(settings.Imports.Select(i => i with { Problem = File.Exists(i.Path) ? "" : "原素材已丢失，请重新导入。" }));
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steam = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>(); string engine = "";
        void Steam(string? path) { if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) steam.Add(System.IO.Path.GetFullPath(path)); }
        foreach (string key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam" })
        { Steam(Registry.GetValue(key, "SteamPath", null) as string); Steam(Registry.GetValue(key, "InstallPath", null) as string); }
        foreach (string initial in steam.ToArray())
        {
            string vdf = System.IO.Path.Combine(initial, "steamapps", "libraryfolders.vdf");
            try
            {
                if (!File.Exists(vdf) || new FileInfo(vdf).Length > 2_000_000) continue;
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"((?:\\\\.|[^\"])*)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    Steam(match.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch (IOException ex) { warnings.Add("有一个 Steam 库暂时无法读取：" + ex.Message); }
        }
        var active = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string lib in steam)
        {
            token.ThrowIfCancellationRequested();
            string installed = System.IO.Path.Combine(lib, "steamapps", "common", "wallpaper_engine");
            string exe = System.IO.Path.Combine(installed, "wallpaper64.exe");
            if (!File.Exists(exe)) exe = System.IO.Path.Combine(installed, "wallpaper32.exe");
            if (File.Exists(exe))
            {
                engine = exe;
                AddRoot(System.IO.Path.Combine(installed, "projects", "myprojects"));
                AddRoot(System.IO.Path.Combine(installed, "projects", "defaultprojects"));
                ReadDesktop(System.IO.Path.Combine(installed, "config.json"), active);
            }
            AddRoot(System.IO.Path.Combine(lib, "steamapps", "workshop", "content", "431960"));
        }
        foreach (string path in settings.Directories)
        {
            AddRoot(path);
            foreach (string install in new[] { path, System.IO.Path.Combine(path, "steamapps", "common", "wallpaper_engine") })
            {
                string executable = System.IO.Path.Combine(install, "wallpaper64.exe");
                if (!File.Exists(executable)) executable = System.IO.Path.Combine(install, "wallpaper32.exe");
                if (!File.Exists(executable)) continue;
                engine = executable; AddRoot(System.IO.Path.Combine(install, "projects", "myprojects")); AddRoot(System.IO.Path.Combine(install, "projects", "defaultprojects"));
                ReadDesktop(System.IO.Path.Combine(install, "config.json"), active);
            }
            AddRoot(System.IO.Path.Combine(path, "steamapps", "workshop", "content", "431960"));
        }
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            foreach (string project in FindProjects(root, token))
            {
                if (!projects.Add(project)) continue;
                try { var asset = ReadProject(project, active, token); if (asset is not null) items.Add(asset); }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
                { warnings.Add(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(project)) + "：项目描述无法读取。"); }
            }
        }
        foreach (string manual in settings.Directories)
        {
            if (!Directory.Exists(manual)) continue;
            foreach (string file in FindLooseMedia(manual, token))
                items.Add(new SkinAsset("directory-" + Hash(file), System.IO.Path.GetFileNameWithoutExtension(file), KindFor(file), file, KindFor(file) == "image" ? file : "", "扫描目录"));
        }
        var hidden = settings.HiddenIds.ToHashSet(StringComparer.Ordinal);
        var final = items.Where(i => !hidden.Contains(i.Id)).GroupBy(i => i.Id).Select(g => g.First()).OrderBy(i => i.Kind == "builtin" ? 0 : i.Desktop.Length > 0 ? 1 : i.IsLocalCopy ? 2 : 3).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new LibraryScan(final, roots.ToList(), engine, warnings);
        void AddRoot(string path) { if (Directory.Exists(path)) roots.Add(System.IO.Path.GetFullPath(path)); }
    }
    private static IEnumerable<string> FindProjects(string root, CancellationToken token)
    {
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0)); int visited = 0;
        while (pending.Count > 0 && visited++ < 10000)
        {
            token.ThrowIfCancellationRequested(); var item = pending.Pop();
            try { if ((File.GetAttributes(item.Path) & FileAttributes.ReparsePoint) != 0) continue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            string project = System.IO.Path.Combine(item.Path, "project.json");
            if (File.Exists(project)) { yield return project; continue; }
            if (item.Depth >= 4) continue;
            string[] children; try { children = Directory.GetDirectories(item.Path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string child in children) pending.Push((child, item.Depth + 1));
        }
    }
    // Manual media folders are references, never copies. Project internals and junctions are excluded.
    private static IEnumerable<string> FindLooseMedia(string root, CancellationToken token)
    {
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((System.IO.Path.GetFullPath(root), 0)); int visited = 0, files = 0;
        while (pending.Count > 0 && visited++ < 10000 && files < 20000)
        {
            token.ThrowIfCancellationRequested(); var item = pending.Pop(); string[] entries, children;
            try
            {
                if ((File.GetAttributes(item.Path) & FileAttributes.ReparsePoint) != 0 || File.Exists(System.IO.Path.Combine(item.Path, "project.json"))) continue;
                entries = Directory.GetFiles(item.Path); children = item.Depth < 4 ? Directory.GetDirectories(item.Path) : Array.Empty<string>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string file in entries)
            {
                token.ThrowIfCancellationRequested(); if (++files > 20000) break;
                if (KindFor(file).Length == 0) continue;
                bool regular; try { regular = (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (regular) yield return file;
            }
            foreach (string child in children) pending.Push((child, item.Depth + 1));
        }
    }
    private static SkinAsset? ReadProject(string file, Dictionary<string, List<string>> active, CancellationToken token)
    {
        if (new FileInfo(file).Length > 1_000_000) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(file)); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("壁纸项目描述必须是对象。");
        string Get(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        string directory = System.IO.Path.GetDirectoryName(file)!;
        string type = Get("type").ToLowerInvariant(), original = ResolveProjectFile(directory, Get("file"));
        string preview = ResolveProjectFile(directory, Get("preview"));
        string title = Get("title"); if (title.Length == 0) title = System.IO.Path.GetFileName(directory);
        string kind = type is "scene" or "web" ? type : Library.KindFor(original);
        string problem = "";
        if (type == "application") { kind = "unsupported"; problem = "应用程序壁纸不作为皮肤运行。"; }
        else if ((original.Length == 0 || !File.Exists(original)) && !(type is "scene" or "web" && File.Exists(ResolveProjectFile(directory, "scene.pkg")))) problem = "项目的原素材不存在，不能以封面代替。";
        else if (kind.Length == 0) { kind = "unsupported"; problem = "此项目没有支持的图片、视频或可提取的原素材。"; }
        string desktop = active.TryGetValue(System.IO.Path.GetFullPath(directory), out var displays) ? string.Join("、", displays) : "";
        var result = new SkinAsset("wallpaper-" + Hash(file), title, kind, kind is "scene" or "web" ? file : original, preview, "Wallpaper Engine", 0, desktop, problem);
        if (problem.Length == 0 && kind is "scene" or "web") result = result with { Problem = OfflineWallpaper.CheckAvailability(result, token) };
        return result;
    }
    private static string ResolveProjectFile(string root, string relative)
    {
        if (relative.Length == 0 || System.IO.Path.IsPathRooted(relative)) return "";
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        if (!path.StartsWith(System.IO.Path.GetFullPath(root) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "";
        for (var current = new DirectoryInfo(System.IO.Path.GetDirectoryName(path)!); current is not null && current.FullName.Length >= root.Length; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return "";
        return path;
    }
    private static void ReadDesktop(string file, Dictionary<string, List<string>> active)
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 8_000_000) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var user in doc.RootElement.EnumerateObject())
            {
                if (user.Value.ValueKind != JsonValueKind.Object || !user.Value.TryGetProperty("general", out var general) || !general.TryGetProperty("wallpaperconfig", out var config) || !config.TryGetProperty("selectedwallpapers", out var selected)) continue;
                foreach (var monitor in selected.EnumerateObject())
                {
                    if (!monitor.Value.TryGetProperty("file", out var value) || value.ValueKind != JsonValueKind.String) continue;
                    string? directory = System.IO.Path.GetDirectoryName(value.GetString()?.Replace('/', '\\'));
                    if (string.IsNullOrEmpty(directory)) continue;
                    string key = System.IO.Path.GetFullPath(directory);
                    if (!active.TryGetValue(key, out var displays)) active[key] = displays = new();
                    displays.Add(monitor.Name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { LocalStore.Log("desktop-config", ex.Message); }
    }
    internal static BitmapImage? TryThumbnail(string path)
    {
        try
        {
            var file = new FileInfo(path); if (!file.Exists) return null;
            long modified = file.LastWriteTimeUtc.Ticks, length = file.Length;
            lock (ThumbnailCache) if (ThumbnailCache.TryGetValue(path, out var cached) && cached.Modified == modified && cached.Length == length) return cached.Bitmap;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 384;
            bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
            lock (ThumbnailCache) { if (ThumbnailCache.Count >= 256) ThumbnailCache.Clear(); ThumbnailCache[path] = (modified, length, bitmap); }
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException) { return null; }
    }
}
