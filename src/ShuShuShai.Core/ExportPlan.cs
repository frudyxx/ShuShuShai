using System.Text.RegularExpressions;

namespace ShuShuShai.Core;
public class NamingRule
{
    public bool Rename { get; set; }
    public string Prefix { get; set; } = "照片";
    public long Start { get; set; } = 1;
}
public enum ExportRangeMode { ByDecision, ByStars }
public enum ExportNamingMode { Original, Linked, Independent }
public class ExportOptions
{
    public ExportRangeMode RangeMode { get; set; }
    public int StarComparison { get; set; } = 2;
    public int StarValue { get; set; } = 3;
    public bool IncludeUnrated { get; set; }
    // Null represents a task saved with the previous comparison-based controls.
    public List<int>? StarLevels { get; set; }
    public List<Decision> Decisions { get; set; } = [Decision.Keep];
    public ExportNamingMode? NamingMode { get; set; }
    public string Parent { get; set; } = "";
    public string FolderName { get; set; } = "";
    public bool Move { get; set; }
    public bool Separate { get; set; } = true;
    public bool Linked { get; set; } = true;
    public NamingRule Shared { get; set; } = new();
    public NamingRule Jpg { get; set; } = new();
    public NamingRule Raw { get; set; } = new();
}
public enum ExportFileState { Pending, Prepared, Committed, SourceRemoved }
public class ExportFile
{
    public string GroupId { get; set; } = "";
    public bool IsRaw { get; set; }
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Temp { get; set; } = "";
    public long Length { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public string? Identity { get; set; }
    public string? Sha256 { get; set; }
    public ExportFileState State { get; set; }
    public string? Error { get; set; }
    public string Preview => $"{Source}  →  {Target}";
    public string StateText => State switch { ExportFileState.Pending=>"待复制",ExportFileState.Prepared=>"临时目标已校验",ExportFileState.Committed=>"目标已提交",_=>"源文件已离开原位" };
}
public class ExportPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TaskId { get; set; } = "";
    public string Root { get; set; } = "";
    public bool Move { get; set; }
    public bool RootCreated { get; set; }
    public bool Complete { get; set; }
    public List<ExportFile> Files { get; set; } = [];
    public List<string> Messages { get; set; } = [];
    public DateTime Created { get; set; } = DateTime.Now;
    public string Summary => $"{(Move ? "移动" : "复制")} · {Files.Select(f => f.GroupId).Distinct().Count()} 组 / {Files.Count} 文件\n{Root}\n{(Complete ? "已完成" : "未完成，可核验后恢复")}";
}
public static partial class ExportPlanner
{
    public static IReadOnlyList<int> SelectedStarLevels(ExportOptions options)
    {
        if (options.StarLevels is { } levels) return levels;
        var legacy = new FilterSettings
        {
            Mode = options.StarComparison, Stars = options.StarValue, IncludeUnrated = options.IncludeUnrated
        };
        var sample = new Photo();
        return Enumerable.Range(0, 6).Where(level =>
        {
            sample.Stars = level;
            return legacy.Matches(sample);
        }).ToArray();
    }

    public static List<Photo> SelectPhotos(PhotoTask task, ExportOptions options)
    {
        var stars = options.RangeMode == ExportRangeMode.ByStars ? SelectedStarLevels(options) : null;
        return task.Photos.Where(p => stars?.Contains(p.Stars) ?? options.Decisions.Contains(p.Decision))
            .OrderBy(p => p, new NaturalPhotoComparer(task.SortByTime)).ToList();
    }
    public static ExportPlan Build(PhotoTask task, ExportOptions options)
    {
        if (task.Sources.Count == 0) throw new InvalidOperationException("请先导入照片。");
        var photos = SelectPhotos(task,options);
        if (photos.Count == 0) throw new InvalidOperationException("没有符合本次导出条件的照片，请调整星级或标记条件。");
        var conflicts = photos.Where(p => p.PairState == PairState.Conflict).ToList();
        if (conflicts.Count > 0) throw new InvalidOperationException("请先解决本次导出照片的配对冲突：\n" + string.Join("\n", conflicts.Select(p => p.JpgPath)));
        bool linked=options.NamingMode.HasValue?options.NamingMode==ExportNamingMode.Linked:options.Linked;
        bool original=options.NamingMode==ExportNamingMode.Original;
        var shared=options.NamingMode==ExportNamingMode.Linked?new NamingRule{Rename=true,Prefix=options.Shared.Prefix,Start=options.Shared.Start}:options.Shared;
        var activeRules=original?Array.Empty<NamingRule>():linked?new[]{shared}:new[]{options.Jpg,options.Raw};
        if (activeRules.Any(r=>r.Rename&&r.Start<0)) throw new InvalidOperationException("起始编号必须是非负整数。");
        string parent = string.IsNullOrWhiteSpace(options.Parent) ? task.Sources[0].Path : options.Parent;
        string name = string.IsNullOrEmpty(options.FolderName) ? $"舒舒筛_{DateTime.Now:yyyyMMdd_HHmmss}" : options.FolderName;
        ValidateName(name); parent = Path.GetFullPath(parent);
        string root = Path.Combine(parent, name); int suffix = 2;
        while (Directory.Exists(root) || File.Exists(root)) root = Path.Combine(parent, $"{name}_{suffix++}");
        var plan = new ExportPlan { TaskId = task.Id, Root = root, Move = options.Move };
        long groupIndex = 0, jpgIndex = 0, rawIndex = 0;
        foreach (var p in photos)
        {
            Add(p, p.JpgPath, false, original?new NamingRule():linked ? shared : options.Jpg, linked ? groupIndex : jpgIndex++);
            if (p.RawPath != null) Add(p, p.RawPath, true, original?new NamingRule():linked ? shared : options.Raw, linked ? groupIndex : rawIndex++);
            groupIndex++;
        }
        void Add(Photo photo, string source, bool raw, NamingRule rule, long index)
        {
            long number=rule.Rename?checked(rule.Start+index):0;
            string finalName = rule.Rename ? $"{rule.Prefix}_{number:D4}{Path.GetExtension(source)}" : Path.GetFileName(source);
            ValidateName(finalName);
            string target = Path.Combine(root, options.Separate ? raw ? "RAW" : "JPG" : "", finalName);
            foreach(var component in target[Path.GetPathRoot(target)!.Length..].Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries))ValidateName(component);
            if (target.Length >= 32760 || target.Split(Path.DirectorySeparatorChar).Any(c => c.Length > 255)) throw new InvalidOperationException($"目标路径过长：{target}");
            if (PathRules.Same(source, target)) throw new InvalidOperationException($"目标与源文件相同：{source}");
            var info = new FileInfo(source);
            if (!info.Exists) throw new FileNotFoundException($"源文件缺失，请刷新或重新定位：{source}", source);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException($"不导出重解析文件：{source}");
            plan.Files.Add(new() { GroupId = photo.Id, Source = source, Target = target, IsRaw = raw, Length = info.Length, ModifiedUtc = info.LastWriteTimeUtc, Temp = Path.Combine(Path.GetDirectoryName(target)!, $".sss-{plan.Id}-{plan.Files.Count}.tmp") });
        }
        var duplicates = plan.Files.GroupBy(f => f.Target, PathRules.Comparer).Where(g => g.Count() > 1).ToList();
        if (duplicates.Count > 0) throw new InvalidOperationException("最终名称冲突；请调整命名或选片：\n" + string.Join("\n", duplicates.Select(g => g.Key + "\n" + string.Join("\n", g.Select(f => "  " + f.Source)))));
        return plan;
    }
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith(' ') || name.EndsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Length > 255 || name is "." or ".." || Reserved().IsMatch(name))
            throw new InvalidOperationException($"Windows 文件名无效：『{name}』。请修改预览中的名称。");
    }
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase)] private static partial Regex Reserved();
}
