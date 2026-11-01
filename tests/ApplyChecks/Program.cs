using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ChatGptSkinStudio;

internal static class Program
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    [STAThread]
    private static int Main()
    {
        var app = new Application();
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var window = new MainWindow(previewOnly: true);
        Task checks = CheckAsync(window);
        var frame = new DispatcherFrame();
        _ = checks.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        checks.GetAwaiter().GetResult();
        Check(new WindowInteropHelper(window).Handle == nint.Zero, "no desktop window, native renderer or official client was opened");
        return 0;
    }
    private static async Task CheckAsync(MainWindow window)
    {
        var list = (ListBox)window.FindName("AssetList");
        var apply = (Button)window.FindName("ApplyButton");
        var item = new SkinAsset("apply-check", "Offscreen fixture", "builtin", "", "", "Skin Studio");
        list.ItemsSource = new[] { item }; list.SelectedItem = item;
        typeof(MainWindow).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, item);
        foreach (string outcome in new[] { "success", "failure", "cancellation" })
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task running = window.RunApplyOperationAsync(item.Id, () => completion.Task);
            Check(!apply.IsEnabled && (string)apply.Content == "正在应用…", outcome + ": duplicate apply disabled while pending");
            Check(((Control)window.FindName("SearchBox")).IsEnabled && ((Control)window.FindName("ScanButton")).IsEnabled
                && ((Control)window.FindName("StopButton")).IsEnabled, outcome + ": browse, scan and restore remain available");
            bool duplicateStarted = false;
            await window.RunApplyOperationAsync("duplicate", () => { duplicateStarted = true; return Task.CompletedTask; });
            Check(!duplicateStarted, outcome + ": second apply does not overlap the first");
            if (outcome == "success") completion.SetResult();
            else if (outcome == "failure") completion.SetException(new InvalidOperationException("fixture failure"));
            else completion.SetCanceled();
            try { await running; }
            catch (Exception ex) when (outcome == "failure" && ex is InvalidOperationException || outcome == "cancellation" && ex is OperationCanceledException) { }
            Check(apply.IsEnabled && (string)apply.Content == "应用全局皮肤", outcome + ": same selection can be applied again after completion");
        }
        bool retried = false;
        await window.RunApplyOperationAsync(item.Id, () => { retried = true; return Task.CompletedTask; });
        Check(retried && apply.IsEnabled, "immediate completion and retry release the apply button");
        try { await window.RunApplyOperationAsync(item.Id, () => throw new InvalidOperationException("fixture synchronous failure")); }
        catch (InvalidOperationException) { }
        Check(apply.IsEnabled, "synchronous start failure also releases the apply button");
        list.SelectedItem = null;
        await window.RunApplyOperationAsync(item.Id, () => Task.CompletedTask);
        Check(!apply.IsEnabled, "completion does not enable apply when no wallpaper is selected");
        Console.WriteLine("All apply lifecycle checks passed without changing user settings or media.");
    }
}
