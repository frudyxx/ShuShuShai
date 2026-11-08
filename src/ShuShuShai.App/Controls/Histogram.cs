using System.Windows;
using System.Windows.Media;
using ShuShuShai.App.Services;
namespace ShuShuShai.App.Controls;
public class Histogram : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(HistogramData), typeof(Histogram), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public HistogramData? Data { get => (HistogramData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(22,22,22)), new Pen(Brushes.DimGray, 1), new Rect(RenderSize));
        if (Data == null) return;
        long max = Math.Max(1, Data.Red.Concat(Data.Green).Concat(Data.Blue).Max());
        Draw(Data.Red, Color.FromArgb(110, 244, 96, 96)); Draw(Data.Green, Color.FromArgb(110, 96, 220, 133)); Draw(Data.Blue, Color.FromArgb(110, 92, 156, 245));
        void Draw(long[] values, Color c)
        {
            var geometry = new StreamGeometry(); using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(0, ActualHeight), true, true);
                for (int i=0;i<256;i++) ctx.LineTo(new Point(i * ActualWidth / 255, ActualHeight - values[i] * (ActualHeight - 4) / max), true, false);
                ctx.LineTo(new Point(ActualWidth, ActualHeight), true, false);
            }
            geometry.Freeze(); dc.DrawGeometry(new SolidColorBrush(c), new Pen(new SolidColorBrush(c), .8), geometry);
        }
    }
}
