using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ShuShuShai.App.ViewModels;
namespace ShuShuShai.App.Controls;
public partial class FolderTree : UserControl
{
    public static readonly DependencyProperty ImportControlsProperty=DependencyProperty.Register(nameof(ImportControls),typeof(bool),typeof(FolderTree),new PropertyMetadata(false));
    public bool ImportControls{get=>(bool)GetValue(ImportControlsProperty);set=>SetValue(ImportControlsProperty,value);}
    public ObservableCollection<FolderNode> Roots{get;}=[];
    public event Action<FolderNode>? RemoveRoot;
    public FolderTree(){InitializeComponent();Tree.ItemsSource=Roots;}
    private void Remove(object sender,RoutedEventArgs e){if((sender as FrameworkElement)?.DataContext is FolderNode node)RemoveRoot?.Invoke(node);e.Handled=true;}
    private void ToggleChildren(object sender,RoutedEventArgs e){if((sender as FrameworkElement)?.DataContext is FolderNode node)node.ToggleAllChildren();e.Handled=true;}
}
