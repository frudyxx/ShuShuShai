namespace ShuShuShai.Core;
public record DirectoryInventory(List<string> Directories, List<Diagnostic> Diagnostics);
public static class DirectoryDiscovery
{
    public static Task<DirectoryInventory> DiscoverAsync(string root, IReadOnlyList<string> knownOutputs, CancellationToken ct) => Task.Run(()=>Discover(root,knownOutputs,ct),ct);
    public static DirectoryInventory Discover(string root, IReadOnlyList<string> knownOutputs, CancellationToken ct)
    {
        var paths=new List<string>();var diagnostics=new List<Diagnostic>();var seen=new HashSet<string>(PathRules.Comparer);var stack=new Stack<string>();stack.Push(PathRules.Normalize(root));
        while(stack.TryPop(out var dir))
        {
            ct.ThrowIfCancellationRequested();if(!seen.Add(dir))continue;
            try
            {
                if((File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0){diagnostics.Add(new(dir,"已跳过重解析目录"));continue;}
                if(knownOutputs.Any(o=>PathRules.IsWithin(dir,o))){diagnostics.Add(new(dir,"已跳过本机已知导出目录"));continue;}
                paths.Add(dir);
                foreach(var child in Directory.EnumerateDirectories(dir).OrderByDescending(p=>p,PathRules.Comparer))stack.Push(child);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){diagnostics.Add(new(dir,ex.Message));}
        }
        return new(paths,diagnostics);
    }
}
