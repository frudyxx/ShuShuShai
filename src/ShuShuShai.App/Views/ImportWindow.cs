using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ShuShuShai.Core;
using ShuShuShai.App.Controls;
using ShuShuShai.App.ViewModels;
namespace ShuShuShai.App.Views;
public class ImportWindow : Window
{
    public FolderTree FoldersTree { get; }=new(){ImportControls=true};
    private readonly TextBlock status=DialogUi.Text("主目录本层默认选中，子目录默认不选。勾选仅控制该层照片。",12);
    private readonly List<Diagnostic> diagnostics=[];
    public List<Diagnostic> Diagnostics=>diagnostics.ToList();
    public List<SourceFolder> Folders=>FoldersTree.Roots.Select(r=>new SourceFolder(r.Path,false,r.All().Where(n=>n.IsSelected).Select(n=>n.Path).ToList())).Where(s=>s.Directories!.Count>0).ToList();
    private readonly Dictionary<string,CancellationTokenSource> pending=new(PathRules.Comparer);
    private readonly IReadOnlyList<string> outputs;
    private readonly Button scan;
    private bool syncing;
    public ImportWindow(IReadOnlyList<string>? knownOutputs=null)
    {
        outputs=knownOutputs??[];DialogUi.Setup(this,"选择要导入的目录",850,680);
        var grid=new Grid{Margin=new Thickness(20)};grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new());grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var header=new StackPanel();header.Children.Add(DialogUi.Text("添加主目录，再逐层选择照片文件夹",20));header.Children.Add(status);grid.Children.Add(header);
        Grid.SetRow(FoldersTree,1);grid.Children.Add(FoldersTree);FoldersTree.RemoveRoot+=RemoveRoot;
        scan=DialogUi.Button("扫描选中目录",(_,_)=>{if(Folders.Count>0&&pending.Count==0)DialogResult=true;else status.Text=pending.Count>0?"正在发现子目录，可取消或稍后开始扫描。":"请至少勾选一个目录。";},"查找勾选目录中可导入的 JPG 和 RAW。");
        var footer=DialogUi.Row(DialogUi.Button("＋ 添加文件夹",(_,_)=>Browse(),"选择一个或多个主目录。"),scan,DialogUi.Button("取消",(_,_)=>Close(),"退出本次目录选择。"));Grid.SetRow(footer,2);grid.Children.Add(footer);Content=grid;
        Closed+=(_,_)=>{foreach(var c in pending.Values)c.Cancel();};
    }
    private void Browse(){var d=new OpenFolderDialog{Title="选择照片主目录（可多选）",Multiselect=true};if(d.ShowDialog(this)==true)foreach(var path in d.FolderNames)_=AddRootAsync(path);}
    public async System.Threading.Tasks.Task AddRootAsync(string path)
    {
        path=PathRules.Normalize(path);if(FoldersTree.Roots.Any(r=>PathRules.Same(r.Path,path)))return;
        var root=new FolderNode(path){IsSelected=true};FoldersTree.Roots.Add(root);root.Changed+=SelectionChanged;
        var cts=new CancellationTokenSource();pending[path]=cts;scan.IsEnabled=false;status.Text="正在后台发现所有子目录…";
        try
        {
            var inventory=await DirectoryDiscovery.DiscoverAsync(path,outputs,cts.Token);
            if(cts.IsCancellationRequested||!FoldersTree.Roots.Contains(root))return;
            var chosen=FoldersTree.Roots.SelectMany(r=>r.All()).Where(n=>n.IsSelected).Select(n=>n.Path).ToHashSet(PathRules.Comparer);
            var replacement=FolderNode.Build(path,inventory.Directories,chosen,true);
            replacement.IsExpanded=root.IsExpanded;
            foreach(var n in replacement.All())n.Changed+=SelectionChanged;
            FoldersTree.Roots[FoldersTree.Roots.IndexOf(root)]=replacement;
            foreach(var n in replacement.All())SelectionChanged(n);
            diagnostics.AddRange(inventory.Diagnostics);
            status.Text=$"发现 {inventory.Directories.Count} 个目录；提示 {diagnostics.Count} 条。可单独勾选每一层。";
            if(diagnostics.Count>0)status.ToolTip=string.Join("\n",diagnostics.Select(d=>d.Path+"\n"+d.Message));
        }
        catch(OperationCanceledException){}
        catch(Exception ex){status.Text="发现目录失败："+ex.Message;diagnostics.Add(new(path,ex.Message));}
        finally{if(pending.TryGetValue(path,out var existing)&&existing==cts)pending.Remove(path);cts.Dispose();scan.IsEnabled=pending.Count==0;}
    }
    private void SelectionChanged(FolderNode changed)
    {
        if(syncing)return;syncing=true;
        try{foreach(var n in FoldersTree.Roots.SelectMany(r=>r.All()).Where(n=>n!=changed&&PathRules.Same(n.Path,changed.Path)))n.IsSelected=changed.IsSelected;}
        finally{syncing=false;}
    }
    private void RemoveRoot(FolderNode root){if(pending.TryGetValue(root.Path,out var c))c.Cancel();FoldersTree.Roots.Remove(root);status.Text="已移除此待导入来源；磁盘文件保留。";}
}
