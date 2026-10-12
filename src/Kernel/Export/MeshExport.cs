using System.Globalization;
using System.Text;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Export;

/// <summary>Треугольник для экспорта: вершины в миллиметрах, в мировых координатах, и грань-источник.</summary>
public readonly record struct ExportTriangle(Vector3d A, Vector3d B, Vector3d C, uint Face);

/// <summary>
/// ЭКСПОРТ СЕТКИ: STL (двоичный и текстовый) и OBJ.
///
/// Источник — разбиение каждой грани в координатах double, то же, на котором
/// проверочный прогон проверяет замкнутость: соседние грани делят одни и те же
/// точки, и файл выходит без щелей. STL хранит координаты во float, но одна и та
/// же точка double всегда даёт один и тот же float — общие вершины остаются общими.
/// Единицы — миллиметры (ни STL, ни OBJ единиц не хранят).
/// </summary>
public static class MeshExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ================================================================ сбор
    /// <summary>Треугольники тел, перенесённые матрицей world (null — как есть).</summary>
    public static List<ExportTriangle> Triangles(IEnumerable<TSolid> bodies, Matrix4d? world = null)
    {
        var list = new List<ExportTriangle>();
        foreach (TSolid body in bodies)
            foreach (TFace face in body.Faces)
            {
                var mb = new MeshBuilder();
                face.Surface.TessellateFace(face, mb);
                var md = new MeshData();
                mb.FillInto(md);

                for (int i = 0; i + 2 < md.Indices.Length; i += 3)
                {
                    Vector3d a = mb.PositionsD[md.Indices[i]], b = mb.PositionsD[md.Indices[i + 1]], c = mb.PositionsD[md.Indices[i + 2]];
                    if (a == b || b == c || c == a) continue;          // вырожденный — не нужен
                    if (world is { } m)
                    {
                        a = Vector3d.TransformPosition(a, m);
                        b = Vector3d.TransformPosition(b, m);
                        c = Vector3d.TransformPosition(c, m);
                    }
                    list.Add(new ExportTriangle(a, b, c, face.Id));
                }
            }
        return list;
    }

    private static Vector3d Normal(ExportTriangle t)
    {
        Vector3d n = Vector3d.Cross(t.B - t.A, t.C - t.A);
        return n.LengthSquared > 0 ? n.Normalized() : Vector3d.Zero;
    }

    // ================================================================ STL
    /// <summary>
    /// Треугольники во float, как их хранит STL, — без выродившихся.
    ///
    /// Две РАЗНЫЕ точки точного ядра бывают ближе шага float (около 2e-6 мм на
    /// координатах порядка 20) и во float сливаются в одну. Треугольник между ними
    /// вырождается, и соседние рёбра перестают сходиться: у вогнутого кольца бобышки
    /// выходило 12 открытых рёбер. Выродившийся во float треугольник не нужен —
    /// как и на экране, где то же делается для совпавших double.
    /// </summary>
    private static List<(Vector3 N, Vector3 A, Vector3 B, Vector3 C)> FloatTriangles(IReadOnlyList<ExportTriangle> tris)
    {
        // СКЛЕЙКА ДО ПЕРЕВОДА ВО FLOAT.
        //
        // Точки ближе, чем float способен различить на этих координатах, склеиваются
        // в одну — в double, по сетке ячеек. Порог — четыре шага float на наибольшей
        // координате, но не меньше 1e-5 мм (шаг сетки точного ядра: точки ближе него
        // и так означают одно место). Без склейки несколько близких точек сливались во
        // float по-разному, и рёбра вокруг них не сходились (проверочный прогон нашёл
        // 11 незамкнутых STL на 2791 тело).
        double extent = 1;
        foreach (var t in tris)
            foreach (var p in new[] { t.A, t.B, t.C })
                extent = Math.Max(extent, Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))));
        double eps = Math.Max(1e-5, 4 * extent * 1.2e-7);

        var cells = new Dictionary<(long, long, long), List<Vector3d>>();
        Vector3d Weld(Vector3d p)
        {
            long cx = (long)Math.Floor(p.X / eps), cy = (long)Math.Floor(p.Y / eps), cz = (long)Math.Floor(p.Z / eps);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                        if (cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var l))
                            foreach (var q in l)
                                if ((q - p).LengthSquared <= eps * eps) return q;
            if (!cells.TryGetValue((cx, cy, cz), out var own)) cells[(cx, cy, cz)] = own = new List<Vector3d>();
            own.Add(p);
            return p;
        }

        var list = new List<(Vector3, Vector3, Vector3, Vector3)>(tris.Count);
        foreach (var t in tris)
        {
            Vector3 a = F(Weld(t.A)), b = F(Weld(t.B)), c = F(Weld(t.C));
            if (a == b || b == c || c == a) continue;
            Vector3d n = Normal(t);
            list.Add(((Vector3)n, a, b, c));
        }

        // Слияние точек во float может сделать два треугольника одинаковыми.
        // Противоположно ориентированные гасят друг друга — убираются оба;
        // одинаково ориентированный повтор — один.
        var seen = new Dictionary<(Vector3, Vector3, Vector3), int>();
        var drop = new bool[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            var (_, a, b, c) = list[i];
            var key = Canon(a, b, c);
            var back = Canon(a, c, b);
            if (seen.TryGetValue(back, out int j) && !drop[j]) { drop[i] = drop[j] = true; seen.Remove(back); continue; }
            if (seen.ContainsKey(key)) { drop[i] = true; continue; }
            seen[key] = i;
        }

        var clean = new List<(Vector3, Vector3, Vector3, Vector3)>(list.Count);
        for (int i = 0; i < list.Count; i++) if (!drop[i]) clean.Add(list[i]);
        return clean;

        // Треугольник с точностью до циклического сдвига — от наименьшей вершины.
        static (Vector3, Vector3, Vector3) Canon(Vector3 a, Vector3 b, Vector3 c)
        {
            static bool Less(Vector3 p, Vector3 q) => p.X < q.X || (p.X == q.X && (p.Y < q.Y || (p.Y == q.Y && p.Z < q.Z)));
            if (Less(b, a) && !Less(c, b)) return (b, c, a);
            if (Less(c, a) && Less(c, b)) return (c, a, b);
            return (a, b, c);
        }

        static Vector3 F(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
    }

    /// <summary>Двоичный STL: заголовок 80 байт, число треугольников, по 50 байт на треугольник.</summary>
    public static void WriteStlBinary(Stream stream, IReadOnlyList<ExportTriangle> tris, string name = "Parametric3D")
    {
        var ft = FloatTriangles(tris);
        using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        var header = new byte[80];
        byte[] text = Encoding.ASCII.GetBytes(("Parametric3D, mm: " + name).PadRight(80)[..80]);
        Array.Copy(text, header, 80);
        w.Write(header);
        w.Write((uint)ft.Count);

        foreach (var (n, a, b, c) in ft)
        {
            W(n); W(a); W(b); W(c);
            w.Write((ushort)0);
        }

        void W(Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    }

    /// <summary>Текстовый STL.</summary>
    public static void WriteStlAscii(TextWriter w, IReadOnlyList<ExportTriangle> tris, string name = "Parametric3D")
    {
        string solid = Sanitize(name);
        w.WriteLine($"solid {solid}");
        foreach (var (n, a, b, c) in FloatTriangles(tris))
        {
            w.WriteLine($"  facet normal {F(n.X)} {F(n.Y)} {F(n.Z)}");
            w.WriteLine("    outer loop");
            w.WriteLine($"      vertex {F(a.X)} {F(a.Y)} {F(a.Z)}");
            w.WriteLine($"      vertex {F(b.X)} {F(b.Y)} {F(b.Z)}");
            w.WriteLine($"      vertex {F(c.X)} {F(c.Y)} {F(c.Z)}");
            w.WriteLine("    endloop");
            w.WriteLine("  endfacet");
        }
        w.WriteLine($"endsolid {solid}");

        // Те же float, что в двоичном STL: одна точка — одна запись, замкнутость сохраняется.
        static string F(float v) => v.ToString("R", Inv);
    }

    // ================================================================ OBJ
    /// <summary>
    /// OBJ: общие вершины (по точному значению double), группа на каждую грань модели —
    /// чтобы принимающая программа видела грани, а не одну безликую сетку.
    /// </summary>
    public static void WriteObj(TextWriter w, IReadOnlyList<ExportTriangle> tris, string name = "Parametric3D")
    {
        w.WriteLine("# Parametric3D export, units: mm");
        w.WriteLine($"o {Sanitize(name)}");

        var ids = new Dictionary<(double, double, double), int>();
        var order = new List<Vector3d>();
        int Id(Vector3d p)
        {
            var k = (p.X, p.Y, p.Z);
            if (!ids.TryGetValue(k, out int at)) { ids[k] = at = order.Count + 1; order.Add(p); }
            return at;
        }

        var faces = new List<(int A, int B, int C, uint Face)>(tris.Count);
        foreach (var t in tris) faces.Add((Id(t.A), Id(t.B), Id(t.C), t.Face));

        foreach (var p in order) w.WriteLine($"v {p.X.ToString("R", Inv)} {p.Y.ToString("R", Inv)} {p.Z.ToString("R", Inv)}");

        uint current = uint.MaxValue;
        foreach (var f in faces.OrderBy(f => f.Face))
        {
            if (f.Face != current) { current = f.Face; w.WriteLine($"g face_{current}"); }
            w.WriteLine($"f {f.A} {f.B} {f.C}");
        }
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
        return sb.Length > 0 ? sb.ToString() : "Parametric3D";
    }

    // ================================================================ чистка
    /// <summary>
    /// Треугольники после склейки точек ближе eps: выродившиеся убраны, совпавшие
    /// противоположные пары погашены, одинаковые повторы — по одному. Нужна там, где
    /// принимающая сторона различает точки хуже, чем точное ядро: float в STL,
    /// объявленная точность в STEP.
    /// </summary>
    public static List<ExportTriangle> Clean(IReadOnlyList<ExportTriangle> tris, double eps)
    {
        var cells = new Dictionary<(long, long, long), List<Vector3d>>();
        Vector3d Weld(Vector3d p)
        {
            long cx = (long)Math.Floor(p.X / eps), cy = (long)Math.Floor(p.Y / eps), cz = (long)Math.Floor(p.Z / eps);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                        if (cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var l))
                            foreach (var q in l)
                                if ((q - p).LengthSquared <= eps * eps) return q;
            if (!cells.TryGetValue((cx, cy, cz), out var own)) cells[(cx, cy, cz)] = own = new List<Vector3d>();
            own.Add(p);
            return p;
        }

        var welded = new List<ExportTriangle>(tris.Count);
        foreach (var t in tris)
        {
            Vector3d a = Weld(t.A), b = Weld(t.B), c = Weld(t.C);
            if (a == b || b == c || c == a) continue;
            welded.Add(new ExportTriangle(a, b, c, t.Face));
        }

        var seen = new Dictionary<(Vector3d, Vector3d, Vector3d), int>();
        var drop = new bool[welded.Count];
        for (int i = 0; i < welded.Count; i++)
        {
            var t = welded[i];
            var key = CanonD(t.A, t.B, t.C);
            var back = CanonD(t.A, t.C, t.B);
            if (seen.TryGetValue(back, out int j) && !drop[j]) { drop[i] = drop[j] = true; seen.Remove(back); continue; }
            if (seen.ContainsKey(key)) { drop[i] = true; continue; }
            seen[key] = i;
        }

        var clean = new List<ExportTriangle>(welded.Count);
        for (int i = 0; i < welded.Count; i++) if (!drop[i]) clean.Add(welded[i]);
        return clean;

        static (Vector3d, Vector3d, Vector3d) CanonD(Vector3d a, Vector3d b, Vector3d c)
        {
            static bool Less(Vector3d p, Vector3d q) => p.X < q.X || (p.X == q.X && (p.Y < q.Y || (p.Y == q.Y && p.Z < q.Z)));
            if (Less(b, a) && !Less(c, b)) return (b, c, a);
            if (Less(c, a) && Less(c, b)) return (c, a, b);
            return (a, b, c);
        }
    }

    // ================================================================ STEP
    /// <summary>
    /// ГРАНЁНЫЙ STEP (AP214, FACETED_BREP).
    ///
    /// Каждое тело — замкнутая оболочка CLOSED_SHELL, каждый треугольник — грань FACE
    /// с контуром POLY_LOOP; точки общие, в double. Это стандартное представление тела
    /// из плоских граней, его читают все CAD-системы. Цилиндр в нём — многогранник:
    /// точной кривой геометрии у ядра нет, и честнее передать гранёную форму, чем
    /// выдавать её за аналитическую. Единицы — миллиметры, точность — 1e-5 мм (шаг
    /// сетки точного ядра).
    /// </summary>
    public static void WriteStepFaceted(TextWriter w, IReadOnlyList<IReadOnlyList<ExportTriangle>> shells,
                                        string name = "Parametric3D", DateTime? stamp = null)
    {
        string id = StepString(name);
        string when = (stamp ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss", Inv);

        w.WriteLine("ISO-10303-21;");
        w.WriteLine("HEADER;");
        w.WriteLine("FILE_DESCRIPTION(('Parametric3D faceted solid export'),'2;1');");
        w.WriteLine($"FILE_NAME('{id}','{when}',(''),(''),'Parametric3D','Parametric3D','');");
        w.WriteLine("FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));");
        w.WriteLine("ENDSEC;");
        w.WriteLine("DATA;");

        int next = 1;
        int E(string body) { int n = next++; w.WriteLine($"#{n}={body};"); return n; }

        int appCtx = E("APPLICATION_CONTEXT('automotive design')");
        E($"APPLICATION_PROTOCOL_DEFINITION('international standard','automotive_design',2000,#{appCtx})");
        int prodCtx = E($"PRODUCT_CONTEXT('',#{appCtx},'mechanical')");
        int product = E($"PRODUCT('{id}','{id}','',(#{prodCtx}))");
        E($"PRODUCT_RELATED_PRODUCT_CATEGORY('part',$,(#{product}))");
        int formation = E($"PRODUCT_DEFINITION_FORMATION('','',#{product})");
        int defCtx = E($"PRODUCT_DEFINITION_CONTEXT('part definition',#{appCtx},'design')");
        int definition = E($"PRODUCT_DEFINITION('design','',#{formation},#{defCtx})");
        int shapeDef = E($"PRODUCT_DEFINITION_SHAPE('','',#{definition})");

        int lenUnit = E("(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.))");
        int angUnit = E("(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.))");
        int solUnit = E("(NAMED_UNIT(*)SI_UNIT($,.STERADIAN.)SOLID_ANGLE_UNIT())");
        int accuracy = E($"UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE(1.E-05),#{lenUnit},'distance_accuracy_value','confusion accuracy')");
        int ctx = E($"(GEOMETRIC_REPRESENTATION_CONTEXT(3)GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT((#{accuracy}))" +
                    $"GLOBAL_UNIT_ASSIGNED_CONTEXT((#{lenUnit},#{angUnit},#{solUnit}))REPRESENTATION_CONTEXT('Context #1','3D Context with UNIT and UNCERTAINTY'))");

        int origin = E("CARTESIAN_POINT('',(0.,0.,0.))");
        int zDir = E("DIRECTION('',(0.,0.,1.))");
        int xDir = E("DIRECTION('',(1.,0.,0.))");
        int placement = E($"AXIS2_PLACEMENT_3D('',#{origin},#{zDir},#{xDir})");

        var breps = new List<int>();
        foreach (var rawShell in shells)
        {
            // Точки ближе удвоенной объявленной точности (1e-5) склеиваются: читатель
            // считает их одной, и щепка между ними схлопывалась у него в грань
            // из одного-двух рёбер — оболочка рвалась (так вышло у OpenCascade).
            var shell = Clean(rawShell, 2e-5);
            if (shell.Count == 0) continue;

            var points = new Dictionary<(double, double, double), int>();
            int P(Vector3d v)
            {
                var k = (v.X, v.Y, v.Z);
                if (!points.TryGetValue(k, out int at)) points[k] = at = E($"CARTESIAN_POINT('',({R(v.X)},{R(v.Y)},{R(v.Z)}))");
                return at;
            }

            // ГРАНИ — ПЛОСКИЕ МНОГОУГОЛЬНИКИ, А НЕ ОТДЕЛЬНЫЕ ТРЕУГОЛЬНИКИ.
            //
            // Треугольники одной грани модели, лежащие в одной плоскости и имеющие общие
            // рёбра, сливаются в один многоугольник; его контуры — рёбра, встречающиеся
            // в группе один раз. Щепки нулевой площади растворяются внутри (у них нет
            // своей плоскости, и отдельной гранью их не записать — выходили дыры).
            // Плоская грань модели — один многоугольник, полоса цилиндра — один
            // четырёхугольник; отверстия в грани — внутренние контуры FACE_BOUND.
            // Каждая грань — FACE_SURFACE с плоскостью: голый FACE OpenCascade не переводит.
            var faces = new List<int>();
            foreach (var poly in PlanarPolygons(shell))
            {
                var (n, x, loops) = poly;
                int origin0 = P(loops[0][0]);
                int nd = E($"DIRECTION('',({R(n.X)},{R(n.Y)},{R(n.Z)}))");
                int xd = E($"DIRECTION('',({R(x.X)},{R(x.Y)},{R(x.Z)}))");
                int ax = E($"AXIS2_PLACEMENT_3D('',#{origin0},#{nd},#{xd})");
                int plane = E($"PLANE('',#{ax})");

                var bounds = new List<int>();
                for (int li = 0; li < loops.Count; li++)
                {
                    int loop = E($"POLY_LOOP('',({string.Join(",", loops[li].Select(v => "#" + P(v)))}))");
                    bounds.Add(E(li == 0 ? $"FACE_OUTER_BOUND('',#{loop},.T.)" : $"FACE_BOUND('',#{loop},.T.)"));
                }
                faces.Add(E($"FACE_SURFACE('',({string.Join(",", bounds.Select(bb => "#" + bb))}),#{plane},.T.)"));
            }

            int closed = E($"CLOSED_SHELL('',({string.Join(",", faces.Select(f => "#" + f))}))");
            breps.Add(E($"FACETED_BREP('{id}',#{closed})"));
        }

        int rep = E($"FACETED_BREP_SHAPE_REPRESENTATION('{id}',({string.Join(",", breps.Select(b => "#" + b))},#{placement}),#{ctx})");
        E($"SHAPE_DEFINITION_REPRESENTATION(#{shapeDef},#{rep})");

        w.WriteLine("ENDSEC;");
        w.WriteLine("END-ISO-10303-21;");

        // Вещественное STEP: обязательно с точкой (1. , 1.5, -2.E-05) и без потери точности.
        static string R(double v)
        {
            string s = v.ToString("R", Inv);
            if (s.Contains('E')) { int e = s.IndexOf('E'); string m = s[..e]; if (!m.Contains('.')) m += "."; return m + s[e..]; }
            return s.Contains('.') ? s : s + ".";
        }
    }

    /// <summary>
    /// Строка STEP (ISO 10303-21): апостроф удваивается, обратная косая — тоже,
    /// символы вне ASCII — штатной записью \X2\hhhh…\X0\ (UTF-16), так что русское
    /// имя детали доходит до принимающей программы, а не превращается в подчёркивания.
    /// </summary>
    /// <summary>
    /// Плоские многоугольники из треугольников оболочки: (нормаль, опорное направление,
    /// контуры — первый внешний). Треугольники сливаются, если принадлежат одной грани
    /// модели, делят ребро и лежат в одной плоскости; щепки нулевой площади — с любым
    /// соседом своей грани.
    /// </summary>
    private static List<(Vector3d N, Vector3d X, List<List<Vector3d>> Loops)> PlanarPolygons(IReadOnlyList<ExportTriangle> tris)
    {
        var ids = new Dictionary<(double, double, double), int>();
        var pts = new List<Vector3d>();
        int Id(Vector3d p) { var k = (p.X, p.Y, p.Z); if (!ids.TryGetValue(k, out int at)) { ids[k] = at = pts.Count; pts.Add(p); } return at; }

        int n = tris.Count;
        var v = new (int A, int B, int C)[n];
        var normal = new Vector3d[n];
        var area = new double[n];
        for (int i = 0; i < n; i++)
        {
            v[i] = (Id(tris[i].A), Id(tris[i].B), Id(tris[i].C));
            Vector3d cr = Vector3d.Cross(tris[i].B - tris[i].A, tris[i].C - tris[i].A);
            area[i] = cr.Length / 2;
            normal[i] = cr.LengthSquared > 1e-24 ? cr.Normalized() : Vector3d.Zero;
        }

        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }

        // Треугольники по неориентированному ребру.
        var byEdge = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < n; i++)
            foreach (var (a, b) in new[] { (v[i].A, v[i].B), (v[i].B, v[i].C), (v[i].C, v[i].A) })
            {
                var k = a < b ? (a, b) : (b, a);
                if (!byEdge.TryGetValue(k, out var l)) byEdge[k] = l = new List<int>();
                l.Add(i);
            }

        bool Coplanar(int i, int j)
        {
            if (normal[i] == Vector3d.Zero || normal[j] == Vector3d.Zero) return true;   // щепка — к соседу
            if (Vector3d.Dot(normal[i], normal[j]) < 1 - 1e-12) return false;
            double off = Math.Abs(Vector3d.Dot(pts[v[j].A] - pts[v[i].A], normal[i]));
            return off < 1e-9 * Math.Max(1, pts[v[i].A].Length);
        }

        foreach (var list in byEdge.Values)
            for (int x = 0; x < list.Count; x++)
                for (int y = x + 1; y < list.Count; y++)
                {
                    int i = list[x], j = list[y];
                    bool sliver = normal[i] == Vector3d.Zero || normal[j] == Vector3d.Zero;
                    // Щепка нулевой площади геометрии не меняет и присоединяется к любому
                    // соседу по ребру — даже другой грани модели: одна, без плоскости,
                    // она выбрасывалась вместе со своими рёбрами, и выходили дыры.
                    if (!sliver && (tris[i].Face != tris[j].Face || !Coplanar(i, j))) continue;
                    parent[Find(i)] = Find(j);
                }

        var result = new List<(Vector3d, Vector3d, List<List<Vector3d>>)>();
        foreach (var g in Enumerable.Range(0, n).GroupBy(Find))
        {
            // Нормаль группы — по самому большому треугольнику.
            int big = g.OrderByDescending(i => area[i]).First();
            if (normal[big] == Vector3d.Zero) continue;            // вся группа — щепки
            Vector3d nn = normal[big];

            // Контурные рёбра: направленные рёбра группы, чьей пары в группе нет.
            var directed = new Dictionary<(int, int), int>();
            foreach (int i in g)
                foreach (var e in new[] { (v[i].A, v[i].B), (v[i].B, v[i].C), (v[i].C, v[i].A) })
                    directed[e] = directed.GetValueOrDefault(e) + 1;
            var boundary = new Dictionary<int, List<int>>();
            foreach (var ((a, b), c) in directed)
            {
                int back = directed.GetValueOrDefault((b, a));
                for (int k = 0; k < c - back; k++)
                {
                    if (!boundary.TryGetValue(a, out var l)) boundary[a] = l = new List<int>();
                    l.Add(b);
                }
            }

            var loops = new List<List<Vector3d>>();
            while (boundary.Count > 0)
            {
                int start = boundary.Keys.First();
                var loop = new List<int>();
                int cur = start;
                for (int guard = 0; guard <= directed.Count + 1; guard++)
                {
                    loop.Add(cur);
                    var outs = boundary[cur];
                    int nxt = outs[^1];
                    outs.RemoveAt(outs.Count - 1);
                    if (outs.Count == 0) boundary.Remove(cur);
                    cur = nxt;
                    if (cur == start) break;
                    if (!boundary.ContainsKey(cur)) break;
                }
                if (loop.Count >= 3) loops.Add(loop.Select(i => pts[i]).ToList());
            }
            if (loops.Count == 0) continue;

            // Внешний контур — с наибольшей площадью в плоскости группы.
            double LoopArea(List<Vector3d> l)
            {
                Vector3d s0 = Vector3d.Zero;
                for (int i = 1; i + 1 < l.Count; i++) s0 += Vector3d.Cross(l[i] - l[0], l[i + 1] - l[0]);
                return Vector3d.Dot(s0, nn) / 2;
            }
            loops = loops.OrderByDescending(LoopArea).ToList();

            Vector3d xx = loops[0][1] - loops[0][0];
            xx = (xx - nn * Vector3d.Dot(xx, nn));
            if (xx.LengthSquared < 1e-24) xx = Math.Abs(nn.X) < 0.9 ? Vector3d.Cross(nn, Vector3d.UnitX) : Vector3d.Cross(nn, Vector3d.UnitY);
            result.Add((nn, xx.Normalized(), loops));
        }
        return result;
    }

    private static string StepString(string s)
    {
        if (string.IsNullOrEmpty(s)) return "Parametric3D";
        var sb = new StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            char ch = s[i];
            if (ch >= 32 && ch <= 126)
            {
                if (ch == '\'') sb.Append("''");
                else if (ch == '\\') sb.Append("\\\\");
                else sb.Append(ch);
                i++;
                continue;
            }
            sb.Append("\\X2\\");
            while (i < s.Length && (s[i] < 32 || s[i] > 126)) sb.Append(((int)s[i++]).ToString("X4", Inv));
            sb.Append("\\X0\\");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Разбор гранёного STEP обратно — для проверки экспорта: каждая оболочка списком
    /// треугольников. Проверяет и то, что все ссылки разрешаются.
    /// </summary>
    public static List<List<(Vector3d, Vector3d, Vector3d)>> ReadStepFaceted(string text)
    {
        var ent = new Dictionary<int, string>();
        int data = text.IndexOf("DATA;", StringComparison.Ordinal);
        foreach (var raw in text[(data + 5)..].Split(';'))
        {
            string st = raw.Trim();
            if (!st.StartsWith('#')) continue;
            int eq = st.IndexOf('=');
            ent[int.Parse(st[1..eq], Inv)] = st[(eq + 1)..].Trim();
        }

        List<int> Refs(string s) => System.Text.RegularExpressions.Regex.Matches(s, @"#(\d+)")
            .Select(m => int.Parse(m.Groups[1].Value, Inv)).ToList();
        string Get(int id) => ent.TryGetValue(id, out var v) ? v : throw new InvalidDataException($"ссылка #{id} не разрешается");

        Vector3d Point(int id)
        {
            string s = Get(id);
            if (!s.StartsWith("CARTESIAN_POINT")) throw new InvalidDataException($"#{id} не точка");
            int open = s.LastIndexOf('(');
            var nums = s[(open + 1)..s.IndexOf(')', open)].Split(',');
            return new Vector3d(double.Parse(nums[0], Inv), double.Parse(nums[1], Inv), double.Parse(nums[2], Inv));
        }

        var result = new List<List<(Vector3d, Vector3d, Vector3d)>>();
        foreach (var (id, s) in ent)
        {
            if (!s.StartsWith("CLOSED_SHELL")) continue;
            var list = new List<(Vector3d, Vector3d, Vector3d)>();
            foreach (int face in Refs(s))
                foreach (int bound in Refs(Get(face)))
                {
                    string b = Get(bound);
                    if (!b.StartsWith("FACE_OUTER_BOUND") && !b.StartsWith("FACE_BOUND")) continue;
                    int loop = Refs(b)[0];
                    var pts = Refs(Get(loop)).Select(Point).ToList();
                    // Веер от первой вершины каждого контура: объём и парность рёбер складываются
                    // и для граней с отверстиями (внутренний контур обходится навстречу).
                    for (int i = 1; i + 1 < pts.Count; i++) list.Add((pts[0], pts[i], pts[i + 1]));
                }
            result.Add(list);
        }
        return result;
    }

    /// <summary>
    /// Проверка гранёного STEP: рёбра считаются по КОНТУРАМ граней (веер от первой
    /// вершины путает счёт на невыпуклых гранях и гранях с отверстиями), объём — по
    /// вееру каждого контура (он складывается верно и для граней с отверстиями).
    /// </summary>
    public static MeshCheck CheckStep(string text)
    {
        var ent = new Dictionary<int, string>();
        int data = text.IndexOf("DATA;", StringComparison.Ordinal);
        foreach (var raw in text[(data + 5)..].Split(';'))
        {
            string st = raw.Trim();
            if (!st.StartsWith('#')) continue;
            int eq = st.IndexOf('=');
            ent[int.Parse(st[1..eq], Inv)] = st[(eq + 1)..].Trim();
        }

        var edges = new Dictionary<(int, int), int>();
        int faces = 0;
        foreach (var (_, s) in ent)
        {
            if (!s.StartsWith("POLY_LOOP")) continue;
            var ids = System.Text.RegularExpressions.Regex.Matches(s, @"#(\d+)").Select(m => int.Parse(m.Groups[1].Value, Inv)).ToList();
            foreach (int id in ids) if (!ent.ContainsKey(id)) throw new InvalidDataException($"ссылка #{id} не разрешается");
            for (int i = 0; i < ids.Count; i++)
            {
                int a = ids[i], b = ids[(i + 1) % ids.Count];
                var k = a < b ? (a, b) : (b, a);
                edges[k] = edges.GetValueOrDefault(k) + 1;
            }
        }
        foreach (var (_, s) in ent) if (s.StartsWith("FACE_SURFACE")) faces++;

        double vol = 0;
        foreach (var shell in ReadStepFaceted(text))
            foreach (var (a, b, c) in shell) vol += Vector3d.Dot(a, Vector3d.Cross(b, c)) / 6;

        return new MeshCheck(faces, edges.Values.Count(n => n != 2), vol,
                             edges.Values.Count(n => n % 2 == 1), edges.Values.Count(n => n > 2 && n % 2 == 0));
    }

    // ================================================================ проверка
    /// <summary>
    /// Сводка сетки файла. Holes — рёбра с нечётным числом треугольников (дыры: такой
    /// файл не примет почти никто). Touching — с чётным больше двух: тело касается
    /// само себя, обычно там, где две его части ближе точности float. OpenEdges — всё,
    /// что не ровно два.
    /// </summary>
    public readonly record struct MeshCheck(int Triangles, int OpenEdges, double Volume, int Holes, int Touching);

    public static MeshCheck Check(IReadOnlyList<(Vector3d A, Vector3d B, Vector3d C)> tris)
    {
        var ids = new Dictionary<(double, double, double), int>();
        int Id(Vector3d p) { var k = (p.X, p.Y, p.Z); if (!ids.TryGetValue(k, out int at)) ids[k] = at = ids.Count; return at; }

        var count = new Dictionary<(int, int), int>();
        double vol = 0;
        foreach (var (a, b, c) in tris)
        {
            vol += Vector3d.Dot(a, Vector3d.Cross(b, c)) / 6;
            int ia = Id(a), ib = Id(b), ic = Id(c);
            foreach (var (u, v) in new[] { (ia, ib), (ib, ic), (ic, ia) })
            {
                var k = u < v ? (u, v) : (v, u);
                count[k] = count.GetValueOrDefault(k) + 1;
            }
        }
        return new MeshCheck(tris.Count, count.Values.Count(n => n != 2), vol,
                             count.Values.Count(n => n % 2 == 1), count.Values.Count(n => n > 2 && n % 2 == 0));
    }

    /// <summary>Прочитать двоичный STL обратно (для проверки экспорта).</summary>
    public static List<(Vector3d, Vector3d, Vector3d)> ReadStlBinary(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        r.ReadBytes(80);
        uint n = r.ReadUInt32();
        var list = new List<(Vector3d, Vector3d, Vector3d)>((int)n);
        for (uint i = 0; i < n; i++)
        {
            R(); var a = R(); var b = R(); var c = R();
            r.ReadUInt16();
            list.Add((a, b, c));
        }
        return list;
        Vector3d R() => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }

    /// <summary>Прочитать OBJ обратно (только v и треугольные f).</summary>
    public static List<(Vector3d, Vector3d, Vector3d)> ReadObj(TextReader reader)
    {
        var v = new List<Vector3d>();
        var list = new List<(Vector3d, Vector3d, Vector3d)>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            if (p[0] == "v") v.Add(new Vector3d(double.Parse(p[1], Inv), double.Parse(p[2], Inv), double.Parse(p[3], Inv)));
            else if (p[0] == "f")
                list.Add((v[int.Parse(p[1].Split('/')[0]) - 1], v[int.Parse(p[2].Split('/')[0]) - 1], v[int.Parse(p[3].Split('/')[0]) - 1]));
        }
        return list;
    }
}
