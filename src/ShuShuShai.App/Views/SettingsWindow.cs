using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShuShuShai.App.Services;
using ShuShuShai.Core;

namespace ShuShuShai.App.Views;

public sealed class SettingsWindow : Window
{
    private readonly TaskStore store;
    private readonly Shortcuts shortcuts;
    private readonly Dictionary<ShortcutAction, ShortcutGesture?> draft;
    private readonly Dictionary<ShortcutAction, Button> bindingButtons = [];
    private readonly TextBlock message = DialogUi.Text("");
    private readonly ContentControl content = new();
    private readonly Button save;
    private ShortcutAction? recording;
    private bool saving, clearing, managingHistory, uninstalling;
    internal bool HistoryChanged { get; private set; }

    public SettingsWindow(TaskStore store, Shortcuts shortcuts, Func<bool> canClear)
        : this(store, shortcuts, canClear, null, null) { }

    internal SettingsWindow(TaskStore store, Shortcuts shortcuts, Func<bool> canClear,
        UpdateService? updates, Func<DownloadedUpdate, Task<string?>>? install,
        Func<Task<string?>>? uninstall = null)
    {
        this.store = store;
        this.shortcuts = shortcuts;
        draft = shortcuts.CreateDraft();
        DialogUi.Setup(this, "舒舒筛 · 设置", 780, 740);
        var keyboard = CreateKeyboardPage();
        var guide = TextPage("使用说明",
            "添加主目录，逐层勾选后扫描。左侧可切换全部来源或指定来源。\n\n"
            + "点击选中一张，Ctrl 点击增减选中项，Shift 点击连续选择。普通点击胶片条会打开大图。大图和总览对选中项批量评分，对比默认只操作活动图。\n\n"
            + "先选中 2–9 张再进入对比。两张时可固定参考图并用翻片键轮换候选。拖矮底部工具栏可切换成单行，放不下的工具可向右滚动查看。\n\n"
            + "全屏右下角显示当前照片状态。按 Esc 退出。100% 查看 JPG 的原始像素。当前键位见「快捷键」。\n\n"
            + "导出范围按星级或标记勾选。移动时每组文件全部复制、校验并提交后，才处理源文件。");
        var about = TextPage("关于我们", $"舒舒筛 · v{App.VersionLabel} 预览版\n\n中文离线 Windows 选片工具\n"
            + "用于浏览 JPG、配对 RAW、评定星级和整理原文件。");
        var updater = updates != null && install != null ? new UpdatePanel(updates, install) : null;
        if (updater != null)
        {
            about.Children.Add(updater);
            updater.HandedOff += () => DialogResult = true;
        }
        var aboutPage = new ScrollViewer { Content = about, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var cache = TextPage("清理预览缓存", "清除可重新生成的 JPG 预览和直方图缓存。照片及任务记录继续保存在原处。");
        var cacheResult = DialogUi.Text("");
        var clear = DialogUi.Button("清理预览缓存", async (sender, _) =>
        {
            if (!canClear()) { cacheResult.Text = "当前有操作正在运行，完成后可以清理缓存。"; return; }
            var button = (Button)sender;
            clearing = true;
            button.IsEnabled = false;
            cacheResult.Text = "正在清理…";
            try
            {
                await Task.Run(ImageService.Shared.ClearCache);
                cacheResult.Text = "预览缓存已清理。需要时会重新生成预览。";
            }
            catch (Exception ex) { cacheResult.Text = "缓存清理未完成。" + ex.Message; }
            finally { clearing = false; button.IsEnabled = true; }
        }, "清除本机可重新生成的图片预览缓存。");
        clear.HorizontalAlignment = HorizontalAlignment.Left;
        cache.Children.Add(clear);
        cache.Children.Add(cacheResult);
        var history = TextPage("已移除的历史记录", "恢复后会重新显示在首页，评分、取舍和配对记录会一起恢复。");
        var historyRows = new StackPanel();
        var historyStatus = DialogUi.Text("");
        history.Children.Add(historyStatus);
        history.Children.Add(historyRows);
        var historyPage = new ScrollViewer { Content = history, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var uninstallPage = TextPage("卸载舒舒筛",
            "卸载移除程序及其快捷方式。照片、已导出文件、选片历史和设置会保留，重新安装后仍可使用。");
        var uninstallStatus = DialogUi.Text("");
        if (uninstall != null)
        {
            var removeApp = DialogUi.Button("卸载舒舒筛", async (sender, _) =>
            {
                if (uninstalling || updater?.Working == true || saving || clearing || managingHistory) return;
                if (MessageBox.Show(this, "卸载舒舒筛并移除快捷方式？照片、选片记录、设置和导出文件会保留。",
                    "确认卸载", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
                    return;
                uninstalling = true;
                IsEnabled = false;
                uninstallStatus.Text = "正在保存进度并启动卸载程序…";
                bool started = false;
                try
                {
                    var failure = await uninstall();
                    uninstallStatus.Text = failure ?? "";
                    started = failure == null;
                }
                catch (Exception ex) { uninstallStatus.Text = "卸载启动失败。" + ex.Message; }
                finally { uninstalling = false; IsEnabled = true; }
                if (started) DialogResult = true;
            }, "确认后卸载当前用户安装的程序，保留选片数据。");
            removeApp.HorizontalAlignment = HorizontalAlignment.Left;
            uninstallPage.Children.Add(removeApp);
            uninstallPage.Children.Add(uninstallStatus);
        }

        var root = new DockPanel { Margin = new Thickness(16) };
        var keyboardFooter = new StackPanel();
        void ShowPage(UIElement page)
        {
            StopRecording();
            content.Content = page;
            keyboardFooter.Visibility = page == keyboard ? Visibility.Visible : Visibility.Collapsed;
        }
        var navigation = new WrapPanel();
        Button[] navigationButtons =
        [
            DialogUi.Button("快捷键", (_, _) => ShowPage(keyboard), "查看或更改选片快捷键。"),
            DialogUi.Button("使用说明", (_, _) => ShowPage(guide), "查看导入、选片和导出的简明说明。"),
            DialogUi.Button("关于我们", (_, _) => ShowPage(aboutPage), "查看当前程序版本及更新入口。"),
            DialogUi.Button("清理预览缓存", (_, _) => ShowPage(cache), "查看预览缓存的用途并清理。"),
            DialogUi.Button("已移除的历史记录", async (_, _) =>
            {
                ShowPage(historyPage);
                await RefreshHistoryAsync();
            }, "恢复或永久删除移出首页的任务。")
        ];
        foreach (var item in navigationButtons) navigation.Children.Add(item);
        if (uninstall != null)
            navigation.Children.Add(DialogUi.Button("卸载", (_, _) => ShowPage(uninstallPage), "查看卸载范围并卸载程序。"));
        DockPanel.SetDock(navigation, Dock.Top);
        root.Children.Add(navigation);

        save = DialogUi.Button("保存快捷键", async (_, _) => await SaveAsync(), "保存并立即启用新的快捷键。");
        save.SetResourceReference(StyleProperty, "Primary");
        keyboardFooter.Children.Add(message);
        keyboardFooter.Children.Add(DialogUi.Row(save,
            DialogUi.Button("恢复默认键位", (_, _) => { StopRecording(); draft.Clear(); UpdateBindings(); }, "恢复草稿的全部默认键位，保存后生效。")));
        var footer = new StackPanel();
        footer.Children.Add(keyboardFooter);
        var close = DialogUi.Button("关闭", (_, _) => Close(), "关闭设置窗口。");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(content);
        ShowPage(keyboard);
        Content = root;
        PreviewKeyDown += RecordKey;
        Closing += (_, e) =>
        {
            e.Cancel = saving || clearing || managingHistory || uninstalling || updater?.Working == true;
            if (updater?.Working == true) updater.CancelOperation();
        };
        Closed += (_, _) => updater?.Release();
        UpdateBindings();

        async Task RefreshHistoryAsync()
        {
            if (managingHistory) return;
            managingHistory = true;
            historyStatus.Text = "正在读取历史记录…";
            try { RenderHistory(await Task.Run(() => store.LoadAll(archived: true))); }
            catch (Exception ex) { historyStatus.Text = "读取历史记录失败。" + ex.Message; }
            finally { managingHistory = false; }
        }

        void RenderHistory(IReadOnlyList<PhotoTask> tasks)
        {
            historyRows.Children.Clear();
            historyStatus.Text = tasks.Count == 0 ? "还没有移除的历史记录。" : "";
            foreach (var task in tasks)
            {
                var row = TextPage(task.SourceTitle, task.HistoryText);
                row.Children.Add(DialogUi.Row(
                    DialogUi.Button("恢复到首页", async (_, _) => await ChangeHistoryAsync(task, delete: false), "让这条任务重新显示在首页。"),
                    DialogUi.Button("永久删除", async (_, _) => await ChangeHistoryAsync(task, delete: true), "确认后删除任务进度，保留照片。")));
                historyRows.Children.Add(row);
            }
        }

        async Task ChangeHistoryAsync(PhotoTask task, bool delete)
        {
            if (managingHistory) return;
            if (delete && new HistoryRemovalWindow(this, task, canArchive: false).ShowDialog() != true) return;
            managingHistory = true;
            historyRows.IsEnabled = false;
            try
            {
                await Task.Run(() =>
                {
                    if (delete) store.DeleteTask(task.Id);
                    else store.SetArchived(task.Id, false);
                });
                HistoryChanged = true;
                RenderHistory(await Task.Run(() => store.LoadAll(archived: true)));
                historyStatus.Text = delete ? "已永久删除选片记录。" : "已恢复到首页。";
            }
            catch (Exception ex) { historyStatus.Text = "未能处理历史记录。" + ex.Message; }
            finally { managingHistory = false; historyRows.IsEnabled = true; }
        }
    }

    private ScrollViewer CreateKeyboardPage()
    {
        var rows = TextPage("快捷键", "点击键位再按新组合。清除或恢复默认后，点击「保存快捷键」生效。Esc 可取消录入，也始终用于退出全屏。");
        foreach (var definition in Shortcuts.Definitions)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new() { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new());
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var label = DialogUi.Text(definition.Name);
            row.Children.Add(label);
            var binding = DialogUi.Button("", (_, _) => StartRecording(definition.Action), $"点击后按键，录入「{definition.Name}」的新组合。");
            binding.Tag = definition.Action;
            binding.LostKeyboardFocus += (_, _) =>
            {
                if (recording == definition.Action) StopRecording();
            };
            Grid.SetColumn(binding, 1);
            row.Children.Add(binding);
            bindingButtons.Add(definition.Action, binding);
            var clear = DialogUi.Button("清除", (_, _) =>
            {
                StopRecording();
                draft[definition.Action] = null;
                UpdateBindings();
            }, $"清除「{definition.Name}」的快捷键。");
            Grid.SetColumn(clear, 2);
            row.Children.Add(clear);
            rows.Children.Add(row);
        }
        return new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static StackPanel TextPage(string title, string text)
    {
        var page = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        page.Children.Add(DialogUi.Text(title, 19));
        page.Children.Add(DialogUi.Text(text));
        return page;
    }

    private void StartRecording(ShortcutAction action)
    {
        StopRecording();
        recording = action;
        bindingButtons[action].Content = "请按键…";
        bindingButtons[action].Focus();
    }

    private void StopRecording()
    {
        if (recording == null) return;
        recording = null;
        UpdateBindings();
    }

    private void RecordKey(object sender, KeyEventArgs e)
    {
        if (recording is not { } action) return;
        e.Handled = true;
        var gesture = ShortcutGesture.From(e);
        if (gesture.Key == Key.Escape) { StopRecording(); return; }
        if (gesture.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt) return;
        if (!gesture.IsAssignable)
        {
            message.Text = "这个键用于系统或界面操作，请换一个组合。";
            return;
        }
        draft[action] = gesture;
        StopRecording();
    }

    private void UpdateBindings()
    {
        foreach (var definition in Shortcuts.Definitions)
            bindingButtons[definition.Action].Content = Shortcuts.BindingText(definition, draft);
        var error = Shortcuts.ValidationError(draft);
        message.Text = error ?? "";
        save.IsEnabled = error == null && !saving;
    }

    private async Task SaveAsync()
    {
        if (saving || Shortcuts.ValidationError(draft) != null) return;
        StopRecording();
        saving = true;
        IsEnabled = false;
        try
        {
            await shortcuts.SaveAsync(store, draft);
            message.Text = "快捷键已保存。";
        }
        catch (Exception ex) { message.Text = "快捷键保存失败。" + ex.Message; }
        finally { saving = false; IsEnabled = true; }
    }
}
