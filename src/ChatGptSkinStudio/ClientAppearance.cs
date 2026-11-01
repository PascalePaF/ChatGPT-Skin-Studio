using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace ChatGptSkinStudio;

// Use the same official controls as a person. No installation/configuration/database edits.
internal static class ClientAppearance
{
    internal static Task SetAsync(ClientWindow client, string theme, CancellationToken token) => Task.Run(() => Set(client, theme, token), token);
    private static void Set(ClientWindow client, string theme, CancellationToken token)
    {
        if (theme is not ("light" or "dark")) throw new ArgumentException("无效的客户端主题。");
        var timer = Stopwatch.StartNew();
        AutomationElement Root()
        {
            token.ThrowIfCancellationRequested();
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("客户端外观设置暂时没有响应。请检查官方设置页是否已打开，再重试。");
            if (DesktopClient.Find() is not { } current || current.Generation != client.Generation || current.Minimized) throw new InvalidOperationException("客户端窗口已变化或最小化，已停止外观切换。");
            return AutomationElement.FromHandle(client.Hwnd);
        }
        AutomationElement? Find(ControlType type, params string[] names) => Root().FindAll(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, type),
                new OrCondition(names.Select(n => (Condition)new PropertyCondition(AutomationElement.NameProperty, n)).ToArray())))
            .Cast<AutomationElement>().FirstOrDefault(e => !e.Current.IsOffscreen && e.Current.IsEnabled);
        AutomationElement Wait(Func<AutomationElement?> search)
        {
            for (;;) { var found = search(); if (found is not null) return found; token.ThrowIfCancellationRequested(); Thread.Sleep(120); _ = Root(); }
        }
        SetForegroundWindow(client.Hwnd);
        for (int attempt = 0; attempt < 10 && GetForegroundWindow() != client.Hwnd; attempt++) { token.ThrowIfCancellationRequested(); Thread.Sleep(80); }
        void Activate(AutomationElement element) => ClickVerifiedElement(element, Root(), client.Hwnd);
        if (Find(ControlType.Button, "Appearance", "外观") is null)
        {
            var profile = Find(ControlType.Button, "Open profile menu", "打开个人资料菜单", "打开个人信息菜单");
            if (profile is null) throw new InvalidOperationException("未找到此版本客户端的官方外观入口。请先打开客户端“设置”，再重试；管理器没有修改客户端文件。");
            Activate(profile);
            var settings = Wait(() => Find(ControlType.MenuItem, "Settings", "设置") ?? Find(ControlType.Button, "Settings", "设置")); Activate(settings);
        }
        Activate(Wait(() => Find(ControlType.Button, "Appearance", "外观")));
        string[] names = theme == "light" ? new[] { "Light", "浅色", "日间" } : new[] { "Dark", "深色", "夜间" };
        var radio = Wait(() => Find(ControlType.RadioButton, names));
        bool clickedCard = false;
        bool Confirmed()
        {
            var current = Find(ControlType.RadioButton, names); if (current is not null && IsSelected(current)) return true;
            string label = theme == "light" ? "Light background color" : "Dark background color";
            return clickedCard && Root().FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, label)).Cast<AutomationElement>().Any(e => !e.Current.IsOffscreen);
        }
        if (!Confirmed())
        {
            // Chromium cards can expose focus-only actions. Click only the freshly verified visible control.
            Activate(Wait(() => Find(ControlType.RadioButton, names))); clickedCard = true;
        }
        while (!Confirmed()) { token.ThrowIfCancellationRequested(); Thread.Sleep(120); _ = Root(); }
        // Only close Settings when a specifically labelled back control is present.
        var rootBounds = Root().Current.BoundingRectangle;
        var back = Root().FindAll(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new OrCondition(new PropertyCondition(AutomationElement.NameProperty, "Back"), new PropertyCondition(AutomationElement.NameProperty, "返回"))))
            .Cast<AutomationElement>().FirstOrDefault(e => !e.Current.IsOffscreen && e.Current.BoundingRectangle.Top > rootBounds.Top + 32 && e.Current.BoundingRectangle.Left < rootBounds.Left + 300);
        if (back is not null) Activate(back);
    }
    private static bool IsSelected(AutomationElement radio)
    {
        if (radio.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? selection)) return ((SelectionItemPattern)selection).Current.IsSelected;
        if (radio.TryGetCurrentPattern(TogglePattern.Pattern, out object? toggle)) return ((TogglePattern)toggle).Current.ToggleState == ToggleState.On;
        return false;
    }
    private static void ClickVerifiedElement(AutomationElement element, AutomationElement root, nint window)
    {
        var bounds = element.Current.BoundingRectangle; var client = root.Current.BoundingRectangle; var type = element.Current.ControlType;
        if (GetForegroundWindow() != window || type != ControlType.RadioButton && type != ControlType.Button && type != ControlType.MenuItem
            || element.Current.IsOffscreen || !element.Current.IsEnabled || !client.Contains(bounds) || bounds.Width < 8 || bounds.Height < 8)
            throw new InvalidOperationException("官方外观控件暂时不可点击，已停止切换。请回到客户端“设置 → 外观”后重试。");
        if (!element.TryGetClickablePoint(out var point)) point = new System.Windows.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        if (!bounds.Contains(point)) throw new InvalidOperationException("官方控件的点击位置无效，已停止切换。");
        GetCursorPos(out var previous); int x = (int)Math.Round(point.X), y = (int)Math.Round(point.Y);
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("无法点击官方主题卡片。");
        try
        {
            var input = new[] { new Input { Mouse = new MouseInput { Flags = 0x0002 } }, new Input { Mouse = new MouseInput { Flags = 0x0004 } } };
            if (SendInput(2, input, Marshal.SizeOf<Input>()) != 2) throw new InvalidOperationException("客户端没有收到主题切换操作。");
        }
        finally { if (GetCursorPos(out var now) && Math.Abs(now.X - x) < 3 && Math.Abs(now.Y - y) < 3) SetCursorPos(previous.X, previous.Y); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public MouseInput Mouse; }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
}
