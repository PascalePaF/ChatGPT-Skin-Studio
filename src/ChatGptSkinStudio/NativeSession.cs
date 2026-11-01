using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChatGptSkinStudio;

internal sealed record NativeState(string State, int Width, int Height, int SourceWidth, int SourceHeight, long Frames, long DecodedFrames, double Duration, int HitPid, string Error);

internal sealed class NativeSession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Process process;
    private readonly EventWaitHandle stop;
    private readonly string directory;
    private readonly Task<string> stderr;
    private readonly OwnedJob job;
    private readonly bool guardian;
    private int disposed;
    public string DirectoryPath => directory;
    public bool Alive { get { try { return !process.HasExited; } catch { return false; } } }
    public NativeState? LastState { get; private set; }

    private NativeSession(Process process, EventWaitHandle stop, string directory, Task<string> stderr, OwnedJob job, bool guardian)
    { this.process = process; this.stop = stop; this.directory = directory; this.stderr = stderr; this.job = job; this.guardian = guardian; }

    public static async Task<NativeSession> StartAsync(Dictionary<string, object?> request, CancellationToken token)
    {
        string helper = Path.Combine(AppContext.BaseDirectory, "native", "SkinRenderer.exe");
        if (!File.Exists(helper)) throw new FileNotFoundException("本地渲染组件缺失，请安装完整版本。", helper);
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatGptSkinStudio", "sessions", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        string stopName = "Local\\CGSS-" + Guid.NewGuid().ToString("N");
        var stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
        using var owner = Process.GetCurrentProcess();
        request["parentPid"] = owner.Id; request["parentBirth"] = owner.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
        request["stopEvent"] = stopName; request["statusPath"] = Path.Combine(directory, "status.json");
        if (request.Remove("exportFrame", out object? export) && export is true) request["snapshotPath"] = Path.Combine(directory, "frame.png");
        if (request.Remove("recordOutput", out object? recording) && recording is true) request["recordPath"] = Path.Combine(directory, "wallpaper.mp4");
        string file = Path.Combine(directory, "request.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(request, Json), new UTF8Encoding(false), token);
        var info = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        info.ArgumentList.Add(file);
        Process? process = null;
        NativeSession? session = null;
        OwnedJob? job = null;
        try
        {
            job = new OwnedJob();
            process = Process.Start(info) ?? throw new InvalidOperationException("本地渲染器没有启动。");
            _ = process.Handle; // Keep the original process handle: never kill a reused PID.
            bool guardian = request.TryGetValue("mode", out var requestedMode) && (string?)requestedMode == "guard";
            if (!guardian) job.Assign(process.Handle); // A scene guardian must survive owner death briefly to close its unique engine window.
            _ = process.StandardOutput.ReadToEndAsync();
            session = new NativeSession(process, stop, directory, process.StandardError.ReadToEndAsync(), job, guardian);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(18))
            {
                token.ThrowIfCancellationRequested();
                var state = session.ReadState();
                bool probe = request.TryGetValue("mode", out object? mode) && ((string?)mode == "probe" || (string?)mode == "guard");
                if ((state?.State is "ready" or "prepared" or "recording" or "completed") && (probe || state.Frames >= 3)) return session;
                if (state?.State == "error" || !session.Alive)
                {
                    string error = state?.Error ?? (session.stderr.IsCompleted ? await session.stderr : "renderer_exited");
                    throw new InvalidOperationException(FriendlyError(error));
                }
                await Task.Delay(60, token);
            }
            throw new TimeoutException("暂未收到有效画面，已保留客户端。请检查素材或 Wallpaper Engine 状态。");
        }
        catch
        {
            if (session is not null) await session.DisposeAsync();
            else { if (process is not null) { try { if (!process.HasExited) process.Kill(); } catch { } process.Dispose(); } job?.Dispose(); stop.Dispose(); }
            if (request.TryGetValue("mode", out var failedMode) && (string?)failedMode == "record")
            { try { File.Delete(Path.Combine(directory, "wallpaper.mp4")); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LocalStore.Log("record-start-cleanup", ex.Message); } }
            throw;
        }
    }

    public NativeState? ReadState()
    {
        try
        {
            string file = Path.Combine(directory, "status.json");
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            LastState = new NativeState(root.GetProperty("state").GetString() ?? "unknown", root.GetProperty("width").GetInt32(), root.GetProperty("height").GetInt32(),
                root.GetProperty("sourceWidth").GetInt32(), root.GetProperty("sourceHeight").GetInt32(), root.GetProperty("frames").GetInt64(),
                root.GetProperty("decodedFrames").GetInt64(), root.GetProperty("duration").GetDouble(),
                root.GetProperty("hitPid").GetInt32(), root.GetProperty("error").GetString() ?? "");
        }
        catch (IOException) { }
        catch (JsonException) { }
        return LastState;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            stop.Set();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(guardian ? 14 : 3));
            try { if (!process.HasExited) await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); }
        }
        catch (InvalidOperationException) { }
        finally { job.Dispose(); process.Dispose(); stop.Dispose(); }
    }

    internal static Dictionary<string, object?> RequestFor(ClientWindow? client, string sourceKind, string sourcePath, string mode = "render", nint preview = default)
    {
        return new Dictionary<string, object?>
        {
            ["mode"] = mode, ["sourceKind"] = sourceKind, ["sourcePath"] = sourcePath,
            ["targetPid"] = client?.Pid ?? 0, ["targetBirth"] = client?.Birth.ToString(CultureInfo.InvariantCulture) ?? "0",
            ["targetHwnd"] = client?.Hwnd.ToInt64().ToString(CultureInfo.InvariantCulture) ?? "0", ["previewHwnd"] = preview.ToInt64().ToString(CultureInfo.InvariantCulture),
            ["fit"] = "glass-dark", ["glassOpacity"] = .86, ["glassBlur"] = 40, ["dim"] = .32, ["surface"] = .72
        };
    }

    internal static string FriendlyError(string code)
    {
        if (code.Contains("record_encoder") || code.Contains("record_finalize")) return "系统未能生成 H.264 视频。可尝试较低的生成分辨率，静态原图仍可直接使用。诊断：" + code;
        if (code.Contains("record_size")) return "此场景分辨率超过本次视频生成上限，请选择 2560 × 1440 或 3840 × 2160。";
        if (code.Contains("resize")) return "窗口尺寸尚未稳定，皮肤层已退出以保留原界面的正常点击。尺寸稳定后可重新应用。";
        if (code.Contains("intro_video")) return "启动动画文件已移动或不存在，请在启动动画页重新选择。";
        if (code.Contains("media_missing")) return "素材已移动或不存在，请重新识别或导入。";
        if (code.Contains("video") || code.Contains("graphics_c00d")) return "此视频未能实际解码。请使用标准 H.264 MP4；其他编码取决于系统已安装的解码器。";
        if (code.Contains("scene")) return "没有读取到可用的离线壁纸原素材；请重新识别本地项目或导入原图、视频。";
        if (code.Contains("client_unverified") || code.Contains("window_changed") || code.Contains("process_changed")) return "客户端窗口已变化，请重新检查当前官方客户端。";
        if (code.Contains("input_intercepted")) return "背景层的输入穿透未通过核验，本次应用已取消。";
        if (code.Contains("capture_no_frames")) return "客户端尚未提供有效画面，请恢复可见窗口后重试。";
        return "本地画面处理失败，原客户端保留。诊断代码：" + code.Trim();
    }
}
