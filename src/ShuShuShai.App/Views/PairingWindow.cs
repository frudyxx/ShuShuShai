using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using ShuShuShai.Core;
using ShuShuShai.App.Controls;
using ShuShuShai.App.Services;
namespace ShuShuShai.App.Views;
public record RawChoice(string Path){public string Name=>System.IO.Path.GetFileName(Path);}
public class PairingWindow : Window
{
    private readonly PhotoTask task;
    private readonly ListBox items=new(),raws=new();
    private readonly PhotoViewer viewer=new();
    private readonly TextBlock description=DialogUi.Text(""),relation=DialogUi.Text(""),empty=DialogUi.Text(""),candidateHint=DialogUi.Text("选择候选后，点击“关联所选 RAW”才会更改关系。",12);
    private readonly StackPanel controls=new();
    private readonly Button[] categories;
    private int category;
    private readonly TaskEditHistory history;
    private readonly Shortcuts shortcuts;
    private readonly Action? edited;
    private readonly Button undo;
    private bool working;
    private Photo? Current=>items.SelectedItem as Photo;
    public PairingWindow(PhotoTask task,Photo? current,TaskEditHistory? history=null,Action? edited=null,Shortcuts? shortcuts=null)
    {
        this.task=task;this.history=history??new();this.edited=edited;this.shortcuts=shortcuts??new();DialogUi.Setup(this,"照片配对与扫描提示",1200,820);
        var grid=new Grid{Margin=new Thickness(16)};grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new());grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        grid.Children.Add(DialogUi.Text("查看 JPG 与 RAW 的对应关系",21));
        categories=Enumerable.Range(0,5).Select(i=>DialogUi.Button("",(_,_)=>ShowCategory(i))).ToArray();var nav=DialogUi.Row(categories);Grid.SetRow(nav,1);grid.Children.Add(nav);
        var body=new Grid();body.ColumnDefinitions.Add(new(){Width=new GridLength(.43,GridUnitType.Star)});body.ColumnDefinitions.Add(new(){Width=new GridLength(.57,GridUnitType.Star)});
        var left=new Grid();left.RowDefinitions.Add(new(){Height=GridLength.Auto});left.RowDefinitions.Add(new());left.Children.Add(empty);items.SelectionChanged+=(_,_)=>ShowPhoto();Grid.SetRow(items,1);left.Children.Add(items);body.Children.Add(left);
        var right=new Grid{Margin=new Thickness(14,0,0,0)};right.RowDefinitions.Add(new(){Height=GridLength.Auto});right.RowDefinitions.Add(new());right.RowDefinitions.Add(new(){Height=GridLength.Auto});right.RowDefinitions.Add(new(){Height=new GridLength(160)});right.RowDefinitions.Add(new(){Height=GridLength.Auto});
        right.Children.Add(description);Grid.SetRow(viewer,1);right.Children.Add(viewer);Grid.SetRow(relation,2);right.Children.Add(relation);Grid.SetRow(raws,3);right.Children.Add(raws);
        raws.ItemTemplate=RawTemplate();controls.Children.Add(candidateHint);controls.Children.Add(DialogUi.Row(DialogUi.Button("关联所选 RAW",(_,_)=>{if(raws.SelectedItem is RawChoice r)Confirm(r.Path);else candidateHint.Text="请先选中一个 RAW 候选文件。";}),DialogUi.Button("本组仅按 JPG 处理",(_,_)=>Confirm(null))));Grid.SetRow(controls,4);right.Children.Add(controls);Grid.SetColumn(right,1);body.Children.Add(right);
        undo=DialogUi.Button("撤回上一步",(_,_)=>Undo());
        Grid.SetRow(body,2);grid.Children.Add(body);var close=DialogUi.Row(DialogUi.Button("自动配对",async(_,_)=>await AutoPairAsync()),undo,DialogUi.Button("关闭",(_,_)=>Close()));Grid.SetRow(close,3);grid.Children.Add(close);Content=grid;
        PreviewKeyDown += (_, e) =>
        {
            if (working || Shortcuts.IsEditing(Keyboard.FocusedElement as DependencyObject)
                || !this.shortcuts.TryResolve(ShortcutGesture.From(e), out var action) || action != ShortcutAction.Undo) return;
            if (!e.IsRepeat) Undo();
            e.Handled = true;
        };
        Closing+=(_,e)=>{if(working){e.Cancel=true;candidateHint.Text="正在完成自动配对…";}};
        category=current==null?(task.Photos.Any(p=>p.PairState==PairState.Conflict)?0:1):Category(current);ShowCategory(category,current);
    }
    private static int Category(Photo p)=>p.PairState==PairState.Conflict?0:p.RawPath!=null?1:2;
    private void UpdateCounts()
    {
        int[] counts=[task.Photos.Count(p=>p.PairState==PairState.Conflict),task.Photos.Count(p=>p.RawPath!=null),task.Photos.Count(p=>p.RawPath==null&&p.PairState!=PairState.Conflict),task.RawOnly.Count,task.Diagnostics.Count];
        string[] labels=["待处理","已配对","仅 JPG","仅 RAW","扫描提示"];
        for(int i=0;i<5;i++){categories[i].Content=$"{labels[i]} {counts[i]}";categories[i].ToolTip=$"查看{labels[i]}清单。";categories[i].Background=(Brush)new BrushConverter().ConvertFromString(i==category?"#65583C":"#383838")!;}
        undo.IsEnabled=history.CanUndo;
        undo.ToolTip=shortcuts.Hint(ShortcutAction.Undo, history.CanUndo ? $"撤回「{history.NextLabel}」。" : "当前没有可撤回的操作。");
    }
    private void ShowCategory(int selected,Photo? focus=null)
    {
        category=selected;UpdateCounts();
        if(selected<=2)
        {
            items.ItemTemplate=PhotoTemplate();var photos=task.Photos.Where(p=>Category(p)==selected).OrderBy(p=>p,new NaturalPhotoComparer(task.SortByTime)).ToList();items.ItemsSource=photos;
            empty.Text=photos.Count==0?selected==0?"没有配对冲突；正常照片无需处理。":"此分类暂无照片。":selected==0?"请选择待处理照片，核对下面的候选文件。":selected==1?"这些照片已正常配对，可直接继续选片。":"仅 JPG 可以正常评分、标记和导出。";
            items.SelectedItem=focus!=null&&photos.Contains(focus)?focus:photos.FirstOrDefault();
        }
        else
        {
            items.ItemTemplate=null;items.ItemsSource=selected==3?task.RawOnly.Select(p=>$"RAW  {Path.GetFileName(p)}\n{p}").ToList():task.Diagnostics.Select(d=>$"{d.Path}\n{d.Message}").ToList();
            empty.Text=selected==3?"仅 RAW 不进入选片区，也不会自动随其他照片导出。":"这里列出无法读取或主动跳过的目录与文件。";ShowPhoto();
        }
    }
    private void ShowPhoto()
    {
        var p=Current;controls.Visibility=raws.Visibility=p==null?Visibility.Collapsed:Visibility.Visible;
        viewer.Path=p?.JpgPath;
        if(p==null){description.Text="选择 JPG 后查看对应关系";relation.Text="";return;}
        int candidates=p.Candidates.Count;
        description.Text=p.PairState==PairState.Conflict?$"待处理：{(candidates>1?$"找到 {candidates} 个可能的 RAW":candidates==1?"多个 JPG 可能对应同一个 RAW":"存在多个同名 JPG，请明确处理方式")}\n{p.Name}":$"{p.PairText} · {p.Name}";
        relation.Text=$"JPG：{p.Name}\n↓\nRAW：{(p.RawPath==null?"尚未关联":Path.GetFileName(p.RawPath))}";relation.ToolTip=$"{p.JpgPath}\n{p.RawPath}";
        var occupied=task.Photos.Where(other=>other!=p&&other.RawPath!=null).Select(other=>other.RawPath!).ToHashSet(PathRules.Comparer);
        var candidatesSet=p.Candidates.ToHashSet(PathRules.Comparer);
        raws.ItemsSource=task.Files.Where(f=>f.IsRaw&&!occupied.Contains(f.Path)).OrderByDescending(f=>candidatesSet.Contains(f.Path)).ThenBy(f=>f.Path,PathRules.Comparer).Select(f=>new RawChoice(f.Path)).ToList();
        raws.SelectedItem=raws.Items.Cast<RawChoice>().FirstOrDefault(r=>p.RawPath!=null&&PathRules.Same(r.Path,p.RawPath));candidateHint.Text="选择候选后，点击“关联所选 RAW”才会更改关系。";
    }
    private void Confirm(string? raw)
    {
        var p=Current;if(p==null)return;
        try{history.ApplyPairing(task,"手动配对 "+p.Name,()=>PairingService.Confirm(task,p,raw));foreach(var photo in task.Photos)photo.NotifyAll();edited?.Invoke();ShowCategory(Category(p),p);candidateHint.Text=raw==null?"已确认本组仅按 JPG 处理；RAW 文件保留。":"配对关系已确认；评分和取舍保持原记录。";}
        catch(Exception ex){candidateHint.Text="未能关联："+ex.Message;}
    }
    public async Task AutoPairAsync()
    {
        if(working)return;working=true;IsEnabled=false;
        try
        {
            var snapshot=JsonSerializer.Deserialize<PhotoTask>(JsonSerializer.Serialize(task))!;
            var result=await Task.Run(()=>PairingService.AutoPair(snapshot));var map=snapshot.Photos.ToDictionary(p=>p.Id);
            history.ApplyPairing(task,"自动配对",()=>{foreach(var p in task.Photos)if(map.TryGetValue(p.Id,out var next)){p.RawPath=next.RawPath;p.PairState=next.PairState;p.Candidates=[..next.Candidates];p.NotifyAll();}task.RawOnly=[..snapshot.RawOnly];});
            edited?.Invoke();ShowCategory(task.Photos.Any(p=>p.PairState==PairState.Conflict)?0:1);empty.Text=result+"（未重新扫描）";
        }
        catch(Exception ex){empty.Text="自动配对失败："+ex.Message;}
        finally{working=false;IsEnabled=true;UpdateCounts();}
    }
    public void Undo()
    {
        if(working)return;var focus=Current;var label=history.Undo(task);if(label==null)return;edited?.Invoke();ShowCategory(focus==null?category:Category(focus),focus);empty.Text="已撤回："+label;
    }
    private static DataTemplate PhotoTemplate()
    {
        var panel=new FrameworkElementFactory(typeof(StackPanel));panel.SetValue(FrameworkElement.MarginProperty,new Thickness(6));
        var row=new FrameworkElementFactory(typeof(DockPanel));var thumb=new FrameworkElementFactory(typeof(Thumbnail));thumb.SetBinding(Thumbnail.PathProperty,new Binding(nameof(Photo.JpgPath)));thumb.SetValue(FrameworkElement.WidthProperty,95.0);thumb.SetValue(FrameworkElement.HeightProperty,65.0);thumb.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,10,0));row.AppendChild(thumb);
        var text=new FrameworkElementFactory(typeof(StackPanel));var name=new FrameworkElementFactory(typeof(TextBlock));name.SetBinding(TextBlock.TextProperty,new Binding(nameof(Photo.Name)));name.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);text.AppendChild(name);var state=new FrameworkElementFactory(typeof(TextBlock));state.SetBinding(TextBlock.TextProperty,new Binding(nameof(Photo.PairText)));text.AppendChild(state);row.AppendChild(text);panel.AppendChild(row);
        var path=new FrameworkElementFactory(typeof(TextBlock));path.SetBinding(TextBlock.TextProperty,new Binding(nameof(Photo.JpgPath)));path.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);path.SetValue(TextBlock.FontSizeProperty,10.0);path.SetValue(FrameworkElement.MarginProperty,new Thickness(0,5,0,0));panel.AppendChild(path);return new(){VisualTree=panel};
    }
    private static DataTemplate RawTemplate()
    {
        var panel=new FrameworkElementFactory(typeof(StackPanel));panel.SetValue(FrameworkElement.MarginProperty,new Thickness(6));var name=new FrameworkElementFactory(typeof(TextBlock));name.SetBinding(TextBlock.TextProperty,new Binding(nameof(RawChoice.Name)));name.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);panel.AppendChild(name);
        var path=new FrameworkElementFactory(typeof(TextBlock));path.SetBinding(TextBlock.TextProperty,new Binding(nameof(RawChoice.Path)));path.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);path.SetValue(TextBlock.FontSizeProperty,10.0);panel.AppendChild(path);return new(){VisualTree=panel};
    }
}
