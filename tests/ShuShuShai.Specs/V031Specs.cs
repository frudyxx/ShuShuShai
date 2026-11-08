using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShuShuShai.App;
using ShuShuShai.App.Views;
using ShuShuShai.Core;

internal static partial class Program
{
    private static async Task Until(Func<bool> ready)
    {
        var limit = DateTime.UtcNow.AddSeconds(6);
        while (!ready() && DateTime.UtcNow < limit) await Task.Delay(20);
        Is(ready(), "等待界面状态更新超时");
    }

    private static async Task V031Specs()
    {
        await Check("v0.3.1 旧星级等价映射与显式空选择持久化", () =>
        {
            var options = new ExportOptions { RangeMode = ExportRangeMode.ByStars };
            Is(ExportPlanner.SelectedStarLevels(options).SequenceEqual([3, 4, 5]));
            options.IncludeUnrated = true;
            Is(ExportPlanner.SelectedStarLevels(options).SequenceEqual([0, 3, 4, 5]));
            options.StarComparison = 3;
            Is(ExportPlanner.SelectedStarLevels(options).SequenceEqual([0, 1, 2, 3]));
            options.StarComparison = 4;
            Is(ExportPlanner.SelectedStarLevels(options).SequenceEqual([0, 3]));
            var store = new TaskStore(Dir("v031范围持久化"));
            var task = new PhotoTask
            {
                ExportOptions = options,
                Photos = Enumerable.Range(0, 6).Select(s => new Photo { Stars = s, Decision = Decision.Reject }).ToList()
            };
            options.StarLevels = [1, 4];
            store.Save(task);
            var saved = store.LoadAll().Single();
            Is(ExportPlanner.SelectPhotos(saved, saved.ExportOptions).Select(p => p.Stars).SequenceEqual([1, 4]));
            options.StarLevels = [];
            store.Save(task);
            saved = store.LoadAll().Single();
            Is(ExportPlanner.SelectPhotos(saved, saved.ExportOptions).Count == 0);
        });

        await Check("v0.3.1 导入第一层可见，下级默认收起，展开与勾选分离", async () =>
        {
            var rootPath = Dir("v031目录树");
            Directory.CreateDirectory(Path.Combine(rootPath, "一级", "二级", "三级"));
            var window = new ImportWindow();
            window.Show();
            try
            {
                await window.AddRootAsync(rootPath);
                var tree = MainWindow.Descendants<TreeView>(window).Single();
                await Until(() => tree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem);
                var root = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
                await Until(() => root.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem);
                var first = (TreeViewItem)root.ItemContainerGenerator.ContainerFromIndex(0);
                var node = window.FoldersTree.Roots.Single();
                Is(root.IsExpanded && !first.IsExpanded && node.IsSelected && !node.Descendants().Any(n => n.IsSelected));
                first.SetCurrentValue(TreeViewItem.IsExpandedProperty, true);
                await Until(() => first.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem);
                var second = (TreeViewItem)first.ItemContainerGenerator.ContainerFromIndex(0);
                Is(!second.IsExpanded && node.Children.Single().IsExpanded && !node.Descendants().Any(n => n.IsSelected));
                node.ToggleAllChildren();
                Is(node.Descendants().All(n => n.IsSelected) && !second.IsExpanded);
            }
            finally { window.Close(); }
        });

        await Check("v0.3.1 活动图按钮高亮、菜单主题、滑杆及胶片条模式", async () =>
        {
            var previous = Environment.GetEnvironmentVariable("SHUSHUSHAI_DATA_DIR");
            Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR", Dir("v031工作区数据"));
            var window = new MainWindow();
            window.Show();
            try
            {
                var source = Dir("v031工作区照片");
                for (int i = 0; i < 28; i++) Jpeg(Path.Combine(source, $"测试_{i:D2}.JPG"), 160, 100);
                await window.VM.ImportAsync([new(source, false, [source])], true);
                var vm = window.VM;
                vm.ViewIndex = 1;
                var slider = (Slider)window.FindName("OverviewSize");
                await Until(() => slider.IsVisible);
                var details = (ScrollViewer)window.FindName("DetailsScroll");
                Button Choice(object value) => MainWindow.Descendants<Button>(details).Single(b => Equals(b.Tag, value));
                var accent = (SolidColorBrush)window.FindResource("Accent");
                bool Active(object value) => Choice(value).BorderBrush is SolidColorBrush brush && brush.Color == accent.Color;
                Is(Active(0) && Active(Decision.Undecided));
                Choice(3).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Choice(Decision.Keep).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Is(Active(3) && Active(Decision.Keep) && !Active(0));
                vm.Undo();
                Is(Active(3) && Active(Decision.Undecided));
                vm.Apply(decision: Decision.Reject);
                Is(Active(Decision.Reject));
                vm.SelectFromFilm(vm.Visible[1], true, false);
                Is(vm.IsGrid && vm.SelectedCount == 2 && Active(0) && Active(Decision.Undecided));
                vm.SelectFromFilm(vm.Visible[3], false, true);
                Is(vm.IsGrid && vm.SelectedCount == 3);
                Is(vm.Compare() && !slider.IsVisible);
                var film = (ListBox)window.FindName("Film");
                await Until(() => MainWindow.Descendants<Border>(film).Any(b => b.Tag is Photo));
                var tile = MainWindow.Descendants<Border>(film).First(b => b.Tag is Photo);
                var chosen = (Photo)tile.Tag;
                tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                Is(vm.IsLarge && vm.Current == chosen && vm.SelectedCount == 1 && !slider.IsVisible);

                var more = MainWindow.Descendants<Button>(window).Single(b => Equals(b.Content, "更多 ▾"));
                foreach (var (target, count) in new (FrameworkElement, int)[] { (more, 3), (tile, 2) })
                {
                    var menu = target.ContextMenu;
                    menu.PlacementTarget = target;
                    menu.IsOpen = true;
                    await Until(() => menu.IsVisible && menu.ActualWidth > 0);
                    Is(menu.OverridesDefaultStyle && menu.Items.OfType<MenuItem>().Count() == count);
                    Is(MainWindow.Descendants<Border>(menu).First().Background is SolidColorBrush bg && bg.Color == Color.FromRgb(41, 41, 41));
                    menu.IsOpen = false;
                }
                vm.ViewIndex = 1;
                var grid = (ListBox)window.FindName("GridList");
                await Until(() => MainWindow.Descendants<ScrollViewer>(grid).Any());
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var gridScroll = MainWindow.Descendants<ScrollViewer>(grid).First();
                gridScroll.ScrollToVerticalOffset(2);
                await Until(() => gridScroll.VerticalOffset > 0 && vm.Task!.GridOffset == gridScroll.VerticalOffset);
                var offset = gridScroll.VerticalOffset;
                vm.SelectFromFilm(vm.Visible[2], false, false);
                MainWindow.Descendants<Button>(window).Single(b => Equals(b.Content, "总览")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                await Until(() => vm.IsGrid && gridScroll.VerticalOffset == offset);
                window.SetFullScreen(true);
                await Task.Delay(120);
                window.SetFullScreen(false);
                await Task.Delay(250);
                var restoredScroll = MainWindow.Descendants<ScrollViewer>(grid).First();
                Is(vm.IsGrid && restoredScroll.VerticalOffset == offset,
                    $"全屏退出后的总览位置为 {restoredScroll.VerticalOffset}，预期 {offset}，任务记录 {vm.Task!.GridOffset}");
            }
            finally { await ConfirmExitAsync(window); Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR", previous); }
        });

        await Check("v0.3.1 导出类别数量、独立记忆和自动预览使用最后一次输入", async () =>
        {
            var x = await Plan();
            var (first, second, third) = (x.Task.Photos[0], x.Task.Photos[1], x.Task.Photos[2]);
            first.Stars = 1; first.Decision = Decision.Reject;
            second.Stars = 4; second.Decision = Decision.Undecided;
            third.Stars = 0; third.Decision = Decision.Keep;
            x.Task.Filter = new() { Mode = 4, Stars = 5 };
            var window = new ExportWindow(x.Task, x.Store);
            window.Show();
            try
            {
                RadioButton Radio(string label) => MainWindow.Descendants<RadioButton>(window).Single(b => Equals(b.Content, label));
                CheckBox Category(string label) => MainWindow.Descendants<CheckBox>(window).Single(b => b.Content.ToString()!.StartsWith(label + "（"));
                Is(!MainWindow.Descendants<Expander>(window).Single().IsVisible);
                await Until(() => window.CurrentPlan?.Files.All(f => f.GroupId == third.Id) == true);
                Radio("按星级").IsChecked = true;
                Is(Equals(Category("1 星").Content, "1 星（1 组）") && Equals(Category("未评分").Content, "未评分（1 组）"));
                Category("1 星").IsChecked = true;
                Category("3 星").IsChecked = false;
                Category("5 星").IsChecked = false;
                await Until(() => window.CurrentPlan?.Files.Select(f => f.GroupId).Distinct().Count() == 2);
                Is(window.CurrentPlan!.Files.All(f => f.GroupId == first.Id || f.GroupId == second.Id));
                Radio("按标记").IsChecked = true;
                Category("保留").IsChecked = false;
                await Until(() => MainWindow.Descendants<TextBlock>(window).Any(t => t.Text.StartsWith("没有符合")));
                Is(window.CurrentPlan == null);
                Category("淘汰").IsChecked = true;
                await Until(() => window.CurrentPlan?.Files.All(f => f.GroupId == first.Id) == true);
                Radio("按星级").IsChecked = true;
                Is(Category("1 星").IsChecked == true && Category("4 星").IsChecked == true);
                Radio("混放").IsChecked = true;
                Radio("联动命名").IsChecked = true;
                var prefix = MainWindow.Descendants<TextBox>(window).Single(t => t.IsVisible && t.Text == "照片");
                prefix.Text = "第一版";
                await Task.Delay(260);
                prefix.Text = "无效?";
                Is(window.CurrentPlan == null);
                await Until(() => MainWindow.Descendants<TextBlock>(window).Any(t => t.Text.Contains("Windows 文件名无效")));
                Is(window.CurrentPlan == null);
                prefix.Text = "中间版";
                prefix.Text = "最终版";
                await Until(() => window.CurrentPlan?.Files.All(f => Path.GetFileName(f.Target).StartsWith("最终版_")) == true);
                var plan = window.CurrentPlan!;
                Is(plan.Files.All(f => Path.GetDirectoryName(f.Target) == plan.Root));
                await window.ExecuteAsync();
                Is(plan.Complete && plan.Files.All(f => File.Exists(f.Source) && File.Exists(f.Target)));
                Is(x.Store.LoadExports(x.Task.Id).Single().Id == plan.Id);
            }
            finally { window.Close(); }
        });

        foreach (bool move in new[] { false, true })
        {
            await Check($"v0.3.1 未完成入口仅核验并继续原{(move ? "移动" : "复制")}计划", async () =>
            {
                var x = await Plan(move);
                using var stop = new CancellationTokenSource();
                await new ExportService(x.Store, (phase, _) => { if (phase == "after-commit") stop.Cancel(); })
                    .RunAsync(x.Plan, null, stop.Token);
                Is(!x.Plan.Complete);
                var completed = x.Plan.Files.Single(f => File.Exists(f.Target));
                var hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(completed.Target));
                x.Task.ExportOptions.Decisions.Clear();
                var window = new ExportWindow(x.Task, x.Store);
                window.Show();
                try
                {
                    var section = MainWindow.Descendants<Expander>(window).Single();
                    Is(section.IsVisible && !section.IsExpanded);
                    section.IsExpanded = true;
                    await Until(() => MainWindow.Descendants<ListBox>(section).Any());
                    MainWindow.Descendants<ListBox>(section).Single().SelectedIndex = 0;
                    Is(window.CurrentPlan?.Id == x.Plan.Id && !window.CurrentPlan.Complete);
                    Is(window.CurrentPlan!.Files.Select(f => f.Target).SequenceEqual(x.Plan.Files.Select(f => f.Target)));
                    Is(x.Plan.Files.Count(f => File.Exists(f.Target)) == 1, "查看原计划不得执行导出");
                    await window.ExecuteAsync();
                    Is(window.CurrentPlan!.Complete && window.CurrentPlan.Files.All(f => File.Exists(f.Target) && File.Exists(f.Source) != move));
                    Is(!section.IsVisible && x.Store.LoadExports(x.Task.Id).Single().Id == x.Plan.Id);
                    Is(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(completed.Target)).SequenceEqual(hash));
                }
                finally { window.Close(); }
            });
        }

        await Check("v0.3.1 继续移动遇到改动过的目标时保留目标与同组源文件", async () =>
        {
            var x = await Plan(move: true);
            using var stop = new CancellationTokenSource();
            await new ExportService(x.Store, (phase, _) => { if (phase == "after-commit") stop.Cancel(); })
                .RunAsync(x.Plan, null, stop.Token);
            var saved = x.Store.LoadExports(x.Task.Id).Single();
            var changed = saved.Files.Single(f => File.Exists(f.Target));
            var foreignBytes = File.ReadAllBytes(changed.Target);
            foreignBytes[0] ^= 1;
            File.WriteAllBytes(changed.Target, foreignBytes);
            await new ExportService(x.Store).RunAsync(saved, null, default);
            Is(!saved.Complete && changed.Error != null);
            Is(File.ReadAllBytes(changed.Target).SequenceEqual(foreignBytes));
            Is(saved.Files.Where(f => f.GroupId == changed.GroupId).All(f => File.Exists(f.Source)));
        });
    }
}
