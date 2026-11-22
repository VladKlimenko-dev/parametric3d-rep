using System.Diagnostics;
using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;
using Parametric3D.Model.Threads;

static partial class Program
{
    static (Part Part, BuildContext Ctx, TFace Face) ThreadBlank(double outerD, double? holeD, double height = 40)
    {
        var part = new Part("thread matrix");
        var s = new Sketch("outer", Frame3.XY);
        part.Add(s);
        s.AddEntity(new SkCircle(new SkPoint(0, 0), outerD / 2));
        var ctx = new BuildContext { Part = part, Parameters = new ParameterTable() };
        var e = new ExtrudeFeature("Blank") { SketchId = s.Id };
        e.Side1.Distance = new ValueRef(height);
        e.Execute(ctx);
        if (holeD is { } hd)
        {
            var hs = new Sketch("hole", Frame3.XY);
            part.Add(hs);
            hs.AddEntity(new SkCircle(new SkPoint(0, 0), hd / 2));
            var h = new ExtrudeFeature("Hole") { SketchId = hs.Id, Cut = true };
            h.Side1.Distance = new ValueRef(height);
            h.Side1.Flip = false;
            h.Execute(ctx);
        }
        var body = ctx.Bodies.Single();
        var face = body.Faces.First(f =>
        {
            var ax = CosmeticThreadFeature.ThreadAxis(f);
            if (ax is not { } a) return false;
            var sh = Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) as Parametric3D.Kernel.Geometry.Ssi.Cylinder;
            return sh != null && Math.Abs(sh.R - (holeD ?? outerD) / 2) < 1e-6
                && Math.Abs(Vector3d.Dot(a.W.Normalized(), Vector3d.UnitZ)) > 0.999;
        });
        return (part, ctx, face);
    }

    static void ThreadMatrixChecks()
    {
        var spec = ThreadDb.All.First(t => t.Designation == "M10" && t.Series == "coarse");
        double major = spec.DMajor;                 // exact OD for a shaft
        double tap = spec.TapDrill ?? spec.IntMinorD; // exact OD for a hole

        // shaft blank diameters; hole blank diameters
        var cases = new (string Name, double ShaftD, double HoleD, bool ShaftExpected, bool HoleExpected)[]
        {
            ("too-big",   major + 4, tap + 4, true,  true),    // still cuttable, nominal warning
            ("too-small", major - 3, tap - 3, false, false),   // no candidate: refused
            ("minor",     spec.ExtRootD, spec.IntMinorD, false, true), // internal minor is a valid tap drill; external strict <
            ("slightly-smaller", major - 0.15*spec.P, spec.IntMinorD + 0.1, true, true),
            ("slightly-larger",  major + 0.15*spec.P, tap + 0.15*spec.P, true, true),
            ("exact-od",  major, tap, true, true),
        };

        void CheckCase(string kind, double d, bool expectedBuild, bool exit)
        {
            var (part, ctx, face) = ThreadBlank(kind == "external" ? d : 24, kind == "internal" ? d : null);
            var feature = new CosmeticThreadFeature { TypeId = spec.Id, Cosmetic = false, SmoothExit = exit };
            feature.Length = new ValueRef(20);
            feature.OnFaces.Add(FaceRefs.Inside(face));
            var beforeBody = ctx.Bodies.Single();
            double beforeVol = AnalyticVolume.Of(beforeBody) ?? double.NaN;
            try
            {
                feature.Execute(ctx);
                var body = ctx.Bodies.Single();
                var valid = SolidValidator.Validate(body);
                Require(expectedBuild, $"built unexpectedly (d={d})");
                Require(valid.IsValid, valid.Summary());
                Require(body.Guarantee == GuaranteeLevel.ExactAnalytic, "thread is not exact: " + body.Guarantee);
                Require(body.Faces.All(f => GeometryAudit.AuthoritativeSurface(f) is { } au && au is not Parametric3D.Kernel.Surfaces.FacetedSurface),
                    "faceted face left after exact thread build");
                Require(feature.LastRoute is "direct-screw" or "direct-runout" or "general-runout" or "exact-boolean", "unexpected route: " + feature.LastRoute);
            }
            catch (RebuildException ex)
            {
                Require(!expectedBuild, $"unexpected refusal (d={d}): {ex.Message}");
                Require(ctx.Bodies.Single() == beforeBody && Math.Abs((AnalyticVolume.Of(ctx.Bodies.Single()) ?? beforeVol) - beforeVol) < 1e-9 * beforeVol,
                    "refused thread changed the body");
            }
        }

        foreach (bool exit in new[] { false, true })
        {
            foreach (var c in cases)
            {
                Check($"THREAD-MATRIX-EXTERNAL-{c.Name}-EXIT{exit}", () => CheckCase("external", c.ShaftD, c.ShaftExpected, exit));
                Check($"THREAD-MATRIX-INTERNAL-{c.Name}-EXIT{exit}", () => CheckCase("internal", c.HoleD, c.HoleExpected, exit));
            }
        }
    }
}
