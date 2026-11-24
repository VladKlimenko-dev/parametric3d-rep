using System.Globalization;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Export;

/// <summary>
/// МИНИМАЛЬНЫЙ ЧИТАТЕЛЬ OCCT BREP (ASCII, формат BRepTools) для SMLib TestFiles/occt_breps.
/// Поддерживает Line, BSplineCurve, Plane, BSplineSurface и топологию Ve/Ed/Wi/Fa/Sh/So.
/// Сопряжения по периодическому шву пока не режутся — такие файлы честно отказываются.
/// Мутация P3D_BREP=0 отключает читатель.
/// </summary>
public static class OcctBrepReader
{
    public static bool Enabled => Environment.GetEnvironmentVariable("P3D_BREP") != "0";

    public static TSolid? Read(string text, List<string>? warnings = null)
    {
        var tk = Tokenize(text);
        int p = 0;
        var inv = CultureInfo.InvariantCulture;

        double D() => double.Parse(tk[p++], inv);
        int I() => int.Parse(tk[p++], inv);

        // заголовок
        while (p < tk.Count && tk[p] != "Locations") p++;
        if (p >= tk.Count) { warnings?.Add("BREP: no Locations section"); return null; }
        p++; int nLoc = I();
        if (nLoc != 0) { warnings?.Add("BREP: locations not supported"); return null; }

        SkipCurve2ds(tk, ref p);

        SkipHeader(tk, ref p, "Curves");
        int nCurves = I();
        var curves = new (ICurve3? C, Vector3d O, Vector3d D)[nCurves];
        for (int i = 0; i < nCurves; i++)
        {
            int type = I();
            if (type == 1)
            {
                var o = new Vector3d(D(), D(), D());
                var d = new Vector3d(D(), D(), D());
                curves[i] = (null, o, d);   // параметрическая прямая, отрезок строим по диапазону ребра
            }
            else if (type == 7)
            {
                int periodic = I();
                int rationalFlag = I();
                int deg = I(), nPoles = I(), nKnots = I();
                if (periodic != 0) { warnings?.Add($"BREP: periodic B-spline curve {i + 1} not yet supported"); return null; }
                bool rat = rationalFlag != 0;
                var poles = new Vector3d[nPoles];
                var w = new double[nPoles];
                for (int k = 0; k < nPoles; k++)
                {
                    poles[k] = new Vector3d(D(), D(), D());
                    w[k] = rat ? D() : 1.0;
                }
                var knots = new double[nKnots]; var mults = new int[nKnots];
                for (int k = 0; k < nKnots; k++) { knots[k] = D(); mults[k] = I(); }
                curves[i] = (new SplineCurve3(poles, deg, rat ? w : null, ExpandKnots(knots, mults)).WithUnitKnots(), default, default);
            }
            else { warnings?.Add($"BREP: curve type {type} not supported"); return null; }
        }

        SkipSection(tk, ref p, "Polygon3D");
        SkipSection(tk, ref p, "PolygonOnTriangulations");

        SkipHeader(tk, ref p, "Surfaces");
        int nSurf = I();
        var surfaces = new ISurface3?[nSurf];
        for (int i = 0; i < nSurf; i++)
        {
            int type = I();
            if (type == 1)
            {
                var o = new Vector3d(D(), D(), D());
                var n = new Vector3d(D(), D(), D());
                var u = new Vector3d(D(), D(), D());
                var v = new Vector3d(D(), D(), D());
                surfaces[i] = new PlaneSurface(new Frame3(o, u, v));
            }
            else if (type == 9)
            {
                int ur = I(), vr = I();      // URational, VRational
                int up = I(), vp = I();      // UPeriodic, VPeriodic
                int ud = I(), vd = I(), nu = I(), nv = I(), nuk = I(), nvk = I();
                if (up != 0 || vp != 0) { warnings?.Add($"BREP: periodic B-spline surface {i + 1} not yet supported"); return null; }
                bool rat = ur != 0 || vr != 0;
                var cp = new Vector3d[nu, nv];
                var w = new double[nu, nv];
                for (int u = 0; u < nu; u++)
                    for (int v = 0; v < nv; v++)
                    {
                        cp[u, v] = new Vector3d(D(), D(), D());
                        w[u, v] = rat ? D() : 1.0;
                    }
                var ku = new double[nuk]; var mu = new int[nuk];
                for (int k = 0; k < nuk; k++) { ku[k] = D(); mu[k] = I(); }
                var kv = new double[nvk]; var mv = new int[nvk];
                for (int k = 0; k < nvk; k++) { kv[k] = D(); mv[k] = I(); }
                surfaces[i] = new NurbsSurface(cp, ud, vd, rat ? w : null, ExpandKnots(ku, mu), ExpandKnots(kv, mv));
            }
            else { warnings?.Add($"BREP: surface type {type} not supported"); return null; }
        }

        SkipSection(tk, ref p, "Triangulations");

        SkipHeader(tk, ref p, "TShapes");
        int nShapes = I();
        var recs = new List<ShapeRec>(nShapes);

        for (int i = 0; i < nShapes; i++)
        {
            string kind = tk[p++];
            var r = new ShapeRec { Kind = kind };
            if (kind == "Ve")
            {
                r.Tolerance = D();
                r.Point = new Vector3d(D(), D(), D());
                p += 2;                       // '0 0'
                SkipFlags(tk, ref p);
                Expect(tk, ref p, "*");
            }
            else if (kind == "Ed")
            {
                r.Tolerance = D();
                p += 3;                       // sameParam sameRange degenerated
                while (p < tk.Count)
                {
                    string t = tk[p];
                    if (t == "0") { p++; break; }
                    if (t == "1")
                    {
                        p++; r.CurveIndex = I();
                        I();                   // location index
                        r.T0 = D(); r.T1 = D();  // first, last
                    }
                    else if (t == "2")
                    {
                        p++; I();              // pcurve index
                        I();                   // location index
                        D(); D();              // first, last
                    }
                    else break;
                }
                SkipFlags(tk, ref p);
                r.Sub = ParseSubShapes(tk, ref p);
            }
            else if (kind == "Fa")
            {
                p++;                         // первый 0
                r.Tolerance = D();
                r.SurfaceIndex = I();
                p++;                         // location 0
                SkipFlags(tk, ref p);
                r.Sub = ParseSubShapes(tk, ref p);
            }
            else if (kind is "Wi" or "Sh" or "So")
            {
                SkipFlags(tk, ref p);
                r.Sub = ParseSubShapes(tk, ref p);
            }
            recs.Add(r);
        }

        // Нумерация BREP: запись i (0-based) -> номер n - i (корень = последняя запись = #1)
        var vertices = new TVertex?[nShapes + 1];
        var edges = new TEdge?[nShapes + 1];
        var faces = new TFace?[nShapes + 1];
        var shells = new TShell?[nShapes + 1];
        var wires = new List<(int Index, int Orientation)>[nShapes + 1];

        for (int i = 0; i < recs.Count; i++)
        {
            int num = nShapes - i;
            var r = recs[i];
            if (r.Kind == "Ve")
                vertices[num] = new TVertex(r.Point) { Tolerance = r.Tolerance };
        }

        for (int i = 0; i < recs.Count; i++)
        {
            int num = nShapes - i;
            var r = recs[i];
            if (r.Kind != "Ed") continue;
            var vr = r.Sub.Where(s => s.Index >= 1 && s.Index <= nShapes).ToList();
            if (vr.Count < 2) { warnings?.Add($"BREP: edge {num} lacks two vertices"); return null; }
            var c = r.CurveIndex >= 1 && r.CurveIndex <= nCurves ? curves[r.CurveIndex - 1] : default;
            ICurve3? ec = c.C;
            if (ec == null && c.D != default)
                ec = new LineCurve3(c.O + c.D * r.T0, c.O + c.D * r.T1);
            if (ec == null) { warnings?.Add($"BREP: edge {num} lacks a curve"); return null; }
            var va = vertices[vr[0].Index]!; var vb = vertices[vr[1].Index]!;
            if ((ec.PointAt(ec.T0) - va.P).LengthSquared > (ec.PointAt(ec.T0) - vb.P).LengthSquared)
                (va, vb) = (vb, va);
            edges[num] = new TEdge { V0 = va, V1 = vb, Curve = ec, Tolerance = r.Tolerance };
        }

        for (int i = 0; i < recs.Count; i++)
        {
            int num = nShapes - i;
            var r = recs[i];
            if (r.Kind != "Wi") continue;
            var loop = new TLoop { IsOuter = true };
            foreach (var s in r.Sub)
                if (s.Index >= 1 && s.Index <= nShapes && edges[s.Index] is { } e)
                    loop.Add(e, s.Orientation < 0);
            wires[num] = r.Sub;
            // сохраним контур для сборки граней (создаём один TLoop на провод)
            r.Loop = loop;
        }

        for (int i = 0; i < recs.Count; i++)
        {
            int num = nShapes - i;
            var r = recs[i];
            if (r.Kind != "Fa") continue;
            var face = new TFace
            {
                Surface = r.SurfaceIndex >= 1 && r.SurfaceIndex <= nSurf ? surfaces[r.SurfaceIndex - 1]! : null!,
                Tag = $"brep:face[{num}]"
            };
            if (face.Surface == null) { warnings?.Add($"BREP: face {num} lacks a surface"); return null; }
            int li = 0;
            foreach (var s in r.Sub)
            {
                if (s.Index >= 1 && s.Index <= nShapes && recs[nShapes - s.Index].Loop is { } loop)
                {
                    loop.Face = face;
                    loop.IsOuter = li == 0;
                    face.Loops.Add(loop);
                    li++;
                }
            }
            faces[num] = face;
        }

        for (int i = 0; i < recs.Count; i++)
        {
            int num = nShapes - i;
            var r = recs[i];
            if (r.Kind != "Sh") continue;
            var shell = new TShell();
            foreach (var s in r.Sub)
                if (s.Index >= 1 && s.Index <= nShapes && faces[s.Index] is { } f)
                { f.Shell = shell; shell.Faces.Add(f); }
            shells[num] = shell;
        }

        TSolid? solid = null;
        for (int i = 0; i < recs.Count; i++)
        {
            int num = nShapes - i;
            var r = recs[i];
            if (r.Kind != "So") continue;
            solid = new TSolid();
            foreach (var s in r.Sub)
                if (s.Index >= 1 && s.Index <= nShapes && shells[s.Index] is { } sh)
                { sh.Solid = solid; solid.Shells.Add(sh); }
        }

        if (solid == null)
        {
            // Корень может быть гранью или оболочкой (у SMLib есть одиночная грань).
            var root = recs[^1];
            if (root.Kind == "Fa" && faces[1] is { } rf)
            {
                var sh = new TShell(); rf.Shell = sh; sh.Faces.Add(rf);
                solid = new TSolid(); sh.Solid = solid; solid.Shells.Add(sh);
            }
            else if (root.Kind == "Sh" && shells[1] is { } rsh)
            {
                solid = new TSolid(); rsh.Solid = solid; solid.Shells.Add(rsh);
            }
        }
        if (solid == null) { warnings?.Add("BREP: no solid"); return null; }
        return solid;
    }

    sealed class ShapeRec
    {
        public string Kind = "";
        public double Tolerance;
        public Vector3d Point;
        public int CurveIndex = -1, SurfaceIndex = -1;
        public double T0, T1 = 1;
        public List<(int Index, int Orientation)> Sub = new();
        public TLoop? Loop;
    }

    static void SkipFlags(List<string> tk, ref int p)
    {
        if (p < tk.Count && tk[p].Length > 1 && tk[p].All(c => c is '0' or '1')) p++;
    }

    static void SkipCurve2ds(List<string> tk, ref int p)
    {
        SkipHeader(tk, ref p, "Curve2ds");
        int n = int.Parse(tk[p++], CultureInfo.InvariantCulture);
        for (int i = 0; i < n; i++)
        {
            int type = int.Parse(tk[p++], CultureInfo.InvariantCulture);
            if (type == 1) p += 4;                      // x y dx dy
            else if (type == 2) p += 3;                 // x y r
            else if (type == 7)
            {
                int rat = int.Parse(tk[p + 1], CultureInfo.InvariantCulture);
                int nP = int.Parse(tk[p + 3], CultureInfo.InvariantCulture);
                int nK = int.Parse(tk[p + 4], CultureInfo.InvariantCulture);
                p += 5; // periodic rational degree nPoles nKnots
                for (int k = 0; k < nP; k++) { p += 2; if (rat != 0) p++; }
                p += 2 * nK;                             // knots + mults interleaved
            }
            else throw new FormatException($"BREP: curve2d type {type} not supported");
        }
    }

    static void Expect(List<string> tk, ref int p, string s)
    {
        if (p >= tk.Count || tk[p++] != s) throw new FormatException($"BREP: expected '{s}'");
    }

    static List<(int Index, int Orientation)> ParseSubShapes(List<string> tk, ref int p)
    {
        var list = new List<(int, int)>();
        while (p < tk.Count && tk[p] != "*")
        {
            string t = tk[p++];
            if (t.StartsWith('+')) list.Add((int.Parse(t[1..], CultureInfo.InvariantCulture), 1));
            else if (t.StartsWith('-')) list.Add((int.Parse(t[1..], CultureInfo.InvariantCulture), -1));
        }
        if (p < tk.Count) p++;
        return list;
    }

    static void SkipHeader(List<string> tk, ref int p, string name)
    {
        while (p < tk.Count && tk[p] != name) p++;
        if (p < tk.Count) p++;
    }

    static void SkipSection(List<string> tk, ref int p, string name)
    {
        while (p < tk.Count && tk[p] != name) p++;
        if (p >= tk.Count) return;
        p++;
        int n = int.Parse(tk[p++], CultureInfo.InvariantCulture);
        if (n != 0) throw new FormatException($"BREP: section '{name}' with {n} records not supported");
    }

    static double[] ExpandKnots(double[] knots, int[] mults)
    {
        var list = new List<double>(mults.Sum());
        for (int i = 0; i < knots.Length; i++)
            for (int j = 0; j < mults[i]; j++) list.Add(knots[i]);
        return list.ToArray();
    }

    static List<string> Tokenize(string text)
    {
        var list = new List<string>(text.Length / 6);
        int i = 0, n = text.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(text[i])) i++;
            if (i >= n) break;
            int start = i;
            while (i < n && !char.IsWhiteSpace(text[i])) i++;
            list.Add(text[start..i]);
        }
        return list;
    }
}
