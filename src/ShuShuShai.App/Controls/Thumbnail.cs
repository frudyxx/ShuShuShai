using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShuShuShai.App.Services;
namespace ShuShuShai.App.Controls;
public class Thumbnail : Grid
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(nameof(Path), typeof(string), typeof(Thumbnail), new PropertyMetadata(null, Changed));
    public string? Path { get => (string?)GetValue(PathProperty); set => SetValue(PathProperty, value); }
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock status = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Silver, FontSize = 11 };
    private CancellationTokenSource? cancel;
    public Thumbnail() { Children.Add(image); Children.Add(status); Loaded += (_, _) => Load(); Unloaded += (_, _) => { cancel?.Cancel(); image.Source = null; }; }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((Thumbnail)d).Load();
    private async void Load()
    {
        cancel?.Cancel(); image.Source = null;
        if (!IsLoaded || Path == null) {status.Text="—";return;}
        var local = cancel = new(); var path = Path; status.Text = "加载…";
        try { var result = await ImageService.Shared.LoadResultAsync(path,480,local.Token,2); if (!local.IsCancellationRequested && Path == path) { image.Source=result.Image;status.Text=result.Warning.Length>0?"⚠":"";ToolTip=result.Warning.Length>0?result.Warning:path; } }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!local.IsCancellationRequested) { status.Text = "读取失败"; ToolTip = ex.Message; } }
    }
}
