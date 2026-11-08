using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ShuShuShai.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(name); return true; }
    public void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public enum Decision { Undecided, Keep, Reject }
public enum PairState { JpgOnly, Paired, Conflict, ConfirmedJpgOnly, ConfirmedPair }
public record SourceFolder(string Path, bool Recursive, List<string>? Directories = null);
public record FileRecord(string Path, long Length, DateTime ModifiedUtc, bool IsRaw);
public enum DiagnosticSeverity { Error, Warning, Information }
public record Diagnostic(string Path, string Message,DiagnosticSeverity Severity=DiagnosticSeverity.Error,string? Code=null);
public class Photo : Observable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string JpgPath { get; set; } = "";
    public string? RawPath { get; set; }
    public string? OriginalJpgPath { get; set; }
    public string? OriginalRawPath { get; set; }
    public PairState PairState { get; set; }
    public List<string> Candidates { get; set; } = [];
    public DateTime ModifiedUtc { get; set; }
    public DateTime? ShotTime { get; set; }
    public long JpgLength { get; set; }
    private int stars;
    public int Stars { get => stars; set { if (Set(ref stars, Math.Clamp(value, 0, 5))) {Raise(nameof(StarDisplay));Raise(nameof(Caption));Raise(nameof(FilmCaption));} } }
    private Decision decision;
    public Decision Decision { get => decision; set { if (Set(ref decision, value)) { Raise(nameof(Caption)); Raise(nameof(FilmCaption));Raise(nameof(DecisionText)); } } }
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(JpgPath);
    [JsonIgnore] public string SourceDirectory => System.IO.Path.GetDirectoryName(OriginalJpgPath ?? JpgPath) ?? "";
    [JsonIgnore] public string SelectionText => IsSelected ? "✓ 已选" : "";
    [JsonIgnore] public string ActiveText => IsActive ? "当前" : "";
    [JsonIgnore] public string CompareRole { get; set; } = "";
    [JsonIgnore] public string PairText => PairState switch { PairState.Conflict => "配对待处理", PairState.Paired or PairState.ConfirmedPair => "JPG + RAW", PairState.ConfirmedJpgOnly => "已确认仅 JPG", _ => "仅 JPG" };
    [JsonIgnore] public string DecisionText => Decision switch { Decision.Keep => "保留", Decision.Reject => "淘汰", _ => "未定" };
    [JsonIgnore] public string StarDisplay => new string('★', Stars) + new string('☆', 5 - Stars);
    [JsonIgnore] public string FilmCaption => $"{StarDisplay} · {DecisionText}";
    [JsonIgnore] public string Caption => $"{FilmCaption} · {PairText}";
    [JsonIgnore] public bool IsSelected { get; set; }
    [JsonIgnore] public bool IsActive { get; set; }
    public void NotifyAll() { Raise(nameof(Name)); Raise(nameof(JpgPath)); Raise(nameof(PairText)); Raise(nameof(Caption)); NotifySelection(); }
    public void NotifySelection() { Raise(nameof(IsSelected)); Raise(nameof(IsActive)); Raise(nameof(SelectionText)); Raise(nameof(ActiveText)); Raise(nameof(CompareRole)); }
}
public class FilterSettings
{
    public int Mode { get; set; } // 0 all, 1 unrated, 2 >=, 3 <=, 4 ==
    public int Stars { get; set; } = 3;
    public bool IncludeUnrated { get; set; }
    public int DecisionIndex { get; set; }
    public string Folder { get; set; } = "";
    public bool SpecificSources { get; set; }
    public List<string> Directories { get; set; } = [];
    public bool Matches(Photo p) => (SpecificSources ? Directories.Contains(p.SourceDirectory, PathRules.Comparer) : string.IsNullOrEmpty(Folder) || PathRules.IsWithin(p.OriginalJpgPath ?? p.JpgPath, Folder)) &&
        (DecisionIndex == 0 || (int)p.Decision == DecisionIndex - 1) && (Mode switch
        { 0 => true, 1 => p.Stars == 0, _ when p.Stars == 0 => IncludeUnrated,
          2 => p.Stars >= Stars, 3 => p.Stars <= Stars, 4 => p.Stars == Stars, _ => true });
}
public class PhotoTask
{
    public int SchemaVersion { get; set; } = 3;
    public bool CompareLinkedRating { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新的选片任务";
    public DateTime LastUsed { get; set; } = DateTime.Now;
    public ObservableCollection<SourceFolder> Sources { get; set; } = [];
    public List<FileRecord> Files { get; set; } = [];
    public List<Photo> Photos { get; set; } = [];
    public List<string> RawOnly { get; set; } = [];
    public List<Diagnostic> Diagnostics { get; set; } = [];
    public FilterSettings Filter { get; set; } = new();
    public bool SortByTime { get; set; }
    public string? CurrentId { get; set; }
    public ExportOptions ExportOptions { get; set; } = new();
    public List<string> OperationLog { get; set; } = [];
    public List<string> ComparisonIds { get; set; } = [];
    public double GridOffset { get; set; }
    public double ThumbnailSize { get; set; } = 190;
    public int ViewIndex { get; set; }
    public bool AutoAdvance { get; set; }
    [JsonIgnore] public string SourceTitle => Sources.Count==0 ? Name : string.Join(" ＋ ", Sources.Select(s=>System.IO.Path.GetFileName(PathRules.Normalize(s.Path)) is {Length:>0} n ? n : s.Path));
    [JsonIgnore] public string HistoryText => $"{LastUsed:MM-dd HH:mm} · {Photos.Count} 组 JPG · 保留 {Photos.Count(p => p.Decision == Decision.Keep)} · 已评分 {Photos.Count(p => p.Stars > 0)}";
    [JsonIgnore] public string SourceDetails => string.Join("\n",Sources.Select((s,i)=>$"{(i==0?"首个来源":"来源 "+(i+1))}：{s.Path}（{s.Directories?.Count ?? 1} 个目录）"));
    [JsonIgnore] public List<Photo?> HistoryPreviews => Photos.OrderBy(p=>p,new NaturalPhotoComparer(SortByTime)).Take(6).Cast<Photo?>().Concat(Enumerable.Repeat<Photo?>(null,6)).Take(6).ToList();
}
public static class PathRules
{
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Same(string a, string b) => Comparer.Equals(Normalize(a), Normalize(b));
    public static bool IsWithin(string path, string folder) => Same(path, folder) || Normalize(path).StartsWith(Normalize(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static readonly HashSet<string> RawExtensions = new([".arw", ".sr2", ".srf", ".crw", ".cr2", ".cr3", ".nef", ".nrw", ".raf", ".dng"], Comparer);
    public static bool IsJpg(string path) => Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
}
public sealed class NaturalPhotoComparer(bool time) : IComparer<Photo>
{
    public int Compare(Photo? x, Photo? y)
    {
        if (x == null || y == null) return x == y ? 0 : x == null ? -1 : 1;
        var c = time ? DateTime.Compare(x.ShotTime ?? x.ModifiedUtc.ToLocalTime(), y.ShotTime ?? y.ModifiedUtc.ToLocalTime()) : Natural(x.Name, y.Name);
        return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(x.JpgPath, y.JpgPath);
    }
    public static int Natural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int a0 = i, b0 = j; while (i < a.Length && char.IsAsciiDigit(a[i])) i++; while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                string an = a[a0..i].TrimStart('0'), bn = b[b0..j].TrimStart('0');
                int n = an.Length.CompareTo(bn.Length); if (n == 0) n = string.CompareOrdinal(an, bn); if (n != 0) return n;
            }
            else { int n = char.ToUpperInvariant(a[i++]).CompareTo(char.ToUpperInvariant(b[j++])); if (n != 0) return n; }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
