using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShuShuShai.Core;
using ShuShuShai.App;
using ShuShuShai.App.Controls;
using ShuShuShai.App.Services;

internal static partial class Program
{
    private static int passed, failed;
    private static readonly string Root = Path.Combine(Path.GetTempPath(),"ShuShuShai.Specs-"+Guid.NewGuid().ToString("N"));
    [STAThread] private static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR",Path.Combine(Root,"runtime-data"));
        var app=new Application();
        foreach (string theme in new[] { "Dark", "Brand" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/舒舒筛;component/Themes/{theme}.xaml", UriKind.Relative) });
        app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        app.Startup+=async(_,_)=>
        {
            try
            {
                if(args.Length==2&&args[0]=="--fixtures") { Fixtures(Path.GetFullPath(args[1])); Console.WriteLine("测试素材已生成："+args[1]); }
                else if(args.Length==2&&args[0]=="--review-seed") { await SeedReview(Path.GetFullPath(args[1])); }
                else if(args.Length==2&&args[0]=="--review-render") { await RenderReview(Path.GetFullPath(args[1])); }
                else if(args.Length==1&&args[0]=="--performance") { Directory.CreateDirectory(Root); await Performance(); }
                else if(args.Length==1&&args[0]=="--v2") { Directory.CreateDirectory(Root); await V2Specs(); }
                else if(args.Length==1&&args[0]=="--v3") { Directory.CreateDirectory(Root); await V3Specs(); }
                else if(args.Length==1&&args[0]=="--v031") { Directory.CreateDirectory(Root); await V031Specs(); }
                else if(args.Length==1&&args[0]=="--v032") { Directory.CreateDirectory(Root); await V032Specs(); }
                else if(args.Length==1&&args[0]=="--v033") { Directory.CreateDirectory(Root); await V033Specs(); }
                else if(args.Length==1&&args[0]=="--v034") { Directory.CreateDirectory(Root); await V034Specs(); }
                else if(args.Length==1&&args[0]=="--v035") { Directory.CreateDirectory(Root); await V035Specs(); }
                else if(args.Length==3&&args[0]=="--verify-signature") { InstallerSignature.Verify(Path.GetFullPath(args[1]), args[2]); Console.WriteLine("SIGNATURE_VALID"); }
                else if(args.Length==2&&args[0]=="--update-serve") { await ServeUpdateFixtureAsync(Path.GetFullPath(args[1])); }
                else if(args.Length==2&&args[0]=="--update-snapshot") { SnapshotUpdateData(Path.GetFullPath(args[1])); }
                else if(args.Length is 5 or 6 && args[0]=="--update-handoff") { await ExerciseUpdateHandoffAsync(Path.GetFullPath(args[1]), new Uri(args[2]), Path.GetFullPath(args[3]), args[4] == "cancel", args.Length == 6 ? Version.Parse(args[5]) : null); }
                else if(args.Length==1&&args[0]=="--zoom-performance") { Directory.CreateDirectory(Root); await ZoomPerformance(); }
                else if(args.Length==2&&args[0]=="--real-jpg") { Directory.CreateDirectory(Root); await RealJpgReview(args[1]); }
                else if(args.Length==2&&args[0]=="--crash-export")
                {
                    var store=new TaskStore(args[1]);var p=store.LoadExports().Single();
                    await new ExportService(store,(phase,_)=>{if(phase=="after-commit")Environment.Exit(23);}).RunAsync(p,null,default);
                }
                else { Directory.CreateDirectory(Root); await CoreSpecs(); await ImageSpecs(); await UiSpecs(); await V2Specs(); await V3Specs(); await V031Specs(); await V032Specs(); await V033Specs(); await V034Specs(); await V035Specs(); }
            }
            catch(Exception ex){failed++;Console.WriteLine("FATAL "+ex);}
            finally{Console.WriteLine($"RESULT {passed} passed / {failed} failed\n临时目录：{Root}");app.Shutdown(failed==0?0:1);}
        };
        return app.Run();
    }
    private static void Is(bool condition,string message="断言不成立"){if(!condition)throw new Exception(message);}
    private static async Task Check(string name,Func<Task> run)
    {try{await run();passed++;Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;Console.WriteLine("FAIL "+name+"\n"+ex);}}
    private static Task Check(string name,Action run)=>Check(name,()=>{run();return Task.CompletedTask;});
    private static string MockPhotoPath(string drive, string relativePath) => Path.Combine(Root, "mock-drives", drive, relativePath);
    private static FileRecord F(string path)=>new(path,42,DateTime.UtcNow,!PathRules.IsJpg(path));
    private static PhotoTask Pair(params string[] paths){var t=new PhotoTask{Files=paths.Select(F).ToList()};PairingService.Rebuild(t);return t;}
    private static string Dir(string name){string path=Path.Combine(Root,name+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    private static string Write(string dir,string name,int bytes=12000){string path=Path.Combine(dir,name);File.WriteAllBytes(path,RandomNumberGenerator.GetBytes(bytes));return path;}
    private static async Task<PhotoTask> Scan(string dir,bool recursive=false){var t=new PhotoTask{Sources=[new(dir,recursive)]};await new ScanService().ScanAsync(t,[],null,default);return t;}
    private static async Task<(PhotoTask Task,TaskStore Store,ExportPlan Plan)> Plan(bool move=false,bool separate=true)
    {
        var dir=Dir("导出 源片");Write(dir,"IMG_1.JPG");Write(dir,"IMG_1.ARW");Write(dir,"IMG_2.jpeg");Write(dir,"IMG_3.JPG");Write(dir,"IMG_3.NEF");
        var t=await Scan(dir);foreach(var p in t.Photos)p.Decision=Decision.Keep;
        var store=new TaskStore(Dir("db"));var plan=ExportPlanner.Build(t,new(){Parent=Dir("目标"),FolderName="结果",Move=move,Separate=separate});return(t,store,plan);
    }
    private static async Task CoreSpecs()
    {
        await Check("同目录优先配对，后缀大小写与完整主名",()=>{var t=Pair(MockPhotoPath("D", @"照片\A.001.JPG"),MockPhotoPath("D", @"照片\a.001.ArW"));Is(t.Photos.Single().RawPath!=null);});
        await Check("跨目录唯一配对",()=>Is(Pair(MockPhotoPath("D", @"a\IMG.JPG"),MockPhotoPath("E", @"b\img.NEF")).Photos.Single().PairState==PairState.Paired));
        await Check("不去除名称中的空格和前缀",()=>Is(Pair(MockPhotoPath("D", @"a\IMG.JPG"),MockPhotoPath("D", @"a\IMG .ARW")).Photos.Single().RawPath==null));
        await Check("同名多个 JPG 全部保留并报告冲突",()=>{var t=Pair(MockPhotoPath("D", @"a\IMG.JPG"),MockPhotoPath("D", @"a\IMG.jpeg"),MockPhotoPath("D", @"a\IMG.ARW"));Is(t.Photos.Count==2&&t.Photos.All(p=>p.PairState==PairState.Conflict));});
        await Check("多 RAW 不猜选，跨目录同名不猜选",()=>{var t=Pair(MockPhotoPath("D", @"a\IMG.JPG"),MockPhotoPath("D", @"b\IMG.ARW"),MockPhotoPath("D", @"c\IMG.NEF"));Is(t.Photos.Single().Candidates.Count==2&&t.Photos.Single().PairState==PairState.Conflict);});
        await Check("手动一对一与追加导入稳定评分",()=>{var t=Pair(MockPhotoPath("D", @"a\IMG.JPG"),MockPhotoPath("D", @"b\IMG.JPG"),MockPhotoPath("D", @"c\IMG.ARW"));var p=t.Photos[0];p.Stars=5;p.Decision=Decision.Keep;var id=p.Id;PairingService.Confirm(t,p,MockPhotoPath("D", @"c\IMG.ARW"));t.Files.Add(F(MockPhotoPath("D", @"d\IMG.NEF")));PairingService.Rebuild(t);Is(t.Photos[0].Id==id&&p.Stars==5&&p.RawPath==MockPhotoPath("D", @"c\IMG.ARW"));bool blocked=false;try{PairingService.Confirm(t,t.Photos[1],p.RawPath);}catch(InvalidOperationException){blocked=true;}Is(blocked);});
        await Check("明确仅 JPG 不会因追加 RAW 改绑",()=>{var t=Pair(MockPhotoPath("D", @"a\IMG.JPG"));var p=t.Photos[0];PairingService.Confirm(t,p,null);t.Files.Add(F(MockPhotoPath("D", @"a\IMG.ARW")));PairingService.Rebuild(t);Is(p.PairState==PairState.ConfirmedJpgOnly&&p.RawPath==null);});
        await Check("父子覆盖去重、中文与递归选项",async()=>{var dir=Dir("中文 路径");var sub=Path.Combine(dir,"子目录");Directory.CreateDirectory(sub);Write(dir,"1.JPG");Write(sub,"2.JPG");var t=new PhotoTask{Sources=[new(sub,false),new(dir,true),new(sub,true)]};await new ScanService().ScanAsync(t,[],null,default);Is(t.Photos.Count==2);Is(t.Sources[0].Path==sub);});
        await Check("非递归扫描不纳入子文件夹",async()=>{var dir=Dir("nonrecursive");Directory.CreateDirectory(Path.Combine(dir,"sub"));Write(dir,"A.JPG");Write(Path.Combine(dir,"sub"),"B.JPG");Is((await Scan(dir)).Photos.Count==1);});
        await Check("已知输出目录跳过",async()=>{var dir=Dir("skip");var output=Path.Combine(dir,"结果");Directory.CreateDirectory(output);Write(output,"1.JPG");var t=new PhotoTask{Sources=[new(dir,true)]};await new ScanService().ScanAsync(t,[output],null,default);Is(t.Photos.Count==0&&t.Diagnostics.Count>0);});
        await Check("缺失文件保留稳定评分及错误",async()=>{var dir=Dir("missing");var f=Write(dir,"a.JPG");var t=await Scan(dir);var id=t.Photos[0].Id;t.Photos[0].Stars=4;File.Delete(f);await new ScanService().ScanAsync(t,[],null,default);Is(t.Photos[0].Id==id&&t.Photos[0].Stars==4&&t.Diagnostics.Count>0);});
        await Check("扫描取消不提交半批扫描结果",async()=>{var t=new PhotoTask{Sources=[new(Dir("cancel"),true)]};using var c=new CancellationTokenSource();c.Cancel();try{await new ScanService().ScanAsync(t,[],null,c.Token);}catch(OperationCanceledException){}Is(t.Photos.Count==0);});
        await Check("星级条件与未评分独立组合",()=>{var p=new Photo{Stars=0};var f=new FilterSettings{Mode=2,Stars=3};Is(!f.Matches(p));f.IncludeUnrated=true;Is(f.Matches(p));p.Stars=4;Is(f.Matches(p));f.Mode=3;Is(!f.Matches(p));f.Mode=4;p.Stars=3;Is(f.Matches(p));f.Mode=1;Is(!f.Matches(p));});
        await Check("评分和取舍独立，整批撤销",()=>{var a=new Photo{Stars=2,Decision=Decision.Keep};var b=new Photo();var h=new RatingHistory();h.Apply([a,b],stars:5);Is(a.Decision==Decision.Keep&&b.Decision==Decision.Undecided);Is(h.Undo()&&a.Stars==2&&b.Stars==0);});
        await Check("自然排序与完整路径稳定排序",()=>{Is(NaturalPhotoComparer.Natural("IMG2.JPG","IMG10.JPG")<0);var p=new NaturalPhotoComparer(false);Is(p.Compare(new(){JpgPath=MockPhotoPath("D", @"a\1.JPG")},new(){JpgPath=MockPhotoPath("D", @"b\1.JPG")})<0);});
        await Check("SQLite 重开恢复源顺序、配对与评分",async()=>{var x=await Plan();x.Task.Photos[0].Stars=5;x.Task.Filter.Mode=4;x.Task.CurrentId=x.Task.Photos[0].Id;x.Store.Save(x.Task);var other=new TaskStore(x.Store.DataDirectory).LoadAll().Single();Is(other.Photos[0].Id==x.Task.Photos[0].Id&&other.Photos[0].Stars==5&&other.Filter.Mode==4&&other.Sources[0].Path==x.Task.Sources[0].Path);});
        await Check("默认首个目录和每次新结果目录",async()=>{var x=await Plan();var p=ExportPlanner.Build(x.Task,new(){FolderName="导出"});Is(Path.GetDirectoryName(p.Root)==x.Task.Sources[0].Path);Directory.CreateDirectory(p.Root);Is(ExportPlanner.Build(x.Task,new(){FolderName="导出"}).Root!=p.Root);});
        await Check("默认不改名、分放平铺、仅导出保留",async()=>{var x=await Plan();Is(x.Plan.Files.Count==5);Is(x.Plan.Files.All(f=>Path.GetFileName(f.Source)==Path.GetFileName(f.Target)));x.Task.Photos[0].Decision=Decision.Reject;Is(ExportPlanner.Build(x.Task,new()).Files.Count<5);});
        await Check("联动编号按组，缺 RAW 不跳组编号，超过四位",async()=>{var x=await Plan();var p=ExportPlanner.Build(x.Task,new(){Shared=new(){Rename=true,Prefix="旅行",Start=9999}});Is(p.Files.Count(f=>Path.GetFileNameWithoutExtension(f.Target)=="旅行_9999")==2);Is(p.Files.Any(f=>Path.GetFileNameWithoutExtension(f.Target)=="旅行_10000"));Is(p.Files.Count(f=>Path.GetFileNameWithoutExtension(f.Target)=="旅行_10001")==2);});
        foreach(var mode in new[]{0,1,2,3})await Check("独立命名组合 "+mode,async()=>{var x=await Plan();var p=ExportPlanner.Build(x.Task,new(){Linked=false,Jpg=new(){Rename=(mode&1)!=0,Prefix="J",Start=0},Raw=new(){Rename=(mode&2)!=0,Prefix="R",Start=8}});Is(p.Files.Where(f=>!f.IsRaw).Select(f=>Path.GetFileName(f.Target)).SequenceEqual((mode&1)!=0?new[]{"J_0000.JPG","J_0001.jpeg","J_0002.JPG"}:new[]{"IMG_1.JPG","IMG_2.jpeg","IMG_3.JPG"}));Is(p.Files.Where(f=>f.IsRaw).Select(f=>Path.GetFileName(f.Target)).SequenceEqual((mode&2)!=0?new[]{"R_0008.ARW","R_0009.NEF"}:new[]{"IMG_1.ARW","IMG_3.NEF"}));});
        await Check("Windows 同名目标冲突阻止导出",async()=>{var dir=Dir("collision");var sub=Path.Combine(dir,"sub");Directory.CreateDirectory(sub);Write(dir,"A.JPG");Write(sub,"a.JPG");var t=await Scan(dir,true);foreach(var p in t.Photos){p.Decision=Decision.Keep;PairingService.Confirm(t,p,null);}bool blocked=false;try{ExportPlanner.Build(t,new());}catch(InvalidOperationException){blocked=true;}Is(blocked);});
        foreach(var name in new[]{"CON.JPG","LPT1.arw","a.","a ","a/b","NUL","COM¹.jpg"})await Check("非法名称拒绝 "+name,()=>{bool blocked=false;try{ExportPlanner.ValidateName(name);}catch(InvalidOperationException){blocked=true;}Is(blocked);});
        foreach(var separate in new[]{true,false})await Check("字节复制哈希一致、原名不变 "+separate,async()=>{var x=await Plan(separate:separate);await new ExportService(x.Store).RunAsync(x.Plan,null,default);Is(x.Plan.Complete);foreach(var f in x.Plan.Files){Is(File.Exists(f.Source));Is(await ExportService.Matches(f.Target,f,default));Is(File.GetLastWriteTimeUtc(f.Target)==f.ModifiedUtc);}});
        await Check("按组安全移动与新旧路径持久化",async()=>{var x=await Plan(move:true);await new ExportService(x.Store).RunAsync(x.Plan,null,default);Is(x.Plan.Complete,string.Join("\n",x.Plan.Messages));Is(x.Plan.Files.All(f=>!File.Exists(f.Source)&&File.Exists(f.Target)));ExportService.ApplyMovedLocations(x.Task,x.Plan);x.Store.Save(x.Task);Is(x.Store.LoadAll().Single().Photos.All(p=>p.OriginalJpgPath!=null&&File.Exists(p.JpgPath)));});
        await Check("同组第二文件失败不移除组内任何源文件",async()=>{var x=await Plan(move:true);var raw=x.Plan.Files.First(f=>f.IsRaw);var svc=new ExportService(x.Store,(phase,f)=>{if(f==raw&&phase=="after-copy")throw new IOException("模拟空间不足");});await svc.RunAsync(x.Plan,null,default);Is(!x.Plan.Complete);var group=x.Plan.Files.Where(f=>f.GroupId==raw.GroupId);Is(group.All(f=>File.Exists(f.Source)));});
        await Check("取消后不开始源文件移除",async()=>{var x=await Plan(move:true);using var cancel=new CancellationTokenSource();await new ExportService(x.Store,(phase,_)=>{if(phase=="before-remove")cancel.Cancel();}).RunAsync(x.Plan,null,cancel.Token);Is(!x.Plan.Complete&&x.Plan.Files.All(f=>File.Exists(f.Source)));});
        await Check("复制校验失败保留源文件",async()=>{var x=await Plan(move:true);await new ExportService(x.Store,(phase,f)=>{if(phase=="after-copy")File.AppendAllText(f.Temp,"corrupted");}).RunAsync(x.Plan,null,default);Is(!x.Plan.Complete&&x.Plan.Files.All(f=>File.Exists(f.Source)));});
        await Check("已有目标不覆盖且日志准确",async()=>{var x=await Plan();await new ExportService(x.Store,(phase,f)=>{if(phase=="before-commit")File.WriteAllText(f.Target,"user-content");}).RunAsync(x.Plan,null,default);Is(!x.Plan.Complete&&x.Plan.Files.All(f=>File.ReadAllText(f.Target)=="user-content"));});
        await Check("提交后中断，重开核验复用持久计划",async()=>{var x=await Plan();bool once=true;await new ExportService(x.Store,(phase,_)=>{if(phase=="after-commit"&&once){once=false;throw new IOException("模拟进程中断");}}).RunAsync(x.Plan,null,default);Is(!x.Plan.Complete);var resume=new TaskStore(x.Store.DataDirectory).LoadExports().Single();await new ExportService(x.Store).RunAsync(resume,null,default);Is(resume.Complete);});
        await Check("源文件变更后拒绝移除且保留已校验目标",async()=>{var x=await Plan(move:true);bool changed=false;await new ExportService(x.Store,(phase,f)=>{if(phase=="before-remove"&&!changed){changed=true;File.AppendAllText(f.Source,"changed");}}).RunAsync(x.Plan,null,default);Is(!x.Plan.Complete);var first=x.Plan.Files[0];Is(File.Exists(first.Source)&&File.Exists(first.Target)&&first.Error!=null);});
        await Check("部分源移除失败后恢复不误删其他文件",async()=>{var x=await Plan(move:true);var raw=x.Plan.Files.First(f=>f.IsRaw);await new ExportService(x.Store,(phase,f)=>{if(phase=="before-remove"&&f==raw)throw new IOException("模拟文件占用");}).RunAsync(x.Plan,null,default);Is(!x.Plan.Complete&&File.Exists(raw.Source));var resume=x.Store.LoadExports().Single();await new ExportService(x.Store).RunAsync(resume,null,default);Is(resume.Complete);});
        await Check("目标断开/路径失效保留源文件",async()=>{var x=await Plan(move:true);File.WriteAllText(x.Plan.Root,"阻挡目录");await new ExportService(x.Store).RunAsync(x.Plan,null,default);Is(!x.Plan.Complete&&x.Plan.Files.All(f=>File.Exists(f.Source)));});
        await Check("未处理配对冲突阻止保留照片导出",async()=>{var x=await Plan();x.Task.Photos[0].PairState=PairState.Conflict;bool blocked=false;try{ExportPlanner.Build(x.Task,new());}catch(InvalidOperationException){blocked=true;}Is(blocked);});
        await Check("真实子进程中断后重开恢复，源文件安全",async()=>
        {
            var x=await Plan(move:true);x.Store.SaveExport(x.Plan);
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
            start.ArgumentList.Add("--crash-export");start.ArgumentList.Add(x.Store.DataDirectory);
            start.Environment["DOTNET_ROOT"]=Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),"..","..",".."));
            using var child=Process.Start(start)!;await child.WaitForExitAsync();Is(child.ExitCode==23,"子进程未在提交后中断");
            Is(x.Plan.Files.All(f=>File.Exists(f.Source)));var resume=x.Store.LoadExports().Single();await new ExportService(x.Store).RunAsync(resume,null,default);Is(resume.Complete&&resume.Files.All(f=>!File.Exists(f.Source)));
        });
    }
    private static void Jpeg(string path,int width,int height,int orientation=1)
    {
        byte[] pixels=new byte[width*height*3];for(int y=0;y<height;y++)for(int x=0;x<width;x++){int i=(y*width+x)*3;pixels[i]=(byte)(x*255/width);pixels[i+1]=(byte)(y*255/height);pixels[i+2]=(byte)(((x/12+y/12)%2)*170+40);}
        var image=BitmapSource.Create(width,height,96,96,PixelFormats.Rgb24,null,pixels,width*3);image.Freeze();
        var metadata=new BitmapMetadata("jpg");metadata.SetQuery("/app1/ifd/{ushort=274}",(ushort)orientation);
        var encoder=new JpegBitmapEncoder{QualityLevel=92};encoder.Frames.Add(BitmapFrame.Create(image,null,metadata,null));using var s=File.Create(path);encoder.Save(s);
    }
    private static async Task ImageSpecs()
    {
        var dir=Dir("images");var high=Path.Combine(dir,"6000x4000.JPG");Jpeg(high,6000,4000);
        await Check("6000×4000 原始像素读取，无固定 3000 限制",async()=>{var b=await ImageService.Shared.LoadAsync(high,0,default);Is(b.PixelWidth==6000&&b.PixelHeight==4000);});
        await Check("缩略图按需解码并及时释放源句柄",async()=>{var b=await ImageService.Shared.LoadAsync(high,480,default);Is(b.PixelWidth<=480);using var s=new FileStream(high,FileMode.Open,FileAccess.ReadWrite,FileShare.None);Is(s.Length>0);});
        for(int orientation=1;orientation<=8;orientation++){int n=orientation;await Check("EXIF 方向 "+n,async()=>{var path=Path.Combine(dir,$"o{n}.JPG");Jpeg(path,60,40,n);var b=await ImageService.Shared.LoadAsync(path,0,default);Is(b.PixelWidth==(n>=5?40:60)&&b.PixelHeight==(n>=5?60:40));});}
        await Check("直方图全像素计数，不改写源照片",async()=>{var hash=SHA256.HashData(File.ReadAllBytes(high));var data=await ImageService.Shared.HistogramAsync(high,default);Is(data.Pixels==24000000&&data.Red.Sum()==24000000&&data.Green.Sum()==24000000&&data.Blue.Sum()==24000000);Is(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(high))));});
        await Check("已知纯色分布 RGB 各 256 分箱",async()=>{var p=Path.Combine(dir,"solid.JPG");var rgb=BitmapSource.Create(12,10,96,96,PixelFormats.Rgb24,null,Enumerable.Repeat(new byte[]{100,150,200},120).SelectMany(x=>x).ToArray(),36);var encoder=new JpegBitmapEncoder{QualityLevel=100};encoder.Frames.Add(BitmapFrame.Create(rgb));using(var s=File.Create(p))encoder.Save(s);var data=await ImageService.Shared.HistogramAsync(p,default);Is(data.Red.Sum()==120&&data.Red.Max()==120&&data.Red[100]+data.Red[101]+data.Red[99]==120);});
        await Check("RAW 被预览服务拒绝",async()=>{bool blocked=false;try{await ImageService.Shared.LoadAsync(Write(dir,"raw.ARW"),0,default);}catch(IOException){blocked=true;}Is(blocked);});
        await Check("损坏 JPG 报错且不影响正常读取",async()=>{var p=Write(dir,"broken.JPG");bool blocked=false;try{await ImageService.Shared.LoadAsync(p,480,default);}catch{blocked=true;}Is(blocked);Is((await ImageService.Shared.LoadAsync(high,480,default)).PixelWidth>0);});
        await Check("缓存键包含修改时间/长度，受控内存",async()=>{var p=Path.Combine(dir,"change.JPG");Jpeg(p,80,40);var first=await ImageService.Shared.LoadAsync(p,480,default);Jpeg(p,100,70);File.SetLastWriteTimeUtc(p,DateTime.UtcNow.AddSeconds(2));var second=await ImageService.Shared.LoadAsync(p,480,default);Is(first.PixelWidth==80&&second.PixelWidth==100&&ImageService.Shared.MemoryUsage.Bytes<=192L*1024*1024);});
        await Check("八种方向旋转及镜像的已知像素位置",()=>
        {
            var raw=BitmapSource.Create(2,2,96,96,PixelFormats.Rgb24,null,new byte[]{10,0,0,20,0,0,30,0,0,40,0,0},6);
            int[][] expected=[[10,20,30,40],[20,10,40,30],[40,30,20,10],[30,40,10,20],[10,30,20,40],[30,10,40,20],[40,20,30,10],[20,40,10,30]];
            for(int n=1;n<=8;n++){var b=ImageService.Orient(raw,n);byte[] px=new byte[12];b.CopyPixels(px,6,0);Is(new[]{(int)px[0],px[3],px[6],px[9]}.SequenceEqual(expected[n-1]),"像素方向错误 "+n);}
        });
        await Check("当前系统 DPI 下 100% 一个图像像素对应一个物理像素",async()=>
        {
            var path=Path.Combine(dir,"white.JPG");var src=BitmapSource.Create(64,40,96,96,PixelFormats.Rgb24,null,Enumerable.Repeat((byte)255,64*40*3).ToArray(),64*3);var enc=new JpegBitmapEncoder{QualityLevel=100};enc.Frames.Add(BitmapFrame.Create(src));using(var s=File.Create(path))enc.Save(s);
            var v=new PhotoViewer{Path=path};var w=new Window{Width=400,Height=300,Content=v};w.Show();await Task.Delay(200);var ready=new TaskCompletionSource();void Frame(PhotoViewer _,string p,int width){if(width==64)ready.TrySetResult();}v.FrameAvailable+=Frame;v.SetZoom(1);await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(60);w.UpdateLayout();var dpi=VisualTreeHelper.GetDpi(v);var capture=new RenderTargetBitmap((int)(v.ActualWidth*dpi.DpiScaleX),(int)(v.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);capture.Render(v);
            byte[] row=new byte[capture.PixelWidth*4];capture.CopyPixels(new Int32Rect(0,capture.PixelHeight/2,capture.PixelWidth,1),row,row.Length,0);int whites=Enumerable.Range(0,capture.PixelWidth).Count(i=>row[i*4]>245&&row[i*4+1]>245&&row[i*4+2]>245);Console.WriteLine($"METRIC physical-pixel DPI={dpi.DpiScaleX:F2} white-span={whites} expected=64");Is(Math.Abs(whites-64)<=1);v.FrameAvailable-=Frame;w.Close();
        });
        foreach(var profileName in new[]{"sRGB Color Space Profile.icm","TPLCD_1659_AdobeRGB.icm"})
        {
            string profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"spool","drivers","color",profileName);
            if(!File.Exists(profile)){Console.WriteLine("NOT TESTED ICC fixture unavailable: "+profileName);continue;}
            await Check("ICC 标记 JPG 预览一致与统计不应用 ICC："+profileName,async()=>
            {
                var source=BitmapSource.Create(80,50,96,96,PixelFormats.Rgb24,null,Enumerable.Repeat(new byte[]{180,100,50},4000).SelectMany(x=>x).ToArray(),240);
                var context=new ColorContext(new Uri(profile));var enc=new JpegBitmapEncoder{QualityLevel=100};enc.Frames.Add(BitmapFrame.Create(source,null,null,new System.Collections.ObjectModel.ReadOnlyCollection<ColorContext>(new[]{context})));string path=Path.Combine(dir,profileName+".JPG");using(var s=File.Create(path))enc.Save(s);
                var full=await ImageService.Shared.LoadAsync(path,0,default);var thumb=await ImageService.Shared.LoadAsync(path,480,default);
                byte[] a=new byte[80*50*4],b=new byte[a.Length];new FormatConvertedBitmap(full,PixelFormats.Bgra32,null,0).CopyPixels(a,80*4,0);new FormatConvertedBitmap(thumb,PixelFormats.Bgra32,null,0).CopyPixels(b,80*4,0);Is(a.SequenceEqual(b));
                var hist=await ImageService.Shared.HistogramAsync(path,default);Is(hist.Red[179]+hist.Red[180]+hist.Red[181]==4000,"统计应使用 JPEG 解码值，不能使用色彩转换后的预览");
                if(profileName.Contains("AdobeRGB"))Is(Math.Abs(a[2]-180)>2||Math.Abs(a[1]-100)>2||Math.Abs(a[0]-50)>2,"ICC 没有影响预览转换");
            });
        }
        await Check("24MP 适屏缓存预热切换耗时记录",async()=>{await ImageService.Shared.LoadAsync(high,1600,default);var timings=new List<double>();for(int i=0;i<30;i++){var sw=Stopwatch.StartNew();await ImageService.Shared.LoadAsync(high,1600,default);timings.Add(sw.Elapsed.TotalMilliseconds);}Console.WriteLine($"METRIC cache same-image P95={timings.Order().ElementAt(28):F2}ms (service only, not end-to-end flip)");});
    }
    private static async Task UiSpecs()
    {
        await Check("启动页无任务时正常关闭窗口",async()=>
        {
            Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR",Dir("empty-ui"));var window=new MainWindow();window.Show();await Task.Delay(100);await ConfirmExitAsync(window);Is(!window.IsVisible,"无任务窗口应在确认后关闭");Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR",null);
        });
        await Check("五千张扫描、总览/胶片条虚拟化及回收滚动",async()=>
        {
            var dir=Dir("5000");var sample=Path.Combine(dir,"IMG_00001.JPG");Jpeg(sample,480,320);for(int i=2;i<=5000;i++)File.Copy(sample,Path.Combine(dir,$"IMG_{i:D5}.JPG"));
            var data=Dir("ui-db");Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR",data);var window=new MainWindow();window.Show();await Task.Delay(100);
            var sw=Stopwatch.StartNew();await window.VM.ImportAsync([new(dir,false)],true);Console.WriteLine($"METRIC 5000 scan+UI={sw.ElapsedMilliseconds}ms");window.VM.ViewIndex=1;await Task.Delay(1200);window.UpdateLayout();
            var grid=(ListBox)window.FindName("GridList");var film=(ListBox)window.FindName("Film");int rows=MainWindow.Descendants<ListBoxItem>(grid).Count(),cells=MainWindow.Descendants<Thumbnail>(grid).Count(),filmCells=MainWindow.Descendants<ListBoxItem>(film).Count();
            Console.WriteLine($"METRIC initial grid rows={rows}, thumbnails={cells}, film={filmCells}, workingSet={Process.GetCurrentProcess().WorkingSet64/1048576}MB");Is(rows<100&&cells<150&&filmCells<100,"虚拟化未生效");
            var scroll=MainWindow.Descendants<ScrollViewer>(grid).First();for(int i=0;i<12;i++){scroll.ScrollToVerticalOffset(scroll.ScrollableHeight*i/11);await Task.Delay(90);}
            int after=MainWindow.Descendants<Thumbnail>(grid).Count();Is(after<150);Console.WriteLine($"METRIC after scrolling thumbnails={after}, workingSet={Process.GetCurrentProcess().WorkingSet64/1048576}MB");
            window.VM.ThumbnailSize=120;await Task.Delay(200);int small=window.VM.Rows[0].Columns;window.VM.ThumbnailSize=330;await Task.Delay(200);Is(window.VM.Rows[0].Columns<small);
            window.VM.Select(window.VM.Visible[0],false,false);window.VM.Select(window.VM.Visible[4],false,true);var active=window.VM.Current;window.VM.Apply(stars:4);Is(window.VM.Current==active,"评分后当前图不应跳回首图");Is(window.VM.Task!.Photos.Count(p=>p.Stars==4)==5);window.VM.Undo();Is(window.VM.Task.Photos.All(p=>p.Stars==0));
            Is(window.VM.Compare(3)&&window.VM.Comparison.Count==5);var compareIds=window.VM.Comparison.Select(p=>p.Id).ToArray();window.VM.CompareLinkedRating=true;window.VM.Apply(decision:Decision.Keep);Is(window.VM.Task.Photos.Count(p=>p.Decision==Decision.Keep)==5);window.VM.RemoveCompare(window.VM.Current!);Is(window.VM.Task.Photos.Count(p=>p.Decision==Decision.Reject)==0);
            window.VM.ViewIndex=1;window.VM.FilterMode=1;Is(window.VM.Visible.Count==5000);
            window.VM.Select(window.VM.Visible[0],false,false);Is(!window.VM.Compare(3));window.VM.Select(window.VM.Visible[8],false,true);window.VM.Compare(3);Is(window.VM.Comparison.Count==9);await Task.Delay(700);
            Is(window.VM.Comparison.All(p=>p.Decision!=Decision.Reject));window.VM.ViewIndex=0;
            window.VM.FilterMode=4;window.VM.FilterStars=5;Is(window.VM.Visible.Count==0&&window.VM.Current==null,"筛选为空时不得保留旧活动图");window.VM.FilterMode=0;
            window.VM.Select(window.VM.Visible[123],false,false);string restore=window.VM.Current!.Id;
            Is(await window.VM.FlushSaveAsync());await ConfirmExitAsync(window);
            var loaded=new TaskStore(data).LoadAll().Single();Is(loaded.Photos.Count==5000);
            using(var reopen=new ShuShuShai.App.ViewModels.MainViewModel(new TaskStore(data))){await reopen.OpenAsync(loaded);Is(reopen.Current?.Id==restore,"重开应恢复当前图而非被首图覆盖");}
            Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR",null);
        });
    }
    private static void Fixtures(string dir)
    {
        Directory.CreateDirectory(dir);for(int i=1;i<=14;i++){Jpeg(Path.Combine(dir,$"测试_{i:D4}.JPG"),i==1?6000:1200,i==1?4000:800,(i%8)+1);if(i%3!=0)File.WriteAllText(Path.Combine(dir,$"测试_{i:D4}.ARW"),"Synthetic RAW fixture; not an actual RAW image.");}
        File.WriteAllText(Path.Combine(dir,"损坏.JPG"),"broken fixture");
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]
    private static extern bool CreateHardLinkW(string link,string existing,IntPtr security);
    private static async Task Performance()
    {
        var dir=Dir("5000-24MP");string first=Path.Combine(dir,"IMG_00001.JPG");Jpeg(first,6000,4000);
        string raw=Path.Combine(dir,"IMG_00001.ARW");File.WriteAllText(raw,"Synthetic RAW placeholder; never decoded.");
        string blockJpg=first,blockRaw=raw;
        for(int i=2;i<=5000;i++)
        {
            string jpgPath=Path.Combine(dir,$"IMG_{i:D5}.JPG"),rawPath=Path.Combine(dir,$"IMG_{i:D5}.ARW");
            if((i-1)%500==0){File.Copy(first,jpgPath);File.Copy(raw,rawPath);blockJpg=jpgPath;blockRaw=rawPath;}
            else if(!CreateHardLinkW(jpgPath,blockJpg,IntPtr.Zero)||!CreateHardLinkW(rawPath,blockRaw,IntPtr.Zero))throw new System.ComponentModel.Win32Exception();
        }
        Console.WriteLine($"DATASET 5000 groups, 6000x4000 JPG {new FileInfo(first).Length/1048576.0:F2}MB, same generated pixels via NTFS hard links; synthetic RAW placeholders");
        Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR",Dir("performance-db"));var window=new MainWindow();window.Show();await Task.Delay(100);var viewer=(PhotoViewer)window.FindName("LargeViewer");
        var firstFrame=new TaskCompletionSource();var total=Stopwatch.StartNew();void First(PhotoViewer _,string path,int width){if(width>0)firstFrame.TrySetResult();}viewer.FrameAvailable+=First;
        await window.VM.ImportAsync([new(dir,false)],true);await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));Console.WriteLine($"METRIC import-to-first-usable-preview={total.Elapsed.TotalMilliseconds:F1}ms");viewer.FrameAvailable-=First;
        await Task.Delay(600);int fit=(int)Math.Ceiling(Math.Max(viewer.ActualWidth,viewer.ActualHeight)*VisualTreeHelper.GetDpi(viewer).DpiScaleX);
        foreach(var p in window.VM.Visible.Take(24)){await ImageService.Shared.LoadAsync(p.JpgPath,480,default);await ImageService.Shared.LoadAsync(p.JpgPath,fit,default);}
        var timings=new List<double>();
        for(int i=1;i<=40;i++)
        {
            string expected=window.VM.Visible[i%24].JpgPath;var ready=new TaskCompletionSource();
            void Frame(PhotoViewer _,string path,int width){if(path==expected&&width>0)ready.TrySetResult();}
            viewer.FrameAvailable+=Frame;var sw=Stopwatch.StartNew();window.VM.Select(window.VM.Visible[i%24],false,false);await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var presented=new TaskCompletionSource();EventHandler? render=null;render=(_,_)=>{CompositionTarget.Rendering-=render;presented.TrySetResult();};CompositionTarget.Rendering+=render;await presented.Task.WaitAsync(TimeSpan.FromSeconds(2));
            timings.Add(sw.Elapsed.TotalMilliseconds);viewer.FrameAvailable-=Frame;await Task.Delay(70);
        }
        Console.WriteLine($"METRIC warm flips UI first-frame P50={timings.Order().ElementAt(19):F1}ms P95={timings.Order().ElementAt(37):F1}ms max={timings.Max():F1}ms; DPI={VisualTreeHelper.GetDpi(viewer).DpiScaleX:F2}");
        var swFull=Stopwatch.StartNew();var full=await ImageService.Shared.LoadAsync(first,0,default);Console.WriteLine($"METRIC 100%-decode={swFull.Elapsed.TotalMilliseconds:F1}ms actual={full.PixelWidth}x{full.PixelHeight}");
        window.VM.ViewIndex=1;await Task.Delay(800);var grid=(ListBox)window.FindName("GridList");var scroll=MainWindow.Descendants<ScrollViewer>(grid).First();
        long peak=0;for(int pass=0;pass<6;pass++){for(int i=0;i<15;i++){scroll.ScrollToVerticalOffset(scroll.ScrollableHeight*i/14);await Task.Delay(75);peak=Math.Max(peak,Process.GetCurrentProcess().WorkingSet64);}Console.WriteLine($"METRIC scroll-pass-{pass+1} workingSet={Process.GetCurrentProcess().WorkingSet64/1048576}MB cache={ImageService.Shared.MemoryUsage.Bytes/1048576}MB cells={MainWindow.Descendants<Thumbnail>(grid).Count()}");}
        window.VM.Select(window.VM.Visible[0],false,false);window.VM.Select(window.VM.Visible[8],false,true);window.VM.Compare(3);await Task.Delay(1300);Console.WriteLine($"METRIC nine-compare count={window.VM.Comparison.Count} workingSet={Process.GetCurrentProcess().WorkingSet64/1048576}MB peak-observed-scroll={peak/1048576}MB process-peak={Process.GetCurrentProcess().PeakWorkingSet64/1048576}MB");
        Is(window.VM.Task!.Photos.Count==5000&&window.VM.Task.Photos.All(p=>p.RawPath!=null));await window.VM.FlushSaveAsync();await ConfirmExitAsync(window);passed++;Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR",null);
    }
}
