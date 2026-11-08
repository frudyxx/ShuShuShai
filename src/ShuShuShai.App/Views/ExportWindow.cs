using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using ShuShuShai.Core;

namespace ShuShuShai.App.Views;

public class ExportWindow : Window
{
    private readonly PhotoTask task;
    private readonly TaskStore store;
    private readonly ExportOptions options;
    private readonly StackPanel form = new(), starPanel = new(), decisionPanel = new(), linkedPanel = new(), independentPanel = new();
    private readonly ListBox preview = new() { FontSize = 11 };
    private readonly ListBox recoveries = new() { DisplayMemberPath = nameof(ExportPlan.Summary), MaxHeight = 108 };
    private readonly Expander recoverySection = new() { Header = "未完成的导出", ToolTip = "展开中断记录，可查看原计划并核验后继续。" };
    private readonly TextBlock summary = DialogUi.Text(""), result = DialogUi.Text("");
    private readonly TextBlock warning = DialogUi.Text("独立命名后，JPG 与 RAW 可能不同名；重新导入时可能无法按同名自动识别为一组。", 12);
    private readonly ProgressBar progress = new() { Height = 7, Margin = new Thickness(4, 6, 4, 6), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer previewDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly SemaphoreSlim previewLock = new(1);
    private readonly Button run, cancelButton, leaveRecovery;
    private ExportPlan? plan;
    private CancellationTokenSource? cancel;
    private bool running, recovering, closed;
    private int generation;

    public ExportOptions Options => options;
    public ExportPlan? CurrentPlan => plan;
    public event Action? ExecutionStarting;

    public ExportWindow(PhotoTask task, TaskStore store)
    {
        this.task = task;
        this.store = store;
        options = task.ExportOptions;
        options.Move = false;
        options.NamingMode ??= options.Linked
            ? options.Shared.Rename ? ExportNamingMode.Linked : ExportNamingMode.Original
            : options.Jpg.Rename || options.Raw.Rename ? ExportNamingMode.Independent : ExportNamingMode.Original;
        if (string.IsNullOrEmpty(options.Parent)) options.Parent = task.Sources.FirstOrDefault()?.Path ?? "";
        options.FolderName = $"舒舒筛_{DateTime.Now:yyyyMMdd_HHmmss}";
        DialogUi.Setup(this, "筛选完毕 · 导出照片", 1150, 940);

        var grid = new Grid { Margin = new Thickness(18) };
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            grid.RowDefinitions.Add(new() { Height = height });

        form.Children.Add(DialogUi.Row(
            Radio("按星级", "Range", options.RangeMode == ExportRangeMode.ByStars, () => SetRange(ExportRangeMode.ByStars)),
            Radio("按标记", "Range", options.RangeMode == ExportRangeMode.ByDecision, () => SetRange(ExportRangeMode.ByDecision))));
        var stars = new WrapPanel();
        var levels = ExportPlanner.SelectedStarLevels(options);
        var starCounts = task.Photos.ToLookup(p => p.Stars);
        foreach (int level in Enumerable.Range(0, 6))
        {
            string label = level == 0 ? "未评分" : $"{level} 星";
            stars.Children.Add(Category(label, starCounts[level].Count(), levels.Contains(level), selected =>
            {
                var next = ExportPlanner.SelectedStarLevels(options).Where(value => value != level).ToList();
                if (selected) next.Add(level);
                options.StarLevels = next;
            }));
        }
        starPanel.Children.Add(stars);
        form.Children.Add(starPanel);

        var marks = new WrapPanel();
        var decisionCounts = task.Photos.ToLookup(p => p.Decision);
        foreach (var (label, value) in new[] { ("保留", Decision.Keep), ("未定", Decision.Undecided), ("淘汰", Decision.Reject) })
            marks.Children.Add(Category(label, decisionCounts[value].Count(), options.Decisions.Contains(value), selected =>
            {
                if (selected && !options.Decisions.Contains(value)) options.Decisions.Add(value);
                else if (!selected) options.Decisions.Remove(value);
            }));
        decisionPanel.Children.Add(marks);
        form.Children.Add(decisionPanel);

        var parent = DialogUi.Edit(options, nameof(options.Parent));
        parent.ClearValue(WidthProperty);
        var browse = DialogUi.Button("浏览…", (_, _) =>
        {
            var picker = new OpenFolderDialog();
            if (picker.ShowDialog(this) != true) return;
            options.Parent = picker.FolderName;
            parent.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            InvalidatePlan();
        }, "选择存放导出结果文件夹的位置。");
        var location = new DockPanel();
        var parentLabel = DialogUi.Text("输出父目录");
        DockPanel.SetDock(parentLabel, Dock.Left);
        DockPanel.SetDock(browse, Dock.Right);
        location.Children.Add(parentLabel);
        location.Children.Add(browse);
        location.Children.Add(parent);
        form.Children.Add(location);
        form.Children.Add(DialogUi.Row(DialogUi.Text("结果目录名"), DialogUi.Edit(options, nameof(options.FolderName), 370)));
        form.Children.Add(DialogUi.Row(DialogUi.Text("操作"),
            Radio("复制 · 保留原片", "Operation", true, () => { options.Move = false; InvalidatePlan(); }),
            Radio("移动 · 校验后移除源文件", "Operation", false, () => { options.Move = true; InvalidatePlan(); })));
        form.Children.Add(DialogUi.Row(DialogUi.Text("目录布局"),
            Radio("分放", "Layout", options.Separate, () => { options.Separate = true; InvalidatePlan(); }),
            Radio("混放", "Layout", !options.Separate, () => { options.Separate = false; InvalidatePlan(); })));
        form.Children.Add(DialogUi.Text("分放：JPG、RAW 各用一个子文件夹。混放：两类文件放在同一个结果文件夹。", 11));
        form.Children.Add(DialogUi.Row(DialogUi.Text("命名方式"),
            Radio("保留原名", "Naming", options.NamingMode == ExportNamingMode.Original, () => SetNaming(ExportNamingMode.Original)),
            Radio("联动命名", "Naming", options.NamingMode == ExportNamingMode.Linked, () => SetNaming(ExportNamingMode.Linked)),
            Radio("独立命名", "Naming", options.NamingMode == ExportNamingMode.Independent, () => SetNaming(ExportNamingMode.Independent))));
        linkedPanel.Children.Add(DialogUi.Row(DialogUi.Text("共同名称"), DialogUi.Edit(options.Shared, nameof(NamingRule.Prefix), 230),
            DialogUi.Text("起始编号"), DialogUi.Edit(options.Shared, nameof(NamingRule.Start), 100)));
        linkedPanel.Children.Add(DialogUi.Text("一组 JPG / RAW 共用一个编号，原扩展名保留。", 11));
        form.Children.Add(linkedPanel);
        independentPanel.Children.Add(Independent("JPG", options.Jpg));
        independentPanel.Children.Add(Independent("RAW", options.Raw));
        independentPanel.Children.Add(warning);
        form.Children.Add(independentPanel);
        form.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => InvalidatePlan()));

        var recoveryContent = new StackPanel();
        recoveryContent.Children.Add(DialogUi.Text("选择记录查看原计划。继续时会先核验已有目标，再处理剩余文件。", 11));
        recoveryContent.Children.Add(recoveries);
        recoverySection.Content = recoveryContent;
        recoveries.SelectionChanged += (_, _) => ReviewRecovery();
        var settings = new StackPanel();
        settings.Children.Add(form);
        settings.Children.Add(recoverySection);
        var scroller = new ScrollViewer { Content = settings, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        grid.Children.Add(scroller);
        SizeChanged += (_, _) => scroller.MaxHeight = Math.Clamp(ActualHeight - 245, 150, 420);

        var previewArea = new DockPanel();
        DockPanel.SetDock(summary, Dock.Top);
        previewArea.Children.Add(summary);
        previewArea.Children.Add(preview);
        Grid.SetRow(previewArea, 1);
        grid.Children.Add(previewArea);

        run = DialogUi.Button("开始复制", async (_, _) => await ExecuteAsync(), "按下方按钮标示的操作执行已显示的计划。");
        run.IsEnabled = false;
        run.SetResourceReference(StyleProperty, "Primary");
        cancelButton = DialogUi.Button("取消导出", (_, _) => cancel?.Cancel(), "请求停止本次导出，并记录已完成的文件。");
        cancelButton.Visibility = Visibility.Collapsed;
        leaveRecovery = DialogUi.Button("返回新导出", (_, _) => LeaveRecovery(), "退出续做预览，重新按当前范围生成计划。");
        leaveRecovery.Visibility = Visibility.Collapsed;
        var footer = new StackPanel();
        footer.Children.Add(progress);
        footer.Children.Add(result);
        footer.Children.Add(DialogUi.Row(run, cancelButton, leaveRecovery, DialogUi.Button("返回选片", (_, _) => Close(), "关闭导出窗口，继续浏览和标记照片。")));
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        Content = grid;

        RefreshRecoveries();
        UpdatePanels();
        previewDelay.Tick += async (_, _) => await BuildAsync();
        Loaded += (_, _) => InvalidatePlan();
        Closing += (_, e) =>
        {
            if (!running) return;
            e.Cancel = true;
            cancel?.Cancel();
            result.Text = "正在取消，等待当前文件操作停止…";
        };
        Closed += (_, _) => { closed = true; generation++; previewDelay.Stop(); };
    }

    private static RadioButton Radio(string label, string group, bool selected, Action action)
    {
        var radio = new RadioButton
        {
            Content = label, GroupName = group, IsChecked = selected,
            ToolTip = group switch
            {
                "Range" => label == "按星级" ? "按勾选的星级确定导出范围。" : "按勾选的取舍状态确定导出范围。",
                "Layout" => label == "分放" ? "JPG 与 RAW 放入各自的子文件夹。" : "JPG 与 RAW 放在同一个结果文件夹。",
                "Operation" => label.StartsWith("复制") ? "复制文件到结果目录，源文件留在原处。" : "整组文件校验完成后才处理源文件。",
                _ => $"为导出的文件选择「{label}」。"
            }
        };
        radio.Checked += (_, _) => action();
        return radio;
    }

    private CheckBox Category(string label, int count, bool selected, Action<bool> setSelected)
    {
        var box = new CheckBox { Content = $"{label}（{count} 组）", IsChecked = selected, ToolTip = $"勾选后纳入「{label}」的照片。" };
        box.Checked += (_, _) => { setSelected(true); InvalidatePlan(); };
        box.Unchecked += (_, _) => { setSelected(false); InvalidatePlan(); };
        return box;
    }

    private StackPanel Independent(string label, NamingRule rule)
    {
        var details = DialogUi.Row(DialogUi.Text("名称"), DialogUi.Edit(rule, nameof(rule.Prefix), 165),
            DialogUi.Text("起始编号"), DialogUi.Edit(rule, nameof(rule.Start), 80));
        details.Visibility = rule.Rename ? Visibility.Visible : Visibility.Collapsed;
        return DialogUi.Row(DialogUi.Text(label),
            Radio("保留原名", label + "Naming", !rule.Rename, () => { rule.Rename = false; details.Visibility = Visibility.Collapsed; InvalidatePlan(); }),
            Radio("自定义", label + "Naming", rule.Rename, () => { rule.Rename = true; details.Visibility = Visibility.Visible; InvalidatePlan(); }), details);
    }

    private void SetRange(ExportRangeMode mode) { options.RangeMode = mode; UpdatePanels(); InvalidatePlan(); }
    private void SetNaming(ExportNamingMode mode) { options.NamingMode = mode; UpdatePanels(); InvalidatePlan(); }

    private void UpdatePanels()
    {
        starPanel.Visibility = options.RangeMode == ExportRangeMode.ByStars ? Visibility.Visible : Visibility.Collapsed;
        decisionPanel.Visibility = options.RangeMode == ExportRangeMode.ByDecision ? Visibility.Visible : Visibility.Collapsed;
        linkedPanel.Visibility = options.NamingMode == ExportNamingMode.Linked ? Visibility.Visible : Visibility.Collapsed;
        independentPanel.Visibility = options.NamingMode == ExportNamingMode.Independent ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearPreview()
    {
        plan = null;
        run.IsEnabled = false;
        preview.ItemsSource = null;
        summary.Text = "正在更新导出预览…";
        result.Text = "";
        progress.Visibility = Visibility.Collapsed;
    }

    private void InvalidatePlan()
    {
        if (!IsLoaded || closed || running || recovering) return;
        generation++;
        ClearPreview();
        previewDelay.Stop();
        previewDelay.Start();
    }

    public async Task BuildAsync()
    {
        previewDelay.Stop();
        if (closed || running || recovering) return;
        int request = ++generation;
        ClearPreview();
        await previewLock.WaitAsync();
        try
        {
            if (request != generation) return;
            if (MainWindow.Descendants<TextBox>(form).Any(box => box.IsVisible && Validation.GetHasError(box)))
                throw new InvalidOperationException("起始编号必须填写非负整数。");
            var snapshot = JsonSerializer.Deserialize<ExportOptions>(JsonSerializer.Serialize(options))!;
            var built = await Task.Run(() => ExportPlanner.Build(task, snapshot));
            if (request != generation) return;

            var jpg = built.Files.Count(f => !f.IsRaw);
            var raw = built.Files.Count - jpg;
            summary.Text = $"本次导出 {jpg} 组 · JPG {jpg} · RAW {raw} · 仅 JPG {jpg - raw} 组 · 约 {built.Files.Sum(f => f.Length) / 1048576.0:N1} MB\n{built.Root}";
            preview.ItemsSource = built.Files.Select(f => f.Preview).ToList();
            plan = built;
            run.Content = built.Move ? "开始移动 · 校验后移除源文件" : "开始复制 · 保留原片";
            run.IsEnabled = true;
        }
        catch (Exception ex)
        {
            if (request != generation) return;
            var details = ex.Message.Split('\n');
            summary.Text = details[0];
            preview.ItemsSource = details.Skip(1).ToList();
        }
        finally { previewLock.Release(); }
    }

    private void RefreshRecoveries()
    {
        var pending = store.LoadExports(task.Id).Where(p => !p.Complete).OrderByDescending(p => p.Created).ToList();
        recoveries.ItemsSource = pending;
        recoverySection.Visibility = pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (pending.Count == 0) recoverySection.IsExpanded = false;
    }

    private void ReviewRecovery()
    {
        if (running || recoveries.SelectedItem is not ExportPlan saved) return;
        generation++;
        previewDelay.Stop();
        ClearPreview();
        recovering = true;
        form.IsEnabled = false;
        leaveRecovery.Visibility = Visibility.Visible;
        summary.Text = $"待继续的原计划 · {saved.Created:yyyy-MM-dd HH:mm}\n{saved.Summary}";
        preview.ItemsSource = saved.Files.Select(f => $"{f.StateText}  {f.Preview}").ToList();
        plan = saved;
        run.Content = saved.Move ? "核验并继续移动" : "核验并继续复制";
        run.IsEnabled = true;
    }

    private void LeaveRecovery()
    {
        recoveries.SelectedItem = null;
        recovering = false;
        leaveRecovery.Visibility = Visibility.Collapsed;
        form.IsEnabled = true;
        InvalidatePlan();
    }

    public async Task ExecuteAsync()
    {
        if (plan == null || running || !run.IsEnabled) return;
        ExecutionStarting?.Invoke();
        running = true;
        form.IsEnabled = recoverySection.IsEnabled = leaveRecovery.IsEnabled = run.IsEnabled = false;
        cancel = new();
        cancelButton.Visibility = progress.Visibility = Visibility.Visible;
        progress.Value = 0;
        result.Text = "正在准备导出…";
        var executing = plan;
        try
        {
            var before = JsonSerializer.Deserialize<PhotoTask>(JsonSerializer.Serialize(task))!;
            await Task.Run(() => store.Save(before));
            await new ExportService(store).RunAsync(executing, new Progress<ExportProgress>(p =>
            {
                progress.Maximum = p.Total;
                progress.Value = p.Done;
                result.Text = $"{p.Phase} {p.Done}/{p.Total} 文件 · 当前 {p.Bytes / 1048576.0:F1}/{p.FileLength / 1048576.0:F1} MB\n{p.File}";
            }), cancel.Token);
            ExportService.ApplyMovedLocations(task, executing);
            task.OperationLog.Add($"{DateTime.Now:O} {executing.Summary}");
            var after = JsonSerializer.Deserialize<PhotoTask>(JsonSerializer.Serialize(task))!;
            await Task.Run(() => store.Save(after));
            summary.Text = (executing.Complete ? "导出完成" : "部分完成 / 已中断")
                + $" · 已校验目标 {executing.Files.Count(f => f.State >= ExportFileState.Committed)}/{executing.Files.Count} · 失败 {executing.Files.Count(f => f.Error != null)}\n{executing.Root}";
            preview.ItemsSource = executing.Files.Select(f => $"{f.StateText}  {f.Preview}{(f.Error == null ? "" : "\n错误：" + f.Error)}")
                .Concat(executing.Messages).ToList();
            result.Text = executing.Complete ? "文件内容经长度与 SHA-256 核验；操作记录已保存。" : "可在「未完成的导出」中选择原计划核验后继续。";
            string logs = Path.Combine(store.DataDirectory, "logs");
            Directory.CreateDirectory(logs);
            await File.WriteAllTextAsync(Path.Combine(logs, executing.Id + ".txt"), summary.Text + "\n" + string.Join("\n", preview.ItemsSource.Cast<string>()));
        }
        catch (OperationCanceledException) { result.Text = "已取消；可在「未完成的导出」中核验后继续。"; }
        catch (Exception ex) { result.Text = "导出或保存失败：" + ex.Message; }
        finally
        {
            running = false;
            form.IsEnabled = !recovering;
            recoverySection.IsEnabled = leaveRecovery.IsEnabled = true;
            cancelButton.Visibility = Visibility.Collapsed;
            cancel.Dispose();
            cancel = null;
            RefreshRecoveries();
        }
    }
}
