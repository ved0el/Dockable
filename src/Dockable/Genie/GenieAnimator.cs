using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Dockable.Interop;

namespace Dockable.Genie;

/// <summary>
/// The macOS "genie" (magic lamp), in the two styles ported from Windhawk's "MacOS Minimize Animation":
/// <list type="bullet">
/// <item><see cref="GenieStyle.MacModern"/> — Potassiumuncher's lamp curve (<c>CalculateLampVertexMacOS</c>):
/// a per-vertex warp that necks the window down into the icon with a sideways ripple.</item>
/// <item><see cref="GenieStyle.MacClassic"/> — the mod's original row-staggered strips (<c>morphAt</c>);
/// also the Windows Animations mod's genie, which is the same algorithm.</item>
/// </list>
/// A restore plays the frames backwards. Rendered with WPF 3D (Viewport3D + MeshGeometry3D); the
/// pre-warmed, reusable, click-through overlay and the render loop live in
/// <see cref="OverlayAnimatorBase"/>. Coordinates are DIP.
/// </summary>
public sealed class GenieAnimator : OverlayAnimatorBase
{
    public enum GenieStyle { MacModern, MacClassic }

    /// <summary>Which genie to play; set before each play.</summary>
    public GenieStyle Style { get; set; } = GenieStyle.MacModern;

    /// <summary>The Windhawk mod's default duration (both of its styles).</summary>
    private const double MacDurationMs = 450;

    /// <summary>MacModern's taskbar-icon rect width, from the mod: 22 wide, centred on the button, its
    /// top on the taskbar's top edge — here the landing line.</summary>
    private const double MacIconWidth = 22;

    /// <summary>MacClassic: how far the morph is staggered across rows (rows nearest the dock lead).</summary>
    private const double MacSpread = 0.65;

    // Mesh resolution, resolved from PerformanceProfile at overlay-build time (coarser when reduced).
    private int Columns = 12;
    private int Rows = 48;

    private MeshGeometry3D? _mesh;
    private Point3DCollection? _positions; // == _mesh.Positions; detached while bulk-mutated each frame
    private ImageBrush? _brush;
    private OrthographicCamera? _camera;

    // Per-play (PreparePlay runs twice on the intercepted path: ShowAtSource's placeholder, then AnimateTo).
    private bool _down; // the dock is beyond the window's bottom (screen-down) vs its top

    protected override double BaseDurationMs => MacDurationMs;

    protected override string ProfileName => "Genie";

    // MacModern eases the whole progress with smoothstep (as the mod does); MacClassic smoothsteps per row.
    protected override double Curve(double progress)
        => Style == GenieStyle.MacModern ? SmoothStep(progress) : progress;

    protected override UIElement BuildOverlayContent()
    {
        (Columns, Rows) = PerformanceProfile.GenieMesh;
        _mesh = BuildMesh();
        _positions = _mesh.Positions; // held so we can detach/reattach it cheaply each frame

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

    protected override void PreparePlay() => _down = Target.Y >= Src.Top + Src.Height / 2;

    protected override void ApplyFrame(double warp)
    {
        if (Style == GenieStyle.MacModern)
            ApplyMacModern(warp);
        else
            ApplyMacClassic(warp);
    }

    // The mod's lamp curve is written for a dock BELOW the window; a dock above runs it mirrored
    // vertically (y → -y, rows counted from the bottom edge), as the mod's classic style does.
    // (Side docks don't exist yet; both styles are vertical-only.)
    private void ApplyMacModern(double p)
    {
        var src = Src;
        var mesh = _mesh!;
        var positions = _positions!;
        // Icon rect in overlay DIP; its top stays at least a pixel below the window top so the curve
        // never divides by ~0 (a window reaching under the landing line).
        double sign = _down ? 1 : -1;
        double top = _down ? src.Top : -src.Bottom; // the window's far edge, in the (mirrored) space
        double ix = Target.X - MacIconWidth / 2, iy = Math.Max(sign * Target.Y, top + 1), iw = MacIconWidth;
        double k = p <= 0.3 ? p / 0.3 : 1.0;
        double j = p > 0.3 ? (p - 0.3) / 0.7 : 0.0;
        double expandHeight = iy - top - src.Height;
        double fullHeight = (iy - top) - expandHeight * (1 - k);
        double height = fullHeight - j * fullHeight;
        double offsetY = iy - top - height - expandHeight * (1 - k);
        double mh = MonitorHeight;
        int rowStride = Columns + 1;

        mesh.Positions = null;
        for (int r = 0; r <= Rows; r++)
        {
            double ty = _down ? (double)r / Rows : 1 - (double)r / Rows; // mesh row r is texture row r/Rows
            double y = ty * height;
            double offsetX = (ix - src.Left) * (y / (fullHeight + 0.1)) * k + (ix - src.Left) * j;
            double ripple = Math.Sin((height - y) / fullHeight * 2 * Math.PI + Math.PI) / 7 * k;
            double yUp = mh - sign * (top + y + offsetY);
            for (int c = 0; c <= Columns; c++)
            {
                double tx = (double)c / Columns;
                double x = tx * iw + tx * (src.Width - iw) * (1 - j) * (1 - ty) + tx * (src.Width - iw) * (1 - k) * ty;
                double effectX = ripple * (src.Left + src.Width * tx - (ix + iw * tx));
                positions[r * rowStride + c] = new Point3D(src.Left + x + offsetX + effectX, yUp, 0);
            }
        }
        mesh.Positions = positions;
    }

    private void ApplyMacClassic(double tt)
    {
        var src = Src;
        var mesh = _mesh!;
        var positions = _positions!;
        double dockX = Target.X, dockY = Target.Y;
        double neckW = Math.Clamp(src.Width * 0.03, 12, 60);
        double cx0 = src.Left + src.Width / 2;
        double mh = MonitorHeight;
        int rowStride = Columns + 1;

        mesh.Positions = null;
        for (int r = 0; r <= Rows; r++)
        {
            double v = (double)r / Rows;
            double m = Clamp01(tt * (1 + MacSpread) - (1 - (_down ? v : 1 - v)) * MacSpread);
            double e = SmoothStep(m);
            double y = src.Top + src.Height * v;
            y += (dockY - y) * e;
            double width = Math.Max(1, src.Width + (neckW - src.Width) * e);
            double left = cx0 + (dockX - cx0) * e - width / 2;
            double yUp = mh - y;
            for (int c = 0; c <= Columns; c++)
                positions[r * rowStride + c] = new Point3D(left + width * c / Columns, yUp, 0);
        }
        mesh.Positions = positions;
    }

    private static double Clamp01(double t) => t < 0 ? 0 : t > 1 ? 1 : t;
}
