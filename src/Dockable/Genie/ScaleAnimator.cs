using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dockable.Genie;

/// <summary>
/// The image-based minimize/restore effects (a lighter alternative to the genie mesh), in two styles:
/// <list type="bullet">
/// <item><b>Scale</b> (macOS): the window shrinks straight into its icon, keeping its aspect, landing
/// icon-sized on the icon's centre.</item>
/// <item><b>Fade</b> (Windows 11): a short, subtle shrink and drift toward the dock while it fades out —
/// the gentlest of the three, for people who find a full warp distracting.</item>
/// </list>
/// A restore plays the frames backwards. The pre-warmed, reusable, click-through overlay and the render
/// loop live in <see cref="OverlayAnimatorBase"/>. Coordinates are DIP.
/// </summary>
public sealed class ScaleAnimator : OverlayAnimatorBase
{
    public enum ScaleStyle { Scale, Fade }

    /// <summary>Which style to play; set before each play.</summary>
    public ScaleStyle Style { get; set; } = ScaleStyle.Scale;

    // --- Tunables (blind constants; adjust by feel) ---
    private const double ScaleDurationMs = 320;
    private const double FadeDurationMs = 240;
    private const double FadeEndScale = 0.82; // Fade: how small the window gets before it's gone
    private const double FadeDrift = 0.3;     // Fade: fraction of the way to the icon it drifts

    private Image? _image;
    private ScaleTransform? _scale;
    private TranslateTransform? _translate;
    private double _endScale;
    private double _travel; // fraction of the source→icon distance covered at warp 1
    private Point _iconCentre;

    protected override double BaseDurationMs => Style == ScaleStyle.Fade ? FadeDurationMs : ScaleDurationMs;

    protected override string ProfileName => Style.ToString();

    protected override UIElement BuildOverlayContent()
    {
        _scale = new ScaleTransform();
        _translate = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(_scale);
        group.Children.Add(_translate);

        _image = new Image { Stretch = Stretch.Fill, RenderTransform = group };
        // Linear (bilinear) rather than Fant: the image is resampled every frame as it scales, and a
        // multi-tap Fant downsample of a full-window bitmap per frame costs far more for no visible gain
        // during a sub-second shrink.
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.Linear);

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
        _scale!.CenterX = Src.Width / 2;  // scale about the image's own centre
        _scale.CenterY = Src.Height / 2;
    }

    protected override void PreparePlay()
    {
        // Target is the landing point just past the icon's top edge (shared with the genie); these
        // effects aim at the icon's centre, half an icon further toward the dock.
        double dir = Target.Y >= Src.Top + Src.Height / 2 ? 1 : -1;
        _iconCentre = new Point(Target.X, Target.Y + dir * TargetTileWidth / 2);

        if (Style == ScaleStyle.Fade)
        {
            _endScale = FadeEndScale;
            _travel = FadeDrift;
        }
        else
        {
            // Land icon-sized: the window's LONGER side fits the icon, aspect kept.
            _endScale = Math.Clamp(TargetTileWidth / Math.Max(Math.Max(Src.Width, Src.Height), 1), 0.01, 1);
            _travel = 1;
        }
    }

    // warp: 0 = the window at its source (identity, opaque — it covers the real window while that
    // minimizes behind it), 1 = landed. The base already applied the velocity curve; mapped straight
    // through here (re-easing would compound it).
    protected override void ApplyFrame(double warp)
    {
        double s = 1.0 + (_endScale - 1.0) * warp;
        _scale!.ScaleX = s;
        _scale.ScaleY = s;

        // Scaling is centred, so move the image's centre from the source centre toward the icon.
        _translate!.X = (_iconCentre.X - (Src.Left + Src.Width / 2)) * _travel * warp;
        _translate.Y = (_iconCentre.Y - (Src.Top + Src.Height / 2)) * _travel * warp;

        // Scale's opacity is the base's end-of-play dissolve alone; Fade owns a full fade on top of it.
        _image!.Opacity = Style == ScaleStyle.Fade ? 1.0 - warp : 1.0;
    }
}
