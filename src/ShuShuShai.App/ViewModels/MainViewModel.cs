using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ShuShuShai.Core;
using ShuShuShai.App.Services;

namespace ShuShuShai.App.ViewModels;
public record PhotoRow(List<Photo> Photos, int Columns);
public class MainViewModel : Observable, IDisposable
{
    public TaskStore Store { get; }
    public ObservableCollection<PhotoTask> History { get; } = [];
    public ObservableCollection<Photo> Visible { get; } = [];
    public ObservableCollection<PhotoRow> Rows { get; } = [];
    public ObservableCollection<Photo> Comparison { get; } = [];
    public TaskEditHistory EditHistory {get;}=new();
    public bool CanUndo=>EditHistory.CanUndo;
    public string UndoLabel=>EditHistory.NextLabel;
    public bool CompareLinkedRating {get=>Task?.CompareLinkedRating??false;set{if(Task==null)return;Task.CompareLinkedRating=value;Raise();Raise(nameof(OperationScope));Dirty();}}
    public string OperationScope=>IsCompare&&!CompareLinkedRating?"操作对象：当前活动照片":"操作对象：已选 "+SelectedCount+" 组";
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly SemaphoreSlim saveLock = new(1);
    private long revision;
    private CancellationTokenSource? detailsCancellation;
    private CancellationTokenSource? prefetchCancellation;
    public CancellationTokenSource? OperationCancellation { get; private set; }
    private PhotoTask? task;
    public PhotoTask? Task { get => task; private set { Set(ref task, value); Raise(nameof(HasTask)); Raise(nameof(IsHome)); Raise(nameof(Title)); } }
    public bool HasTask => Task != null;
    public bool IsHome => !HasTask;
    public string Title => Task?.SourceTitle ?? "舒舒筛";
    private bool busy;
    public bool Busy { get => busy; private set { Set(ref busy, value); Raise(nameof(Ready)); } }
    public bool Ready => !Busy;
    public event Action? ImagesInvalidated;
    public event Action? RowsRebuilt;
    public bool RebuildingRows { get; private set; }
    private string status = "选择一个文件夹，开始选片";
    public string Status { get => status; set => Set(ref status, value); }
    private string saveStatus = "";
    public string SaveStatus { get => saveStatus; set => Set(ref saveStatus, value); }
    private string saveError = "";
    public string SaveError { get => saveError; private set => Set(ref saveError, value); }
    private string stats = "";
    public string Stats { get => stats; private set => Set(ref stats, value); }
    private Photo? current;
    private bool refreshing;
    public Photo? Current
    {
        get => current;
        set
        {
            if (refreshing && value == null) return;
            if (current == value) return;
            if (current != null) { current.IsActive = false; current.NotifySelection(); }
            if (!Set(ref current, value)) return;
            if (value != null) { value.IsActive = true; value.NotifySelection(); }
            if (Task != null) { Task.CurrentId = value?.Id; Dirty(); }
            Raise(nameof(CurrentTitle)); Raise(nameof(CurrentPaths)); Raise(nameof(Position)); Raise(nameof(PhotoPosition)); Raise(nameof(OperationScope)); LoadDetails(); Prefetch();
        }
    }
    public string CurrentTitle => Current == null ? "没有活动照片" : $"{Current.Name}\n{Current.Caption}";
    public string CurrentPaths => Current == null ? "" : $"JPG  {Current.JpgPath}\n\nRAW  {Current.RawPath ?? "—"}";
    public string PhotoPosition => $"{(Current == null ? 0 : Visible.IndexOf(Current) + 1)} / {Visible.Count}";
    public string Position => $"{PhotoPosition}  ·  已选 {SelectedCount} 组";
    private string metadata = "—";
    public string Metadata { get => metadata; set => Set(ref metadata, value); }
    private string histogramStatus = "";
    public string HistogramStatus { get => histogramStatus; set => Set(ref histogramStatus, value); }
    private HistogramData? histogram;
    public HistogramData? Histogram { get => histogram; set => Set(ref histogram, value); }
    private int view;
    public int ViewIndex { get => view; set { if (!Set(ref view, value)) return; if (Task != null) { Task.ViewIndex = value; Dirty(); } Raise(nameof(IsLarge)); Raise(nameof(IsGrid)); Raise(nameof(IsCompare)); Raise(nameof(IsDual));Raise(nameof(OperationScope)); } }
    public bool IsLarge => ViewIndex == 0; public bool IsGrid => ViewIndex == 1; public bool IsCompare => ViewIndex is 2 or 3; public bool IsDual => IsCompare && Comparison.Count==2;
    public int CompareColumns=>Comparison.Count<=4?2:3;
    public int CompareRows=>Math.Max(1,(Comparison.Count+CompareColumns-1)/CompareColumns);
    public bool LinkedZoom { get; set; } = true;
    public bool AutoAdvance { get => Task?.AutoAdvance ?? false; set { if (Task != null) { Task.AutoAdvance = value; Dirty(); } Raise(); } }
    public int FilterMode { get => Task?.Filter.Mode ?? 0; set { if (Task == null || Task.Filter.Mode == value) return; Task.Filter.Mode = value; Raise(); Refresh(); } }
    public int FilterStars { get => Task?.Filter.Stars ?? 3; set { if (Task == null || Task.Filter.Stars == value) return; Task.Filter.Stars = value; Raise(); Refresh(); } }
    public bool IncludeUnrated { get => Task?.Filter.IncludeUnrated ?? false; set { if (Task == null || Task.Filter.IncludeUnrated == value) return; Task.Filter.IncludeUnrated = value; Raise(); Refresh(); } }
    public bool SpecificSources { get=>Task?.Filter.SpecificSources??false;set{if(Task==null||Task.Filter.SpecificSources==value)return;Task.Filter.SpecificSources=value;Raise();Refresh();} }
    public void SetSourceDirectories(IEnumerable<string> directories){if(Task==null)return;Task.Filter.Directories=directories.Distinct(PathRules.Comparer).ToList();Refresh();}
    public int DecisionFilter { get => Task?.Filter.DecisionIndex ?? 0; set { if (Task == null || Task.Filter.DecisionIndex == value) return; Task.Filter.DecisionIndex = value; Raise(); Refresh(); } }
    public int SortIndex { get => Task?.SortByTime == true ? 1 : 0; set { if (Task == null || value == SortIndex) return; Task.SortByTime = value == 1; Raise(); Refresh(); if (value == 1) _ = ReadShotTimes(); } }
    public double ThumbnailSize { get => Task?.ThumbnailSize ?? 190; set { if (Task == null) return; Task.ThumbnailSize = value; Raise(); Raise(nameof(ThumbnailHeight)); RebuildRows(); Dirty(); } }
    public double ThumbnailHeight => ThumbnailSize * .68;
    private double filmHeight=180;
    public double FilmHeight {get=>filmHeight;set{Set(ref filmHeight,value);Raise(nameof(FilmThumbnailHeight));Raise(nameof(FilmTileWidth));}}
    public double FilmThumbnailHeight=>Math.Max(24,FilmHeight-59);
    public double FilmTileWidth=>Math.Max(120,FilmThumbnailHeight*1.4+16);
    public double GridWidth { get; set; } = 850;
    public string StarCounts => Task == null ? "" : string.Join("  ·  ", Enumerable.Range(0,6).Select(i => $"{(i==0 ? "未评分" : i+"★")} {Task.Photos.Count(p=>p.Stars==i)}"));
    public string FolderText => Task == null ? "" : string.Join("\n\n", Task.Sources.Select((f,i) => $"{(i==0 ? "① 首个导入目录" : $"{i+1}. 导入目录")}\n{f.Path}\n{(f.Recursive ? "包含子文件夹" : "仅此文件夹")}"));
    public MainViewModel(TaskStore store)
    { Store = store; saveTimer.Tick += async (_, _) => { saveTimer.Stop();if(Busy){saveTimer.Start();return;}await FlushSaveAsync(); };ImageService.Shared.ReadFailed+=ReadFailed;ImageService.Shared.PreviewReady+=PreviewReady;EditHistory.Changed+=()=>{Raise(nameof(CanUndo));Raise(nameof(UndoLabel));}; }
    private void PreviewReady(string path,PreviewResult result)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(()=>
        {
            if(Task==null||!Task.Photos.Any(p=>PathRules.Comparer.Equals(p.JpgPath,path)))return;
            var prior=Task.Diagnostics.Where(d=>PathRules.Comparer.Equals(d.Path,path)&&(d.Code is "preview-read" or "preview-color" or "preview-metadata"||d.Message.StartsWith("JPG 读取失败"))).ToList();
            var next=new List<Diagnostic>();if(result.ColorWarning!=null)next.Add(new(path,result.ColorWarning,DiagnosticSeverity.Warning,"preview-color"));if(result.MetadataWarning!=null)next.Add(new(path,result.MetadataWarning,DiagnosticSeverity.Warning,"preview-metadata"));
            if(prior.SequenceEqual(next))return;foreach(var d in prior)Task.Diagnostics.Remove(d);Task.Diagnostics.AddRange(next);UpdateStatistics();Dirty();
        }));
    }
    private void ReadFailed(string path,string message)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(()=>
        {
            if(Task==null||!Task.Photos.Any(p=>PathRules.Comparer.Equals(p.JpgPath,path))||Task.Diagnostics.Any(d=>PathRules.Comparer.Equals(d.Path,path)&&d.Message.StartsWith("JPG 读取失败")))return;
            Task.Diagnostics.Add(new(path,"JPG 读取失败："+message,DiagnosticSeverity.Error,"preview-read"));UpdateStatistics();Dirty();
        }));
    }
    public void Dispose(){ImageService.Shared.ReadFailed-=ReadFailed;ImageService.Shared.PreviewReady-=PreviewReady;detailsCancellation?.Cancel();prefetchCancellation?.Cancel();saveTimer.Stop();}
    public async System.Threading.Tasks.Task InitializeAsync()
    {
        var history = await System.Threading.Tasks.Task.Run(()=>
        {
            var loaded=Store.LoadAll();var exports=Store.LoadExports();
            foreach(var h in loaded)foreach(var e in exports.Where(e=>e.TaskId==h.Id&&e.Move))ExportService.ApplyMovedLocations(h,e);
            return loaded;
        });
        History.Clear(); foreach (var h in history) History.Add(h);
    }
    public async System.Threading.Tasks.Task OpenAsync(PhotoTask next)
    {
        if (!await FlushSaveAsync()) throw new IOException(SaveStatus);
        detailsCancellation?.Cancel(); EditHistory.Clear();
        if (current != null) { current.IsActive = false; current.Raise(nameof(Photo.IsActive)); }
        current = null;selectedIds.Clear();referenceId=null;
        var restoreId=next.CurrentId;
        Task = next;next.Filter.IncludeUnrated=false; next.LastUsed = DateTime.Now; ViewIndex = next.ViewIndex;Raise(nameof(CompareLinkedRating));
        Comparison.Clear(); if(IsCompare)foreach (var id in next.ComparisonIds)selectedIds.Add(id);
        referenceId=next.ComparisonIds.FirstOrDefault();
        foreach (var name in new[]{nameof(FilterMode),nameof(FilterStars),nameof(IncludeUnrated),nameof(DecisionFilter),nameof(SortIndex),nameof(ThumbnailSize),nameof(ThumbnailHeight),nameof(FolderText),nameof(AutoAdvance),nameof(SpecificSources)}) Raise(name);
        Refresh(false); Current = (IsCompare?next.Photos:Visible.AsEnumerable()).FirstOrDefault(p => p.Id == restoreId) ?? Visible.FirstOrDefault();
        if(!IsCompare&&Current!=null)selectedIds.Add(Current.Id);
        SelectionChanged();
        Dirty(); Status = "";
    }
    public async System.Threading.Tasks.Task HomeAsync() { if(!await FlushSaveAsync())throw new IOException(SaveStatus); detailsCancellation?.Cancel();prefetchCancellation?.Cancel(); Current = null; selectedIds.Clear();Task = null; Comparison.Clear(); Visible.Clear(); Rows.Clear(); await InitializeAsync(); }
    public async System.Threading.Tasks.Task ImportAsync(List<SourceFolder> folders, bool newTask)
    {
        if (newTask) await OpenAsync(new PhotoTask { Name = new DirectoryInfo(folders[0].Path).Name });
        if (Task == null) return;
        foreach (var f in folders)
        {
            var old=Task.Sources.FirstOrDefault(s=>PathRules.Same(s.Path,f.Path));
            if(old==null)Task.Sources.Add(f);
            else if(f.Directories!=null){int index=Task.Sources.IndexOf(old);var prior=old.Directories??(old.Recursive?DirectoryDiscovery.Discover(old.Path,[],default).Directories:[old.Path]);Task.Sources[index]=old with{Recursive=false,Directories=prior.Concat(f.Directories).Distinct(PathRules.Comparer).ToList()};}
        }
        Raise(nameof(FolderText));Raise(nameof(Title)); await ScanAsync();
    }
    public async System.Threading.Tasks.Task ScanAsync()
    {
        if (Task == null || Busy) return;
        EditHistory.Clear();Busy = true; OperationCancellation = new(); Status = "扫描照片文件…";
        try
        {
            var outputs = await System.Threading.Tasks.Task.Run(() => Store.LoadExports().Where(e=>e.RootCreated).Select(e=>e.Root).ToList());
            await new ScanService().ScanAsync(Task, outputs, new Progress<string>(s => Status = s), OperationCancellation.Token);
            Refresh();if(selectedIds.Count==0&&Current!=null)Select(Current,false,false); ImagesInvalidated?.Invoke();LoadDetails(); Status = $"扫描完成 · {Task.Diagnostics.Count} 条提示（在扫描清单中查看）";
        }
        catch (OperationCanceledException) { Status = "扫描已取消，原选片记录保留"; }
        catch (Exception ex) { Status = "扫描失败：" + ex.Message; }
        finally { Busy = false; OperationCancellation.Dispose(); OperationCancellation = null; Dirty(); }
    }
    public void Refresh(bool dirty = true)
    {
        if (Task == null) return;
        var next = Task.Photos.Where(Task.Filter.Matches).OrderBy(p=>p,new NaturalPhotoComparer(Task.SortByTime)).ToList();
        var previous = Current;
        refreshing = true;
        try { Visible.Clear(); foreach (var p in next) Visible.Add(p); }
        finally { refreshing = false; }
        var visibleIds=next.Select(p=>p.Id).ToHashSet();selectedIds.RemoveWhere(id=>!visibleIds.Contains(id));
        Current = previous != null && next.Contains(previous) ? previous : next.FirstOrDefault();
        SelectionChanged();
        Raise(nameof(Current));
        RebuildRows(); UpdateStatistics();
        Raise(nameof(StarCounts)); Raise(nameof(Position)); Raise(nameof(CurrentTitle)); Raise(nameof(CurrentPaths));
        if (dirty) Dirty();
    }
    private void UpdateStatistics()
    {
        if(Task==null)return;
        Stats = $"{Task.Photos.Count} 组可浏览 JPG · 保留 {Task.Photos.Count(p => p.Decision == Decision.Keep)}\n"
            + $"配对 {Task.Photos.Count(p => p.RawPath != null)} · 仅 JPG {Task.Photos.Count(p => p.RawPath == null && p.PairState != PairState.Conflict)} · 仅 RAW {Task.RawOnly.Count}\n"
            + $"冲突 {Task.Photos.Count(p => p.PairState == PairState.Conflict)} · 读取错误 {Task.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error && !d.Message.StartsWith("已跳过"))} · 警告 {Task.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning)}\n"
            + $"实际文件 {Task.Files.Count}";
    }
    public void RebuildRows()
    {
        int columns = Math.Max(1, (int)(GridWidth / (ThumbnailSize + 12)));
        RebuildingRows=true;
        try { Rows.Clear(); foreach (var row in Visible.Chunk(columns)) Rows.Add(new(row.ToList(), columns)); }
        finally { RebuildingRows=false; }
        RowsRebuilt?.Invoke();
    }
    public int SelectedCount => selectedIds.Count;
    public bool HasSelection => SelectedCount>0;
    public IReadOnlyList<Photo> SelectedPhotos => Visible.Where(p=>selectedIds.Contains(p.Id)).ToList();
    private readonly HashSet<string> selectedIds=[];
    private string? anchorId,referenceId;
    public void Select(Photo p,bool ctrl,bool shift)
    {
        if(Task==null||!Visible.Contains(p))return;
        if(shift&&anchorId!=null)
        {
            int a=Visible.ToList().FindIndex(x=>x.Id==anchorId),b=Visible.IndexOf(p);
            if(!ctrl)selectedIds.Clear();
            for(int i=Math.Max(0,Math.Min(a,b));i<=Math.Max(a,b);i++)selectedIds.Add(Visible[i].Id);
        }
        else
        {
            if(!ctrl)selectedIds.Clear();
            if(ctrl&&selectedIds.Contains(p.Id))selectedIds.Remove(p.Id);else selectedIds.Add(p.Id);
            anchorId=p.Id;
        }
        Current=p;SelectionChanged();
    }
    public void SelectFromFilm(Photo photo, bool ctrl, bool shift)
    {
        if (Busy || !Visible.Contains(photo)) return;
        if (!ctrl && !shift) ViewIndex = 0;
        Select(photo, ctrl, shift);
    }
    public void SelectAll()
    {
        selectedIds.Clear();foreach(var p in Visible)selectedIds.Add(p.Id);
        anchorId=Current?.Id;SelectionChanged();
    }
    public void ClearSelection(){selectedIds.Clear();SelectionChanged();}
    private void SelectionChanged()
    {
        if(Task==null)return;
        foreach(var p in Task.Photos){bool next=selectedIds.Contains(p.Id);if(p.IsSelected!=next){p.IsSelected=next;p.NotifySelection();}}
        if(IsCompare)SyncComparison();
        Raise(nameof(SelectedCount));Raise(nameof(HasSelection));Raise(nameof(Position));Raise(nameof(PhotoPosition));Raise(nameof(OperationScope));
    }
    public void Step(int delta)
    {
        if(Visible.Count==0)return;
        if(IsDual)
        {
            var reference=Comparison[0];int index=Visible.IndexOf(Comparison[1]);
            for(int i=0;i<Visible.Count;i++){index=(index+delta+Visible.Count)%Visible.Count;if(Visible[index]!=reference)break;}
            if(Visible[index]==reference)return;
            selectedIds.Clear();selectedIds.Add(reference.Id);selectedIds.Add(Visible[index].Id);referenceId=reference.Id;
            Current=Visible[index];SelectionChanged();
        }
        else if(!IsCompare){int index=Math.Clamp(Visible.IndexOf(Current!)+delta,0,Visible.Count-1);Select(Visible[index],false,false);}
        else {int i=Comparison.IndexOf(Current!);Current=Comparison[(i+delta+Comparison.Count)%Comparison.Count];}
    }
    public bool Compare(int mode=3)
    {
        if(SelectedCount<2||SelectedCount>9){Status="请先选中 2–9 张照片，再进入对比";return false;}
        ViewIndex=3;SyncComparison();if(Current==null||!Comparison.Contains(Current))Current=Comparison.Last();return true;
    }
    private void SyncComparison()
    {
        if(Task==null)return;
        var chosen=SelectedPhotos.ToList();
        if(chosen.Count<2||chosen.Count>9)
        {
            Comparison.Clear();ViewIndex=chosen.Count>9?1:0;
            Status=chosen.Count>9?"已保留全部选中项；对比最多九张，可减少选择后进入对比":"少于两张，已返回大图";
            Current=chosen.Contains(Current!)?Current:chosen.FirstOrDefault()??Visible.FirstOrDefault();
            SaveComparison();return;
        }
        if(chosen.Count==2)
        {
            var reference=chosen.Find(p=>p.Id==referenceId)??chosen[0];referenceId=reference.Id;
            chosen.Remove(reference);chosen.Insert(0,reference);
        }
        if(!Comparison.Select(p=>p.Id).SequenceEqual(chosen.Select(p=>p.Id))){Comparison.Clear();foreach(var p in chosen)Comparison.Add(p);}
        foreach(var p in Task.Photos){p.CompareRole=chosen.Count==2&&chosen.Contains(p)?p==chosen[0]?"参考图":"候选图":"";p.NotifySelection();}
        if(Current==null||!chosen.Contains(Current))Current=chosen.Last();
        Raise(nameof(IsDual));Raise(nameof(CompareColumns));Raise(nameof(CompareRows));SaveComparison();
    }
    public void RemoveCompare(Photo p){selectedIds.Remove(p.Id);SelectionChanged();}
    public void ReferenceCurrent(){if(!IsDual||Current==null||!Comparison.Contains(Current))return;referenceId=Current.Id;SyncComparison();Status="参考图已固定；方向键轮换候选图";}
    private void SaveComparison(){if(Task!=null){Task.ComparisonIds=Comparison.Select(p=>p.Id).ToList();Dirty();}}
    public void Apply(int? stars=null,Decision? decision=null)
    {
        if(Task==null||SelectedCount==0){Status="请先选中照片";return;}
        var targets=IsCompare&&!CompareLinkedRating?(Current!=null&&Comparison.Contains(Current)?new List<Photo>{Current}:[]):SelectedPhotos.ToList();
        if(targets.Count==0)return;EditHistory.ApplyRatings(targets,stars,decision);
        Task.OperationLog.Add($"{DateTime.Now:O} 已选 {targets.Count} 组：评分 {stars?.ToString()??"不变"}，取舍 {decision?.ToString()??"不变"}");
        if(AutoAdvance&&IsLarge&&targets.Count==1)Step(1);
        Refresh();
    }
    public void Undo(){if(Task==null)return;var label=EditHistory.Undo(Task);if(label==null)return;Task.OperationLog.Add($"{DateTime.Now:O} 撤回 {label}");Refresh();Status="已撤回："+label;}
    public void Dirty() { if (Task == null) return; revision++; SaveStatus = "待保存…"; saveTimer.Stop(); saveTimer.Start(); }
    public async System.Threading.Tasks.Task<bool> FlushSaveAsync()
    {
        saveTimer.Stop(); if (Task == null) return true;
        long savedRevision = revision;
        // Snapshot on UI thread; SQLite and serialization of the snapshot never race with UI edits.
        var snapshot = JsonSerializer.Deserialize<PhotoTask>(JsonSerializer.Serialize(Task))!;
        await saveLock.WaitAsync();
        try
        {
            await System.Threading.Tasks.Task.Run(() => Store.Save(snapshot));
            if (revision == savedRevision) { SaveStatus = "已保存"; SaveError = ""; }
            return true;
        }
        catch (Exception ex) { SaveError = SaveStatus = "保存失败：" + ex.Message; return false; }
        finally { saveLock.Release(); }
    }
    private async void LoadDetails()
    {
        detailsCancellation?.Cancel(); var cts = detailsCancellation = new(); var p = Current; var owner = Task?.Id;
        Histogram = null; Metadata = p == null ? "—" : "读取拍摄信息…"; HistogramStatus = p == null ? "" : "计算原始 JPG 全像素…";
        if (p == null) return;
        try
        {
            try
            {
                var info = await System.Threading.Tasks.Task.Run(()=>MetadataService.Read(p.JpgPath), cts.Token);
                if (cts.IsCancellationRequested || Task?.Id != owner || Current != p) return;
                Metadata = info.Text; p.ShotTime = info.ShotTime;
            }
            catch(Exception ex) when(ex is not OperationCanceledException)
            {if(cts.IsCancellationRequested||Task?.Id!=owner||Current!=p)return;Metadata="部分拍摄信息无法读取："+ex.Message;}
            var data = await ImageService.Shared.HistogramAsync(p.JpgPath, cts.Token);
            if (cts.IsCancellationRequested || Task?.Id != owner || Current != p) return;
            Histogram = data; HistogramStatus = $"RGB · 各 256 分箱 · {data.Pixels:N0} 像素";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) { Metadata = "读取失败：" + ex.Message; HistogramStatus = "直方图不可用";ReadFailed(p.JpgPath,ex.Message); } }
    }
    private async void Prefetch()
    {
        prefetchCancellation?.Cancel();var cts=prefetchCancellation=new();if(Current==null||IsCompare)return;
        int index=Visible.IndexOf(Current);var paths=new[]{index+1,index-1}.Where(i=>i>=0&&i<Visible.Count).Select(i=>Visible[i].JpgPath).ToArray();
        try { await System.Threading.Tasks.Task.Delay(180,cts.Token);foreach(var path in paths)await ImageService.Shared.LoadResultAsync(path,480,cts.Token,3); }
        catch(OperationCanceledException){}catch(Exception ex){System.Diagnostics.Trace.WriteLine("相邻预读："+ex.Message);}
    }
    private async System.Threading.Tasks.Task ReadShotTimes()
    {
        if (Task == null || Busy) return; Busy = true; OperationCancellation = new(); var next = Task;
        try
        {
            var photos = next.Photos.ToList(); var ct = OperationCancellation.Token;
            await System.Threading.Tasks.Task.Run(()=>
            {
                foreach (var p in photos) { ct.ThrowIfCancellationRequested(); try { p.ShotTime = MetadataService.Read(p.JpgPath).ShotTime; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MetadataExtractor.ImageProcessingException) { System.Diagnostics.Trace.WriteLine(ex); } }
            }, ct);
            Refresh(); Status = "已按拍摄时间排序，缺失时回退修改时间";
        }
        catch(OperationCanceledException) { Status = "时间读取已取消"; }
        finally { Busy = false; OperationCancellation.Dispose(); OperationCancellation = null; }
    }
}
