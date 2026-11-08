using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ShuShuShai.Core;
using ShuShuShai.App;
using ShuShuShai.App.Controls;
using ShuShuShai.App.Services;
using ShuShuShai.App.Views;

internal static partial class Program
{
    private static async Task<(MainWindow Window,string Photos)> V3Window(int count=9,bool high=false)
    {
        var photos=Dir("v3-照片");string first=Path.Combine(photos,"IMG_0001.JPG");Jpeg(first,high?6000:800,high?4000:600);
        for(int i=2;i<=count;i++)File.Copy(first,Path.Combine(photos,$"IMG_{i:D4}.JPG"));
        Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR",Dir("v3-数据"));var w=new MainWindow();w.Show();await Task.Delay(140);await w.VM.ImportAsync([new(photos,false,[photos])],true);await Task.Delay(180);w.UpdateLayout();return(w,photos);
    }
    private static async Task CloseV3(MainWindow w){await ConfirmExitAsync(w);Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR",Path.Combine(Root,"runtime-data"));}
    private static void BrokenProfileJpeg(string path)
    {
        Jpeg(path,320,240);var jpg=File.ReadAllBytes(path);byte[] profile=new byte[128];profile[0]=0;profile[1]=0;profile[2]=2;profile[3]=48;
        System.Text.Encoding.ASCII.GetBytes("mntrRGB XYZ ").CopyTo(profile,12);System.Text.Encoding.ASCII.GetBytes("acsp").CopyTo(profile,36);
        var payload=System.Text.Encoding.ASCII.GetBytes("ICC_PROFILE\0").Concat(new byte[]{1,1}).Concat(profile).ToArray();using var output=new MemoryStream();output.Write(jpg,0,2);output.WriteByte(255);output.WriteByte(226);output.WriteByte((byte)((payload.Length+2)>>8));output.WriteByte((byte)(payload.Length+2));output.Write(payload);output.Write(jpg,2,jpg.Length-2);File.WriteAllBytes(path,output.ToArray());
    }
    private static async Task V3Specs()
    {
        await Check("v3 评分取舍及配对按同一顺序连续撤回，空操作不占步",()=>
        {
            var t=Pair(MockPhotoPath("D", @"测试\A.JPG"),MockPhotoPath("D", @"测试\A.CR3"));var p=t.Photos.Single();var history=new TaskEditHistory();Is(!history.ApplyRatings([p],0,null)&&!history.CanUndo);
            history.ApplyRatings([p],4,null);history.ApplyPairing(t,"手工仅 JPG",()=>PairingService.Confirm(t,p,null));history.ApplyRatings([p],null,Decision.Keep);
            history.Undo(t);Is(p.Stars==4&&p.Decision==Decision.Undecided&&p.PairState==PairState.ConfirmedJpgOnly);history.Undo(t);Is(p.RawPath!=null&&p.PairState==PairState.Paired&&p.Stars==4&&t.RawOnly.Count==0);history.Undo(t);Is(p.Stars==0&&!history.CanUndo);
        });
        await Check("v3 自动配对只处理未解决项，保留正常及手工关系，整次可撤回",()=>
        {
            var t=Pair(MockPhotoPath("D", @"测试\A.JPG"),MockPhotoPath("D", @"测试\A.CR3"),MockPhotoPath("D", @"测试\B.JPG"),MockPhotoPath("D", @"测试\B.CR3"),MockPhotoPath("D", @"测试\C.JPG"),MockPhotoPath("D", @"测试\C.CR3"),MockPhotoPath("D", @"测试\E.JPG"),MockPhotoPath("D", @"测试\E.CR3"),MockPhotoPath("D", @"测试\E.NEF"));
            var a=t.Photos.Single(p=>p.Name=="A.JPG");var b=t.Photos.Single(p=>p.Name=="B.JPG");var c=t.Photos.Single(p=>p.Name=="C.JPG");PairingService.Confirm(t,c,null);b.RawPath=null;b.PairState=PairState.JpgOnly;t.RawOnly.Add(MockPhotoPath("D", @"测试\B.CR3"));a.Stars=5;
            var history=new TaskEditHistory();AutoPairSummary? result=null;history.ApplyPairing(t,"自动配对",()=>result=PairingService.AutoPair(t));Is(result!.Added==1&&result.Conflicts==1&&a.PairState==PairState.Paired&&c.PairState==PairState.ConfirmedJpgOnly&&b.RawPath!=null&&a.Stars==5);
            history.Undo(t);Is(b.RawPath==null&&c.PairState==PairState.ConfirmedJpgOnly&&a.Stars==5);
        });
        await Check("v3 v2 任务迁移备份，只重置选片未评分，不影响导出条件",()=>
        {
            var store=new TaskStore(Dir("v2迁移"));var t=new PhotoTask{SchemaVersion=2,CompareLinkedRating=true,Filter=new(){IncludeUnrated=true},ExportOptions=new(){IncludeUnrated=true},Photos=[new(){Id="稳定",Stars=5}]};store.Save(t);
            var migrated=store.LoadAll().Single();Is(migrated.SchemaVersion==3&&!migrated.CompareLinkedRating&&!migrated.Filter.IncludeUnrated&&migrated.ExportOptions.IncludeUnrated&&migrated.Photos[0].Id=="稳定"&&migrated.Photos[0].Stars==5&&Directory.GetFiles(Path.Combine(store.DataDirectory,"backups")).Length==1);
        });
        await Check("v3 历史六宫格不足补空位，不重复照片",()=>
        {
            var t=new PhotoTask{Photos=[new(){JpgPath=MockPhotoPath("D", @"2.JPG")},new(){JpgPath=MockPhotoPath("D", @"1.JPG")}]};var slots=t.HistoryPreviews;Is(slots.Count==6&&slots.Count(p=>p!=null)==2&&slots[0]!.Name=="1.JPG"&&slots.Skip(2).All(p=>p==null));
        });
        await Check("v3 ICC 异常仍能预览，警告随磁盘缓存恢复且原片不变",async()=>
        {
            var path=Path.Combine(Dir("坏ICC"),"配置异常.JPG");BrokenProfileJpeg(path);string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));string cache=Dir("兼容缓存");
            using(var service=new ImageService(cache))foreach(int width in new[]{480,1024,0}){var result=await service.LoadResultAsync(path,width,default);Is(result.Image.PixelWidth>0&&result.ColorWarning!=null);}
            using(var service=new ImageService(cache)){var cached=await service.LoadResultAsync(path,480,default);Is(cached.ColorWarning!=null&&service.DecodeCount==0);}
            Is(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))==hash);
        });
        await Check("v3 坏缓存从原 JPG 重建，真正损坏的 JPG 单独失败",async()=>
        {
            var root=Dir("坏缓存");var path=Path.Combine(root,"正常.JPG");Jpeg(path,800,600);var cache=Dir("磁盘缓存");string disk;
            using(var service=new ImageService(cache)){await service.LoadAsync(path,480,default);disk=service.CacheFilePath(path,480);}File.WriteAllText(disk,"broken cache");
            using(var service=new ImageService(cache)){Is((await service.LoadAsync(path,480,default)).PixelWidth==480&&service.DecodeCount==1);var broken=Write(root,"损坏.JPG",20);bool rejected=false;try{await service.LoadAsync(broken,480,default);}catch(Exception){rejected=true;}Is(rejected&&(await service.LoadAsync(path,480,default)).PixelWidth==480);}
            File.WriteAllText(disk,"broken read-only cache");File.SetAttributes(disk,FileAttributes.ReadOnly);try{using var locked=new ImageService(cache);Is((await locked.LoadAsync(path,480,default)).PixelWidth==480,"缓存不可覆盖不应导致原 JPG 无法预览");}finally{File.SetAttributes(disk,FileAttributes.Normal);}
        });
        await Check("v3 兼容预览恢复后将旧读取失败替换为色彩警告",async()=>
        {
            var (w,_)=await V3Window(1);try
            {
                var p=w.VM.Current!;BrokenProfileJpeg(p.JpgPath);w.VM.Task!.Diagnostics.Add(new(p.JpgPath,"JPG 读取失败：位图颜色上下文无效。"));await ImageService.Shared.LoadResultAsync(p.JpgPath,480,default);await Task.Delay(100);
                Is(!w.VM.Task.Diagnostics.Any(d=>d.Path==p.JpgPath&&d.Message.StartsWith("JPG 读取失败"))&&w.VM.Task.Diagnostics.Any(d=>d.Path==p.JpgPath&&d.Code=="preview-color"&&d.Severity==DiagnosticSeverity.Warning));
            }finally{await CloseV3(w);}
        });
        await Check("v3 同一解码请求合并，取消一个订阅不取消其他订阅",async()=>
        {
            var path=Path.Combine(Dir("请求合并"),"大图.JPG");Jpeg(path,3000,2000);using var service=new ImageService(Dir("合并缓存"));using var cancel=new CancellationTokenSource();
            var first=service.LoadResultAsync(path,2048,cancel.Token);var pending=Enumerable.Range(0,8).Select(_=>service.LoadResultAsync(path,2048,default)).ToArray();cancel.Cancel();try{await first;}catch(OperationCanceledException){}
            var results=await Task.WhenAll(pending);Is(service.DecodeCount==1&&results.All(p=>ReferenceEquals(p.Image,results[0].Image)));
        });
        await Check("v3 显式刷新重新读取同路径变化的 JPG",async()=>
        {
            var (w,_)=await V3Window(1);try
            {
                var viewer=(PhotoViewer)w.FindName("LargeViewer");w.SetSelectionZoom(1);await Task.Delay(100);var ready=new TaskCompletionSource();void Frame(PhotoViewer _,string path,int width){if(width==1000)ready.TrySetResult();}
                viewer.FrameAvailable+=Frame;Jpeg(w.VM.Current!.JpgPath,1000,700);await w.VM.ScanAsync();await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));await Task.Delay(100);viewer.FrameAvailable-=Frame;Is(w.VM.Histogram?.Pixels==700000,"刷新扫描后直方图也必须对应新内容");
            }finally{await CloseV3(w);}
        });
        await Check("v3 对比默认只改当前，联动同时控制评分和取舍，其他视图批量",async()=>
        {
            var (w,_)=await V3Window(4);try
            {
                var vm=w.VM;vm.Select(vm.Visible[0],false,false);vm.Select(vm.Visible[2],false,true);vm.Compare();Is(!vm.CompareLinkedRating);vm.Apply(stars:4);vm.Apply(decision:Decision.Keep);Is(vm.Task!.Photos.Count(p=>p.Stars==4)==1&&vm.Task.Photos.Count(p=>p.Decision==Decision.Keep)==1);
                vm.CompareLinkedRating=true;vm.Apply(stars:5);vm.Apply(decision:Decision.Reject);Is(vm.Task.Photos.Count(p=>p.Stars==5)==3&&vm.Task.Photos.Count(p=>p.Decision==Decision.Reject)==3);
                vm.Undo();vm.Undo();vm.Undo();vm.Undo();Is(vm.Task.Photos.All(p=>p.Stars==0&&p.Decision==Decision.Undecided));vm.ViewIndex=1;vm.SelectAll();vm.Apply(stars:3);vm.ViewIndex=0;vm.Apply(decision:Decision.Keep);Is(vm.Task.Photos.All(p=>p.Stars==3&&p.Decision==Decision.Keep));
            }finally{await CloseV3(w);}
        });
        await Check("v3 配对窗口与主窗口共用撤回栈，自动配对不扫描",async()=>
        {
            var (w,root)=await V3Window(2);try
            {
                var vm=w.VM;var raw=Write(root,"IMG_0001.CR3");vm.Task!.Files.Add(F(raw));vm.Apply(stars:2);var dialog=new PairingWindow(vm.Task,vm.Current,vm.EditHistory,()=>vm.Refresh()){Owner=w};dialog.Show();await dialog.AutoPairAsync();Is(vm.Task.Photos[0].RawPath==raw&&vm.Task.Photos.Count==2);dialog.Undo();Is(vm.Task.Photos[0].RawPath==null&&vm.Task.Photos[0].Stars==2);dialog.Undo();Is(vm.Task.Photos[0].Stars==0);dialog.Close();
            }finally{await CloseV3(w);}
        });
        await Check("v3 两条高度边界独立调整，保存重开且全屏恢复",async()=>
        {
            var (w,_)=await V3Window(9);string data=w.VM.Store.DataDirectory;try
            {
                var tools=(RowDefinition)w.FindName("ToolsRow");var film=(RowDefinition)w.FindName("FilmRow");double f=film.Height.Value;
                ((Thumb)w.FindName("ToolsSplitter")).RaiseEvent(new DragDeltaEventArgs(0,-20){RoutedEvent=Thumb.DragDeltaEvent});w.UpdateLayout();double t=tools.Height.Value;Is(t>96&&film.Height.Value==f);
                ((Thumb)w.FindName("FilmSplitter")).RaiseEvent(new DragDeltaEventArgs(0,-25){RoutedEvent=Thumb.DragDeltaEvent});w.UpdateLayout();Is(film.Height.Value>f&&tools.Height.Value==t);f=film.Height.Value;
                ((Thumb)w.FindName("FilmSplitter")).RaiseEvent(new DragCompletedEventArgs(0,0,false){RoutedEvent=Thumb.DragCompletedEvent});await Task.Delay(100);w.SetFullScreen(true);await Task.Delay(80);w.SetFullScreen(false);Is(tools.Height.Value==t&&film.Height.Value==f&&((ColumnDefinition)w.FindName("LeftEdgeColumn")).Width.Value==16);
                await CloseV3(w);Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR",data);var next=new MainWindow();next.Show();await Task.Delay(220);Is(Math.Abs(((RowDefinition)next.FindName("ToolsRow")).Height.Value-t)<1&&Math.Abs(((RowDefinition)next.FindName("FilmRow")).Height.Value-f)<1);await CloseV3(next);
            }finally{if(w.IsVisible)await CloseV3(w);}
        });
        await Check("v3 胶片条无文件名与选中字样，右击菜单不改变选择",async()=>
        {
            var (w,_)=await V3Window(5);try
            {
                w.VM.SelectAll();var ids=w.VM.SelectedPhotos.Select(p=>p.Id).ToArray();var film=(ListBox)w.FindName("Film");var texts=MainWindow.Descendants<TextBlock>(film).Select(t=>t.Text).ToList();Is(!texts.Any(t=>t.Contains("IMG_")||t.Contains("当前")||t.Contains("已选")));
                var caption=MainWindow.Descendants<TextBlock>(film).First(t=>t.Text.Contains("·"));Is(caption.TranslatePoint(new(0,caption.ActualHeight),film).Y<=film.ActualHeight-SystemParameters.HorizontalScrollBarHeight,"星级与取舍不能被水平滚动条遮挡");
                var border=MainWindow.Descendants<Border>(film).First(b=>b.Tag is Photo);var photo=(Photo)border.Tag;var menu=border.ContextMenu;menu.PlacementTarget=border;menu.IsOpen=true;await Task.Delay(80);Is(((MenuItem)menu.Items[0]).Header.ToString()==photo.Name&&((MenuItem)menu.Items[1]).Header.ToString()=="打开文件所在位置"&&w.VM.SelectedPhotos.Select(p=>p.Id).SequenceEqual(ids));menu.IsOpen=false;
                File.Delete(photo.JpgPath);w.ShowPhotoLocation(photo.Id);Is(w.VM.Status.Contains("不存在"));
            }finally{await CloseV3(w);}
        });
        await Check("v3 2/4/9 张一次缩放倍率相同，不累乘，不重建控件，平移不解码",async()=>
        {
            var (w,_)=await V3Window(9);try
            {
                foreach(int count in new[]{2,4,9})
                {
                    w.VM.ViewIndex=1;w.VM.Select(w.VM.Visible[0],false,false);w.VM.Select(w.VM.Visible[count-1],false,true);w.VM.Compare();await Task.Delay(250);w.UpdateLayout();var viewers=MainWindow.Descendants<PhotoViewer>((ItemsControl)w.FindName("ComparisonView")).ToArray();double scale=viewers.First(v=>v.Path==w.VM.Current!.JpgPath).EffectiveZoom;
                    w.ZoomSelection(1.25);Is(viewers.All(v=>Math.Abs(v.EffectiveZoom-scale*1.25)<.0001));Is(viewers.SequenceEqual(MainWindow.Descendants<PhotoViewer>((ItemsControl)w.FindName("ComparisonView"))));await Task.Delay(200);long reads=ImageService.Shared.DecodeCount;foreach(var v in viewers)v.Sync(v.Zoom,.1,.1);await Task.Delay(160);Is(reads==ImageService.Shared.DecodeCount);
                }
            }finally{await CloseV3(w);}
        });
    }
    private static async Task RealJpgReview(string folder)
    {
        using var service=new ImageService(Dir("真实样本只读缓存"));
        foreach(var path in Directory.EnumerateFiles(folder).Where(PathRules.IsJpg).Take(8))await Check("真实 JPG 只读："+Path.GetFileName(path),async()=>
        {
            string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));var stamp=File.GetLastWriteTimeUtc(path);
            foreach(int width in new[]{480,2048,0}){var result=await service.LoadResultAsync(path,width,default);Is(result.Image.PixelWidth>0);Console.WriteLine($"REAL size={width} actual={result.Image.PixelWidth}x{result.Image.PixelHeight} compatibility={result.ColorWarning!=null}");}
            Is(hash==Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))&&stamp==File.GetLastWriteTimeUtc(path));
        });
    }
    private static async Task ZoomPerformance()
    {
        var (w,_)=await V3Window(9,true);try
        {
            foreach(int count in new[]{2,4,9})
            {
                w.VM.ViewIndex=1;w.VM.Select(w.VM.Visible[0],false,false);w.VM.Select(w.VM.Visible[count-1],false,true);w.VM.Compare();await Task.Delay(400);w.SetSelectionZoom(null);await Task.Delay(250);
                var times=new List<double>();
                for(int i=0;i<40;i++)
                {
                    var next=new TaskCompletionSource();EventHandler? handler=null;handler=(_,_)=>{CompositionTarget.Rendering-=handler;next.TrySetResult();};CompositionTarget.Rendering+=handler;
                    var sw=Stopwatch.StartNew();w.ZoomSelection(i%2==0?1.25:.8);await next.Task.WaitAsync(TimeSpan.FromSeconds(3));times.Add(sw.Elapsed.TotalMilliseconds);await Task.Delay(20);
                }
                var viewers=MainWindow.Descendants<PhotoViewer>((ItemsControl)w.FindName("ComparisonView")).ToArray();var seen=new HashSet<string>();var full=new TaskCompletionSource();
                void Ready(PhotoViewer _,string path,int width){if(width==6000){seen.Add(path);if(seen.Count==count)full.TrySetResult();}}
                foreach(var v in viewers)v.FrameAvailable+=Ready;var original=Stopwatch.StartNew();w.SetSelectionZoom(1);await full.Task.WaitAsync(TimeSpan.FromSeconds(15));foreach(var v in viewers)v.FrameAvailable-=Ready;
                double p95=times.Order().ElementAt(37);Console.WriteLine($"ZOOM {count} images 6000x4000 P50={times.Order().ElementAt(19):F1}ms P95={p95:F1}ms max={times.Max():F1}ms full-details={original.Elapsed.TotalMilliseconds:F1}ms WS={Process.GetCurrentProcess().WorkingSet64/1048576}MB");Is(p95<=100,"预热缩放首个画面超过100ms");
                w.SetSelectionZoom(null);await Task.Delay(450);
            }
            Console.WriteLine($"ZOOM peak-working-set={Process.GetCurrentProcess().PeakWorkingSet64/1048576}MB cache={ImageService.Shared.MemoryUsage.Bytes/1048576}MB");passed++;
        }finally{await CloseV3(w);}
    }
}
