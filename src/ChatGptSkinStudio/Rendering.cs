using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;

namespace ChatGptSkinStudio;

internal sealed class PreviewHost : HwndHost
{
    public nint HandleValue { get; private set; }
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        HandleValue = CreateWindowEx(0, "STATIC", "", 0x40000000 | 0x10000000 | 0x02000000 | 0x00000004, 0, 0, 100, 100, parent.Handle, nint.Zero, nint.Zero, nint.Zero);
        if (HandleValue == nint.Zero) throw new System.ComponentModel.Win32Exception();
        return new HandleRef(this, HandleValue);
    }
    protected override void DestroyWindowCore(HandleRef window) { DestroyWindow(window.Handle); HandleValue = nint.Zero; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(int ex, string cls, string text, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
}

internal sealed class RenderLease : IAsyncDisposable
{
    private NativeSession? native;
    public OfflineMedia? Material { get; private set; }
    public NativeSession Session => native ?? throw new ObjectDisposedException(nameof(RenderLease));
    private RenderLease() { }
    public static async Task<RenderLease> StartAsync(SkinAsset asset, Preferences settings, ClientWindow? client, string mode, nint preview, bool animate, CancellationToken token, bool transient = false, Action<string>? progress = null)
    {
        if (!asset.Usable) throw new InvalidOperationException(asset.Problem);
        var lease = new RenderLease();
        try
        {
            progress?.Invoke("正在读取壁纸原素材");
            lease.Material = await OfflineWallpaper.ResolveAsync(asset, token);
            var request = NativeSession.RequestFor(client, lease.Material.Kind, lease.Material.Path, mode, preview);
            request["fit"] = settings.Fit; request["dim"] = settings.Dim; request["surface"] = settings.Surface; request["preset"] = asset.Preset;
            request["glassOpacity"] = settings.GlassOpacity; request["glassBlur"] = settings.GlassBlur;
            request["introSeconds"] = animate && settings.IntroEnabled ? settings.IntroSeconds : 0;
            request["introPath"] = settings.IntroPath;
            if (transient) { request["maxSeconds"] = settings.IntroSeconds + .1; request["introOnly"] = true; }
            progress?.Invoke("素材已读取，正在连接客户端并准备背景画面");
            lease.native = await NativeSession.StartAsync(request, token);
            return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        var ownNative = Interlocked.Exchange(ref native, null);
        if (ownNative is not null) await ownNative.DisposeAsync();
    }
}

internal static class PreferenceSnapshot
{
    internal static Preferences Of(Preferences source) => new()
    { Fit = source.Fit, FitRevision = source.FitRevision, GlassOpacity = source.GlassOpacity, GlassBlur = source.GlassBlur, Dim = source.Dim, Surface = source.Surface, IntroEnabled = source.IntroEnabled, IntroPath = source.IntroPath, IntroSeconds = source.IntroSeconds, ResumeOnClient = source.ResumeOnClient };
}
