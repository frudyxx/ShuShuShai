using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ShuShuShai.App;
using ShuShuShai.App.Controls;
using ShuShuShai.App.Views;
using ShuShuShai.Core;

internal static partial class Program
{
    private static async Task<Window> ClosePromptAsync(MainWindow window)
    {
        window.Close();
        Window? prompt = null;
        await Until(() => (prompt = window.OwnedWindows.OfType<Window>()
            .SingleOrDefault(child => child.IsVisible && child.Title == "关闭舒舒筛")) != null);
        return prompt!;
    }

    private static Button CloseButton(Window prompt, string id) => MainWindow.Descendants<Button>(prompt)
        .Single(button => AutomationProperties.GetAutomationId(button) == id);

    private static void ChooseClose(Window prompt, string id) =>
        CloseButton(prompt, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task ConfirmExitAsync(MainWindow window)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        ChooseClose(await ClosePromptAsync(window), "ExitApplication");
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task V033Specs()
    {
        await Check("启动窗首帧起停留约 1.2 秒，慢加载和减少动画使用静态标志", async () =>
        {
            foreach (bool animate in new[] { true, false })
            {
                var splash = new StartupWindow(animate);
                var ready = new TaskCompletionSource();
                if (animate) ready.SetResult();
                var stopwatch = new Stopwatch();
                splash.ContentRendered += (_, _) => { if (!stopwatch.IsRunning) stopwatch.Start(); };
                var wordmark = (FrameworkElement)splash.FindName("Wordmark");
                var upper = (TranslateTransform)splash.FindName("UpperOffset");
                var status = (TextBlock)splash.FindName("LoadingStatus");
                Is(animate ? wordmark.Opacity == 0 && upper.X < 0 : wordmark.Opacity == 1 && upper.X == 0);
                splash.Show();
                try
                {
                    await splash.PlayAsync(ready.Task).WaitAsync(TimeSpan.FromSeconds(5));
                    Is(stopwatch.ElapsedMilliseconds >= 1180 && wordmark.Opacity == 1 && Math.Abs(upper.X) < .01);
                    Is((status.Visibility == Visibility.Visible) == !animate);
                    Console.WriteLine($"METRIC splash {(animate ? "motion" : "static+slow")} first-frame-ms={stopwatch.ElapsedMilliseconds} DPI={VisualTreeHelper.GetDpi(splash).PixelsPerInchX:F0}");
                }
                finally { splash.Close(); }
            }
        });

        await Check("来源树逐层展开，折叠与刷新不清空隐藏勾选", async () =>
        {
            var data = Dir("目录树数据");
            var source = Dir("目录树来源");
            var sub = Directory.CreateDirectory(Path.Combine(source, "第一层")).FullName;
            var deep = Directory.CreateDirectory(Path.Combine(sub, "第二层")).FullName;
            var jpg = Path.Combine(deep, "B.JPG");
            Jpeg(Path.Combine(source, "A.JPG"), 100, 80);
            Jpeg(jpg, 100, 80);
            Write(source, "B.ARW");
            var previous = Environment.GetEnvironmentVariable("SHUSHUSHAI_DATA_DIR");
            Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR", data);
            var window = new MainWindow();
            window.Show();
            try
            {
                await Task.Delay(120);
                await window.VM.ImportAsync([new(source, false, [source, sub, deep])], true);
                var folderTree = (FolderTree)window.FindName("SourceTree");
                var tree = MainWindow.Descendants<TreeView>(folderTree).Single();
                TreeViewItem? root = null;
                TreeViewItem? child = null;
                await Until(() =>
                {
                    root = tree.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                    child = root?.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                    return child != null;
                });
                Is(root!.IsExpanded && !child!.IsExpanded);
                var node = folderTree.Roots.Single();
                var subNode = node.Children.Single();
                var deepNode = subNode.Children.Single();
                child!.SetCurrentValue(TreeViewItem.IsExpandedProperty, true);
                await Until(() => subNode.IsExpanded);
                Is(!window.VM.SpecificSources && window.VM.Visible.Count == 2);
                deepNode.IsSelected = true;
                root.SetCurrentValue(TreeViewItem.IsExpandedProperty, false);
                await Until(() => !node.IsExpanded);
                Is(window.VM.SpecificSources && window.VM.Visible.Single().JpgPath == jpg);

                await window.VM.ScanAsync();
                var extra = Dir("追加目录");
                await window.VM.ImportAsync([new(extra, false, [extra])], false);
                node = folderTree.Roots[0];
                Is(!node.IsExpanded && node.Children.Single().IsExpanded);
                Is(node.Children.Single().Children.Single().IsSelected);
                Is(folderTree.Roots[1].IsExpanded && window.VM.Visible.Single().JpgPath == jpg);
            }
            finally
            {
                await ConfirmExitAsync(window);
                Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR", previous);
            }
        });

        await Check("关闭提示支持取消和最小化，恢复全屏对比并在确认退出时保存", async () =>
        {
            var (window, _) = await V3Window(3);
            try
            {
                var vm = window.VM;
                vm.SelectAll();
                vm.Compare();
                window.SetFullScreen(true);
                var task = vm.Task!;
                var active = vm.Current;
                var selected = vm.Comparison.ToArray();
                var prompt = await ClosePromptAsync(window);
                var cancel = CloseButton(prompt, "CancelClose");
                Is(cancel.IsDefault && cancel.IsCancel);
                await Until(() => Keyboard.FocusedElement == cancel);
                window.Close();
                Is(window.OwnedWindows.OfType<Window>().Count(child => child.IsVisible) == 1);
                ChooseClose(prompt, "CancelClose");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Until(() => window.IsActive);
                Is(window.IsVisible && window.IsFullScreen && vm.Current == active);

                ChooseClose(await ClosePromptAsync(window), "MinimizeApplication");
                await Until(() => window.WindowState == WindowState.Minimized);
                window.WindowState = WindowState.Normal;
                window.Activate();
                Is(window.IsFullScreen && vm.IsCompare && vm.Task == task && vm.Current == active);
                Is(vm.Comparison.SequenceEqual(selected));
                var scanPrompt = await ClosePromptAsync(window);
                var scan = vm.ScanAsync();
                var scanToken = vm.OperationCancellation!.Token;
                ChooseClose(scanPrompt, "ExitApplication");
                await scan;
                Is(scanToken.IsCancellationRequested && window.IsVisible && vm.Task == task);
                Is(vm.Status.Contains("可再次关闭软件"));
                vm.Select(active!, false, false);
                vm.Apply(stars: 4);
                Console.WriteLine($"METRIC workspace DPI={VisualTreeHelper.GetDpi(window).PixelsPerInchX:F0}");
                await ConfirmExitAsync(window);
                Is(vm.Store.LoadAll().Single(saved => saved.Id == task.Id).Photos.Single(photo => photo.Id == active!.Id).Stars == 4);
            }
            finally
            {
                if (window.IsVisible) await ConfirmExitAsync(window);
                Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR", Path.Combine(Root, "runtime-data"));
            }
        });
    }
}
