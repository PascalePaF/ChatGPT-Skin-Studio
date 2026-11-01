using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ChatGptSkinStudio;

internal static class Program
{
    [STAThread]
    private static int Main()
    {

        var app = new Application();
        var window = new MainWindow(previewOnly: true);
        var updateLayout = typeof(MainWindow).GetMethod("UpdateLibraryVisibility", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var expandedField = typeof(MainWindow).GetField("expanded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        expandedField.SetValue(window, true); updateLayout.Invoke(window, null);
        var content = (FrameworkElement)window.Content;
        ((TextBlock)window.FindName("AssetTitle")).Text = "用于检查长标题的高清场景动态壁纸 · 本地副本";
        ((TextBlock)window.FindName("AssetSource")).Text = "场景 · 离线提取 · Wallpaper Engine · 桌面正在使用";
        ((TextBlock)window.FindName("ResolutionText")).Text = "原素材 3840 × 2160 · 预览 590 × 300 · 10 秒 · 静音循环";
        ((TextBlock)window.FindName("PreviewCaption")).Text = "离线场景原图，不含场景特效。完整等比；空余区域使用黑色底毛玻璃。主体清晰、不裁切、不变形，没有彩色泛光。";
        ((TextBlock)window.FindName("OperationStatus")).Text = "正在生成高清动态壁纸，其他壁纸仍可浏览；这里加入较长说明，检查窄窗口与多行状态下的底部操作区域。";
        ((TextBlock)window.FindName("ResolutionText")).TextWrapping = TextWrapping.NoWrap;
        ((TextBlock)window.FindName("PreviewCaption")).TextWrapping = TextWrapping.NoWrap;
        // Pure layout: never Show, activate, move or resize a desktop window.
        foreach (var dimensions in new[] { new Size(1104, 680), new Size(1404, 860) })
        {
            var record = (FrameworkElement)window.FindName("RecordPanel"); record.Visibility = Visibility.Visible;
            var cancel = (FrameworkElement)window.FindName("CancelRecordButton"); cancel.Visibility = Visibility.Visible;
            var progress = (FrameworkElement)window.FindName("RecordProgress"); progress.Visibility = Visibility.Visible;
            content.Measure(dimensions); content.Arrange(new Rect(dimensions)); content.UpdateLayout();
            var occupied = new System.Collections.Generic.List<(string Name, Rect Bounds)>();
            foreach (string name in new[] { "ApplyButton", "RecordButton", "CancelRecordButton", "FitBox", "PreviewMode", "SaveExtractedButton", "OfflineVideosButton", "RecordQualityBox", "RecordDurationBox", "PauseButton", "StopButton", "ImportButton", "ExpandButton", "GroupFilterBox", "MoveGroupBox", "MoveButton", "RemoveButton", "NewGroupButton", "DayThemeButton", "NightThemeButton", "UiThemeBox", "SettingsTab", "LibraryTab" })
            {
                var item = (FrameworkElement)window.FindName(name);
                var location = item.TranslatePoint(new Point(), content);
                if (item.ActualWidth < 30 || item.ActualHeight < 20 || location.X < 0 || location.Y < 0 || location.X + item.ActualWidth > dimensions.Width + 1 || location.Y + item.ActualHeight > dimensions.Height + 1)
                    throw new Exception($"{name} outside {dimensions}: {location} {item.RenderSize}");
                Console.WriteLine($"PASS {dimensions.Width}×{dimensions.Height}: {name} within content ({item.ActualWidth:0}×{item.ActualHeight:0})");
                var bounds = new Rect(location, item.RenderSize);
                foreach (var other in occupied)
                {
                    var intersection = Rect.Intersect(bounds, other.Bounds);
                    if (!intersection.IsEmpty && intersection.Width > .5 && intersection.Height > .5) throw new Exception($"{name} overlaps {other.Name}.");
                }
                occupied.Add((name, bounds));
            }
            var preview = (FrameworkElement)window.FindName("PreviewContainer");
            Console.WriteLine("Preview rows: " + string.Join(", ", System.Linq.Enumerable.Select(((Grid)window.FindName("PreviewPanel")).RowDefinitions, r => r.ActualHeight.ToString("0.#"))));
            if (preview.ActualWidth < 500 || preview.ActualHeight < 100) throw new Exception($"Preview has insufficient space: {preview.ActualWidth}×{preview.ActualHeight}.");
            double original = ((FrameworkElement)window.FindName("AssetList")).ActualWidth;
            expandedField.SetValue(window, false); updateLayout.Invoke(window, null);
            content.Measure(dimensions); content.Arrange(new Rect(dimensions)); content.UpdateLayout();
            if (((FrameworkElement)window.FindName("PreviewPanel")).Visibility != Visibility.Collapsed || ((FrameworkElement)window.FindName("AssetList")).ActualWidth < original + 500)
                throw new Exception("Collapsing preview did not give the full content width to the wallpaper gallery.");
            if (((FrameworkElement)window.FindName("ExpandButton")).Visibility != Visibility.Visible) throw new Exception("Cannot reopen preview.");
            foreach (string name in new[] { "RecordButton", "CancelRecordButton", "RecordDurationBox", "RecordQualityBox", "RecordProgress", "SaveExtractedButton", "OfflineVideosButton" })
            {
                var item = (FrameworkElement)window.FindName(name);
                for (DependencyObject? current = item; current is not null && !ReferenceEquals(current, content); current = System.Windows.Media.VisualTreeHelper.GetParent(current))
                    if (current is FrameworkElement element && element.Visibility != Visibility.Visible) throw new Exception($"Collapsing preview hides {name} through {element.Name}.");
                var point = item.TranslatePoint(new Point(), content);
                if (item.ActualWidth < 10 || point.Y < 0 || point.Y + item.ActualHeight > dimensions.Height + 1) throw new Exception($"Collapsed gallery loses access to {name}.");
            }
            Console.WriteLine("PASS recording, cancellation, progress, save and offline-video access survive preview collapse.");
            Console.WriteLine("PASS preview collapses, full-width gallery stays visible, reopening control remains accessible.");
            expandedField.SetValue(window, true); updateLayout.Invoke(window, null);
        }
        if (new WindowInteropHelper(window).Handle != nint.Zero) throw new Exception("Layout check unexpectedly created a desktop window.");
        Console.WriteLine("PASS all layout checks; no desktop window was shown or created.");
        return 0;
    }
}
