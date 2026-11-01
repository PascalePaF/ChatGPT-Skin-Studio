using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatGptSkinStudio;

internal sealed record ClientWindow(nint Hwnd, int Pid, long Birth, string Family, int Width, int Height, bool Minimized)
{
    public string Generation => Pid + ":" + Birth + ":" + Hwnd.ToInt64();
}

internal static class DesktopClient
{
    internal static async Task<ClientWindow> RestoreForApplyAsync(ClientWindow client, CancellationToken token)
    {
        if (!client.Minimized) return client;
        if (Find() is not { } verified || verified.Generation != client.Generation) throw new InvalidOperationException("客户端窗口已变化，请重新检查连接。");
        if (!ShowWindowAsync(client.Hwnd, 9)) throw new InvalidOperationException("客户端窗口暂时无法恢复，请从任务栏恢复后重试。");
        for (int attempt = 0; attempt < 25; attempt++)
        {
            await Task.Delay(80, token);
            if (Find() is { Minimized: false } current && current.Generation == client.Generation) return current;
        }
        throw new InvalidOperationException("客户端窗口尚未恢复，请从任务栏恢复后重试。");
    }
    internal static ClientWindow? Find()
    {
        var processes = new Dictionary<int, (long Birth, string Family)>();
        foreach (var name in new[] { "ChatGPT", "Codex" })
        foreach (var process in Process.GetProcessesByName(name))
        using (process)
        {
            var identity = ReadIdentity(process.Id);
            if (identity is not null) processes[process.Id] = identity.Value;
        }
        var windows = new List<ClientWindow>();
        nint foreground = GetAncestor(GetForegroundWindow(), 2);
        EnumWindows((window, unused) =>
        {
            GetWindowThreadProcessId(window, out uint id);
            if (!processes.TryGetValue((int)id, out var identity) || !IsWindowVisible(window) || GetWindow(window, 4) != nint.Zero) return true;
            var type = new StringBuilder(128);
            GetClassName(window, type, type.Capacity);
            if (type.ToString() != "Chrome_WidgetWin_1") return true;
            bool minimized = IsIconic(window);
            Rect r;
            if (DwmGetWindowAttribute(window, 9, out r, Marshal.SizeOf<Rect>()) != 0 && !GetWindowRect(window, out r)) return true;
            if (!minimized && (r.Right - r.Left < 480 || r.Bottom - r.Top < 320)) return true;
            windows.Add(new ClientWindow(window, (int)id, identity.Birth, identity.Family,
                Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top), minimized));
            return true;
        }, nint.Zero);
        return windows.FirstOrDefault(w => w.Hwnd == foreground && !w.Minimized)
            ?? windows.Where(w => !w.Minimized).OrderByDescending(w => (long)w.Width * w.Height).FirstOrDefault()
            ?? windows.FirstOrDefault();
    }

    private static (long Birth, string Family)? ReadIdentity(int pid)
    {
        nint handle = OpenProcess(0x1000, false, pid);
        if (handle == nint.Zero) return null;
        try
        {
            uint length = 0;
            if (GetPackageFamilyName(handle, ref length, null) != 122 || length > 512) return null;
            var family = new StringBuilder((int)length);
            if (GetPackageFamilyName(handle, ref length, family) != 0) return null;
            string name = family.ToString();
            if (name != "OpenAI.Codex_2p2nqsd0c76g0" && name != "OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0") return null;
            if (!GetProcessTimes(handle, out long birth, out _, out _, out _)) return null;
            return (birth, name);
        }
        finally { CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out Rect value, int size);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(nint process, ref uint length, StringBuilder? name);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);
}
