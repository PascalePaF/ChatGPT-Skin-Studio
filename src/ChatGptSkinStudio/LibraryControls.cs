using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ChatGptSkinStudio;

public partial class MainWindow
{
    private void AllWallpapers_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = ""; UpdateGroups("*"); FilterBox.SelectedIndex = 0; UpdateFilter();
        SetStatus($"已清除筛选，显示完整壁纸库：{library.Items.Count} 张。");
    }
    private void OfflineVideos_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(0); SearchBox.Text = ""; UpdateGroups("*"); FilterBox.SelectedIndex = 6; UpdateFilter();
        int count = library.Items.Count(a => a.IsLocalCopy && a.Kind == "video");
        SetStatus(count > 0 ? $"已显示 {count} 段离线视频。双击可预览，点击“应用全局皮肤”后使用。" : "本地库中还没有视频。可导入 MP4，或选择 Wallpaper 场景生成离线副本。");
    }
    private async void ClientTheme_Click(object sender, RoutedEventArgs e)
    {
        if (changingClientTheme || closing || applyTask is { IsCompleted: false }) return;
        var target = DesktopClient.Find();
        if (target is null || target.Minimized) { ClientThemeStatus.Text = "请先恢复官方客户端主窗口，再切换主题。"; return; }
        bool restore = active is not null, wasPaused = paused;
        string theme = ClientThemeBox.SelectedIndex == 0 ? "light" : "dark";
        changingClientTheme = true; ClientThemeButton.IsEnabled = false; UpdateSelectionControls();
        ClientThemeStatus.Text = "正在通过官方外观设置切换…";
        try
        {
            await StopAsync(true); await StopPreviewAsync(); await StopIntroPreviewAsync();
            clientThemeTask = ClientAppearance.SetAsync(target, theme, lifetime.Token); await clientThemeTask;
            settings.ClientTheme = theme; Save();
            ClientThemeStatus.Text = "已确认客户端切换为" + (theme == "light" ? "日间浅色" : "夜间深色") + "，无需重启。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ClientThemeStatus.Text = ex.Message; LocalStore.Log("client-theme", ex.ToString()); }
        finally
        {
            changingClientTheme = false; ClientThemeButton.IsEnabled = true;
            if (!closing && restore && library.Items.FirstOrDefault(a => a.Id == settings.AppliedId) is SkinAsset applied) await BeginApplyAsync(applied, false);
            else if (wasPaused && !closing) { paused = true; PauseButton.Content = "继续皮肤"; UpdateApplied(); }
            UpdateSelectionControls(); QueuePreview();
        }
    }
    private void UpdateSelectionControls()
    {
        int count = AssetList.SelectedItems.Count;
        SelectionText.Text = $"选中 {count} 张";
        ApplyButton.IsEnabled = count == 1 && selected?.Usable == true && applyTask is not { IsCompleted: false } && !closing && !changingClientTheme;
        ApplyButton.ToolTip = count > 1 ? "选择一张壁纸后应用；多选可批量移动和移出库。" : "只设置 ChatGPT 全局皮肤，不更改 Windows 桌面";
        RemoveButton.IsEnabled = MoveButton.IsEnabled = count > 0 && !closing;
    }
    private void UpdateGroups(string? filterId = null)
    {
        filterId ??= (GroupFilterBox.SelectedItem as WallpaperGroup)?.Id ?? "*";
        string moveId = (MoveGroupBox.SelectedItem as WallpaperGroup)?.Id ?? "";
        updatingGroups = true;
        try
        {
            var filters = new[] { new WallpaperGroup("*", "全部分组"), new WallpaperGroup("", "未分组") }.Concat(settings.Groups).ToList();
            GroupFilterBox.ItemsSource = filters; GroupFilterBox.SelectedItem = filters.FirstOrDefault(g => g.Id == filterId) ?? filters[0];
            var destinations = new[] { new WallpaperGroup("", "未分组") }.Concat(settings.Groups).ToList();
            MoveGroupBox.ItemsSource = destinations; MoveGroupBox.SelectedItem = destinations.FirstOrDefault(g => g.Id == moveId) ?? destinations[0];
        }
        finally { updatingGroups = false; }
        UpdateGroupButtons();
    }
    private void UpdateGroupButtons()
    {
        bool custom = GroupFilterBox.SelectedItem is WallpaperGroup { Id.Length: > 0 } group && group.Id != "*";
        RenameGroupButton.IsEnabled = DeleteGroupButton.IsEnabled = custom;
    }
    private void GroupFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || updatingGroups) return; UpdateGroupButtons(); UpdateFilter();
    }
    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        string? name = AskName("新建分组", "给这一组风景起个名字", ""); if (name is null) return;
        try { var group = Catalog.Create(settings, name); Save(); UpdateGroups(group.Id); UpdateFilter(); SetStatus("已创建分组“" + group.Name + "”。在全部壁纸中选中素材后，可移动到这个分组。"); }
        catch (Exception ex) { Report(ex); }
    }
    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if (GroupFilterBox.SelectedItem is not WallpaperGroup group || group.Id is "" or "*") return;
        string? name = AskName("重命名分组", "分组里的壁纸会保留", group.Name); if (name is null) return;
        try { Catalog.Rename(settings, group.Id, name); Save(); UpdateGroups(group.Id); SetStatus("分组名称已更新。"); }
        catch (Exception ex) { Report(ex); }
    }
    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (GroupFilterBox.SelectedItem is not WallpaperGroup group || group.Id is "" or "*") return;
        if (MessageBox.Show(this, "删除分组“" + group.Name + "”？\n里面的壁纸会回到“未分组”，原文件与已应用的皮肤保留。", "删除分组", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Catalog.DeleteGroup(settings, group.Id); Save(); UpdateGroups(""); UpdateFilter(); SetStatus("分组已删除，壁纸已移到“未分组”。");
    }
    private void Move_Click(object sender, RoutedEventArgs e)
    {
        var assets = AssetList.SelectedItems.Cast<SkinAsset>().ToList(); if (assets.Count == 0 || MoveGroupBox.SelectedItem is not WallpaperGroup group) return;
        try { Catalog.Move(settings, assets.Select(a => a.Id), group.Id); Save(); UpdateGroups(); UpdateFilter(); SetStatus($"已将 {assets.Count} 张壁纸移到“{group.Name}”。已应用皮肤保留。"); }
        catch (Exception ex) { Report(ex); }
    }
    private async void Asset_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (selected is null || expanded || ItemsControl.ContainerFromElement(AssetList, e.OriginalSource as DependencyObject) is null) return;
        expanded = true; settings.PreviewExpanded = true; UpdateLibraryVisibility(); Save(); await Dispatcher.Yield(DispatcherPriority.Loaded); QueuePreview();
    }
    private void DayTheme_Click(object sender, RoutedEventArgs e) => UiThemeBox.SelectedIndex = 1;
    private void NightTheme_Click(object sender, RoutedEventArgs e) => UiThemeBox.SelectedIndex = 0;
    private void UpdateThemeButtons()
    {
        DayThemeButton.Style = (Style)FindResource(settings.UiTheme == "light" ? "Primary" : typeof(Button));
        NightThemeButton.Style = (Style)FindResource(settings.UiTheme == "dark" ? "Primary" : typeof(Button));
    }
    private void AutoScan_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized) return; settings.AutoScan = AutoScanBox.IsChecked == true; Save();
        SetStatus(settings.AutoScan ? "自动发现已开启，每 30 秒后台扫描一次。" : "自动发现已关闭，仍可点击“立即扫描”。");
    }
    private void UpdateHiddenCount()
    {
        HiddenCountText.Text = settings.HiddenIds.Count + " 张已移出"; RestoreHiddenButton.IsEnabled = settings.HiddenIds.Count > 0;
    }
    private async void RestoreHidden_Click(object sender, RoutedEventArgs e)
    {
        int count = settings.HiddenIds.Count;
        if (count == 0 || MessageBox.Show(this, $"恢复 {count} 张已移出的壁纸到库中？\n已删除的管理器副本无法恢复，外部壁纸会按当前目录重新发现。", "恢复壁纸", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        settings.HiddenIds.Clear(); Save(); UpdateHiddenCount(); await ScanAsync();
    }
    private string? AskName(string title, string detail, string initial)
    {
        var dialog = MakeDialog(title, 420);
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = detail, Margin = new Thickness(0, 0, 0, 16), TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Text = initial, MaxLength = 60, Style = (Style)FindResource(typeof(TextBox)) }; panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Style = (Style)FindResource(typeof(Button)) };
        var save = new Button { Content = "保存", IsDefault = true, Style = (Style)FindResource("Primary"), Margin = new Thickness() };
        save.Click += (_, _) => { if (string.IsNullOrWhiteSpace(input.Text)) { input.Focus(); return; } dialog.DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons); dialog.Content = new Border { Padding = new Thickness(24), Child = panel };
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }
    private Window MakeDialog(string title, double width)
    {
        var dialog = new Window { Title = title, Owner = this, Width = width, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = FontFamily, FontSize = 13 };
        dialog.Resources.MergedDictionaries.Add(Resources); dialog.SetResourceReference(BackgroundProperty, "Canvas"); dialog.SetResourceReference(ForegroundProperty, "Ink");
        dialog.SourceInitialized += (_, _) => UiTheme.Apply(dialog, settings.UiTheme); return dialog;
    }
    private bool? AskRemoval(int count, int copies)
    {
        var dialog = MakeDialog("移出壁纸库", 460);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = $"将选中的 {count} 张壁纸移出库？", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Wallpaper Engine 与手动目录中的原文件会保留。移出后自动扫描不会重新添加，可在设置中恢复。", TextWrapping = TextWrapping.Wrap, LineHeight = 23 });
        var delete = new CheckBox { Content = $"同时删除管理器保存的 {copies} 份本地副本（无法恢复）", IsEnabled = copies > 0, Margin = new Thickness(0, 18, 0, 0) }; panel.Children.Add(delete);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Style = (Style)FindResource(typeof(Button)) });
        var confirm = new Button { Content = "移出壁纸库", IsDefault = true, Style = (Style)FindResource("Primary"), Margin = new Thickness() }; confirm.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(confirm); panel.Children.Add(buttons); dialog.Content = new Border { Padding = new Thickness(24), Child = panel };
        return dialog.ShowDialog() == true ? delete.IsChecked == true : null;
    }
}
