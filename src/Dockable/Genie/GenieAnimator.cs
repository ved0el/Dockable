using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Dockable.Interop;

namespace Dockable.Genie;

/// <summary>
/// Plays a macOS-style "genie" warp: a captured window image mapped onto a fine grid mesh
/// that flows and pinches into the dock target over a short duration. Rendered with WPF 3D
/// (Viewport3D + animated MeshGeometry3D). The pre-warmed, reusable, click-through overlay and
/// the render loop live in <see cref="OverlayAnimatorBase"/>.
///
/// Coordinates are device-independent (DIP); rects are in virtual-screen DIPs.
/// </summary>
public sealed class GenieAnimator : OverlayAnimatorBase
{
    // Mesh resolution. Finer horizontally so the Suck black-hole spiral stays smooth; resolved from
    // PerformanceProfile at overlay-build time (coarser when reduced → less per-frame GPU buffer upload).
    private int Columns = 12;
    private int Rows = 48;

    /// <summary>Which curve the mesh warp uses; both share the engine, differing only in shaping.</summary>
    public enum GenieStyle { Suck, Genie }

    /// <summary>Per-style curve parameters.</summary>
    /// <param name="Stagger">How far the leading rows run ahead of the trailing ones. This IS the
    /// genie: at 1.2 the bottom row has been swallowed by the tile while the top row hasn't moved
    /// yet, so the sheet is strung out along the path and pulls through a neck anchored at the dock.
    /// Low values make every row shrink in unison — a rectangle that just narrows and drops.</param>
    /// <param name="Duration">Animation length in ms (before EffectSpeed).</param>
    private readonly record struct StyleParams(double Stagger, double Duration);

    private static StyleParams ParamsFor(GenieStyle style) => style switch
    {
        // Smoke into a bottle: heavy stagger, and a row's width tracks its own position along the path,
        // so it reaches the tile width exactly AT the tile — the neck stays anchored to the dock.
        GenieStyle.Genie => new StyleParams(Stagger: 1.2, Duration: 820),
        // Black hole: every point is dragged straight toward the target, nearest points first — so the
        // window stretches and collapses into the spot. Stagger = how strongly nearer points lead.
        _ => new StyleParams(Stagger: 0.95, Duration: 300),
    };

    /// <summary>Neck width at full warp, as a fraction of the landing tile's width (with a floor in
    /// DIP). Roughly the icon's own width, like the Dock: the sheet is swallowed by something the size
    /// of the icon, it does not converge to a tip.</summary>
    private const double NeckWidthFactor = 0.85;
    private const double NeckWidthFloor = 10;

    /// <summary>How late the width collapse happens relative to the row's descent (exponent on the
    /// eased local progress). THIS is what separates a neck from a cone: at 1.0 the width shrinks in
    /// lockstep with the drop, so the whole path tapers evenly and reads as a pointed funnel. Above 1
    /// the body keeps nearly its full width for most of the travel and pinches only in the last
    /// stretch, right at the dock — the Dock's short, near-parallel neck under a full-width body.</summary>
    private const double WidthPinchPower = 2.5;

    /// <summary>Which curve to warp with; set before each play (defaults to the Suck funnel).</summary>
    public GenieStyle Style { get; set; } = GenieStyle.Suck;

    private MeshGeometry3D? _mesh;
    private Point3DCollection? _positions; // == _mesh.Positions; detached while bulk-mutated each frame
    private ImageBrush? _brush;
    private OrthographicCamera? _camera;

    // Per-play precomputed invariants (depend only on Src/Target/_leadFromTop, fixed across a play's
    // frames): source vertex coords, the black-hole fall-in lag, and the genie per-row lead/origin-Y.
    // Recomputing these (esp. the per-vertex sqrt distance) every frame was pure waste.
    private int _vertexCount;
    private double[]? _ox, _oy;          // source vertex positions (overlay-local DIP)
    private double[]? _lag;              // black-hole normalized distance-to-target (0 near … 1 far)
    private double[]? _leadRow;          // genie per-row flow lead (0 = leads, 1 = trails)
    private double[]? _origYRow;         // genie per-row source Y

    // Which window edge leads the warp: the edge nearest the dock tile. Top-anchored docks lead from
    // the top (flow downward→up into the tile); bottom-anchored docks lead from the bottom.
    private bool _leadFromTop;
    private StyleParams _params = ParamsFor(GenieStyle.Suck); // resolved at the start of each play

    protected override double BaseDurationMs => _params.Duration;

    protected override string ProfileName => Style.ToString();

    protected override UIElement BuildOverlayContent()
    {
        (Columns, Rows) = PerformanceProfile.GenieMesh;
        _mesh = BuildMesh();
        _positions = _mesh.Positions; // held so we can detach/reattach it cheaply each frame

        _vertexCount = (Columns + 1) * (Rows + 1);
        _ox = new double[_vertexCount];
        _oy = new double[_vertexCount];
        _lag = new double[_vertexCount];
        _leadRow = new double[Rows + 1];
        _origYRow = new double[Rows + 1];

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
                double u = (double)i / Columns;
                mesh.Positions.Add(new Point3D(0, 0, 0)); // filled in by ApplyFrame
                mesh.TextureCoordinates.Add(new Point(u, v));
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

    /// <summary>
    /// Resolves the per-play style/lead, then precomputes everything that stays fixed for the whole
    /// play (source vertex coords, the black-hole fall-in lag with its per-vertex distance, and the
    /// genie per-row lead/origin) so the per-frame <see cref="ApplyFrame"/> only does the cheap warp
    /// interpolation — no <c>sqrt</c> per vertex per frame. The base calls this once Src/Target are
    /// final. (At warp 0 every vertex maps to its source position regardless of the lead, so
    /// recomputing the lead for ShowAtSource's frame 0 is inert.)
    /// </summary>
    protected override void PreparePlay()
    {
        _params = ParamsFor(Style);
        _leadFromTop = LeadsFromTop();

        var src = Src;
        double tx = Target.X, ty = Target.Y;
        int rowStride = Columns + 1;

        for (int j = 0; j <= Rows; j++)
        {
            double v = (double)j / Rows;
            double rowY = src.Top + v * src.Height;
            _origYRow![j] = rowY;
            // The edge nearest the dock leads the flow: top rows (v=0) for a top-anchored dock, bottom
            // rows (v=1) otherwise.
            _leadRow![j] = _leadFromTop ? v : 1 - v;
            for (int i = 0; i <= Columns; i++)
            {
                int idx = j * rowStride + i;
                _ox![idx] = src.Left + (double)i / Columns * src.Width;
                _oy![idx] = rowY;
            }
        }

        // Black-hole fall-in lag: normalized distance to the target (0 at the target … 1 at the farthest
        // corner), so the vertices nearest the target arrive first. Constant across the play's frames.
        double maxDist = 1e-3;
        maxDist = Math.Max(maxDist, Distance(src.Left - tx, src.Top - ty));
        maxDist = Math.Max(maxDist, Distance(src.Right - tx, src.Top - ty));
        maxDist = Math.Max(maxDist, Distance(src.Left - tx, src.Bottom - ty));
        maxDist = Math.Max(maxDist, Distance(src.Right - tx, src.Bottom - ty));
        double invMax = 1.0 / maxDist;
        for (int idx = 0; idx < _vertexCount; idx++)
            _lag![idx] = Clamp01(Distance(_ox![idx] - tx, _oy![idx] - ty) * invMax);
    }

    /// <summary>Recomputes vertex positions for a given warp amount, per the active style (the genie
    /// eases per-vertex inside, so the base's raw warp is exactly what these curves expect).</summary>
    protected override void ApplyFrame(double warp)
    {
        if (Style == GenieStyle.Suck)
            UpdateMeshBlackHole(warp);
        else
            UpdateMeshGenie(warp);
    }

    /// <summary>
    /// Black-hole collapse: every vertex is pulled straight toward the target, with the vertices nearest
    /// the target arriving first (so the window stretches and collapses into the point — gravity, not a
    /// uniform funnel). At full warp everything reaches the target (the thumbnail's spot).
    /// </summary>
    private void UpdateMeshBlackHole(double warp)
    {
        var mesh = _mesh!;
        var positions = _positions!;
        double tx = Target.X, ty = Target.Y, h = MonitorHeight;
        double stagger = _params.Stagger;
        double baseProgress = warp * (1 + stagger);

        // Detach the collection so the 637 element writes don't each propagate a change notification up
        // to the mesh; reattach once at the end for a single re-realization of the vertex buffer.
        mesh.Positions = null;
        for (int idx = 0; idx < _vertexCount; idx++)
        {
            double e = SmoothStep(Clamp01(baseProgress - _lag![idx] * stagger));
            double x = Lerp(_ox![idx], tx, e); // straight pull toward the thumbnail's spot
            double y = Lerp(_oy![idx], ty, e);
            positions[idx] = new Point3D(x, h - y, 0);
        }
        mesh.Positions = positions;
    }

    /// <summary>
    /// Genie funnel: rows flow in a heavy stagger, each one narrowing toward the tile in step with its
    /// own descent — so width is a function of where the row IS, and the pinch sits at the dock while
    /// the body above it stays full width. That correlation is the neck; the stagger is the flow.
    /// </summary>
    private void UpdateMeshGenie(double warp)
    {
        var mesh = _mesh!;
        var positions = _positions!;
        var src = Src;
        var target = Target;
        double stagger = _params.Stagger;
        double srcCenterX = src.Left + src.Width / 2;
        int rowStride = Columns + 1;
        double neckWidth = Math.Max(NeckWidthFloor, TargetTileWidth * NeckWidthFactor);
        double h = MonitorHeight;
        double baseProgress = warp * (1 + stagger);

        mesh.Positions = null; // detach for cheap bulk mutation (see UpdateMeshBlackHole)
        for (int j = 0; j <= Rows; j++)
        {
            double e = SmoothStep(Clamp01(baseProgress - _leadRow![j] * stagger));
            // Width collapses LATER than the descent (see WidthPinchPower) so the neck is short and
            // sits at the dock, instead of the whole path tapering into a cone.
            double wE = Math.Pow(e, WidthPinchPower);
            double rowCenterX = Lerp(srcCenterX, target.X, e); // the sideways slide still tracks the descent
            double rowWidth = Lerp(src.Width, neckWidth, wE);
            // 3D Y is up; screen Y is down — flip into the orthographic camera's space.
            double yUp = h - Lerp(_origYRow![j], target.Y, e);

            int rowBase = j * rowStride;
            for (int i = 0; i <= Columns; i++)
            {
                double x = rowCenterX + ((double)i / Columns - 0.5) * rowWidth;
                positions[rowBase + i] = new Point3D(x, yUp, 0);
            }
        }
        mesh.Positions = positions;
    }

    /// <summary>The tile is above the window's center → the dock is on the top edge, so lead from the top.</summary>
    private bool LeadsFromTop() => Target.Y < Src.Top + Src.Height / 2;

    private static double Clamp01(double t) => t < 0 ? 0 : t > 1 ? 1 : t;
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Distance(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);
}
