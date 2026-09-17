using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dockable.Genie;

/// <summary>
/// A simple minimize/restore animation: the captured window image scales down toward its dock tile
/// (and reverses on restore). A lighter alternative to the genie warp. The pre-warmed, reusable,
/// click-through overlay and the render loop live in <see cref="OverlayAnimatorBase"/>.
///
/// Coordinates are device-independent (DIP); rects are in virtual-screen DIPs.
/// </summary>
public sealed class ScaleAnimator : OverlayAnimatorBase
{
    private Image? _image;
    private ScaleTransform? _scale;
    private TranslateTransform? _translate;
    private double _endScale;

    protected override double BaseDurationMs => 230;

    protected override string ProfileName => "Scale";

    protected override UIElement BuildOverlayContent()
    {
        _scale = new ScaleTransform();
        _translate = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(_scale);
        group.Children.Add(_translate);

        _image = new Image { Stretch = Stretch.Fill, RenderTransform = group };
        // LowQuality (bilinear) rather than HighQuality (Fant): the image is resampled every frame as it
        // scales, and multi-tap Fant downsampling of a full-window bitmap per frame is far more expensive.
        // During a fast shrink-to-tile the difference is imperceptible, and it lands tiny (or, on restore,
        // is immediately replaced by the real window), so bilinear is the right trade for smooth frames.
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.LowQuality);

        var canvas = new Canvas();
        canvas.Children.Add(_image);
        return canvas;
    }

    protected override void SetContent(BitmapSource bitmap)
    {
        _image!.Source = bitmap;
        _image.Width = Src.Width;
        _image.Height = Src.Height;
        Canvas.SetLeft(_image, Src.Left);
        Canvas.SetTop(_image, Src.Top);
        _scale!.CenterX = Src.Width / 2;  // scale about the image's own center
        _scale.CenterY = Src.Height / 2;
    }

    protected override void PreparePlay()
        => _endScale = Math.Clamp(TargetTileWidth / Math.Max(Src.Width, 1), 0.04, 0.25);

    // warp: 0 = full window at source, 1 = shrunk onto the dock tile. The base already applied the
    // velocity curve, so this maps it straight through — re-easing here would compound the two.
    protected override void ApplyFrame(double warp)
    {
        double t = warp;

        double s = 1.0 + (_endScale - 1.0) * t;
        _scale!.ScaleX = s;
        _scale.ScaleY = s;

        // Scaling is centered, so move the image's center from the source center to the tile.
        double srcCenterX = Src.Left + Src.Width / 2;
        double srcCenterY = Src.Top + Src.Height / 2;
        _translate!.X = (Target.X - srcCenterX) * t;
        _translate.Y = (Target.Y - srcCenterY) * t;

        _image!.Opacity = 1.0 - 0.2 * t; // gentle fade as it lands
    }
}
