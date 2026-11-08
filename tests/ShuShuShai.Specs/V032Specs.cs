using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ShuShuShai.App;
using ShuShuShai.App.Services;
using ShuShuShai.App.Views;
using ShuShuShai.Core;

internal static partial class Program
{
    private static KeyEventArgs PressIn(Window window, Key key, UIElement? target = null)
    {
        var source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("窗口尚未显示");
        var e = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        (target ?? window).RaiseEvent(e);
        return e;
    }

    private static async Task V032Specs()
    {
        await Check("v0.3.2 五星格式覆盖 0–5 及评分通知", () =>
        {
            var photo = new Photo();
            string[] expected = ["☆☆☆☆☆", "★☆☆☆☆", "★★☆☆☆", "★★★☆☆", "★★★★☆", "★★★★★"];
            int changes = 0;
            photo.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Photo.StarDisplay)) changes++; };
            for (int i = 0; i <= 5; i++)
            {
                photo.Stars = i;
                Is(photo.StarDisplay == expected[i] && photo.FilmCaption.StartsWith(expected[i]) && photo.Caption.StartsWith(expected[i]));
            }
            Is(changes == 5);
        });

        await Check("v0.3.2 快捷键默认别名、冲突、保留键与清除持久化", async () =>
        {
            var store = new TaskStore(Dir("键位设置"));
            var bindings = new Shortcuts();
            Is(bindings.TryResolve(new(Key.D3), out var action) && action == ShortcutAction.Rate3);
            Is(bindings.TryResolve(new(Key.NumPad3), out action) && action == ShortcutAction.Rate3);
            Is(bindings.TryResolve(new(Key.OemPlus, ModifierKeys.Shift), out action) && action == ShortcutAction.ZoomIn);
            Is(bindings.TryResolve(new(Key.Add), out action) && action == ShortcutAction.ZoomIn);
            Is(bindings.TryResolve(new(Key.Z, ModifierKeys.Control), out action) && action == ShortcutAction.Undo);
            Is(!new ShortcutGesture(Key.Escape).IsAssignable && !new ShortcutGesture(Key.F4, ModifierKeys.Alt).IsAssignable);
            var draft = bindings.CreateDraft();
            draft[ShortcutAction.Overview] = new(Key.D);
            Is(Shortcuts.ValidationError(draft)?.Contains("大图") == true);
            draft[ShortcutAction.Large] = null;
            draft[ShortcutAction.Rate3] = new(Key.R);
            await bindings.SaveAsync(store, draft);
            var reopened = new Shortcuts();
            reopened.Load(store);
            Is(reopened.TryResolve(new(Key.D), out action) && action == ShortcutAction.Overview);
            Is(!reopened.TryResolve(new(Key.G), out _) && !reopened.TryResolve(new(Key.NumPad3), out _));
            Is(reopened.TryResolve(new(Key.R), out action) && action == ShortcutAction.Rate3);
            Is(reopened.BindingText(ShortcutAction.Large) == "未绑定");
            await reopened.SaveAsync(store, []);
            var restored = new Shortcuts();
            restored.Load(store);
            Is(restored.TryResolve(new(Key.G), out action) && action == ShortcutAction.Overview);
        });

        await Check("v0.3.2 设置录入、冲突禁存、恢复默认、版本及缓存反馈", async () =>
        {
            var store = new TaskStore(Dir("设置窗口"));
            var shortcuts = new Shortcuts();
            var window = new SettingsWindow(store, shortcuts, () => true);
            window.Show();
            try
            {
                Button Find(string name) => MainWindow.Descendants<Button>(window).Single(b => b.IsVisible && Equals(b.Content, name));
                void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => MainWindow.Descendants<Button>(window).Any(b => b.Tag is ShortcutAction.Overview));
                var overview = MainWindow.Descendants<Button>(window).Single(b => Equals(b.Tag, ShortcutAction.Overview));
                Click(overview);
                Is(PressIn(window, Key.D).Handled);
                Is(!Find("保存快捷键").IsEnabled && shortcuts.BindingText(ShortcutAction.Overview) == "G");
                Click(overview);
                Is(PressIn(window, Key.T).Handled && Find("保存快捷键").IsEnabled);
                Click(Find("保存快捷键"));
                await Until(() => shortcuts.BindingText(ShortcutAction.Overview) == "T" && window.IsEnabled);
                Click(Find("恢复默认键位"));
                Click(Find("保存快捷键"));
                await Until(() => shortcuts.BindingText(ShortcutAction.Overview) == "G" && window.IsEnabled);
                Click(Find("关于我们"));
                string version = "v" + typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
                await Until(() => MainWindow.Descendants<TextBlock>(window).Any(t => t.IsVisible && t.Text.Contains(version)));
                Click(Find("清理预览缓存"));
                await Until(() => MainWindow.Descendants<Button>(window).Count(b => b.IsVisible && Equals(b.Content, "清理预览缓存")) == 2);
                var clear = MainWindow.Descendants<Button>(window).Last(b => b.IsVisible && Equals(b.Content, "清理预览缓存"));
                var protectedFile = Path.Combine(ImageService.Shared.CacheDirectory, "测试记录保留.txt");
                Directory.CreateDirectory(ImageService.Shared.CacheDirectory);
                File.WriteAllText(protectedFile, "测试");
                Click(clear);
                await Until(() => MainWindow.Descendants<TextBlock>(window).Any(t => t.IsVisible && t.Text.StartsWith("预览缓存已清理")));
                Is(File.Exists(protectedFile));
            }
            finally { window.Close(); }
        });

        await Check("v0.3.2 快捷键即时重绑、提示同步、输入保护及配对撤回", async () =>
        {
            var (window, _) = await V3Window(4);
            try
            {
                var vm = window.VM;
                var draft = window.Shortcuts.CreateDraft();
                draft[ShortcutAction.Overview] = new(Key.T);
                draft[ShortcutAction.Undo] = new(Key.K);
                await window.Shortcuts.SaveAsync(vm.Store, draft);
                var overview = MainWindow.Descendants<Button>(window).Single(b => Equals(b.Tag, ShortcutAction.Overview));
                Is(overview.ToolTip?.ToString()?.EndsWith("T") == true && ToolTipService.GetInitialShowDelay(overview) == 2000);
                Is(!PressIn(window, Key.G).Handled);
                Is(PressIn(window, Key.T).Handled && vm.IsGrid);
                var slider = (Slider)window.FindName("OverviewSize");
                await Until(() => slider.IsVisible);
                slider.Focus();
                await Until(() => slider.IsKeyboardFocusWithin);
                Is(!PressIn(window, Key.Left, slider).Handled, "滑杆方向键不得被翻片抢占");
                var panel = (Grid)window.Content;
                var input = new TextBox { Width = 100, Height = 25 };
                panel.Children.Add(input);
                input.Focus();
                await Until(() => input.IsKeyboardFocusWithin);
                Is(!PressIn(window, Key.D4, input).Handled && vm.Current!.Stars == 0);
                panel.Children.Remove(input);
                overview.Focus();
                Is(PressIn(window, Key.D4).Handled && vm.Current!.Stars == 4);

                var active = vm.Current!;
                var pairing = new PairingWindow(vm.Task!, active, vm.EditHistory, () => vm.Refresh(), window.Shortcuts) { Owner = window };
                pairing.Show();
                try
                {
                    await Until(() => MainWindow.Descendants<Button>(pairing).Any(b => Equals(b.Content, "撤回上一步")));
                    var undo = MainWindow.Descendants<Button>(pairing).Single(b => Equals(b.Content, "撤回上一步"));
                    Is(undo.ToolTip?.ToString()?.EndsWith("K") == true);
                    Is(PressIn(pairing, Key.K).Handled && active.Stars == 0);
                }
                finally { pairing.Close(); }
            }
            finally { await CloseV3(window); }
        });

        await Check("v0.3.2 单行横滚、细深色滚动条及高度重开恢复", async () =>
        {
            var (window, _) = await V3Window(5);
            var data = window.VM.Store.DataDirectory;
            try
            {
                window.Width = window.MinWidth;
                var rows = (StackPanel)window.FindName("ToolRows");
                var scroll = (ScrollViewer)window.FindName("ToolbarScroll");
                var tools = (RowDefinition)window.FindName("ToolsRow");
                var grip = (Thumb)window.FindName("ToolsSplitter");
                void Drag(double pixels) => grip.RaiseEvent(new DragDeltaEventArgs(0, pixels) { RoutedEvent = Thumb.DragDeltaEvent });
                Is(rows.Orientation == Orientation.Vertical);
                Drag(55);
                await Until(() => rows.Orientation == Orientation.Horizontal && scroll.ScrollableWidth > 0);
                Is(tools.Height.Value == 48);
                var bar = MainWindow.Descendants<ScrollBar>(scroll).Single(b => b.IsVisible);
                var track = (Track)bar.Template.FindName("PART_Track", bar);
                Is(bar.ActualHeight == 10 && track.Thumb.Background is SolidColorBrush brush && brush.Color == Color.FromRgb(102, 102, 102));
                scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                await Until(() => scroll.HorizontalOffset > 0);
                scroll.ScrollToHorizontalOffset(0);
                await Until(() => scroll.HorizontalOffset == 0);
                track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                track.Thumb.RaiseEvent(new DragDeltaEventArgs(100, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                track.Thumb.RaiseEvent(new DragCompletedEventArgs(100, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                await Until(() => scroll.HorizontalOffset > 0);
                Drag(-42);
                await Until(() => rows.Orientation == Orientation.Vertical);
                Drag(42);
                grip.RaiseEvent(new DragCompletedEventArgs(0, 42, false) { RoutedEvent = Thumb.DragCompletedEvent });
            }
            finally { await CloseV3(window); }

            Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR", data);
            var reopened = new MainWindow();
            reopened.Show();
            try
            {
                await Until(() => ((StackPanel)reopened.FindName("ToolRows")).Orientation == Orientation.Horizontal);
                Is(((RowDefinition)reopened.FindName("ToolsRow")).Height.Value == 48);
            }
            finally { await CloseV3(reopened); }
        });

        await Check("v0.3.2 全屏状态随筛选、评分、撤回和多图活动项变化", async () =>
        {
            var (window, _) = await V3Window(4);
            try
            {
                var vm = window.VM;
                vm.Select(vm.Visible[2], false, false);
                vm.Apply(stars: 3, decision: Decision.Keep);
                vm.DecisionFilter = 2;
                window.SetFullScreen(true);
                var overlay = (Border)window.FindName("FullScreenStatus");
                string Status() => string.Join(" · ", MainWindow.Descendants<TextBlock>(overlay).Select(t => t.Text));
                await Until(() => overlay.IsVisible && Status() == "★★★☆☆ · 保留 · 1 / 1");
                vm.Apply(stars: 5);
                Is(Status() == "★★★★★ · 保留 · 1 / 1");
                vm.Undo();
                Is(Status() == "★★★☆☆ · 保留 · 1 / 1");
                window.SetFullScreen(false);
                Is(!overlay.IsVisible);
                vm.DecisionFilter = 0;
                vm.Select(vm.Visible[0], false, false);
                vm.Select(vm.Visible[2], true, false);
                Is(vm.Compare());
                window.SetFullScreen(true);
                vm.Current = vm.Visible[0];
                Is(Status() == "☆☆☆☆☆ · 未定 · 1 / 4");
                vm.Current = vm.Visible[2];
                Is(Status() == "★★★☆☆ · 保留 · 3 / 4");
                Is(PressIn(window, Key.Escape).Handled && vm.IsCompare && !overlay.IsVisible);
            }
            finally { await CloseV3(window); }
        });
    }
}
