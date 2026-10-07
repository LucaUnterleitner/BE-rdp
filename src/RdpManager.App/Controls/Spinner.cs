using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RdpManager.App.Controls;

/// <summary>Small busy indicator (an open circle). The rotation runs only while the element is visible.</summary>
public sealed class Spinner : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Spinner), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly RotateTransform _rotate = new();
    private static readonly DoubleAnimation Spin = CreateSpin();

    public Spinner()
    {
        RenderTransform = _rotate;
        RenderTransformOrigin = new Point(0.5, 0.5);
        IsHitTestVisible = false;
        IsVisibleChanged += (_, e) => _rotate.BeginAnimation(RotateTransform.AngleProperty, (bool)e.NewValue ? Spin : null);
    }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private static DoubleAnimation CreateSpin()
    {
        var a = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.8))) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(a, 30);
        a.Freeze();
        return a;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var r = Math.Max(1, Math.Min(w, h) / 2 - 1.5);
        var c = new Point(w / 2, h / 2);
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X - r, c.Y);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, true, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, new Pen(Foreground, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
    }
}
