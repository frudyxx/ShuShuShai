using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
namespace ShuShuShai.App.Views;
internal static class DialogUi
{
    public static void Setup(Window window, string title, double width, double height)
    { window.Title = title; window.Width = Math.Min(width,SystemParameters.WorkArea.Width-30); window.Height = Math.Min(height,SystemParameters.WorkArea.Height-30); window.MinWidth = Math.Min(window.Width,650); window.MinHeight = Math.Min(window.Height,450); window.WindowStartupLocation = WindowStartupLocation.CenterOwner; window.SetResourceReference(FrameworkElement.StyleProperty, typeof(Window)); }
    public static TextBlock Text(string text, int size = 13) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4,6,4,6) };
    public static Button Button(string text, RoutedEventHandler action, string? hint = null)
    { var b = new Button { Content = text, ToolTip = hint ?? text }; b.Click += action; return b; }
    public static TextBox Edit(object owner, string path, double width = 150)
    { var t = new TextBox { Width=width }; t.SetBinding(TextBox.TextProperty,new Binding(path){Source=owner,Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged}); return t; }
    public static CheckBox Check(string text, object owner, string path)
    { var c = new CheckBox{Content=text}; c.SetBinding(CheckBox.IsCheckedProperty,new Binding(path){Source=owner,Mode=BindingMode.TwoWay});return c; }
    public static StackPanel Row(params UIElement[] children) { var p = new StackPanel{Orientation=Orientation.Horizontal}; foreach(var c in children)p.Children.Add(c);return p; }
}
