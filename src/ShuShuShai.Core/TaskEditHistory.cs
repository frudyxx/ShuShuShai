namespace ShuShuShai.Core;

public sealed class TaskEditHistory
{
    private record PhotoState(string Id,int Stars,Decision Decision,string? Raw,PairState Pair,List<string> Candidates);
    private record Change(string Label,List<PhotoState> Before,List<string>? RawOnly);
    private readonly Stack<Change> changes=[];
    public event Action? Changed;
    public bool CanUndo=>changes.Count>0;
    public string NextLabel=>changes.TryPeek(out var next)?next.Label:"没有可撤回的操作";
    private static PhotoState Capture(Photo p)=>new(p.Id,p.Stars,p.Decision,p.RawPath,p.PairState,[..p.Candidates]);
    public bool ApplyRatings(IEnumerable<Photo> photos,int? stars,Decision? decision)
    {
        var targets=photos.DistinctBy(p=>p.Id).Where(p=>(stars.HasValue&&p.Stars!=stars.Value)||(decision.HasValue&&p.Decision!=decision.Value)).ToList();
        if(targets.Count==0)return false;
        changes.Push(new($"{targets.Count} 组照片的{(stars.HasValue?"评分":"取舍")}",targets.Select(Capture).ToList(),null));
        foreach(var p in targets){if(stars.HasValue)p.Stars=stars.Value;if(decision.HasValue)p.Decision=decision.Value;}Changed?.Invoke();return true;
    }
    public bool ApplyPairing(PhotoTask task,string label,Action operation)
    {
        var before=task.Photos.Select(Capture).ToList();var raws=task.RawOnly.ToList();
        try { operation(); }
        catch { Restore(task,before,raws,true);throw; }
        var map=task.Photos.ToDictionary(p=>p.Id);
        var delta=before.Where(s=>map.TryGetValue(s.Id,out var p)&&(p.RawPath!=s.Raw||p.PairState!=s.Pair||!p.Candidates.SequenceEqual(s.Candidates,PathRules.Comparer))).ToList();
        if(delta.Count==0&&raws.SequenceEqual(task.RawOnly,PathRules.Comparer))return false;
        changes.Push(new(label,delta,raws));Changed?.Invoke();return true;
    }
    private static void Restore(PhotoTask task,List<PhotoState> states,List<string>? raws,bool pairing)
    {
        var map=task.Photos.ToDictionary(p=>p.Id);
        foreach(var s in states)if(map.TryGetValue(s.Id,out var p))
        {
            if(pairing){p.RawPath=s.Raw;p.PairState=s.Pair;p.Candidates=[..s.Candidates];p.NotifyAll();}
            else {p.Stars=s.Stars;p.Decision=s.Decision;}
        }
        if(raws!=null)task.RawOnly=[..raws];
    }
    public string? Undo(PhotoTask task)
    {
        if(!changes.TryPop(out var entry))return null;
        Restore(task,entry.Before,entry.RawOnly,entry.RawOnly!=null);Changed?.Invoke();return entry.Label;
    }
    public void Clear(){changes.Clear();Changed?.Invoke();}
}
