using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChatGptSkinStudio;

internal static class SceneRecording
{
    internal static (int Width, int Height) DesktopSize()
    {
        int width = Math.Max(640, GetSystemMetrics(0)), height = Math.Max(360, GetSystemMetrics(1));
        double scale = Math.Min(1, 3840d / Math.Max(width, height));
        return (Math.Max(2, (int)(width * scale) / 2 * 2), Math.Max(2, (int)(height * scale) / 2 * 2));
    }
    internal static async Task<SkinAsset> CreateAsync(SkinAsset asset, string engine, int width, int height, int seconds, IProgress<double>? progress, CancellationToken token)
    {
        if (asset.Origin != "Wallpaper Engine" || asset.Kind is not ("scene" or "web") || !File.Exists(asset.Path)) throw new InvalidOperationException("请选择已下载的场景或网页壁纸。");
        if (!Path.IsPathFullyQualified(engine) || !File.Exists(engine) || Path.GetFileName(engine) is not ("wallpaper64.exe" or "wallpaper32.exe")) throw new InvalidOperationException("没有找到本机 Wallpaper Engine。静态原图仍可离线使用；生成动态副本需安装引擎并添加它的目录。");
        if (width < 2 || height < 2 || width > 4096 || height > 4096 || (long)width * height > 8847360 || seconds < 1 || seconds > 60) throw new ArgumentOutOfRangeException(nameof(width));
        using (var project = JsonDocument.Parse(await File.ReadAllTextAsync(asset.Path, token)))
            if (project.RootElement.GetProperty("type").GetString()?.ToLowerInvariant() is not ("scene" or "web")) throw new InvalidOperationException("不运行应用程序类型的壁纸。");
        string title = "CGSS.Render." + Guid.NewGuid().ToString("N");
        NativeSession? guardian = null, recording = null;
        Process? launchedEngine = null;
        OwnedJob? engineJob = null;
        string output = "";
        try
        {
            bool running = false;
            foreach (var process in new[] { "wallpaper64", "wallpaper32" }.SelectMany(Process.GetProcessesByName))
            {
                using (process) { try { if (string.Equals(Path.GetDirectoryName(process.MainModule?.FileName), Path.GetDirectoryName(engine), StringComparison.OrdinalIgnoreCase)) running = true; } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { } }
            }
            if (!running)
            {
                token.ThrowIfCancellationRequested();
                engineJob = new OwnedJob();
                launchedEngine = Process.Start(new ProcessStartInfo(engine) { WorkingDirectory = Path.GetDirectoryName(engine)!, UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("Wallpaper Engine没有启动。");
                _ = launchedEngine.Handle; engineJob.Assign(launchedEngine.Handle);
                await Task.Delay(2000, token);
                if (launchedEngine.HasExited) throw new InvalidOperationException("Wallpaper Engine未保持运行。可先手动打开一次引擎，再生成动态副本；静态原图不受影响。");
            }
            var guard = NativeSession.RequestFor(null, "builtin", "", "guard"); guard["sourceTitle"] = title; guard["enginePath"] = engine;
            guardian = await NativeSession.StartAsync(guard, token);
            int left = GetSystemMetrics(76) - width - 128, top = GetSystemMetrics(77);
            await ControlAsync(engine, token, "-control", "openWallpaper", "-file", asset.Path, "-playInWindow", title, "-width", width.ToString(CultureInfo.InvariantCulture), "-height", height.ToString(CultureInfo.InvariantCulture), "-x", left.ToString(CultureInfo.InvariantCulture), "-y", top.ToString(CultureInfo.InvariantCulture), "-borderless", "true", "-activate", "false");
            nint window = nint.Zero;
            var waiting = Stopwatch.StartNew();
            while (waiting.Elapsed < TimeSpan.FromSeconds(18))
            {
                token.ThrowIfCancellationRequested(); window = FindWindow(null, title); if (window != nint.Zero) break;
                await Task.Delay(100, token);
            }
            if (window == nint.Zero) throw new TimeoutException("引擎没有返回独立场景窗口；本次生成已取消，原有皮肤保留。");
            GetWindowThreadProcessId(window, out uint pid);
            using var source = Process.GetProcessById((int)pid);
            string root = Path.GetDirectoryName(engine)! + Path.DirectorySeparatorChar;
            if (source.MainModule?.FileName?.StartsWith(root, StringComparison.OrdinalIgnoreCase) != true) throw new InvalidOperationException("场景窗口来源不匹配。");
            nint style = GetWindowLongPtr(window, -16); SetWindowLongPtr(window, -16, new nint(style.ToInt64() & ~0x00C40000L));
            if (!SetWindowPos(window, new nint(1), left, top, width, height, 0x0010 | 0x0020 | 0x0040)) throw new System.ComponentModel.Win32Exception();
            await Task.Delay(800, token);
            var request = NativeSession.RequestFor(null, "window", "", "record");
            request["sourceTitle"] = title; request["sourcePid"] = pid; request["sourceBirth"] = source.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
            request["sourceHwnd"] = window.ToInt64().ToString(CultureInfo.InvariantCulture); request["recordOutput"] = true; request["recordSeconds"] = seconds; request["maxSeconds"] = seconds + 45;
            recording = await NativeSession.StartAsync(request, token); output = Path.Combine(recording.DirectoryPath, "wallpaper.mp4");
            var timeout = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var state = recording.ReadState();
                if (state?.State == "completed") break;
                if (state?.State == "error") throw new InvalidOperationException(NativeSession.FriendlyError(state.Error));
                if (!recording.Alive || timeout.Elapsed.TotalSeconds > seconds + 30) throw new InvalidOperationException("视频生成未完整结束，没有添加损坏的副本。");
                progress?.Report(Math.Clamp((state?.Duration ?? 0) / seconds * 100, 0, 99));
                await Task.Delay(200, token);
            }
            var result = await LocalStore.ImportAsync(output, token);
            progress?.Report(100);
            return result with { Title = asset.Title + " · 动态副本", Origin = "本地生成", Detail = $"离线 MP4 · {width}×{height} · {seconds}秒静音片段" };
        }
        finally
        {
            try { if (recording is not null) await recording.DisposeAsync(); }
            finally
            {
                try { if (guardian is not null) await guardian.DisposeAsync(); }
                finally
                {
                    engineJob?.Dispose(); launchedEngine?.Dispose();
                    if (output.Length > 0) { try { File.Delete(output); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LocalStore.Log("record-cleanup", ex.Message); } }
                }
            }
        }
    }
    private static async Task ControlAsync(string engine, CancellationToken token, params string[] args)
    {
        var info = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(engine)! };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法发送独立窗口命令。");
        _ = process.Handle;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException("Wallpaper Engine拒绝了本次独立窗口命令。");
    }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? className, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
