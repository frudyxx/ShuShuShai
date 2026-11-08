using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using ShuShuShai.Core;
using ShuShuShai.App.Controls;
using ShuShuShai.App.Services;
using ShuShuShai.App.ViewModels;
using ShuShuShai.App.Views;
namespace ShuShuShai.App;
public record WorkspaceLayout(double Left=230,double Right=280,double Film=180,bool LeftCollapsed=false,bool RightCollapsed=false,double Tools=96);
public partial class MainWindow : Window
{
    private const double MinToolsHeight = 48;
    private const double CompactToolsHeight = 76;
    public MainViewModel VM { get; }
    public Shortcuts Shortcuts { get; } = new();
    private bool closing,closePending,exportOpen,restoringGrid,sourceSync,fullScreen;
    private readonly SemaphoreSlim layoutSaveLock = new(1);
    private PhotoTask? sourceTreeTask;
    private Task? workspaceInitialization;
    private DispatcherOperation? gridRestore;
    private double leftWidth=230,rightWidth=280,filmHeight=180,toolsHeight=96;
    private bool leftCollapsed,rightCollapsed;
    private Rect normalBounds;
    private WindowState normalState;
    private int fullScreenReturnView;
    private readonly DispatcherTimer fullScreenTimer=new(){Interval=TimeSpan.FromSeconds(2)};
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly WindowChrome darkChrome = new()
    {
        CaptionHeight = 32, GlassFrameThickness = new(0), UseAeroCaptionButtons = false
    };
    public bool IsFullScreen=>fullScreen;
    public MainWindow() : this(new TaskStore(UpdateInstallation.DataDirectory)) { }

    internal MainWindow(TaskStore store)
    {
        InitializeComponent();VM=new(store);DataContext=VM;
        Title = $"舒舒筛 · v{App.VersionLabel} 预览版";
        StateChanged += (_, _) => UpdateMaximizeButton();
        foreach(var setter in ((Style)Resources["Tile"]).Setters.OfType<Setter>())if(setter.Value is ContextMenu menu)menu.Opened+=ThumbnailMenuOpened;
        VM.ImagesInvalidated+=()=>{LargeViewer.RefreshImage();foreach(var v in Descendants<PhotoViewer>(ComparisonView))v.RefreshImage();RebuildSources();};
        VM.RowsRebuilt+=()=>{if(VM.IsGrid)RestoreGrid();};
        VM.PropertyChanged+=(_,e)=>
        {
            if(e.PropertyName is nameof(VM.Position) or nameof(VM.Current) or nameof(VM.Stats))
            {EmptyState.Visibility=VM.HasTask&&VM.Visible.Count==0?Visibility.Visible:Visibility.Collapsed;if(VM.Current!=null)Film.ScrollIntoView(VM.Current);}
            if(e.PropertyName==nameof(VM.ViewIndex)){if(VM.IsGrid)RestoreGrid();if(fullScreen&&VM.IsGrid)SetFullScreen(false);}
            if(e.PropertyName==nameof(VM.Task))
            {
                if (sourceTreeTask == null && VM.HasTask) Pulse(TaskArea, .78, 150);
                RebuildSources();
            }
            if (e.PropertyName is nameof(VM.Status) or nameof(VM.Busy)) UpdateNotices();
            if (e.PropertyName == nameof(VM.SaveError))
                SaveNotice.Visibility = VM.SaveError.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        };
        Loaded+=async(_,_)=>await InitializeWorkspaceAsync();
        SizeChanged+=(_,_)=>{ClampBottomHeights(); if (WindowState == WindowState.Maximized) UpdateMaximizedInsets();};
        Closed+=(_,_)=>{VM.Dispose();fullScreenTimer.Stop();toastTimer.Stop();};
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        fullScreenTimer.Tick+=(_,_)=>{if(fullScreen&&FullScreenTools.IsMouseOver)return;fullScreenTimer.Stop();if(fullScreen)FullScreenTools.Visibility=Visibility.Collapsed;};
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Install the maximize hit test before WPF's WindowChrome hook.
        ((HwndSource)PresentationSource.FromVisual(this)!).AddHook(CaptionHitTest);
        WindowChrome.SetWindowChrome(this, darkChrome);
    }

    private IntPtr CaptionHitTest(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int hitTest = 0x84, maximizeButton = 9;
        if (message != hitTest || fullScreen || !MaximizeButton.IsVisible || !MaximizeButton.IsEnabled)
            return IntPtr.Zero;
        long screen = lParam.ToInt64();
        var point = MaximizeButton.PointFromScreen(new Point((short)screen, (short)(screen >> 16)));
        if (!new Rect(MaximizeButton.RenderSize).Contains(point)) return IntPtr.Zero;
        handled = true;
        return new IntPtr(maximizeButton);
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void CloseWindow(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    private void ToggleMaximized(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void UpdateMaximizeButton()
    {
        bool maximized = WindowState == WindowState.Maximized;
        UpdateMaximizedInsets();
        MaximizeButton.Content = maximized ? "❐" : "□";
        AutomationProperties.SetName(MaximizeButton, maximized ? "还原" : "最大化");
        MaximizeButton.ToolTip = maximized ? "还原窗口大小。" : "最大化窗口。";
    }

    private void UpdateMaximizedInsets()
    {
        if (fullScreen || WindowState != WindowState.Maximized) { RootLayout.Margin = new Thickness(0); return; }
        var hwnd = new WindowInteropHelper(this).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetWindowRect(hwnd, out var bounds) || !GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return;
        // A maximized HWND extends beyond the monitor work area by its invisible resize frame.
        var dpi = VisualTreeHelper.GetDpi(this);
        RootLayout.Margin = new Thickness(
            Math.Max(0, info.Work.Left - bounds.Left) / dpi.DpiScaleX,
            Math.Max(0, info.Work.Top - bounds.Top) / dpi.DpiScaleY,
            Math.Max(0, bounds.Right - info.Work.Right) / dpi.DpiScaleX,
            Math.Max(0, bounds.Bottom - info.Work.Bottom) / dpi.DpiScaleY);
    }

    private void UpdateNotices()
    {
        ScanNotice.Visibility = VM.Busy && (leftCollapsed || fullScreen) ? Visibility.Visible : Visibility.Collapsed;
        toastTimer.Stop();
        bool show = !VM.Busy && VM.Status.Length > 0;
        Toast.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) toastTimer.Start();
    }

    private async void RetrySave(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        try { if (await VM.FlushSaveAsync()) VM.Status = "选片进度已保存"; }
        finally { button.IsEnabled = true; }
    }

    internal Task InitializeWorkspaceAsync() => workspaceInitialization ??= LoadWorkspaceAsync();

    private async Task LoadWorkspaceAsync()
    {
        await VM.InitializeAsync();
        LoadLayout();
        LoadShortcuts();
    }

    private async void Home(object sender,RoutedEventArgs e){if(VM.Busy||VM.IsHome)return;if(fullScreen)SetFullScreen(false);await VM.HomeAsync();}
    private async void ContinueTask(object sender,RoutedEventArgs e){if(!VM.Busy&&(sender as FrameworkElement)?.Tag is PhotoTask p){await VM.OpenAsync(p);RebuildSources();RestoreGrid();}}
    private async void RemoveHistory(object sender, RoutedEventArgs e)
    {
        if (!VM.IsHome || (sender as FrameworkElement)?.Tag is not PhotoTask task) return;
        var dialog = new HistoryRemovalWindow(this, task, canArchive: true);
        if (dialog.ShowDialog() != true) return;
        HomePage.IsEnabled = false;
        try
        {
            await Task.Run(() =>
            {
                if (dialog.Choice == HistoryRemoval.Archive) VM.Store.SetArchived(task.Id, true);
                else VM.Store.DeleteTask(task.Id);
            });
            if (VM.History.FirstOrDefault(entry => entry.Id == task.Id) is { } entry)
                VM.History.Remove(entry);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法删除历史记录"); }
        finally { HomePage.IsEnabled = true; }
    }
    private void NewImport(object sender,RoutedEventArgs e)=>Import(true);
    private void AddImport(object sender,RoutedEventArgs e)=>Import(false);
    private async void Import(bool newTask)
    {
        if(VM.Busy)return;
        var outputs=await System.Threading.Tasks.Task.Run(()=>VM.Store.LoadExports().Where(p=>p.RootCreated).Select(p=>p.Root).ToList());
        if (closePending || !IsLoaded) return;
        var dialog=new ImportWindow(outputs){Owner=this};if(dialog.ShowDialog()!=true)return;
        if(newTask)
        {
            var old=VM.History.FirstOrDefault(t=>t.Sources.Any(s=>PathRules.Same(s.Path,dialog.Folders[0].Path)));
            if(old!=null)
            {
                var answer=MessageBox.Show(this,$"这个来源已有任务『{old.SourceTitle}』。\n是：继续已有任务；否：新建任务；取消：返回。","继续还是新建",MessageBoxButton.YesNoCancel);
                if(answer==MessageBoxResult.Cancel)return;
                if(answer==MessageBoxResult.Yes){await VM.OpenAsync(old);newTask=false;}
            }
        }
        await VM.ImportAsync(dialog.Folders,newTask);
        if(VM.Task!=null){VM.Task.Diagnostics.AddRange(dialog.Diagnostics);VM.Dirty();}RebuildSources();
    }
    private async void RefreshScan(object sender,RoutedEventArgs e)=>await VM.ScanAsync();
    private void Cancel(object sender,RoutedEventArgs e)=>VM.OperationCancellation?.Cancel();
    private void RebuildSources()
    {
        if(VM==null)return;sourceSync=true;
        try
        {
            var oldRoots = ReferenceEquals(sourceTreeTask, VM.Task)
                ? SourceTree.Roots.ToLookup(root => root.Path, PathRules.Comparer) : null;
            sourceTreeTask = VM.Task;
            SourceTree.Roots.Clear();if(VM.Task==null)return;
            foreach(var source in VM.Task.Sources)
            {
                var dirs=source.Directories??(source.Recursive?VM.Task.Files.Where(f=>PathRules.IsWithin(f.Path,source.Path)).Select(f=>Path.GetDirectoryName(f.Path)!).Append(source.Path).Distinct(PathRules.Comparer).ToList():[source.Path]);
                var root=FolderNode.Build(source.Path,dirs,VM.Task.Filter.Directories,true);
                var expansion = oldRoots?[root.Path].FirstOrDefault()?.All()
                    .ToDictionary(node => node.Path, node => node.IsExpanded, PathRules.Comparer);
                foreach (var node in root.All())
                {
                    if (expansion?.TryGetValue(node.Path, out bool expanded) == true) node.IsExpanded = expanded;
                    node.Changed += SourceChanged;
                }
                SourceTree.Roots.Add(root);
            }
            SpecificSourcesRadio.IsChecked=VM.SpecificSources;AllSourcesRadio.IsChecked=!VM.SpecificSources;
        }
        finally{sourceSync=false;}
    }
    private void SourceChanged(FolderNode changed)
    {
        if(sourceSync||VM.Task==null)return;sourceSync=true;
        try
        {
            foreach(var n in SourceTree.Roots.SelectMany(r=>r.All()).Where(n=>n!=changed&&PathRules.Same(n.Path,changed.Path)))n.IsSelected=changed.IsSelected;
            VM.SetSourceDirectories(SourceTree.Roots.SelectMany(r=>r.All()).Where(n=>n.IsSelected).Select(n=>n.Path));
            VM.SpecificSources=true;SpecificSourcesRadio.IsChecked=true;
        }
        finally{sourceSync=false;}
    }
    private void AllSourcesChecked(object sender,RoutedEventArgs e){if(VM!=null&&!sourceSync)VM.SpecificSources=false;}
    private void SpecificSourcesChecked(object sender,RoutedEventArgs e){if(VM!=null&&!sourceSync)VM.SpecificSources=true;}
    private void OnTile(object sender,MouseButtonEventArgs e)
    {
        if(VM.Busy||(sender as FrameworkElement)?.Tag is not Photo p)return;
        VM.Select(p,Keyboard.Modifiers.HasFlag(ModifierKeys.Control),Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        if(e.ClickCount==2)VM.ViewIndex=0;e.Handled=true;
    }
    private void FilmTile(object sender,MouseButtonEventArgs e){if(VM.Busy||(sender as FrameworkElement)?.Tag is not Photo p)return;VM.SelectFromFilm(p,Keyboard.Modifiers.HasFlag(ModifierKeys.Control),Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));e.Handled=true;}
    private void LargeClicked(object sender,MouseButtonEventArgs e){if(VM.SelectedCount==0&&VM.Current!=null)VM.Select(VM.Current,false,false);}
    private void CompareClicked(object sender,MouseButtonEventArgs e){if((sender as FrameworkElement)?.Tag is not Photo p)return;VM.Current=p;if(e.ClickCount==2)VM.ViewIndex=0;}
    private void FilmWheel(object sender,MouseWheelEventArgs e){var scroll=Descendants<ScrollViewer>(Film).FirstOrDefault();if(scroll==null)return;scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset-(e.Delta/120.0)*3);e.Handled=true;}
    private void ToolbarWheel(object sender, MouseWheelEventArgs e)
    {
        if (ToolbarScroll.ScrollableWidth <= 0 || OverviewSize.IsMouseOver || Descendants<ComboBox>(Toolbar).Any(c => c.IsMouseOver)) return;
        ToolbarScroll.ScrollToHorizontalOffset(ToolbarScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
    private IEnumerable<PhotoViewer> Viewers()=>VM.IsCompare?Descendants<PhotoViewer>(ComparisonView).Where(v=>VM.LinkedZoom||v.Path==VM.Current?.JpgPath):[LargeViewer];
    private PhotoViewer? ZoomSource()=>VM.IsCompare?Descendants<PhotoViewer>(ComparisonView).FirstOrDefault(v=>v.Path==VM.Current?.JpgPath):LargeViewer;
    public void ZoomSelection(double factor)=>ZoomSource()?.ZoomBy(factor);
    public void SetSelectionZoom(double? value)=>ZoomSource()?.SetZoom(value);
    private void SetReference(object sender,RoutedEventArgs e)=>VM.ReferenceCurrent();
    private void RemoveCompare(object sender,RoutedEventArgs e){if((sender as FrameworkElement)?.Tag is Photo p)VM.RemoveCompare(p);e.Handled=true;}
    private void ComparisonViewerLoaded(object sender,RoutedEventArgs e){var v=(PhotoViewer)sender;v.TransformChanged-=SyncCompare;v.TransformChanged+=SyncCompare;}
    private void SyncCompare(PhotoViewer source,double? zoom,double x,double y){if(VM.LinkedZoom)foreach(var v in Viewers().Where(v=>v!=source))v.Sync(zoom,x,y);}
    private void RunTool(object sender, RoutedEventArgs e) => Execute((ShortcutAction)((Button)sender).Tag);
    private void Stars(object sender,RoutedEventArgs e) => Execute(ShortcutAction.Unrate + (int)((Button)sender).Tag);
    private void Mark(object sender,RoutedEventArgs e) => Execute((Decision)((Button)sender).Tag switch
    {
        Decision.Keep => ShortcutAction.Keep,
        Decision.Reject => ShortcutAction.Reject,
        _ => ShortcutAction.Undecide
    });

    private void Execute(ShortcutAction action)
    {
        if (VM.Busy || !VM.HasTask) return;
        switch (action)
        {
            case ShortcutAction.Large: VM.ViewIndex = 0; break;
            case ShortcutAction.Overview: VM.ViewIndex = 1; RestoreGrid(); break;
            case ShortcutAction.Compare: VM.Compare(); break;
            case ShortcutAction.FullScreen: SetFullScreen(!fullScreen); break;
            case ShortcutAction.Previous: VM.Step(-1); break;
            case ShortcutAction.Next: VM.Step(1); break;
            case ShortcutAction.Fit: SetSelectionZoom(null); break;
            case ShortcutAction.ActualPixels: SetSelectionZoom(1); break;
            case ShortcutAction.ZoomIn: ZoomSelection(1.25); break;
            case ShortcutAction.ZoomOut: ZoomSelection(.8); break;
            case ShortcutAction.SelectAll: VM.SelectAll(); break;
            case ShortcutAction.Undo: VM.Undo(); break;
            case >= ShortcutAction.Unrate and <= ShortcutAction.Rate5:
                VM.Apply(stars: action - ShortcutAction.Unrate);
                if (VM.HasSelection) Pulse(fullScreen ? FullScreenStatus : RatingChoices, .65, 130);
                break;
            case ShortcutAction.Keep: VM.Apply(decision: Decision.Keep); break;
            case ShortcutAction.Reject: VM.Apply(decision: Decision.Reject); break;
            case ShortcutAction.Undecide: VM.Apply(decision: Decision.Undecided); break;
        }
    }
    private void WorkspaceSize(object sender,SizeChangedEventArgs e){if(VM==null)return;double width=Math.Max(100,Workspace.ActualWidth-20);if(Math.Abs(width-VM.GridWidth)>1){VM.GridWidth=width;VM.RebuildRows();}ClampBottomHeights();}
    private void GridScrolled(object sender,ScrollChangedEventArgs e){if(!restoringGrid&&VM?.Task!=null&&!VM.RebuildingRows&&VM.IsGrid&&e.VerticalChange!=0){VM.Task.GridOffset=e.VerticalOffset;VM.Dirty();}}
    private void RestoreGrid()
    {
        if (!VM.IsGrid || VM.Task is not { } task) return;
        restoringGrid = true;
        gridRestore?.Abort();
        gridRestore = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (VM.IsGrid && VM.Task == task)
                Descendants<ScrollViewer>(GridList).FirstOrDefault()?.ScrollToVerticalOffset(task.GridOffset);
            // ScrollToVerticalOffset is applied in layout, along with any pending window resize.
            gridRestore = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                restoringGrid = false;
                gridRestore = null;
            }));
        }));
    }
    private void Pairing(object sender,RoutedEventArgs e)=>ShowPairing(null);
    private void CurrentPairing(object sender,RoutedEventArgs e)=>ShowPairing(VM.Current);
    private void ShowPairing(Photo? current){if(VM.Task==null||VM.Busy)return;new PairingWindow(VM.Task,current,VM.EditHistory,()=>VM.Refresh(),Shortcuts){Owner=this}.ShowDialog();foreach(var p in VM.Task.Photos)p.NotifyAll();VM.Refresh();}
    private async void Export(object sender, RoutedEventArgs e)
    {
        if (VM.Task == null || VM.Busy || exportOpen || closePending) return;
        exportOpen = true;
        try
        {
            bool saved = await VM.FlushSaveAsync();
            if (closePending || !IsLoaded) return;
            if (!saved)
            {
                MessageBox.Show(this, VM.SaveStatus);
                return;
            }
            var dialog = new ExportWindow(VM.Task, VM.Store) { Owner = this };
            dialog.ExecutionStarting += VM.EditHistory.Clear;
            dialog.ShowDialog();
            foreach (var photo in VM.Task.Photos) photo.NotifyAll();
            VM.Refresh();
            await VM.FlushSaveAsync();
        }
        finally { exportOpen = false; }
    }
    private void More(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var menu = button.ContextMenu;
        foreach (var item in menu.Items.OfType<MenuItem>())
            if (int.TryParse(item.Tag as string, out int sort)) item.IsChecked = VM.SortIndex == sort;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }
    private void SortPhotos(object sender, RoutedEventArgs e)
    {
        if (!VM.Busy && sender is MenuItem item && int.TryParse(item.Tag as string, out int sort))
            VM.SortIndex = sort;
    }
    private void ThumbnailMenuOpened(object sender,RoutedEventArgs e)
    {
        var menu=(ContextMenu)sender;if((menu.PlacementTarget as FrameworkElement)?.Tag is not Photo p){menu.IsOpen=false;return;}
        menu.Items.Clear();menu.Items.Add(new MenuItem{Header=p.Name,IsEnabled=false,ToolTip=p.JpgPath});
        var show=new MenuItem{Header="打开文件所在位置",Tag=p.Id,ToolTip="在文件资源管理器中定位此 JPG。"};show.Click+=(_,_)=>ShowPhotoLocation(p.Id);menu.Items.Add(show);
    }
    public void ShowPhotoLocation(string id)
    {
        var p=VM.Task?.Photos.FirstOrDefault(p=>p.Id==id);if(p==null)return;
        if(!File.Exists(p.JpgPath)){VM.Status="照片文件已不存在，请刷新扫描或重新定位："+p.JpgPath;return;}
        try{Process.Start(new ProcessStartInfo("explorer.exe"){Arguments="/select,\""+p.JpgPath+"\"",UseShellExecute=true});}
        catch(Exception ex){VM.Status="无法打开文件位置："+ex.Message;}
    }
    private async void Relocate(object sender,RoutedEventArgs e)
    {
        if(VM.Current==null||VM.Task==null||VM.Busy)return;var d=new OpenFileDialog{Filter="JPG 照片|*.jpg;*.jpeg",Title="重新定位当前 JPG（保留评分）"};if(d.ShowDialog(this)!=true)return;
        if(VM.Task.Photos.Any(p=>p!=VM.Current&&PathRules.Same(p.JpgPath,d.FileName))){MessageBox.Show(this,"这个 JPG 已在本任务中。");return;}
        var p=VM.Current;var old=p.JpgPath;p.OriginalJpgPath??=old;p.JpgPath=d.FileName;var f=new FileInfo(d.FileName);VM.Task.Files.RemoveAll(x=>PathRules.Same(x.Path,old));VM.Task.Files.Add(new(f.FullName,f.Length,f.LastWriteTimeUtc,false));if(p.RawPath!=null)p.PairState=PairState.ConfirmedPair;
        p.NotifyAll();VM.Current=null;VM.Current=p;VM.Refresh();await VM.FlushSaveAsync();
    }
    private async void ShowSettings(object sender, RoutedEventArgs e)
    {
        if (closing || closePending || exportOpen) return;
        bool installed = UpdateInstallation.IsInstalled();
        var updates = new UpdateService(VM.Store, App.CurrentVersion, installed, UpdateInstallation.UpdateSource(),
            release: UpdateInstallation.ReleaseSource(), signingCertificate: UpdateInstallation.SigningCertificate());
        var dialog = new SettingsWindow(VM.Store, Shortcuts, () => !VM.Busy, updates, package => PrepareUpdateAsync(updates, package),
            installed ? () => PrepareAppHandoffAsync(() => Task.Run(UpdateInstallation.StartUninstall), "卸载") : null)
        { Owner = this };
        if (dialog.ShowDialog() == true && closing) { Close(); return; }
        if (dialog.HistoryChanged) await VM.InitializeAsync();
    }

    internal Task<string?> PrepareUpdateAsync(UpdateService updates, DownloadedUpdate package) =>
        PrepareAppHandoffAsync(() => updates.LaunchInstallerAsync(package), "安装更新");

    private async Task<string?> PrepareAppHandoffAsync(Func<Task> launch, string action)
    {
        if (exportOpen) return $"请先完成导出并关闭导出窗口，再{action}。";
        if (VM.Busy) return $"当前仍在扫描或处理照片。请等待操作结束后再{action}。";
        if (closePending || closing) return "软件正在关闭，请稍后重试。";
        closePending = true;
        RootLayout.IsEnabled = false;
        try
        {
            var failure = await SaveBeforeCloseAsync();
            if (failure != null) return failure;
            await launch();
            closing = true;
            return null;
        }
        finally
        {
            closePending = false;
            if (!closing) RootLayout.IsEnabled = true;
        }
    }

    private void LoadShortcuts()
    {
        try { Shortcuts.Load(VM.Store); }
        catch (Exception ex) { VM.Status = "快捷键读取失败，已使用默认键位。" + ex.Message; }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (closePending) return;
        var gesture = ShortcutGesture.From(e);
        if (Shortcuts.IsEditing(Keyboard.FocusedElement as DependencyObject, gesture.Key)
            || Descendants<ComboBox>(this).Any(c => c.IsDropDownOpen)) return;
        if (gesture == new ShortcutGesture(Key.Escape) && fullScreen)
        {
            SetFullScreen(false);
            e.Handled = true;
            return;
        }
        if (VM.Busy || !VM.HasTask || !Shortcuts.TryResolve(gesture, out var action)) return;
        if (!e.IsRepeat || action is ShortcutAction.Previous or ShortcutAction.Next or ShortcutAction.ZoomIn or ShortcutAction.ZoomOut)
            Execute(action);
        e.Handled = true;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        if (closePending || OwnedWindows.OfType<Window>().Any(window => window.IsVisible)) return;
        closePending = true;
        Dispatcher.BeginInvoke(new Action(ResolveCloseRequest));
    }

    private async void ResolveCloseRequest()
    {
        try
        {
            var prompt = new ClosePrompt(this);
            prompt.ShowDialog();
            if (prompt.Choice == CloseChoice.Cancel) return;
            if (prompt.Choice == CloseChoice.Minimize)
            {
                WindowState = WindowState.Minimized;
                return;
            }
            if (VM.Busy)
            {
                VM.PropertyChanged -= CloseCancellationFinished;
                VM.PropertyChanged += CloseCancellationFinished;
                VM.OperationCancellation?.Cancel();
                VM.Status = "正在取消当前操作，请在停止后关闭";
                return;
            }

            RootLayout.IsEnabled = false;
            var failure = await SaveBeforeCloseAsync();
            if (failure != null)
            {
                MessageBox.Show(this, failure, "舒舒筛");
                return;
            }
            closing = true;
            Close();
        }
        finally
        {
            closePending = false;
            if (!closing) RootLayout.IsEnabled = true;
        }
    }

    private async Task<string?> SaveBeforeCloseAsync()
    {
        if (!await VM.FlushSaveAsync()) return VM.SaveStatus + "\n记录尚未保存，请检查磁盘后重试。";
        if (!await SaveLayout()) return VM.Status + "\n请检查磁盘后重试。";
        return null;
    }

    private void CloseCancellationFinished(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VM.Busy) || VM.Busy) return;
        VM.PropertyChanged -= CloseCancellationFinished;
        VM.Status += " · 可再次关闭软件";
    }

    private static void Pulse(UIElement element, double start, int milliseconds)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(start, 1, TimeSpan.FromMilliseconds(milliseconds))
        { FillBehavior = FillBehavior.Stop });
    }

    private void ToggleLeft(object sender, RoutedEventArgs e)
    {
        leftCollapsed = !leftCollapsed;
        ApplyPanelLayout();
        if (!leftCollapsed) Pulse(LeftPanel, .7, 150);
        _ = SaveLayout();
    }

    private void ToggleRight(object sender, RoutedEventArgs e)
    {
        rightCollapsed = !rightCollapsed;
        ApplyPanelLayout();
        if (!rightCollapsed) Pulse(RightPanel, .7, 150);
        _ = SaveLayout();
    }
    private void LayoutDragged(object sender,DragCompletedEventArgs e)
    {
        if(fullScreen)return;if(!leftCollapsed&&LeftColumn.ActualWidth>0)leftWidth=LeftColumn.ActualWidth;if(!rightCollapsed&&RightColumn.ActualWidth>0)rightWidth=RightColumn.ActualWidth;
        filmHeight=FilmRow.Height.Value;toolsHeight=ToolsRow.Height.Value;VM.FilmHeight=filmHeight;UpdateMinimumWidth();_=SaveLayout();
    }
    private void UpdateMinimumWidth()=>MinWidth=Math.Max(1080,(leftCollapsed?0:leftWidth+6)+(rightCollapsed?0:rightWidth+6)+368);
    private double BottomBudget=>TaskArea.ActualHeight-12-210;
    private void FilmResized(object sender,DragDeltaEventArgs e)
    {
        if(fullScreen)return;filmHeight=Math.Clamp(FilmRow.Height.Value-e.VerticalChange,88,Math.Max(88,Math.Min(360,BottomBudget-toolsHeight)));FilmRow.Height=new(filmHeight);VM.FilmHeight=filmHeight;
    }
    private void ToolsResized(object sender,DragDeltaEventArgs e)
    {
        if (fullScreen) return;
        toolsHeight = Math.Clamp(ToolsRow.Height.Value - e.VerticalChange, MinToolsHeight,
            Math.Max(MinToolsHeight, Math.Min(180, BottomBudget - filmHeight)));
        ApplyToolsHeight();
    }
    private void ApplyToolsHeight()
    {
        ToolsRow.Height = new(toolsHeight);
        var orientation = toolsHeight < CompactToolsHeight ? Orientation.Horizontal : Orientation.Vertical;
        if (ToolRows.Orientation == orientation) return;
        ToolRows.Orientation = orientation;
        ToolbarScroll.ScrollToHorizontalOffset(0);
    }
    private void ClampBottomHeights()
    {
        if(fullScreen||VM==null||!VM.HasTask||TaskArea.ActualHeight<1)return;
        double budget=BottomBudget;
        if(filmHeight+toolsHeight>budget)
        {
            filmHeight=Math.Max(88,Math.Min(filmHeight,budget-toolsHeight));toolsHeight=Math.Max(MinToolsHeight,Math.Min(toolsHeight,budget-filmHeight));
            FilmRow.Height=new(filmHeight);ApplyToolsHeight();VM.FilmHeight=filmHeight;
        }
    }
    private void ApplyPanelLayout()
    {
        LeftColumn.MinWidth=leftCollapsed?0:160;LeftColumn.Width=new(leftCollapsed?0:leftWidth);LeftSplitColumn.Width=new(leftCollapsed?0:6);LeftPanel.Visibility=leftCollapsed?Visibility.Collapsed:Visibility.Visible;
        RightColumn.MinWidth=rightCollapsed?0:220;RightColumn.Width=new(rightCollapsed?0:rightWidth);RightSplitColumn.Width=new(rightCollapsed?0:6);RightPanel.Visibility=rightCollapsed?Visibility.Collapsed:Visibility.Visible;
        LeftToggle.Content=leftCollapsed?"▶":"◀";RightToggle.Content=rightCollapsed?"◀":"▶";
        FilmRow.Height=new(filmHeight);ApplyToolsHeight();VM.FilmHeight=filmHeight;UpdateMinimumWidth();ClampBottomHeights();
        if (VM.Busy) UpdateNotices();
    }
    private void LoadLayout()
    {
        try
        {
            var json=VM.Store.GetSetting("layout-v3")??VM.Store.GetSetting("layout-v2");
            if(json!=null){var l=JsonSerializer.Deserialize<WorkspaceLayout>(json)!;leftWidth=Math.Clamp(l.Left,160,420);rightWidth=Math.Clamp(l.Right,220,440);filmHeight=Math.Clamp(l.Film,88,360);toolsHeight=Math.Clamp(l.Tools,MinToolsHeight,180);leftCollapsed=l.LeftCollapsed;rightCollapsed=l.RightCollapsed;}ApplyPanelLayout();
        }
        catch(Exception ex){VM.Status="布局恢复失败，使用默认布局："+ex.Message;}
    }
    private async Task<bool> SaveLayout()
    {
        if (fullScreen || VM == null) return true;
        var json = JsonSerializer.Serialize(new WorkspaceLayout(leftWidth,rightWidth,filmHeight,leftCollapsed,rightCollapsed,toolsHeight));
        await layoutSaveLock.WaitAsync();
        try
        {
            await Task.Run(() => VM.Store.SetSetting("layout-v3", json));
            return true;
        }
        catch(Exception ex) { VM.Status = "布局保存失败：" + ex.Message; return false; }
        finally { layoutSaveLock.Release(); }
    }
    public void SetFullScreen(bool enabled)
    {
        if(enabled==fullScreen||!VM.HasTask)return;
        if(enabled)
        {
            normalBounds=RestoreBounds;normalState=WindowState;fullScreenReturnView=VM.ViewIndex;
            if(VM.IsGrid)VM.ViewIndex=0;
            fullScreen=true;PhotoViewer.SetSpinnerInset(Workspace, 42);
            HeaderBar.Visibility=Toolbar.Visibility=Film.Visibility=LeftPanel.Visibility=RightPanel.Visibility=LeftSplitter.Visibility=RightSplitter.Visibility=FilmSplitter.Visibility=ToolsSplitter.Visibility=LeftToggle.Visibility=RightToggle.Visibility=Visibility.Collapsed;
            HeaderRow.Height=ToolsRow.Height=FilmSplitRow.Height=ToolsSplitRow.Height=new(0);ToolsRow.MinHeight=0;FilmRow.MinHeight=0;LeftEdgeColumn.Width=RightEdgeColumn.Width=new(0);FilmRow.Height=new(0);LeftColumn.MinWidth=RightColumn.MinWidth=0;LeftColumn.Width=RightColumn.Width=LeftSplitColumn.Width=RightSplitColumn.Width=new(0);
            MinWidth=MinHeight=0;WindowState=WindowState.Normal;WindowChrome.SetWindowChrome(this, null);WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
            IntPtr hwnd=new WindowInteropHelper(this).Handle;var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};GetMonitorInfo(MonitorFromWindow(hwnd,2),ref info);SetWindowPos(hwnd,IntPtr.Zero,info.Monitor.Left,info.Monitor.Top,info.Monitor.Right-info.Monitor.Left,info.Monitor.Bottom-info.Monitor.Top,0x64);
            FullScreenTools.Visibility=FullScreenStatus.Visibility=Visibility.Visible;fullScreenTimer.Start();
        }
        else
        {
            fullScreen=false;PhotoViewer.SetSpinnerInset(Workspace, 0);
            fullScreenTimer.Stop();FullScreenTools.Visibility=FullScreenStatus.Visibility=Visibility.Collapsed;WindowStyle=WindowStyle.SingleBorderWindow;ResizeMode=ResizeMode.CanResize;WindowChrome.SetWindowChrome(this, darkChrome);MinWidth=1080;MinHeight=720;
            Left=normalBounds.Left;Top=normalBounds.Top;Width=normalBounds.Width;Height=normalBounds.Height;WindowState=normalState;
            HeaderBar.Visibility=Toolbar.Visibility=Film.Visibility=LeftSplitter.Visibility=RightSplitter.Visibility=FilmSplitter.Visibility=ToolsSplitter.Visibility=LeftToggle.Visibility=RightToggle.Visibility=Visibility.Visible;
            HeaderRow.Height=new(40);FilmSplitRow.Height=ToolsSplitRow.Height=new(6);FilmRow.MinHeight=88;ToolsRow.MinHeight=MinToolsHeight;LeftEdgeColumn.Width=RightEdgeColumn.Width=new(16);ApplyPanelLayout();
            if(fullScreenReturnView==1&&VM.IsLarge)VM.ViewIndex=1;if(VM.IsGrid)RestoreGrid();
        }
    }
    private void WindowMouseMove(object sender,MouseEventArgs e){if(!fullScreen)return;if(e.GetPosition(this).Y<85){FullScreenTools.Visibility=Visibility.Visible;fullScreenTimer.Stop();fullScreenTimer.Start();}}
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct MonitorInfo{public int Size;public NativeRect Monitor,Work;public uint Flags;}
    [DllImport("user32.dll")]private static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect bounds);
    [DllImport("user32.dll",CharSet=CharSet.Auto)]private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int w,int h,uint flags);
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T t)yield return t;foreach(var other in Descendants<T>(child))yield return other;}}
}
