using System.Collections.ObjectModel;
using System.IO;
using ShuShuShai.Core;
namespace ShuShuShai.App.ViewModels;
public sealed class FolderNode : Observable
{
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(PathRules.Normalize(Path)) is {Length:>0} name?name:Path;
    public FolderNode? Parent { get; }
    public ObservableCollection<FolderNode> Children { get; }=[];
    public bool IsRoot=>Parent==null;
    public bool Selectable { get; set; }=true;
    private bool expanded;
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
    private bool selected;
    public bool IsSelected { get=>selected;set{if(Set(ref selected,value)){Changed?.Invoke(this);for(var p=Parent;p!=null;p=p.Parent)p.Raise(nameof(AllChildrenSelected));}} }
    public event Action<FolderNode>? Changed;
    public bool? AllChildrenSelected { get {var nodes=Descendants().Where(n=>n.Selectable).ToList();int count=nodes.Count(n=>n.IsSelected);return count==0?false:count==nodes.Count?true:null;} }
    public FolderNode(string path,FolderNode? parent=null){Path=PathRules.Normalize(path);Parent=parent;expanded=IsRoot;}
    public IEnumerable<FolderNode> Descendants(){foreach(var c in Children){yield return c;foreach(var d in c.Descendants())yield return d;}}
    public IEnumerable<FolderNode> All(){yield return this;foreach(var n in Descendants())yield return n;}
    public void ToggleAllChildren(){bool next=AllChildrenSelected!=true;foreach(var n in Descendants().Where(n=>n.Selectable))n.IsSelected=next;Raise(nameof(AllChildrenSelected));}
    public static FolderNode Build(string root,IEnumerable<string> paths,IEnumerable<string> selected,bool restrictToPaths=false)
    {
        var node=new FolderNode(root);var map=new Dictionary<string,FolderNode>(PathRules.Comparer){{node.Path,node}};
        var actual=paths.Select(PathRules.Normalize).ToHashSet(PathRules.Comparer);
        foreach(var path in actual.OrderBy(p=>p.Length).ThenBy(p=>p,PathRules.Comparer))
        {
            if(!PathRules.IsWithin(path,node.Path))continue;
            Ensure(path);
        }
        FolderNode Ensure(string path)
        {
            if(map.TryGetValue(path,out var found))return found;
            string parent=System.IO.Path.GetDirectoryName(path)!;var p=Ensure(parent);var child=new FolderNode(path,p);p.Children.Add(child);map[path]=child;return child;
        }
        var chosen=selected.Select(PathRules.Normalize).ToHashSet(PathRules.Comparer);
        foreach(var n in node.All()){n.Selectable=!restrictToPaths||actual.Contains(n.Path);n.IsSelected=n.Selectable&&chosen.Contains(n.Path);}
        return node;
    }
}
