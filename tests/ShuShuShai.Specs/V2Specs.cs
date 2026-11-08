using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using ShuShuShai.Core;
using ShuShuShai.App;
using ShuShuShai.App.Controls;
using ShuShuShai.App.ViewModels;
using ShuShuShai.App.Views;

internal static partial class Program
{
    private static async Task V2Specs()
    {
        await Check("v2 精确选择只扫描每层，父子来源仍去重",async()=>
        {
            var root=Dir("精确目录");var a=Path.Combine(root,"甲");var b=Path.Combine(a,"更深层");Directory.CreateDirectory(b);
            Write(root,"1.JPG");Write(a,"2.JPG");Write(b,"3.JPG");
            var t=new PhotoTask{Sources=[new(root,false,[root,a]),new(a,false,[a])]};await new ScanService().ScanAsync(t,[],null,default);Is(t.Photos.Count==2&&t.Photos.All(p=>p.Name!="3.JPG"));
        });
        await Check("v2 子目录发现、默认未选、一键全选与部分选择",async()=>
        {
            var root=Dir("导入树");Directory.CreateDirectory(Path.Combine(root,"甲","更深层"));Directory.CreateDirectory(Path.Combine(root,"乙"));
            var dialog=new ImportWindow();await dialog.AddRootAsync(root);var n=dialog.FoldersTree.Roots.Single();Is(n.IsSelected&&n.Descendants().All(x=>!x.IsSelected));
            n.ToggleAllChildren();Is(n.AllChildrenSelected==true&&dialog.Folders.Single().Directories!.Count==4);n.Descendants().First().IsSelected=false;Is(n.AllChildrenSelected==null);dialog.Close();
        });
        await Check("v2 已知导出目录发现时跳过并说明",async()=>
        {
            var root=Dir("发现跳过");var output=Path.Combine(root,"输出");Directory.CreateDirectory(output);var inventory=await DirectoryDiscovery.DiscoverAsync(root,[output],default);Is(!inventory.Directories.Contains(output)&&inventory.Diagnostics.Count==1);
            var dialog=new ImportWindow([root]);await dialog.AddRootAsync(root);Is(dialog.Folders.Count==0&&!dialog.FoldersTree.Roots[0].Selectable);dialog.Close();
        });
        await Check("v2 重叠目录勾选同步与目录发现取消",async()=>
        {
            var root=Dir("重叠树");var child=Path.Combine(root,"子目录");Directory.CreateDirectory(child);var dialog=new ImportWindow();await dialog.AddRootAsync(root);await dialog.AddRootAsync(child);
            var parentChild=dialog.FoldersTree.Roots[0].Descendants().Single();Is(parentChild.IsSelected);parentChild.IsSelected=false;Is(!dialog.FoldersTree.Roots[1].IsSelected);dialog.Close();
            using var cancel=new CancellationTokenSource();cancel.Cancel();bool stopped=false;try{await DirectoryDiscovery.DiscoverAsync(root,[],cancel.Token);}catch(OperationCanceledException){stopped=true;}Is(stopped);
        });
        await Check("v2 多来源只按 JPG 导入目录，移动后归属保留",()=>
        {
            var p=new Photo{JpgPath=MockPhotoPath("D", @"JPG\A.JPG"),RawPath=MockPhotoPath("D", @"RAW\A.CR3")};var f=new FilterSettings{SpecificSources=true,Directories=[MockPhotoPath("D", @"RAW")]};Is(!f.Matches(p));f.Directories=[MockPhotoPath("D", @"JPG")];Is(f.Matches(p));
            p.OriginalJpgPath=p.JpgPath;p.JpgPath=MockPhotoPath("E", @"输出\新名.JPG");Is(f.Matches(p));f.Directories=[];Is(!f.Matches(p));
        });
        await Check("v2 历史列出全部主目录并六宫格预览",()=>
        {
            var t=new PhotoTask{Sources=[new(MockPhotoPath("D", @"外滩"),false),new(MockPhotoPath("E", @"苏州"),false)],Photos=Enumerable.Range(1,10).Select(i=>new Photo{JpgPath=MockPhotoPath("D", $@"外滩\{i}.JPG")}).ToList()};
            Is(t.SourceTitle=="外滩 ＋ 苏州"&&t.HistoryPreviews.Count==6&&t.SourceDetails.Contains("首个来源"));
        });
        await Check("v2 按星级导出完全忽略保留淘汰，标记模式完全忽略星级",()=>
        {
            var t=new PhotoTask{Photos=[new(){Stars=5,Decision=Decision.Reject},new(){Stars=0,Decision=Decision.Keep},new(){Stars=4,Decision=Decision.Undecided},new(){Stars=2,Decision=Decision.Reject}]};
            var o=new ExportOptions{RangeMode=ExportRangeMode.ByStars,StarComparison=2,StarValue=3};Is(ExportPlanner.SelectPhotos(t,o).Count==2&&ExportPlanner.SelectPhotos(t,o).Any(p=>p.Decision==Decision.Reject));
            o.RangeMode=ExportRangeMode.ByDecision;Is(ExportPlanner.SelectPhotos(t,o).Single().Stars==0);o.Decisions=[Decision.Reject,Decision.Undecided];Is(ExportPlanner.SelectPhotos(t,o).Count==3);
        });
        await Check("v2 导出星级等于/小于等于/未评分条件",()=>
        {
            var t=new PhotoTask{Photos=Enumerable.Range(0,6).Select(i=>new Photo{Stars=i}).ToList()};var o=new ExportOptions{RangeMode=ExportRangeMode.ByStars,StarValue=3,StarComparison=4};Is(ExportPlanner.SelectPhotos(t,o).Single().Stars==3);o.StarComparison=3;Is(ExportPlanner.SelectPhotos(t,o).Count==3);o.IncludeUnrated=true;Is(ExportPlanner.SelectPhotos(t,o).Count==4);
        });
        await Check("v2 三种命名模式只应用当前模式配置",async()=>
        {
            var x=await Plan();var o=new ExportOptions{NamingMode=ExportNamingMode.Original,Shared=new(){Rename=true,Prefix="不应使用",Start=9},Jpg=new(){Rename=true,Prefix="J",Start=5}};
            Is(ExportPlanner.Build(x.Task,o).Files.All(f=>Path.GetFileName(f.Source)==Path.GetFileName(f.Target)));o.NamingMode=ExportNamingMode.Linked;o.Shared.Rename=false;o.Shared.Prefix="共同";Is(ExportPlanner.Build(x.Task,o).Files.All(f=>Path.GetFileName(f.Target).StartsWith("共同_")));
            o.NamingMode=ExportNamingMode.Independent;var p=ExportPlanner.Build(x.Task,o);Is(p.Files.Where(f=>!f.IsRaw).All(f=>Path.GetFileName(f.Target).StartsWith("J_"))&&p.Files.Where(f=>f.IsRaw).All(f=>Path.GetFileName(f.Source)==Path.GetFileName(f.Target)));
        });
        await Check("v2 冲突只阻挡本次范围，执行和恢复使用固定计划",async()=>
        {
            var x=await Plan();x.Task.Photos[0].Stars=5;x.Task.Photos[0].Decision=Decision.Reject;x.Task.Photos[1].PairState=PairState.Conflict;
            var o=new ExportOptions{RangeMode=ExportRangeMode.ByStars,StarValue=5,StarComparison=4,NamingMode=ExportNamingMode.Original};var p=ExportPlanner.Build(x.Task,o);Is(p.Files.Count==2);x.Task.Photos[0].Stars=0;
            await new ExportService(x.Store).RunAsync(p,null,default);Is(p.Complete&&p.Files.All(f=>File.Exists(f.Target)));
        });
        await Check("v2 旧任务一致备份、迁移精确目录与命名，身份评分不变",()=>
        {
            var root=Dir("旧库");var store=new TaskStore(root);var t=new PhotoTask{Sources=[new(MockPhotoPath("D", @"旧任务"),true)],Files=[F(MockPhotoPath("D", @"旧任务\子目录\a.JPG"))],Photos=[new(){Id="stable",JpgPath=MockPhotoPath("D", @"旧任务\子目录\a.JPG"),Stars=5,PairState=PairState.ConfirmedJpgOnly}],CurrentId="stable",ExportOptions=new(){Linked=false,Jpg=new(){Rename=true,Prefix="旧名"}}};store.Save(t);
            var json=JsonNode.Parse(JsonSerializer.Serialize(t))!.AsObject();json.Remove("SchemaVersion");
            using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"tasks.db"),Pooling=false}.ToString())){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE tasks SET json=$json";cmd.Parameters.AddWithValue("$json",json.ToJsonString());cmd.ExecuteNonQuery();}
            var migrated=store.LoadAll().Single();Is(migrated.Sources[0].Directories!.Contains(MockPhotoPath("D", @"旧任务\子目录"))&&migrated.Photos[0].Id=="stable"&&migrated.Photos[0].Stars==5&&migrated.CurrentId=="stable"&&migrated.ExportOptions.NamingMode==ExportNamingMode.Independent);
            Is(Directory.GetFiles(Path.Combine(root,"backups")).Length==1);store.LoadAll();Is(Directory.GetFiles(Path.Combine(root,"backups")).Length==1);
        });
        await Check("v2 迁移事务失败保留原任务和导出记录",()=>
        {
            var root=Dir("迁移回滚");var store=new TaskStore(root);var task=new PhotoTask{SchemaVersion=1,Photos=[new(){Id="保留ID",Stars=4}]};store.Save(task);store.SaveExport(new(){Id="原导出",TaskId=task.Id});
            using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"tasks.db"),Pooling=false}.ToString())){db.Open();using var command=db.CreateCommand();command.CommandText="CREATE TRIGGER block_migration BEFORE UPDATE ON tasks BEGIN SELECT RAISE(ABORT,'fault'); END;";command.ExecuteNonQuery();}
            bool blocked=false;try{store.LoadAll();}catch(SqliteException){blocked=true;}Is(blocked&&Directory.GetFiles(Path.Combine(root,"backups")).Length==1&&store.LoadExports().Single().Id=="原导出");
            using var original=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"tasks.db"),Pooling=false}.ToString());original.Open();using var query=original.CreateCommand();query.CommandText="SELECT json FROM tasks";var kept=JsonSerializer.Deserialize<PhotoTask>((string)query.ExecuteScalar()!)!;Is(kept.SchemaVersion==1&&kept.Photos.Single().Id=="保留ID"&&kept.Photos.Single().Stars==4);
        });
        await Check("v2 全视图批量、Ctrl+A、对比联动与隐藏选择清除",async()=>
        {
            var root=Dir("新选片");string sample=Path.Combine(root,"1.JPG");Jpeg(sample,400,300);for(int i=2;i<=15;i++)File.Copy(sample,Path.Combine(root,$"{i}.JPG"));
            using var vm=new MainViewModel(new TaskStore(Dir("选片库")));await vm.ImportAsync([new(root,false,[root])],true);
            vm.SelectAll();Is(vm.SelectedCount==15);vm.ViewIndex=0;vm.Apply(stars:4);Is(vm.Task!.Photos.All(p=>p.Stars==4));vm.Undo();Is(vm.Task.Photos.All(p=>p.Stars==0));
            vm.ClearSelection();vm.Apply(decision:Decision.Reject);Is(vm.Task.Photos.All(p=>p.Decision==Decision.Undecided));
            vm.Select(vm.Visible[0],false,false);vm.Select(vm.Visible[1],true,false);Is(vm.Compare()&&vm.IsDual);var reference=vm.Comparison[0];vm.Step(1);Is(vm.SelectedCount==2&&vm.Comparison[0]==reference&&vm.Comparison.All(p=>p.IsSelected));
            vm.CompareLinkedRating=true;vm.Apply(decision:Decision.Keep);Is(vm.Task.Photos.Count(p=>p.Decision==Decision.Keep)==2);vm.RemoveCompare(vm.Current!);Is(vm.IsLarge&&vm.SelectedCount==1);vm.Undo();Is(vm.Task.Photos.All(p=>p.Decision==Decision.Undecided));
            vm.SelectAll();vm.FilterMode=4;vm.FilterStars=5;Is(vm.Visible.Count==0&&vm.SelectedCount==0&&vm.Current==null);vm.FilterMode=0;
            vm.Select(vm.Visible[0],false,false);vm.Select(vm.Visible[8],false,true);Is(vm.Compare()&&vm.Comparison.Count==9);vm.SelectAll();Is(vm.IsGrid&&vm.SelectedCount==15);
        });
        await Check("v2 全屏单图/多图、面板尺寸和网格位置恢复",async()=>
        {
            var root=Dir("全屏");string sample=Path.Combine(root,"1.JPG");Jpeg(sample,400,300);for(int i=2;i<=15;i++)File.Copy(sample,Path.Combine(root,$"{i}.JPG"));
            Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR",Dir("全屏库"));var w=new MainWindow();w.Show();await Task.Delay(120);await w.VM.ImportAsync([new(root,false,[root])],true);
            await Task.Delay(150);w.UpdateLayout();
            w.VM.ViewIndex=1;var left=(ColumnDefinition)w.FindName("LeftColumn");var right=(ColumnDefinition)w.FindName("RightColumn");double l=left.ActualWidth,r=right.ActualWidth;
            var filmRow=(RowDefinition)w.FindName("FilmRow");double originalFilm=filmRow.ActualHeight;
            var divider=(System.Windows.Controls.Primitives.Thumb)w.FindName("FilmSplitter");divider.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0,-45){RoutedEvent=System.Windows.Controls.Primitives.Thumb.DragDeltaEvent});w.UpdateLayout();
            Is(filmRow.ActualHeight>originalFilm+30&&Math.Abs(w.VM.FilmHeight-filmRow.ActualHeight)<2,"胶片条高度边界必须实际改变预览条");
            var histogram=(FrameworkElement)w.FindName("HistogramSection");var point=histogram.TranslatePoint(new(0,0),w);var details=(ScrollViewer)w.FindName("DetailsScroll");details.ScrollToEnd();w.UpdateLayout();Is(histogram.TranslatePoint(new(0,0),w)==point,"直方图必须独立于下方信息滚动");
            var film=(ListBox)w.FindName("Film");var filmScroll=MainWindow.Descendants<ScrollViewer>(film).First();filmScroll.ScrollToHorizontalOffset(0);await Task.Delay(60);int selected=w.VM.SelectedCount;
            film.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=UIElement.PreviewMouseWheelEvent});await Task.Delay(60);Is(filmScroll.HorizontalOffset>0&&selected==w.VM.SelectedCount,"滚轮应横向滚动且不改变选中");
            w.SetFullScreen(true);await Task.Delay(100);Is(w.IsFullScreen&&w.VM.IsLarge&&w.WindowStyle==WindowStyle.None);w.SetFullScreen(false);await Task.Delay(100);Is(!w.IsFullScreen&&w.VM.IsGrid&&Math.Abs(left.ActualWidth-l)<2&&Math.Abs(right.ActualWidth-r)<2);
            w.VM.Select(w.VM.Visible[0],false,false);w.VM.Select(w.VM.Visible[2],false,true);w.VM.Compare();var ids=w.VM.Comparison.Select(p=>p.Id).ToArray();w.SetFullScreen(true);await Task.Delay(100);Is(w.VM.Comparison.Count==3);w.SetFullScreen(false);Is(w.VM.Comparison.Select(p=>p.Id).SequenceEqual(ids)&&w.VM.SelectedCount==3);
            await ConfirmExitAsync(w);Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR",null);
        });
        await Check("v2 面板和胶片条保存重开，工作区最小宽度保留",async()=>
        {
            var data=Dir("布局保存");Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR",data);var w=new MainWindow();w.Show();await Task.Delay(150);var photos=Dir("布局照片");Jpeg(Path.Combine(photos,"布局.JPG"),400,300);await w.VM.ImportAsync([new(photos,false,[photos])],true);await Task.Delay(150);w.UpdateLayout();
            var left=(ColumnDefinition)w.FindName("LeftColumn");var right=(ColumnDefinition)w.FindName("RightColumn");var film=(RowDefinition)w.FindName("FilmRow");left.Width=new(410);right.Width=new(430);film.Height=new(180);w.UpdateLayout();
            ((System.Windows.Controls.Primitives.Thumb)w.FindName("LeftSplitter")).RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0,0,false){RoutedEvent=System.Windows.Controls.Primitives.Thumb.DragCompletedEvent});await Task.Delay(100);await ConfirmExitAsync(w);
            var reopened=new MainWindow();reopened.Show();await Task.Delay(200);Is(Math.Abs(((ColumnDefinition)reopened.FindName("LeftColumn")).Width.Value-410)<2&&Math.Abs(((ColumnDefinition)reopened.FindName("RightColumn")).Width.Value-430)<2&&Math.Abs(((RowDefinition)reopened.FindName("FilmRow")).Height.Value-180)<2&&reopened.MinWidth>=1180,$"保存值 {reopened.VM.Store.GetSetting("layout-v2")}；重开 {((ColumnDefinition)reopened.FindName("LeftColumn")).Width.Value}/{((ColumnDefinition)reopened.FindName("RightColumn")).Width.Value}/{((RowDefinition)reopened.FindName("FilmRow")).Height.Value} Min={reopened.MinWidth}");
            await ConfirmExitAsync(reopened);Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR",null);
        });
        await Check("v2 导出界面范围和三模式可以生成真实计划",async()=>
        {
            var x=await Plan();foreach(var p in x.Task.Photos){p.Decision=Decision.Reject;p.Stars=5;}
            var w=new ExportWindow(x.Task,x.Store);w.Show();await Task.Delay(150);w.Options.RangeMode=ExportRangeMode.ByStars;w.Options.StarComparison=4;w.Options.StarValue=5;w.Options.NamingMode=ExportNamingMode.Linked;w.Options.Shared.Prefix="界面联动";
            await w.BuildAsync();Is(w.CurrentPlan?.Files.Count==5&&w.CurrentPlan.Files.All(f=>Path.GetFileName(f.Target).StartsWith("界面联动_")));await w.ExecuteAsync();Is(w.CurrentPlan!.Complete&&w.CurrentPlan.Files.All(f=>File.Exists(f.Source)&&File.Exists(f.Target)));w.Close();
        });
    }
    private static async Task SeedReview(string root)
    {
        var a=Path.Combine(root,"外滩");var b=Path.Combine(root,"苏州");var raw=Path.Combine(b,"RAW");var deep=Path.Combine(a,"第二天","夜景");Directory.CreateDirectory(raw);Directory.CreateDirectory(deep);Fixtures(a);
        Jpeg(Path.Combine(b,"测试_0001.JPG"),1200,800);Jpeg(Path.Combine(deep,"夜景.JPG"),900,600);
        File.WriteAllText(Path.Combine(raw,"仅原始.CR3"),"Synthetic RAW");File.WriteAllText(Path.Combine(raw,"夜景.CR3"),"Synthetic RAW");File.WriteAllText(Path.Combine(raw,"夜景.NEF"),"Synthetic RAW");
        var t=new PhotoTask{Sources=[new(a,false,[a,deep]),new(b,false,[b,raw])]};await new ScanService().ScanAsync(t,[],null,default);
        foreach(var p in t.Photos){p.Stars=p.Name.Contains("0001")?5:3;p.Decision=Decision.Keep;}t.CurrentId=t.Photos.First(p=>p.Name=="测试_0001.JPG").Id;t.ViewIndex=1;
        var store=new TaskStore(Path.Combine(root,"data"));store.Save(t);Console.WriteLine("REVIEW_DATA "+store.DataDirectory);
    }
    private static async Task RenderReview(string root)
    {
        Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR",Path.Combine(root,"data"));var shots=Path.Combine(root,"renders");Directory.CreateDirectory(shots);
        async Task Shot(Window window,string name)
        {
            await Task.Delay(500);window.UpdateLayout();var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(window);
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(window.ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(window);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(shots,name+".png"));encoder.Save(stream);Console.WriteLine("RENDER "+name);
        }
        var main=new MainWindow();main.Show();await Task.Delay(300);await Shot(main,"01-home");await main.VM.OpenAsync(main.VM.History.First());main.VM.ViewIndex=1;await Shot(main,"02-grid");
        main.VM.Select(main.VM.Visible[2],false,false);main.VM.Select(main.VM.Visible[3],true,false);main.VM.Compare();await Shot(main,"03-compare-two");main.VM.SetSourceDirectories([]);
        main.VM.Select(main.VM.Visible[4],true,false);main.SetFullScreen(true);await Shot(main,"04-fullscreen-multi");main.SetFullScreen(false);main.VM.ViewIndex=0;await Shot(main,"05-large");
        var import=new ImportWindow(){Owner=main};import.Show();await import.AddRootAsync(Path.Combine(root,"外滩"));import.FoldersTree.Roots[0].Descendants().Last().IsSelected=true;await Shot(import,"06-import-tree");import.Close();
        var pair=new PairingWindow(main.VM.Task!,null){Owner=main};pair.Show();await Shot(pair,"07-pairing");pair.Close();
        var task=main.VM.Task!;foreach(var p in task.Photos.Where(p=>p.PairState==PairState.Conflict))PairingService.Confirm(task,p,null);
        task.ExportOptions.NamingMode=ExportNamingMode.Original;var export=new ExportWindow(task,main.VM.Store){Owner=main};export.Show();await export.BuildAsync();await Shot(export,"08-export-original");export.Close();
        task.ExportOptions.RangeMode=ExportRangeMode.ByStars;task.ExportOptions.StarValue=5;task.ExportOptions.NamingMode=ExportNamingMode.Independent;task.ExportOptions.Jpg.Rename=true;task.ExportOptions.Jpg.Prefix="精选";var separate=new ExportWindow(task,main.VM.Store){Owner=main};separate.Show();await separate.BuildAsync();await Shot(separate,"09-export-independent");separate.Close();
        var prompt=await ClosePromptAsync(main);await Shot(prompt,"10-close");ChooseClose(prompt,"CancelClose");
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await ConfirmExitAsync(main);Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR",null);
    }
}
