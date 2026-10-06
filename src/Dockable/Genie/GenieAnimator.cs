using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Dockable.Interop;

namespace Dockable.Genie;

/// <summary>
/// Plays the macOS "genie" (a.k.a. magic lamp — the same algorithm KDE's Magic Lamp uses): a captured
/// window image on a grid mesh whose SIDES follow fixed S-curves from the window's edges down to a neck
/// the width of the dock icon. Two overlapping phases:
/// <list type="number">
/// <item><b>Bend</b> — rows stay put while the S-curve fades in, so the edge nearest the dock bows in
/// toward the icon.</item>
/// <item><b>Slide</b> — the whole sheet travels along the curve toward the dock; every row's width is
/// read off the curve at the row's CURRENT position, so the shape stays a stable lamp while the content
/// pours through the neck and is pressed into a short band inside the icon.</item>
/// </list>
/// A restore plays the same frames backwards (slide out, then un-bend). Width as a function of screen
/// position — not of each row's own progress — is what makes it read as the Dock rather than a cone.
/// Rendered with WPF 3D (Viewport3D + MeshGeometry3D); the pre-warmed, reusable, click-through overlay
/// and the render loop live in <see cref="OverlayAnimatorBase"/>. Coordinates are DIP.
/// </summary>
public sealed class GenieAnimator : OverlayAnimatorBase
{
    // Mesh resolution, resolved from PerformanceProfile at overlay-build time (coarser when reduced).
    // Rows carry the curve; columns only need to keep the texture mapping smooth.
    private int Columns = 12;
    private int Rows = 48;

    // --- Tunables (blind constants; adjust by feel) ---

    /// <summary>Nominal length (ms) at speed 1× (tuned by feel: 520 read as rushed).</summary>
    private const double DurationMs = 650;

    /// <summary>The bend phase runs over warp [0, BendEnd]; the slide over [SlideStart, 1]. The overlap
    /// keeps the motion continuous — the sheet starts travelling while it is still curling in.</summary>
    private const double BendEnd = 0.45;
    private const double SlideStart = 0.2;

    /// <summary>Neck width at the landing line (just above the icon's top edge), as a fraction of the
    /// icon's width (floor in DIP): a stream a little narrower than the icon pouring into it.</summary>
    private const double NeckWidthFactor = 0.7;
    private const double NeckWidthFloor = 8;

    /// <summary>Past the neck the stream sinks into the icon (it ends IN the icon, not at a point),
    /// still narrowing to <see cref="TailWidthFactor"/> of the neck (~0.42 icon), then dissolves
    /// (FadeStartWarp).</summary>
    private const double TailWidthFactor = 0.6;

    /// <summary>How deep into the icon (fraction of its height, from its top edge) the stream's far end
    /// stops. Below 0.5 on purpose: the whole window is packed above that line, so its visual mass sits
    /// higher than the line itself — 0.35 makes the last frames read as the icon's middle.</summary>
    private const double EndDepthFactor = 0.35;

    /// <summary>The landing line sits this far above the icon's top edge — mirrors DockWindow's
    /// LandingLiftDip, so the band below can end exactly at the icon's centre.</summary>
    private const double LandingLiftDip = 6;

    private MeshGeometry3D? _mesh;
    private Point3DCollection? _positions; // == _mesh.Positions; detached while bulk-mutated each frame
    private ImageBrush? _brush;
    private OrthographicCamera? _camera;

    // Per-play invariants (recomputed by PreparePlay — which runs twice on the intercepted path, once
    // with ShowAtSource's placeholder target and again from AnimateTo; both are complete recomputes).
    private bool _down;        // the dock is beyond the window's bottom (screen-down) vs its top
    private double _depthD;    // distance from the window's far edge to the landing line (the neck)
    private double _bandH;     // depth of the final band inside the icon
    private double _neckL, _neckR;
    private double[]? _rowDepth; // each row's starting depth from the far edge

    protected override double BaseDurationMs => DurationMs;

    protected override string ProfileName => "Genie";

    // The bend/slide phases carry their own easing (below); a second curve on top would compound.
    protected override double Curve(double progress) => progress;

    protected override UIElement BuildOverlayContent()
    {
        (Columns, Rows) = PerformanceProfile.GenieMesh;
        _mesh = BuildMesh();
        _positions = _mesh.Positions; // held so we can detach/reattach it cheaply each frame
        _rowDepth = new double[Rows + 1];

        _brush = new ImageBrush { Stretch = Stretch.Fill };
        var model = new GeometryModel3D(_mesh, new DiffuseMaterial(_brush));

        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Colors.White)); // unshaded: show the texture as-is
        group.Children.Add(model);

        _camera = new OrthographicCamera(new Point3D(0, 0, 100), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 100);
        var viewport = new Viewport3D { Camera = _camera };
        viewport.Children.Add(new ModelVisual3D { Content = group });
        return viewport;
    }

    /// <summary>Matches the orthographic camera to the monitor the overlay was just sized to.</summary>
    protected override void OnOverlaySynced(Rect monitorDip)
    {
        _camera!.Width = monitorDip.Width;
        _camera.Position = new Point3D(monitorDip.Width / 2, monitorDip.Height / 2, 100);
    }

    protected override void SetContent(BitmapSource bitmap) => _brush!.ImageSource = bitmap;

    /// <summary>Re-resolves the mesh resolution from <see cref="PerformanceProfile"/> after a mode change.
    /// The overlay is idle (hidden) between warps, so tearing it down here lets the next play rebuild it
    /// at the new resolution. A no-op when the resolution is unchanged.</summary>
    public void RefreshQuality()
    {
        var (cols, rows) = PerformanceProfile.GenieMesh;
        if (!HasOverlay || (cols == Columns && rows == Rows))
            return;
        TearDownOverlay(); // finalizes any in-flight warp first (should be none while idle)
    }

    private MeshGeometry3D BuildMesh()
    {
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection((Columns + 1) * (Rows + 1)),
            TextureCoordinates = new PointCollection((Columns + 1) * (Rows + 1)),
            TriangleIndices = new Int32Collection(Columns * Rows * 6),
        };

        for (int j = 0; j <= Rows; j++)
        {
            double v = (double)j / Rows;
            for (int i = 0; i <= Columns; i++)
            {
                mesh.Positions.Add(new Point3D(0, 0, 0)); // filled in by ApplyFrame
                mesh.TextureCoordinates.Add(new Point((double)i / Columns, v));
            }
        }

        int rowStride = Columns + 1;
        for (int j = 0; j < Rows; j++)
        {
            for (int i = 0; i < Columns; i++)
            {
                int topLeft = j * rowStride + i;
                int topRight = topLeft + 1;
                int bottomLeft = topLeft + rowStride;
                int bottomRight = bottomLeft + 1;

                mesh.TriangleIndices.Add(topLeft);
                mesh.TriangleIndices.Add(bottomLeft);
                mesh.TriangleIndices.Add(topRight);

                mesh.TriangleIndices.Add(topRight);
                mesh.TriangleIndices.Add(bottomLeft);
                mesh.TriangleIndices.Add(bottomRight);
            }
        }

        return mesh;
    }

    /// <summary>Resolves direction, the neck line and width, and each row's starting depth. Depth is
    /// measured from the window's FAR edge toward the dock, so one code path serves a dock below or
    /// above the window.</summary>
    protected override void PreparePlay()
    {
        var src = Src;
        double h = Math.Max(src.Height, 1);
        _down = Target.Y >= src.Top + src.Height / 2;
        double depth = _down ? Target.Y - src.Top : src.Bottom - Target.Y;
        // A window whose near edge already reaches past the landing line (a maximized window against
        // the dock, an auto-hidden dock) would put the neck inside it; keep a sane minimum so the curve
        // never degenerates (and nothing divides by ~0).
        _depthD = Math.Max(depth, Math.Max(h * 0.35, 1));
        _bandH = Math.Max(4, LandingLiftDip + TargetTileWidth * EndDepthFactor);

        double half = Math.Max(NeckWidthFloor, TargetTileWidth * NeckWidthFactor) / 2;
        _neckL = Target.X - half;
        _neckR = Target.X + half;

        for (int j = 0; j <= Rows; j++)
        {
            double v = (double)j / Rows;
            _rowDepth![j] = (_down ? v : 1 - v) * h;
        }
    }

    protected override void ApplyFrame(double warp)
    {
        var mesh = _mesh!;
        var positions = _positions!;
        var src = Src;
        double h = Math.Max(src.Height, 1);
        double D = _depthD;

        double bend = SmootherStep(Clamp01(warp / BendEnd));
        // Slide: starts from rest but is still moving when the tail hits the icon (velocity 0.8 at the
        // end) — the Dock pulls the window IN, it doesn't ease it to a stop. Restores mirror this, so
        // the window leaves the icon briskly and settles at its frame.
        double ts = Clamp01((warp - SlideStart) / (1 - SlideStart));
        double slide = ts * ts * (2.2 - 1.2 * ts);
        double travel = slide * D;                      // the far edge reaches the neck at slide = 1
        double squeeze = Lerp(1.0, _bandH / h, slide);  // past the neck, rows pack into the icon band
        int rowStride = Columns + 1;
        double mh = MonitorHeight;

        mesh.Positions = null; // detach for cheap bulk mutation; reattach once below
        for (int j = 0; j <= Rows; j++)
        {
            double d = _rowDepth![j] + travel;
            double left, right;
            if (d >= D)
            {
                d = D + (d - D) * squeeze;
                // Inside the icon the stream keeps narrowing toward a thin tail.
                double taper = Lerp(1.0, TailWidthFactor, SmoothStep(Clamp01((d - D) / _bandH)));
                left = Lerp(Target.X, _neckL, taper);
                right = Lerp(Target.X, _neckR, taper);
            }
            else
            {
                double k = SmoothStep(d / D); // the lamp's S-curve: full width far away, neck at the dock
                left = Lerp(src.Left, _neckL, k);
                right = Lerp(src.Right, _neckR, k);
            }
            // The curve fades in during the bend; at warp 0 every row is exactly its source span.
            left = Lerp(src.Left, left, bend);
            right = Lerp(src.Right, right, bend);

            double y = _down ? src.Top + d : src.Bottom - d;
            double yUp = mh - y; // 3D Y is up; screen Y is down — flip into the camera's space
            int rowBase = j * rowStride;
            double span = right - left;
            for (int i = 0; i <= Columns; i++)
                positions[rowBase + i] = new Point3D(left + span * i / Columns, yUp, 0);
        }
        mesh.Positions = positions;
    }

    private static double SmootherStep(double t) => t * t * t * (t * (6 * t - 15) + 10);
    private static double Clamp01(double t) => t < 0 ? 0 : t > 1 ? 1 : t;
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
