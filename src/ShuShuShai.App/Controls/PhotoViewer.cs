using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShuShuShai.App.Services;
namespace ShuShuShai.App.Controls;
public class PhotoViewer : FrameworkElement
{
    public static readonly DependencyProperty PathProperty=DependencyProperty.Register(nameof(Path),typeof(string),typeof(PhotoViewer),new PropertyMetadata(null,(d,_)=>((PhotoViewer)d).Reset()));
    public string? Path{get=>(string?)GetValue(PathProperty);set=>SetValue(PathProperty,value);}
    public static readonly DependencyProperty SpinnerInsetProperty = DependencyProperty.RegisterAttached(
        "SpinnerInset", typeof(double), typeof(PhotoViewer),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public static void SetSpinnerInset(DependencyObject element, double value) => element.SetValue(SpinnerInsetProperty, value);
    public static double GetSpinnerInset(DependencyObject element) => (double)element.GetValue(SpinnerInsetProperty);
    private BitmapSource? bitmap;
    private CancellationTokenSource? cancel;
    private string message="请选择一张 JPG",warning="";
    private readonly DispatcherTimer spinner = new() { Interval = TimeSpan.FromMilliseconds(60) };
    internal bool IsLoading { get; private set; }
    private Point? drag;
    private double panX,panY;
    private int originalW,originalH,loadedSize;
    public double? Zoom{get;private set;}
    public double EffectiveZoom=>Zoom??FitPhysicalScale();
    public event Action<PhotoViewer,double?,double,double>? TransformChanged;
    public event Action<PhotoViewer,string,int>? FrameAvailable;
    public string ZoomText=>Zoom.HasValue?$"{Zoom:P0} · 原始 JPG 像素":"适合窗口";
    public PhotoViewer()
    {
        ClipToBounds=true;Focusable=true;
        spinner.Tick += (_, _) => InvalidateVisual();
        Loaded+=(_,_)=>RequestImage(false);Unloaded+=(_,_)=>ReleasePreview();
        IsVisibleChanged+=(_,_)=>{if(IsVisible)RequestImage(false);else ReleasePreview();};
        SizeChanged+=(_,_)=>{if(Zoom==null)RequestImage(true);InvalidateVisual();};
        MouseLeftButtonDown+=(_,e)=>{Focus();if(e.ClickCount==2)SetZoom(Zoom.HasValue?null:1);else{drag=e.GetPosition(this);CaptureMouse();}};
        MouseLeftButtonUp+=(_,_)=>{drag=null;ReleaseMouseCapture();};
        MouseMove+=(_,e)=>{if(drag is not Point old||bitmap==null||ActualWidth==0||ActualHeight==0)return;var now=e.GetPosition(this);panX+=(now.X-old.X)/ActualWidth;panY+=(now.Y-old.Y)/ActualHeight;drag=now;InvalidateVisual();TransformChanged?.Invoke(this,Zoom,panX,panY);};
        MouseWheel+=(_,e)=>{ZoomBy(e.Delta>0?1.25:.8);e.Handled=true;};
    }
    private void ReleasePreview()
    {
        cancel?.Cancel();
        bitmap = null;
        loadedSize = 0;
        SetLoading(null);
    }

    private void SetLoading(string? description)
    {
        IsLoading = description != null;
        AutomationProperties.SetHelpText(this, description ?? "");
        if (IsLoading && SystemParameters.ClientAreaAnimation) spinner.Start();
        else spinner.Stop();
        InvalidateVisual();
    }
    private double FitPhysicalScale()=>originalW==0?1:Math.Min(ActualWidth/originalW,ActualHeight/originalH)*VisualTreeHelper.GetDpi(this).DpiScaleX;
    private void Reset(){cancel?.Cancel();Zoom=null;panX=panY=0;bitmap=null;originalW=originalH=loadedSize=0;warning="";RequestImage(false);}
    public void ZoomBy(double factor)=>SetZoom(Math.Clamp(EffectiveZoom*factor,.05,8));
    public void SetZoom(double? zoom){Zoom=zoom;panX=panY=0;InvalidateVisual();RequestImage(zoom!=1);TransformChanged?.Invoke(this,Zoom,panX,panY);}
    public void RefreshImage(){cancel?.Cancel();bitmap=null;loadedSize=originalW=originalH=0;RequestImage(false);}
    public void Sync(double? zoom,double x,double y)
    {bool changed=Zoom!=zoom;Zoom=zoom;panX=x;panY=y;InvalidateVisual();if(changed)RequestImage(zoom!=1,1);}
    private int RequiredSize()
    {
        int longest=Math.Max(originalW,originalH);if(longest==0)return 480;
        double needed=longest*EffectiveZoom;
        if(needed>=longest)return 0;
        foreach(int rung in new[]{480,1024,2048,4096})if(rung>=needed)return rung>=longest?0:rung;
        return 0;
    }
    private async void RequestImage(bool debounce,int priority=0)
    {
        cancel?.Cancel();
        if (!IsLoaded || !IsVisible || Path == null || ActualWidth < 2)
        {
            if (Path == null) message = "请选择一张 JPG";
            SetLoading(null);
            return;
        }
        var local=cancel=new();var path=Path;
        message = "";
        int requiredNow = RequiredSize(), targetNow = requiredNow == 0 ? Math.Max(originalW, originalH) : requiredNow;
        SetLoading(bitmap == null || loadedSize < targetNow
            ? Zoom >= 1 ? "正在读取原始 JPG 像素" : "正在加载 JPG 预览"
            : null);
        try
        {
            if(debounce)await Task.Delay(120,local.Token);
            if(originalW==0)
            {
                var fast=await ImageService.Shared.LoadResultAsync(path,480,local.Token,priority);
                if(local.IsCancellationRequested||Path!=path)return;Accept(fast,path);
            }
            int required=RequiredSize(),longest=Math.Max(originalW,originalH),target=required==0?longest:required;
            bool demote=Zoom==null&&loadedSize>target*1.8;
            if(loadedSize>=target&&!demote){SetLoading(null);FrameAvailable?.Invoke(this,path,bitmap!.PixelWidth);return;}
            if (loadedSize < target) SetLoading(required == 0 ? "正在读取原始 JPG 像素" : "正在补充清晰预览");
            var next=await ImageService.Shared.LoadResultAsync(path,required,local.Token,priority);
            if(local.IsCancellationRequested||Path!=path)return;Accept(next,path);
            SetLoading(null);
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!local.IsCancellationRequested&&Path==path){bitmap=null;loadedSize=0;message="JPG 读取失败："+ex.Message;SetLoading(null);}}
    }
    private void Accept(PreviewResult result,string path)
    {
        bitmap=result.Image;originalW=result.OriginalWidth>0?result.OriginalWidth:bitmap.PixelWidth;originalH=result.OriginalHeight>0?result.OriginalHeight:bitmap.PixelHeight;
        loadedSize=Math.Max(bitmap.PixelWidth,bitmap.PixelHeight);warning=result.Warning;message="";InvalidateVisual();FrameAvailable?.Invoke(this,path,bitmap.PixelWidth);
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(25,25,25)),null,new Rect(RenderSize));
        if(bitmap!=null)
        {
            double scale=EffectiveZoom/VisualTreeHelper.GetDpi(this).DpiScaleX;double w=originalW*scale,h=originalH*scale;
            dc.DrawImage(bitmap,new Rect((ActualWidth-w)/2+panX*ActualWidth,(ActualHeight-h)/2+panY*ActualHeight,w,h));
        }
        string text=message.Length>0?message:ZoomText;if(warning.Length>0)text+="\n"+warning;
        var label=new FormattedText(text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),12,warning.Length>0?Brushes.Khaki:Brushes.White,VisualTreeHelper.GetDpi(this).PixelsPerDip){MaxTextWidth=Math.Max(20,ActualWidth-30)};
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(190,25,25,25)),null,new Rect(8,Math.Max(0,ActualHeight-label.Height-22),Math.Max(0,Math.Min(ActualWidth-16,label.Width+16)),label.Height+12));
        dc.DrawText(label,new Point(16,Math.Max(0,ActualHeight-label.Height-16)));
        if (IsLoading && ActualWidth >= 48 && ActualHeight >= 36)
        {
            var center = new Point(ActualWidth - 23, Math.Max(16, ActualHeight - 23 - GetSpinnerInset(this)));
            dc.DrawEllipse(Brushes.Black, null, center, 14, 14);
            dc.DrawEllipse(null, new Pen(Brushes.DimGray, 2.5), center, 8, 8);
            double angle = SystemParameters.ClientAreaAnimation ? (Environment.TickCount64 % 900) / 900d * Math.Tau : -Math.PI / 2;
            Point ArcPoint(double a) => new(center.X + 8 * Math.Cos(a), center.Y + 8 * Math.Sin(a));
            var arc = new StreamGeometry();
            using (var geometry = arc.Open())
            {
                geometry.BeginFigure(ArcPoint(angle), false, false);
                geometry.ArcTo(ArcPoint(angle + Math.PI * 1.3), new Size(8, 8), 0, true, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, new Pen(Brushes.Khaki, 2.5), arc);
        }
    }
}
