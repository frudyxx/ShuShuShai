namespace ShuShuShai.Core;

public class ScanService
{
    public Task ScanAsync(PhotoTask task, IReadOnlyList<string> knownOutputs, IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var files = new Dictionary<string, FileRecord>(PathRules.Comparer);
        var seen = new HashSet<string>(PathRules.Comparer);
        var errors = new List<Diagnostic>();
        foreach(var source in task.Sources)
        {
            List<string> directories;
            if(source.Directories!=null) directories=source.Directories;
            else if(source.Recursive)
            {
                var inventory=DirectoryDiscovery.Discover(source.Path,knownOutputs,ct);
                directories=inventory.Directories;errors.AddRange(inventory.Diagnostics);
            }
            else directories=[source.Path];
            foreach(var directory in directories)
            {
                ct.ThrowIfCancellationRequested();var dir=PathRules.Normalize(directory);
                if(!seen.Add(dir))continue;
                try
                {
                    if(!PathRules.IsWithin(dir,source.Path)){errors.Add(new(dir,"目录不在所选主目录范围内，未扫描"));continue;}
                    if((File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0){errors.Add(new(dir,"已跳过重解析目录"));continue;}
                    if(knownOutputs.Any(p=>PathRules.IsWithin(dir,p))){errors.Add(new(dir,"已跳过本机已知导出目录"));continue;}
                    foreach(var path in Directory.EnumerateFiles(dir))
                    {
                        ct.ThrowIfCancellationRequested();
                        if(!PathRules.IsJpg(path)&&!PathRules.RawExtensions.Contains(Path.GetExtension(path)))continue;
                        try
                        {
                            var f=new FileInfo(path);
                            if((f.Attributes&FileAttributes.ReparsePoint)!=0){errors.Add(new(path,"已跳过重解析文件"));continue;}
                            files.TryAdd(f.FullName,new(f.FullName,f.Length,f.LastWriteTimeUtc,!PathRules.IsJpg(path)));
                            if(files.Count%100==0)progress?.Report($"已发现 {files.Count} 个照片文件…");
                        }
                        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){errors.Add(new(path,ex.Message));}
                    }
                }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){errors.Add(new(dir,ex.Message));}
            }
        }
        ct.ThrowIfCancellationRequested();
        var prior=task.Files.Where(f=>!files.ContainsKey(f.Path)).ToList();
        foreach(var f in prior)errors.Add(new(f.Path,"文件缺失或不在本次可读取范围；原选片记录保留"));
        task.Files=files.Values.Concat(prior).ToList();task.Diagnostics=errors;PairingService.Rebuild(task);
    },ct);
}
public static class PairingService
{
    public static void Rebuild(PhotoTask task,bool preserveExisting=false)
    {
        var old = task.Photos.ToDictionary(p => p.JpgPath, PathRules.Comparer);
        var usedRaw = new HashSet<string>(PathRules.Comparer);
        var photos = new List<Photo>();
        foreach (var f in task.Files.Where(f => !f.IsRaw))
        {
            var p = old.GetValueOrDefault(f.Path) ?? new Photo { JpgPath = f.Path };
            p.ModifiedUtc = f.ModifiedUtc; p.JpgLength = f.Length;
            if ((p.PairState == PairState.ConfirmedPair || preserveExisting&&p.PairState==PairState.Paired) && p.RawPath != null) usedRaw.Add(p.RawPath);
            else if (p.PairState != PairState.ConfirmedJpgOnly) { p.RawPath = null; p.PairState = PairState.JpgOnly; }
            p.Candidates = []; photos.Add(p);
        }
        var raws = task.Files.Where(f => f.IsRaw && !usedRaw.Contains(f.Path)).ToList();
        var localRaw = raws.GroupBy(r=>Path.Combine(Path.GetDirectoryName(r.Path)!,Path.GetFileNameWithoutExtension(r.Path)),PathRules.Comparer).ToDictionary(g=>g.Key,g=>g.ToList(),PathRules.Comparer);
        var namedRaw = raws.GroupBy(r=>Path.GetFileNameWithoutExtension(r.Path),PathRules.Comparer).ToDictionary(g=>g.Key,g=>g.ToList(),PathRules.Comparer);
        var locked = photos.Where(p => p.PairState is PairState.ConfirmedPair or PairState.ConfirmedJpgOnly || preserveExisting&&p.PairState==PairState.Paired).ToHashSet();
        var ambiguous = new HashSet<Photo>();
        var local = photos.Where(p => !locked.Contains(p)).GroupBy(p => Path.Combine(Path.GetDirectoryName(p.JpgPath)!, Path.GetFileNameWithoutExtension(p.JpgPath)), PathRules.Comparer);
        foreach (var g in local)
        {
            var rr = localRaw.GetValueOrDefault(g.Key) ?? [];
            if (g.Count() == 1 && rr.Count == 1) { var p = g.Single(); p.RawPath = rr[0].Path; p.PairState = PairState.Paired; usedRaw.Add(rr[0].Path); }
            else if (g.Count() > 1 || rr.Count > 1) foreach (var p in g) ambiguous.Add(p);
        }
        foreach (var g in photos.Where(p => !locked.Contains(p) && p.RawPath == null).GroupBy(p => Path.GetFileNameWithoutExtension(p.JpgPath), PathRules.Comparer))
        {
            var rr = (namedRaw.GetValueOrDefault(g.Key) ?? []).Where(r => !usedRaw.Contains(r.Path)).ToList();
            if (g.Count() == 1 && rr.Count == 1 && !ambiguous.Contains(g.Single()))
            { var p = g.Single(); p.RawPath = rr[0].Path; p.PairState = PairState.Paired; usedRaw.Add(rr[0].Path); }
            else if (rr.Count > 0 || g.Count() > 1 || g.Any(ambiguous.Contains))
                foreach (var p in g) { p.PairState = PairState.Conflict; p.Candidates = rr.Select(r => r.Path).ToList(); }
        }
        task.Photos = photos;
        task.RawOnly = task.Files.Where(f => f.IsRaw && !usedRaw.Contains(f.Path)).Select(f => f.Path).ToList();
    }
    public static void Confirm(PhotoTask task, Photo p, string? raw)
    {
        if (raw != null && task.Photos.Any(other => other.Id != p.Id && other.RawPath != null && PathRules.Same(other.RawPath, raw)))
            throw new InvalidOperationException("这个 RAW 已属于另一组照片，请先解除该组关系。");
        p.RawPath = raw; p.PairState = raw == null ? PairState.ConfirmedJpgOnly : PairState.ConfirmedPair;
        Rebuild(task);
    }
    public static AutoPairSummary AutoPair(PhotoTask task)
    {
        var old=task.Photos.ToDictionary(p=>p.Id,p=>p.RawPath);
        Rebuild(task,true);
        return new(task.Photos.Count(p=>p.RawPath!=null&&old.GetValueOrDefault(p.Id)==null),task.Photos.Count(p=>p.PairState==PairState.Conflict),task.Photos.Count(p=>p.RawPath==null),task.RawOnly.Count);
    }
}
public record AutoPairSummary(int Added,int Conflicts,int JpgOnly,int RawOnly)
{public override string ToString()=>$"新增配对 {Added} 组 · 待处理 {Conflicts} · 未关联 JPG {JpgOnly} · 仅 RAW {RawOnly}";}
