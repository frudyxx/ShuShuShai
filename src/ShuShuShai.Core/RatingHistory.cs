namespace ShuShuShai.Core;
public sealed class RatingHistory
{
    private readonly Stack<List<(Photo Photo, int Stars, Decision Decision)>> history = new();
    public void Apply(IEnumerable<Photo> photos, int? stars = null, Decision? decision = null)
    {
        var list = photos.Distinct().Select(p => (Photo: p, p.Stars, p.Decision)).ToList();
        if (list.Count == 0) return;
        history.Push(list);
        foreach (var (p, _, _) in list) { if (stars.HasValue) p.Stars = stars.Value; if (decision.HasValue) p.Decision = decision.Value; }
    }
    public bool Undo()
    {
        if (!history.TryPop(out var list)) return false;
        foreach (var (p, stars, decision) in list) { p.Stars = stars; p.Decision = decision; } return true;
    }
    public void Clear() => history.Clear();
}
