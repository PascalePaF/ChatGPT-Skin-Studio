using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ChatGptSkinStudio;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--shutdown")
            {
                try { using var signal = EventWaitHandle.OpenExisting("Local\\ChatGptSkinStudio-Shutdown-1"); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
                return 0;
            }
            if (args.Length >= 2 && args[0] == "--diagnostics")
            {
                var client = DesktopClient.Find();
                File.WriteAllText(args[1], JsonSerializer.Serialize(client is null ? null : new { Hwnd = client.Hwnd.ToInt64().ToString(), client.Pid, client.Birth, client.Family, client.Width, client.Height, client.Minimized }));
                return 0;
            }
            if (args.Length >= 3 && args[0] == "--verify-background") return VerifyAsync(args[1], args[2]).GetAwaiter().GetResult();
            if (args.Length >= 3 && args[0] == "--inspect-wallpaper") return InspectAsync(args[1], args[2], true).GetAwaiter().GetResult();
            if (args.Length >= 3 && args[0] == "--inspect-media") return InspectAsync(args[1], args[2], false).GetAwaiter().GetResult();
            if (args.Length >= 3 && args[0] == "--extract-wallpaper") return ExtractAsync(args[1], args[2]).GetAwaiter().GetResult();
            if (args.Length >= 2 && args[0] == "--library-inventory")
            {
                var result = Library.Scan(LocalStore.Load(), CancellationToken.None);
                File.WriteAllText(args[1], JsonSerializer.Serialize(result)); return 0;
            }
            using var single = new Mutex(true, "Local\\ChatGptSkinStudio-1", out bool first);
            if (!first) { MessageBox.Show("管理器已在运行。可从任务栏或托盘打开。", "ChatGPT Skin Studio"); return 0; }
            var app = new Application();
            app.DispatcherUnhandledException += (_, e) => { LocalStore.Log("unhandled", e.Exception.ToString()); e.Handled = true; MessageBox.Show(e.Exception.Message, "ChatGPT Skin Studio"); };
            return app.Run(new MainWindow(args.Contains("--tray"),args.Contains("--preview-only")));
        }
        catch (Exception ex)
        {
            if (args.Length > 1) { try { File.WriteAllText(args[^1] + ".error.txt", ex.ToString()); } catch { } }
            else MessageBox.Show(ex.Message, "ChatGPT Skin Studio");
            return 1;
        }
    }
    private static async Task<int> VerifyAsync(string source, string report)
    {
        var client = DesktopClient.Find() ?? throw new InvalidOperationException("没有找到可见的官方客户端主窗口。");
        client = await DesktopClient.RestoreForApplyAsync(client, CancellationToken.None);
        string kind = Path.GetExtension(source).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? "video" : "image";
        var request = NativeSession.RequestFor(client, kind, source);
        var settings = LocalStore.Load(); request["fit"] = settings.Fit; request["dim"] = settings.Dim; request["surface"] = settings.Surface;
        request["glassOpacity"] = settings.GlassOpacity; request["glassBlur"] = settings.GlassBlur;
        request["exportFrame"] = true; request["maxSeconds"] = 30;
        await using var session = await NativeSession.StartAsync(request, CancellationToken.None);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { ClientPid = client.Pid, client.Width, client.Height, Session = session.DirectoryPath, State = session.ReadState() }));
        await Task.Delay(12000);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { ClientPid = client.Pid, client.Width, client.Height, Session = session.DirectoryPath, State = session.ReadState() }));
        return 0;
    }
    private static async Task<int> InspectAsync(string source, string report, bool wallpaper)
    {
        var library = Library.Scan(LocalStore.Load(), CancellationToken.None);
        var asset = wallpaper ? library.Items.FirstOrDefault(i => i.Id == "wallpaper-" + Library.Hash(Path.GetFullPath(source)) || string.Equals(i.Path, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            : new SkinAsset("inspect", Path.GetFileName(source), Library.KindFor(source), Path.GetFullPath(source), "", "本地文件");
        if (asset is null || !asset.Usable) throw new InvalidOperationException(asset?.Problem ?? "没有识别到此壁纸。");
            var material = await OfflineWallpaper.ResolveAsync(asset, CancellationToken.None);
            var request = NativeSession.RequestFor(null, material.Kind, material.Path, "probe");
            request["exportFrame"] = true;
            await using var session = await NativeSession.StartAsync(request, CancellationToken.None);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { asset.Title, asset.Kind, Material = material, Session = session.DirectoryPath, State = session.ReadState() }));
            return 0;
    }
    private static async Task<int> ExtractAsync(string project, string report)
    {
        var library = Library.Scan(LocalStore.Load(), CancellationToken.None);
        var asset = library.Items.FirstOrDefault(i => i.Id == "wallpaper-" + Library.Hash(Path.GetFullPath(project))) ?? throw new InvalidOperationException("没有识别到这个本地壁纸项目。");
        var material = await OfflineWallpaper.ResolveAsync(asset, CancellationToken.None);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { asset.Title, asset.Kind, Material = material }));
        return 0;
    }
}
