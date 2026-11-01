using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ChatGptSkinStudio;

public partial class MainWindow : Window
{
    private readonly Preferences settings;
    private readonly DispatcherTimer monitor = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer previewDelay = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private readonly DispatcherTimer libraryTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim previewGate = new(1, 1);
    private readonly SemaphoreSlim introGate = new(1, 1);
    private readonly SemaphoreSlim libraryGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly PreviewHost previewHost = new(), introHost = new();
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private LibraryScan library = new(Library.Builtins(), new(), "", new());
    private SkinAsset? selected;
    private RenderLease? preview, active, introPreview, transient;
    private CancellationTokenSource? previewCancellation, applyCancellation, introCancellation;
    private Task? applyTask;
    private Task? recordingTask;
    private CancellationTokenSource? recordingCancellation;
    private Task? monitorTask;
    private CancellationTokenSource? monitorCancellation;
    private readonly EventWaitHandle shutdown = new(false, EventResetMode.ManualReset, "Local\\ChatGptSkinStudio-Shutdown-1");
    private ClientWindow? client;
    private string seenGeneration = "", autoAttempt = "", appliedTitle = "";
    private string applyingAssetId = "";
    private string previewAssetId = "";
    private bool initialized, closing, allowClose, paused, expanded, monitorBusy, scanning, firstObserve = true, updatingSelection, updatingGroups, changingClientTheme;
    private Task? scanWork;
    private Task? clientThemeTask;
    private int previewSequence;
    private int introSequence;
    private int activeVersion;
    private readonly bool startInTray;
    private bool previewOnly;

    public MainWindow(bool startInTray = false, bool previewOnly = false)
    {
        this.startInTray = startInTray; this.previewOnly=previewOnly; settings = LocalStore.Load(); shutdown.Reset();
        InitializeComponent();
        UiTheme.Apply(this, settings.UiTheme);
        SourceInitialized += (_, _) => UiTheme.Apply(this, settings.UiTheme);
        UiThemeBox.SelectedIndex = settings.UiTheme switch { "light" => 1, "halloween" => 2, _ => 0 };
        expanded = settings.PreviewExpanded; UpdateLibraryVisibility();
        ClientThemeBox.SelectedIndex = settings.ClientTheme == "light" ? 0 : 1;
        AutoScanBox.IsChecked = settings.AutoScan;
        UpdateGroups(); UpdateHiddenCount(); UpdateThemeButtons();
        RecordDurationBox.SelectedIndex = settings.RecordSeconds <= 10 ? 0 : settings.RecordSeconds >= 30 ? 2 : 1;
        PreviewContainer.Children.Add(previewHost); IntroPreviewContainer.Children.Add(introHost);
        FitBox.SelectedIndex = settings.Fit switch { "glass-light" => 1, "contain" => 2, "cover" => 3, "stretch" => 4, _ => 0 }; DimSlider.Value = settings.Dim; SurfaceSlider.Value = settings.Surface;
        GlassOpacitySlider.Value = settings.GlassOpacity; GlassBlurSlider.Value = settings.GlassBlur; UpdateGlassControls();
        IntroEnabledBox.IsChecked = settings.IntroEnabled; IntroDuration.Value = settings.IntroSeconds;
        ResumeBox.IsChecked = settings.ResumeOnClient; AutoStartBox.IsChecked = ReadAutoStart();
        UpdateIntroLabels(); DirectoryList.ItemsSource = settings.Directories.ToArray();
        Loaded += OnLoaded; Closing += OnClosing;
        monitor.Tick += Monitor_Tick;
        previewDelay.Tick += async (_, _) => { previewDelay.Stop(); await RefreshPreviewAsync(); };
        libraryTimer.Tick += async (_, _) => { if (settings.AutoScan) await ScanAsync(true); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        initialized = true;
        CreateTray();
        await ScanAsync();
        UpdateClient(); monitor.Start(); libraryTimer.Start();
        if (startInTray) HideToTray();
    }
    private void CreateTray()
    {
        using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/ChatGptSkinStudio;component/Assets/app.ico"))!.Stream;
        using var loadedIcon = new System.Drawing.Icon(iconStream, 32, 32);
        trayIcon = (System.Drawing.Icon)loadedIcon.Clone();
        tray = new Forms.NotifyIcon { Text = "ChatGPT Skin Studio", Icon = trayIcon, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开管理器", null, (_, _) => Dispatcher.BeginInvoke(ShowManager));
        menu.Items.Add("恢复客户端原样", null, (_, _) => Dispatcher.BeginInvoke(async () => await StopAsync()));
        menu.Items.Add("退出并停止所有皮肤", null, (_, _) => Dispatcher.BeginInvoke(Close));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowManager);
    }
    private void ShowManager() { Show(); WindowState = WindowState.Normal; Activate(); QueuePreview(); }
    private void HideToTray() { Hide(); SetStatus("已在托盘运行。关闭窗口会真正退出，也可从托盘菜单退出。"); }
    private void Background_Click(object sender, RoutedEventArgs e) => HideToTray();

    private async Task ScanAsync(bool silent = false)
    {
        if (closing || silent && scanning) return;
        try { await libraryGate.WaitAsync(lifetime.Token); } catch (OperationCanceledException) { return; }
        if (closing) { libraryGate.Release(); return; }
        scanning = true; ScanButton.IsEnabled = false;
        if (!silent) SetStatus("正在识别本地壁纸目录…");
        try
        {
            var snapshot = new Preferences { Directories = settings.Directories.ToList(), Imports = settings.Imports.ToList(), HiddenIds = settings.HiddenIds.ToList() };
            var work = Task.Run(() => Library.Scan(snapshot, lifetime.Token), lifetime.Token); scanWork = work;
            var result = await work; if (closing) return;
            result = result with { Items = result.Items.Where(a => !settings.HiddenIds.Contains(a.Id, StringComparer.Ordinal)).ToList() };
            int added = result.Items.Count(a => library.Items.All(old => old.Id != a.Id));
            library = result; string id = selected?.Id ?? settings.SelectedId;
            UpdateFilter(id); UpdateClient(); UpdateHiddenCount();
            if (!silent || added > 0) SetStatus((silent ? $"自动加入 {added} 张新壁纸 · " : "") + $"库中 {result.Items.Count} 张壁纸 · {result.Roots.Count} 个目录" + (result.Warnings.Count > 0 ? $" · {result.Warnings.Count} 个项目暂不可读" : ""));
            foreach (string warning in result.Warnings) LocalStore.Log("scan", warning);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Report(ex); }
        finally { scanning = false; ScanButton.IsEnabled = true; libraryGate.Release(); }
    }
    private void UpdateFilter(string? keep = null)
    {
        if (!initialized) return;
        bool explicitSelection = keep is not null && keep != selected?.Id;
        keep ??= selected?.Id;
        string query = SearchBox.Text.Trim(); int type = FilterBox.SelectedIndex; string group = (GroupFilterBox.SelectedItem as WallpaperGroup)?.Id ?? "*";
        var filtered = library.Items.Where(a => (query.Length == 0 || a.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)) &&
            Catalog.Matches(settings, a.Id, group) && (type switch { 1 => a.Kind == "builtin", 2 => a.IsLocalCopy, 3 => a.Origin == "Wallpaper Engine", 4 => a.Desktop.Length > 0, 5 => a.Origin == "扫描目录", 6 => a.IsLocalCopy && a.Kind == "video", _ => true })).ToList();
        CountText.Text = $"{filtered.Count} / {library.Items.Count} 张"; EmptyLibraryText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!explicitSelection && AssetList.ItemsSource is List<SkinAsset> existing && existing.SequenceEqual(filtered)) { UpdateSelectionControls(); return; }
        var retained = explicitSelection ? new System.Collections.Generic.HashSet<string>(new[] { keep! }, StringComparer.Ordinal) : AssetList.SelectedItems.Cast<SkinAsset>().Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        updatingSelection = true;
        try
        {
            AssetList.ItemsSource = filtered;
            foreach (var asset in filtered.Where(a => retained.Contains(a.Id))) AssetList.SelectedItems.Add(asset);
            if (AssetList.SelectedItems.Count == 0) AssetList.SelectedItem = filtered.FirstOrDefault(a => a.Id == keep) ?? filtered.FirstOrDefault();
        }
        finally { updatingSelection = false; }
        UpdateSelected(filtered.FirstOrDefault(a => a.Id == keep && AssetList.SelectedItems.Contains(a)) ?? AssetList.SelectedItem as SkinAsset);
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) => UpdateFilter();
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => UpdateFilter();
    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();
    private void Asset_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || updatingSelection) return;
        UpdateSelected(e.AddedItems.OfType<SkinAsset>().LastOrDefault() ?? AssetList.SelectedItem as SkinAsset);
    }
    private void UpdateSelected(SkinAsset? asset)
    {
        bool changed = selected?.Id != asset?.Id || selected?.Path != asset?.Path || selected?.Problem != asset?.Problem;
        if (asset is null)
        {
            selected = null; AssetTitle.Text = "没有匹配的壁纸"; AssetSource.Text = ""; ResolutionText.Text = ""; PreviewCaption.Text = "";
            SaveExtractedButton.IsEnabled = false; UpdateSelectionControls(); UpdateRecordControls(); if (changed) QueuePreview(); return;
        }
        selected = asset; settings.SelectedId = asset.Id; Save();
        AssetTitle.Text = asset.Title; AssetSource.Text = asset.Subtitle + (asset.Desktop.Length > 0 ? " · " + asset.Desktop : "");
        UpdateSelectionControls();
        SaveExtractedButton.IsEnabled = asset.Origin == "Wallpaper Engine" && asset.Usable && !savingOffline;
        UpdateRecordControls();
        if (changed) ResolutionText.Text = "读取原素材尺寸中…";
        UpdateFitCaption();
        if (changed) QueuePreview();
    }
    private void QueuePreview()
    {
        if (!initialized || closing || !expanded || changingClientTheme) return;
        previewCancellation?.Cancel(); previewDelay.Stop(); previewDelay.Start();
    }
    private async Task RefreshPreviewAsync()
    {
        if (!initialized || closing || !expanded || changingClientTheme || LibraryPage.Visibility != Visibility.Visible || !IsVisible || previewHost.HandleValue == nint.Zero) return;
        int generation = ++previewSequence; previewCancellation?.Cancel(); previewCancellation?.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); previewCancellation = cancellation;
        await previewGate.WaitAsync();
        try
        {
            if (generation != previewSequence || cancellation.IsCancellationRequested) return;
            var old = preview; preview = null; previewAssetId = ""; if (old is not null) await old.DisposeAsync();
            PreviewContainer.Visibility = Visibility.Hidden; PreviewPlaceholder.Visibility = Visibility.Visible;
            if (selected is null) { PreviewMessage.Text = "选择壁纸后查看实际画面。"; return; }
            var asset = selected;
            PreviewMessage.Text = asset.Usable ? asset.Kind is "scene" or "web" ? "正在从本地壁纸提取高清原素材，无需启动 Wallpaper Engine…" : "正在读取原素材…" : asset.Problem;
            if (!asset.Usable) return;
            ClientWindow? target = PreviewMode.SelectedIndex == 1 ? DesktopClient.Find() : null;
            if (target?.Minimized == true) target = null;
            if (PreviewMode.SelectedIndex == 1 && target is null) { PreviewMessage.Text = "恢复官方客户端窗口后可预览全局效果。原始壁纸预览仍可使用。"; return; }
            // A visible native host is required before creating its child; it never takes focus.
            var session = await RenderLease.StartAsync(asset, PreferenceSnapshot.Of(settings), target, "preview", previewHost.HandleValue, false, cancellation.Token);
            if (generation != previewSequence || cancellation.IsCancellationRequested) { await session.DisposeAsync(); return; }
            preview = session; previewAssetId = asset.Id; PreviewContainer.Visibility = Visibility.Visible; PreviewPlaceholder.Visibility = Visibility.Collapsed; UpdateResolution(); UpdateFitCaption();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PreviewContainer.Visibility = Visibility.Hidden; PreviewMessage.Text = ex.Message; ResolutionText.Text = "预览未就绪"; LocalStore.Log("preview", ex.Message); }
        finally { previewGate.Release(); }
    }
    private void UpdateResolution()
    {
        if(previewAssetId!=selected?.Id)return;
        var state = preview?.Session.ReadState(); if (state is null) return;
        if(state.State=="resizing"){ResolutionText.Text="客户端尺寸变化中，正在等待匹配的新画面…";return;}
        if(selected is { Kind: "builtin", Preset: 3 }){ResolutionText.Text=$"原创场景 {state.SourceWidth} × {state.SourceHeight} · 实时烛光、夜雾与光点";return;}
        if(selected?.Kind=="builtin"){ResolutionText.Text=$"内置实时图形 · 按预览窗口 {state.Width} × {state.Height} 生成";return;}
        ResolutionText.Text = $"原素材 {state.SourceWidth} × {state.SourceHeight}  ·  预览 {state.Width} × {state.Height}" +
            (state.Duration > 0 ? $"  ·  {state.Duration:0.#} 秒 · 静音循环" : selected?.Kind is "scene" or "web" ? "  ·  离线静态原图" : "");
    }
    private void Appearance_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        settings.Fit = FitBox.SelectedIndex switch { 1 => "glass-light", 2 => "contain", 3 => "cover", 4 => "stretch", _ => "glass-dark" }; settings.Dim = DimSlider.Value; settings.Surface = SurfaceSlider.Value;
        settings.GlassOpacity = GlassOpacitySlider.Value; settings.GlassBlur = GlassBlurSlider.Value;
        UpdateFitCaption(); UpdateGlassControls();
        Save(); QueuePreview();
        SetStatus("预览参数已更新，点击“应用全局皮肤”后用于客户端。");
    }
    private void UpdateFitCaption()
    {
        string source = (previewAssetId == selected?.Id ? preview?.Material?.Description : null) ?? (selected?.Kind == "scene" ? "离线场景原图，不含场景特效" : selected?.Kind == "web" ? "离线网页原素材，不运行网页脚本" : selected?.Detail.Length > 0 ? selected.Detail : "原始素材画面");
        source += "。";
        PreviewCaption.Text = source + (settings.Fit switch
        {
            "glass-light" => "完整等比；空余区域使用白色底毛玻璃。主体清晰、不裁切、不变形，适合浅色界面。",
            "contain" => "完整等比，空余区域使用深色纯底；不裁切、不变形。",
            "cover" => "等比铺满窗口；会裁掉部分边缘，主体比例不变。需要保留整张壁纸时选择完整等比。",
            "stretch" => "拉伸铺满窗口，物体比例可能改变。",
            _ => "完整等比；空余区域使用黑色底毛玻璃。主体清晰、不裁切、不变形，没有彩色泛光。"
        });
        if (selected is { Kind: "builtin", Preset: not 3 }) PreviewCaption.Text = "内置实时图形按当前窗口生成，没有需要补边的空余区域。";
    }
    private void UpdateGlassControls()
    {
        GlassControls.IsEnabled = settings.Fit is "glass-dark" or "glass-light";
        GlassOpacityLabel.Text = $"底色浓度  {settings.GlassOpacity:P0}";
        GlassBlurLabel.Text = $"模糊程度  {settings.GlassBlur:0}";
    }
    private void PreviewMode_Changed(object sender, SelectionChangedEventArgs e) => QueuePreview();
    private void PreviewAgain_Click(object sender, RoutedEventArgs e) => QueuePreview();
    private async void Expand_Click(object sender, RoutedEventArgs e)
    {
        expanded = !expanded; settings.PreviewExpanded = expanded; UpdateLibraryVisibility(); Save();
        if (expanded) { await Dispatcher.Yield(DispatcherPriority.Loaded); QueuePreview(); } else await StopPreviewAsync();
    }
    private void UpdateLibraryVisibility()
    {
        LibraryColumn.Width = expanded ? new GridLength(238) : new GridLength(1, GridUnitType.Star); GapColumn.Width = new GridLength(expanded ? 18 : 0);
        PreviewColumn.Width = expanded ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PreviewPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        AssetList.ItemsPanel = (ItemsPanelTemplate)FindResource(expanded ? "ListItems" : "GalleryItems");
        AssetList.ItemTemplate = expanded ? null : (DataTemplate)FindResource("GalleryTemplate");
        ScrollViewer.SetCanContentScroll(AssetList, expanded);
        ExpandButton.Content = expanded ? "收起预览 · 查看全部壁纸" : "展开预览";
    }
    private void UiTheme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        settings.UiTheme = UiThemeBox.SelectedIndex switch { 1 => "light", 2 => "halloween", _ => "dark" };
        UiTheme.Apply(this, settings.UiTheme); UpdateThemeButtons(); Save();
    }
    private async void Halloween_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(0); await StopIntroPreviewAsync(); SearchBox.Text = ""; FilterBox.SelectedIndex = 1;
        GroupFilterBox.SelectedIndex = 0; UpdateFilter("builtin-halloween");
        if (!expanded) Expand_Click(sender, e); else QueuePreview();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        previewOnly=false;
        if (selected is not null) await BeginApplyAsync(selected, false);
    }
    private async Task BeginApplyAsync(SkinAsset asset, bool animate)
    {
        if (applyTask is { IsCompleted: false } || closing) return;
        applyCancellation?.Dispose(); applyCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        monitorCancellation?.Cancel();
        var captured = PreferenceSnapshot.Of(settings); int version = ++activeVersion;
        await RunApplyOperationAsync(asset.Id, () => ApplyCoreAsync(asset, captured, animate, applyCancellation.Token, version));
    }
    internal async Task RunApplyOperationAsync(string assetId, Func<Task> operation)
    {
        if (applyTask is { IsCompleted: false } || closing) return;
        applyingAssetId = assetId;
        ApplyButton.IsEnabled = false; ApplyButton.Content = "正在应用…";
        SetStatus("正在读取壁纸原素材，其他预览和浏览功能可继续使用。");
        try { applyTask = operation(); await applyTask; }
        finally
        {
            // Release the completed operation before checking whether the user can apply again.
            // Checking from inside ApplyCoreAsync sees that same task as incomplete.
            applyTask = null; applyingAssetId = "";
            ApplyButton.Content = "应用全局皮肤"; UpdateSelectionControls();
        }
    }
    private async Task ApplyCoreAsync(SkinAsset asset, Preferences captured, bool animate, CancellationToken token, int version)
    {
        RenderLease? pending = null;
        try
        {
            var target = DesktopClient.Find() ?? throw new InvalidOperationException("请先从官方普通入口打开 ChatGPT 桌面端。");
            target = await DesktopClient.RestoreForApplyAsync(target, token);
            pending = await RenderLease.StartAsync(asset, captured, target, "render", nint.Zero, animate, token,
                progress: phase => SetStatus(phase + "，其他预览和浏览功能可继续使用。"));
            token.ThrowIfCancellationRequested();
            if (version != activeVersion) throw new OperationCanceledException();
            var old = active; active = pending; pending = null;
            var opening = transient; transient = null;
            paused = false; PauseButton.Content = "暂停皮肤"; appliedTitle = asset.Title;
            settings.AppliedId = asset.Id; settings.AutoApply = true; Save(); seenGeneration = target.Generation;
            var state = active.Session.ReadState();
            SetStatus(state?.State == "prepared" ? "全局画面已准备。客户端当前被其他窗口遮挡，切回后可见。" : "全局皮肤已呈现，可直接继续使用客户端。");
            UpdateApplied();
            if (old is not null) await old.DisposeAsync();
            if (opening is not null) await opening.DisposeAsync();
        }
        catch (OperationCanceledException) { if (!closing) SetStatus("已取消本次应用。"); }
        catch (Exception ex) { Report(ex); }
        finally
        {
            if (pending is not null) await pending.DisposeAsync();
        }
    }
    private async Task StopAsync(bool preserve = false)
    {
        int version = ++activeVersion;
        monitorCancellation?.Cancel();
        applyCancellation?.Cancel(); if (applyTask is { IsCompleted: false }) await applyTask;
        if (monitorTask is { IsCompleted: false }) await monitorTask;
        if (version != activeVersion) return;
        var old = active; active = null;
        var opening = transient; transient = null;
        paused = false; PauseButton.Content = "暂停皮肤";
        if (!preserve) { settings.AutoApply = false; Save(); }
        UpdateApplied(); if (!closing) SetStatus("已恢复客户端原样。客户端继续运行。");
        if (old is not null) await old.DisposeAsync();
        if (opening is not null) await opening.DisposeAsync();
    }
    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopAsync();
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (applyTask is { IsCompleted: false }) { SetStatus("应用完成后可暂停；也可点“恢复原样”取消本次应用。"); return; }
        if (paused)
        {
            var asset = library.Items.FirstOrDefault(a => a.Id == settings.AppliedId);
            if (asset is not null) await BeginApplyAsync(asset, false); else SetStatus("上次皮肤已不存在，请重新选择。");
        }
        else if (active is not null)
        {
            activeVersion++; var old = active; active = null; paused = true; PauseButton.Content = "继续皮肤";
            UpdateApplied(); SetStatus("皮肤已暂停，原客户端继续运行。");
            await old.DisposeAsync();
        }
    }
    private void UpdateApplied()
    {
        var state = active?.Session.ReadState();
        AppliedStatus.Text = paused ? "已暂停 · " + appliedTitle : active is null ? "尚未应用皮肤" : state?.State == "resizing" ? "窗口调整中 · 原界面保持可操作，尺寸稳定后恢复皮肤" : $"{appliedTitle} · 全局 {state?.Width} × {state?.Height} · 原素材 {state?.SourceWidth} × {state?.SourceHeight} · {active.Material?.Description}";
    }
    private void UpdateClient()
    {
        client = DesktopClient.Find();
        ClientStatus.Text = client is null ? "等待官方客户端" : client.Minimized ? "客户端已最小化" : $"客户端已识别 · {client.Width} × {client.Height}";
        ClientStatus.SetResourceReference(TextBlock.ForegroundProperty, client is null || client.Minimized ? "Muted" : "Accent");
        ConnectionDetail.Text = client is null ? "没有找到可见的官方客户端主窗口。从原来的官方入口打开即可；管理器不替换启动入口。" : client.Minimized ? "官方客户端正在运行，但窗口已最小化。恢复后即可直接设置皮肤。" : $"已验证官方安装身份，窗口 {client.Width} × {client.Height}。可以直接应用，无需关闭当前任务或重启客户端。";
        ConnectionDetail.Text += "\n本地壁纸读取与离线提取不需要启动 Wallpaper Engine。" + (library.Roots.Count == 0 ? "可添加下载好的项目目录或Steam库。" : $"已识别 {library.Roots.Count} 个本地壁纸目录。");
    }
    private void Monitor_Tick(object? sender, EventArgs e)
    {
        if (!closing && shutdown.WaitOne(0)) { Close(); return; }
        if (monitorBusy || closing || changingClientTheme) return;
        monitorTask = ObserveClientAsync();
    }
    private async Task ObserveClientAsync()
    {
        if (monitorBusy || closing) return; monitorBusy = true;
        try
        {
            UpdateClient(); UpdateResolution();
            if (active is not null && (!active.Session.Alive || active.Session.ReadState()?.State is "error" or "stopped"))
            {
                int version=activeVersion; var ended = active; var endedState=ended.Session.ReadState(); active = null; await ended.DisposeAsync();
                if(version==activeVersion){SetStatus(client is null ? "客户端窗口已关闭，所属皮肤后台已停止。" : !string.IsNullOrEmpty(endedState?.Error) ? NativeSession.FriendlyError(endedState.Error) : "皮肤后台已停止。可重新应用；客户端保留。"); UpdateApplied();}
            }
            if(preview is not null&&(!preview.Session.Alive||preview.Session.ReadState()?.State is "error" or "stopped"))
            {
                var ended=preview;var endedState=ended.Session.ReadState();preview=null;previewAssetId="";
                PreviewContainer.Visibility=Visibility.Hidden;PreviewPlaceholder.Visibility=Visibility.Visible;
                PreviewMessage.Text=!string.IsNullOrEmpty(endedState?.Error)?NativeSession.FriendlyError(endedState.Error):"预览已结束，点击重新预览可再次读取真实画面。";
                ResolutionText.Text="预览未就绪";await ended.DisposeAsync();
            }
            if (transient is not null && !transient.Session.Alive) { var ended = transient; transient = null; await ended.DisposeAsync(); }
            string generation = client is null ? "" : client.Generation;
            bool appeared = client is { Minimized: false } && generation != seenGeneration;
            if (client is null) seenGeneration = "";
            if (appeared)
            {
                seenGeneration = generation;
                if (!previewOnly && !paused && settings.AutoApply && settings.ResumeOnClient && autoAttempt != generation)
                {
                    autoAttempt = generation;
                    var asset = library.Items.FirstOrDefault(a => a.Id == settings.AppliedId);
                    if (asset is not null) await BeginApplyAsync(asset, !firstObserve);
                }
                else if (!previewOnly && !firstObserve && settings.IntroEnabled && active is null && !paused && applyTask is not { IsCompleted: false })
                {
                    try {
                        int version = activeVersion;
                        monitorCancellation?.Dispose(); monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        var next = await RenderLease.StartAsync(Library.Builtins()[0], PreferenceSnapshot.Of(settings), client, "render", nint.Zero, true, monitorCancellation.Token, true);
                        if(version==activeVersion&&!closing)transient=next;else await next.DisposeAsync();
                    }
                    catch (Exception ex) { LocalStore.Log("intro", ex.Message); }
                }
            }
            firstObserve = false;
            if (active is not null) UpdateApplied();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LocalStore.Log("monitor", ex.Message); }
        finally { monitorBusy = false; }
    }

    private void ShowPage(int page)
    {
        LibraryPage.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        IntroPage.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        var buttons = new[] { LibraryTab, IntroTab, SettingsTab };
        for (int i = 0; i < buttons.Length; i++) buttons[i].Style = (Style)FindResource(i == page ? "NavigationActive" : "Navigation");
    }
    private async void LibraryTab_Click(object sender, RoutedEventArgs e) { ShowPage(0); await StopIntroPreviewAsync(); QueuePreview(); }
    private async void IntroTab_Click(object sender, RoutedEventArgs e) { ShowPage(1); await StopPreviewAsync(); await PlayIntroPreviewAsync(); }
    private async void SettingsTab_Click(object sender, RoutedEventArgs e) { ShowPage(2); await StopPreviewAsync(); await StopIntroPreviewAsync(); UpdateClient(); }
    private async Task StopPreviewAsync()
    {
        previewDelay.Stop(); previewSequence++; previewCancellation?.Cancel();
        await previewGate.WaitAsync();
        try { var old = preview; preview = null; previewAssetId = ""; if (old is not null) await old.DisposeAsync(); }
        finally { previewGate.Release(); }
    }
    private async Task StopIntroPreviewAsync()
    {
        introSequence++; introCancellation?.Cancel();
        await introGate.WaitAsync();
        try { var old = introPreview; introPreview = null; if (old is not null) await old.DisposeAsync(); }
        finally { introGate.Release(); }
    }
    private async Task PlayIntroPreviewAsync()
    {
        if (closing || IntroPage.Visibility != Visibility.Visible) return;
        int generation = ++introSequence; introCancellation?.Cancel(); introCancellation?.Dispose();
        introCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var cancellation = introCancellation;
        var captured = PreferenceSnapshot.Of(settings); captured.IntroEnabled = true;
        await introGate.WaitAsync();
        try
        {
            if (generation != introSequence || cancellation.IsCancellationRequested) return;
            var old = introPreview; introPreview = null; if (old is not null) await old.DisposeAsync();
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            if (introHost.HandleValue == nint.Zero || cancellation.IsCancellationRequested) return;
            IntroPreviewContainer.Visibility=Visibility.Hidden;
            var next = await RenderLease.StartAsync(Library.Builtins()[0], captured, null, "preview", introHost.HandleValue, true, cancellation.Token);
            if (cancellation.IsCancellationRequested || IntroPage.Visibility != Visibility.Visible) { await next.DisposeAsync(); return; }
            introPreview = next; IntroPreviewContainer.Visibility=Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Report(ex); }
        finally { introGate.Release(); }
    }
    private async void IntroPreview_Click(object sender, RoutedEventArgs e) => await PlayIntroPreviewAsync();
    private void IntroSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        settings.IntroEnabled = IntroEnabledBox.IsChecked == true; settings.IntroSeconds = IntroDuration.Value; UpdateIntroLabels(); Save();
    }
    private void UpdateIntroLabels()
    {
        IntroDurationLabel.Text = settings.IntroSeconds.ToString("0.0") + " 秒";
        IntroFileLabel.Text = string.IsNullOrEmpty(settings.IntroPath) ? "内置极光过渡" : Path.GetFileName(settings.IntroPath) + " · 原始 MP4";
    }
    private void ChooseIntro_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择本地启动动画", Filter = "MP4 视频|*.mp4", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        settings.IntroPath = dialog.FileName; UpdateIntroLabels(); Save(); SetStatus("启动动画已保存，可点“播放预览”查看。");
    }
    private void ClearIntro_Click(object sender, RoutedEventArgs e) { settings.IntroPath = ""; UpdateIntroLabels(); Save(); }
    private void GeneralSetting_Changed(object sender, RoutedEventArgs e)
    { if (!initialized) return; settings.ResumeOnClient = ResumeBox.IsChecked == true; Save(); }
    private bool ReadAutoStart()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("ChatGptSkinStudio") is string value && value.Contains(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase); }
        catch (System.Security.SecurityException) { return false; }
    }
    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            string exe = Path.Combine(AppContext.BaseDirectory, "ChatGptSkinStudio.exe");
            if (AutoStartBox.IsChecked == true) key.SetValue("ChatGptSkinStudio", "\"" + exe + "\" --tray");
            else key.DeleteValue("ChatGptSkinStudio", false);
            SetStatus(AutoStartBox.IsChecked == true ? "已启用登录启动；启动后留在托盘。" : "已关闭登录启动。");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { initialized = false; AutoStartBox.IsChecked = ReadAutoStart(); initialized = true; Report(ex); }
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入原始图片或视频", Filter = "图片和视频|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.mp4;*.m4v;*.wmv|图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif|视频|*.mp4;*.m4v;*.wmv", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        ImportButton.IsEnabled = false;
        try
        {
            SkinAsset? last = null; int count = 0;
            foreach (string path in dialog.FileNames)
            {
                SetStatus($"正在保存原素材 {++count}/{dialog.FileNames.Length}，不压缩或转换…");
                last = await LocalStore.ImportAsync(path, lifetime.Token); settings.Imports.Add(last); Save();
            }
            if (last is not null) { settings.SelectedId = last.Id; selected = null; FilterBox.SelectedIndex = 0; SearchBox.Text = ""; UpdateGroups("*"); }
            await ScanAsync(); if (last is not null) UpdateFilter(last.Id);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Report(ex); }
        finally { ImportButton.IsEnabled = true; }
    }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var assets = AssetList.SelectedItems.Cast<SkinAsset>().ToList(); if (assets.Count == 0) return;
        bool InUse() => assets.Any(a => a.Id == applyingAssetId || a.Id == settings.AppliedId && (active is not null || paused));
        if (InUse()) { SetStatus("选中项里有正在应用或使用的皮肤，请先点击“恢复原样”，再移出这一张。其他壁纸可照常管理。"); return; }
        bool? deleteCopies = AskRemoval(assets.Count, assets.Count(a => a.IsLocalCopy)); if (deleteCopies is null) return;
        try {
            await StopPreviewAsync();
            if (InUse()) { SetStatus("选中素材已开始应用，请先恢复原样。"); QueuePreview(); return; }
            foreach (var item in assets)
            {
                if (deleteCopies == true && item.IsLocalCopy) { LocalStore.DeleteImport(item); settings.Imports.RemoveAll(a => a.Id == item.Id); settings.GroupAssignments.Remove(item.Id); }
                Catalog.Hide(settings, new[] { item.Id });
            }
            Save(); selected = null; UpdateHiddenCount(); await ScanAsync(); SetStatus($"已移出 {assets.Count} 张壁纸。自动扫描不会加回，外部原文件保留。");
        }
        catch (Exception ex) { Report(ex); }
    }
    private async void AddDirectory_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择壁纸项目、Steam 库或图片 / 视频目录", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(new FormsOwner(this)) != Forms.DialogResult.OK) return;
        string path = Path.GetFullPath(dialog.SelectedPath);
        if (!settings.Directories.Contains(path, StringComparer.OrdinalIgnoreCase)) settings.Directories.Add(path);
        Save(); DirectoryList.ItemsSource = settings.Directories.ToArray(); await ScanAsync();
    }
    private async void RemoveDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (DirectoryList.SelectedItem is not string path) return;
        settings.Directories.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)); Save(); DirectoryList.ItemsSource = settings.Directories.ToArray(); await ScanAsync();
    }
    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (selected is null || selected.Path.Length == 0) { SetStatus("这是内置程序生成的背景，没有外部素材文件。"); return; }
        try { string? directory = Path.GetDirectoryName(selected.Path); if (directory is not null && Directory.Exists(directory)) Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); }
        catch (Exception ex) { Report(ex); }
    }
    private void OpenData_Click(object sender, RoutedEventArgs e)
    { try { Directory.CreateDirectory(LocalStore.Root); Process.Start(new ProcessStartInfo(LocalStore.Root) { UseShellExecute = true }); } catch (Exception ex) { Report(ex); } }
    private void Help_Click(object sender, RoutedEventArgs e)
    {
        string file = Path.Combine(AppContext.BaseDirectory, "使用说明.txt");
        if (File.Exists(file)) { try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch (Exception ex) { Report(ex); } }
        else SetStatus("在壁纸库选中素材，预览后点击“应用全局皮肤”。当前设置页包含运行方式与显示限制。");
    }
    private void CheckConnection_Click(object sender, RoutedEventArgs e) { UpdateClient(); SetStatus(ConnectionDetail.Text); }
    private bool savingOffline;
    private Task? savingOfflineTask;
    private async void SaveExtracted_Click(object sender, RoutedEventArgs e)
    {
        if (savingOffline || selected is not { Origin: "Wallpaper Engine", Usable: true } asset) return;
        savingOffline = true; SaveExtractedButton.IsEnabled = false;
        savingOfflineTask = SaveExtractedCoreAsync(asset, lifetime.Token);
        try { await savingOfflineTask; }
        finally { savingOffline = false; if (!closing) SaveExtractedButton.IsEnabled = selected is { Origin: "Wallpaper Engine", Usable: true }; }
    }
    private async Task SaveExtractedCoreAsync(SkinAsset asset, CancellationToken token)
    {
        SkinAsset? imported = null;
        bool saved = false;
        SetStatus("正在保存原素材到本地库。其他壁纸仍可浏览；无需启动 Wallpaper Engine。");
        try
        {
            var source = await OfflineWallpaper.ResolveAsync(asset, token);
            imported = await LocalStore.ImportAsync(source.Path, token);
            token.ThrowIfCancellationRequested();
            imported = imported with { Title = asset.Title + (source.Kind == "image" ? " · 原图" : " · 视频"), Origin = "本地抓取", Detail = source.Description };
            settings.Imports.Add(imported);
            try { LocalStore.Save(settings); saved = true; }
            catch
            {
                settings.Imports.RemoveAll(i => i.Id == imported.Id);
                try { LocalStore.DeleteImport(imported); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { LocalStore.Log("save-copy-cleanup", cleanup.Message); }
                imported = null; throw;
            }
            await ScanAsync();
            SetStatus("已保存“" + asset.Title + "”到本地库。原项目移动或移除后，这份素材仍可使用。");
        }
        catch (OperationCanceledException) { if (!saved && imported is not null) { try { LocalStore.DeleteImport(imported); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { LocalStore.Log("save-copy-cancel", cleanup.Message); } } }
        catch (Exception ex) { Report(ex); }
    }
    private void UpdateRecordControls()
    {
        bool busy = recordingTask is { IsCompleted: false };
        bool eligible = selected is { Origin: "Wallpaper Engine", Kind: "scene" or "web" } && AssetList.SelectedItems.Count == 1;
        RecordPanel.Visibility = Visibility.Visible;
        RecordButton.IsEnabled = !busy && !closing && eligible;
        RecordButton.ToolTip = busy ? "正在生成，可浏览其他壁纸或取消本次任务。" : eligible ? "生成当前场景的静音 MP4，完成后保存在离线视频中。" : "选择一张 Wallpaper Engine 场景或网页后生成；已有视频可直接应用。";
        CancelRecordButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RecordProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RecordDurationBox.IsEnabled = RecordQualityBox.IsEnabled = !busy && eligible;
    }
    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (closing || recordingTask is { IsCompleted: false } || selected is not { Origin: "Wallpaper Engine", Kind: "scene" or "web" } asset) return;
        int seconds = RecordDurationBox.SelectedIndex switch { 0 => 10, 2 => 30, _ => 20 };
        var size = RecordQualityBox.SelectedIndex switch { 1 => (Width: 2560, Height: 1440), 2 => (Width: 3840, Height: 2160), _ => SceneRecording.DesktopSize() };
        settings.RecordSeconds = seconds; Save();
        recordingCancellation?.Dispose(); recordingCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        RecordProgress.Value = 0;
        recordingTask = RecordCoreAsync(asset, library.Engine, size.Width, size.Height, seconds, recordingCancellation.Token);
        UpdateRecordControls();
        try { await recordingTask; } finally { UpdateRecordControls(); }
    }
    private void CancelRecord_Click(object sender, RoutedEventArgs e)
    {
        recordingCancellation?.Cancel(); SetStatus("正在取消生成并回收本次独立窗口，已应用皮肤保留。");
    }
    private async Task RecordCoreAsync(SkinAsset asset, string engine, int width, int height, int seconds, CancellationToken token)
    {
        SkinAsset? result = null; bool saved = false;
        SetStatus("正在准备一次性场景渲染。其他壁纸可以继续浏览；已应用皮肤保留。");
        var progress = new Progress<double>(value => { RecordProgress.Value = value; if (!closing && !token.IsCancellationRequested) SetStatus($"正在生成“{asset.Title}” · {value:0}% · {width} × {height}，完成后离线播放。"); });
        try
        {
            result = await SceneRecording.CreateAsync(asset, engine, width, height, seconds, progress, token);
            token.ThrowIfCancellationRequested(); settings.Imports.Add(result);
            try { LocalStore.Save(settings); saved = true; }
            catch { settings.Imports.RemoveAll(i => i.Id == result.Id); throw; }
            selected = null; settings.SelectedId = result.Id; SearchBox.Text = ""; FilterBox.SelectedIndex = 0; UpdateGroups("*");
            await ScanAsync(); UpdateFilter(result.Id);
            if (AssetList.SelectedItem is SkinAsset added && added.Id == result.Id) AssetList.ScrollIntoView(added);
            SetStatus("动态副本已保存，完整壁纸库保留显示。以后可从“离线视频”直接找到副本；点击应用才切换皮肤。");
        }
        catch (OperationCanceledException) { if (!closing) SetStatus("已取消生成，原有皮肤保留。"); }
        catch (Exception ex) { Report(ex); }
        finally
        {
            if (!saved && result is not null) { try { LocalStore.DeleteImport(result); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LocalStore.Log("record-copy-cleanup", ex.Message); } }
        }
    }
    private void Save() { try { LocalStore.Save(settings); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus("本地设置暂时无法保存：" + ex.Message); } }
    private void SetStatus(string text) => OperationStatus.Text = text;
    private void Report(Exception ex) { LocalStore.Log("manager", ex.ToString()); SetStatus(ex.Message); }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return; e.Cancel = true; if (closing) return;
        closing = true; SetStatus("正在结束所属背景与本地读取任务…"); monitor.Stop(); previewDelay.Stop(); libraryTimer.Stop(); lifetime.Cancel();
        try { await StopAsync(true); await StopPreviewAsync(); await StopIntroPreviewAsync(); if (clientThemeTask is not null) { try { await clientThemeTask; } catch (Exception ex) { LocalStore.Log("theme-exit", ex.Message); } } if (scanWork is not null) { try { await scanWork; } catch (OperationCanceledException) { } } if (savingOfflineTask is not null) await savingOfflineTask; if (recordingTask is not null) await recordingTask; }
        finally { recordingCancellation?.Dispose(); tray?.Dispose(); tray = null; trayIcon?.Dispose(); trayIcon = null; shutdown.Dispose(); lifetime.Dispose(); allowClose = true; _ = Dispatcher.BeginInvoke(new Action(Close)); }
    }
    private sealed class FormsOwner : Forms.IWin32Window
    {
        public nint Handle { get; }
        public FormsOwner(Window window) { Handle = new System.Windows.Interop.WindowInteropHelper(window).Handle; }
    }
}
