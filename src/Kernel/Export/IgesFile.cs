using System.Globalization;
using System.Text;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;

namespace Parametric3D.Kernel.Export;

/// <summary>
/// ФАЙЛ IGES: устройство и запись.
///
/// Зачем он нужен, когда есть STEP. IGES старше и проще, и до сих пор остаётся общим языком со
/// станочными системами, раскройными и старыми чертёжными пакетами: там, где STEP не читают,
/// читают IGES. Для обмена геометрией этого часто достаточно.
///
/// Устройство файла непривычное и жёсткое: пять разделов, строки ровно по 80 знаков, в 73-м
/// знаке буква раздела, в 74–80 — номер строки. Данные каждой сущности разнесены по ДВУМ
/// разделам: в разделе описаний (D) лежат две строки с видом сущности и ссылкой на её данные, в
/// разделе данных (P) — сами числа. Номера строк должны сойтись, иначе файл не примут.
/// </summary>
public sealed class IgesFile
{
    /// <summary>Виды сущностей, которые мы пишем и читаем.</summary>
    public const int TypeLine = 110;
    public const int TypeArc = 100;
    public const int TypeSpline = 126;
    public const int TypePlane = 108;
    public const int TypeSurfaceOfRevolution = 120;
    public const int TypeTabulatedCylinder = 122;
    public const int TypeSplineSurface = 128;
    public const int TypeTrimmedSurface = 144;
    public const int TypeCurveOnSurface = 142;
    public const int TypeComposite = 102;

    private readonly List<(int Type, List<double> Data, string Label)> _entities = new();
    /// <summary>Указатель матрицы преобразования (124) в разделе описаний — у сущности с этим номером.</summary>
    private readonly Dictionary<int, int> _xform = new();
    public const int TypeTransform = 124;

    /// <summary>
    /// Добавить сущность: вид, её числа и имя. Имя кладётся в раздел описаний (отведённые под
    /// него восемь знаков) — так подписи граней переживают обмен, а не теряются.
    /// </summary>
    public int Add(int type, IEnumerable<double> data, string label = "")
    {
        _entities.Add((type, data.ToList(), label));
        // K10: указатель на сущность по IGES — номер ПЕРВОЙ строки её описания в разделе D
        // (нечётный: 1, 3, 5, …), а не порядковый номер. Прежде писался порядковый номер, и
        // любая другая система читала ссылки файла не на те сущности.
        return 2 * _entities.Count - 1;
    }

    /// <summary>Подписать уже добавленную сущность.</summary>
    public void Label(int entity, string label)
    {
        int i = (entity + 1) / 2;
        if (i >= 1 && i <= _entities.Count)
            _entities[i - 1] = (_entities[i - 1].Type, _entities[i - 1].Data, label);
    }

    public int Count => _entities.Count;

    /// <summary>m.x2: откат — убрать сущности, добавленные после отметки Count (попытка записи грани не удалась: без этого
    /// в файле оставались ничьи поверхности, и OpenCascade читал их лишними гранями).</summary>
    public void Truncate(int count)
    {
        if (count >= _entities.Count) return;
        _entities.RemoveRange(count, _entities.Count - count);
        foreach (var k in _xform.Keys.Where(k => k > 2 * count - 1).ToList()) _xform.Remove(k);
    }

    /// <summary>Отрезок: два конца.</summary>
    public int AddLine(Vector3d a, Vector3d b) =>
        Add(TypeLine, new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z });

    /// <summary>
    /// Дуга. В IGES она лежит в плоскости, параллельной XY, и задаётся смещением по Z, центром и
    /// концами; наклонные дуги выражаются через матрицу преобразования — её мы пока не пишем, и
    /// дуга в другой плоскости честно отвергается, а не искажается молча.
    /// </summary>
    public int AddArcXY(Vector3d centre, Vector3d start, Vector3d end) =>
        Add(TypeArc, new[] { centre.Z, centre.X, centre.Y, start.X, start.Y, end.X, end.Y });

    /// <summary>
    /// Сплайн (рациональная В-сплайновая кривая). Порядок чисел задан описанием формата: степень
    /// и число точек, признаки, узлы, веса, точки, пределы параметра.
    /// </summary>
    public int AddSpline(SplineCurve3 c)
    {
        int n = c.ControlPoints.Count - 1;
        int k = c.Degree;

        var data = new List<double> { n, k, 0, 0, 0, 0 };     // вид: не плоская, не замкнутая, рациональная, не периодическая
        foreach (double t in c.Knots) data.Add(t);
        foreach (double w in c.Weights) data.Add(w);
        foreach (var p in c.ControlPoints) { data.Add(p.X); data.Add(p.Y); data.Add(p.Z); }
        data.Add(c.Knots[k]);
        data.Add(c.Knots[c.Knots.Count - k - 1]);
        data.Add(0); data.Add(0); data.Add(1);                 // нормаль плоскости, если кривая плоская

        return Add(TypeSpline, data);
    }

    /// <summary>
    /// ПЛОСКОСТЬ (108): уравнение a·x + b·y + c·z = d плюс точка на ней и размер для показа.
    /// Неограниченная плоскость сама по себе телом не является — её обрезают контуром, и для
    /// этого есть отдельная сущность.
    /// </summary>
    public int AddPlane(Vector3d normal, Vector3d at, double size = 100)
    {
        Vector3d n = normal.LengthSquared < 1e-18 ? Vector3d.UnitZ : normal.Normalized();
        double d = Vector3d.Dot(n, at);
        return Add(TypePlane, new[] { n.X, n.Y, n.Z, d, 0, at.X, at.Y, at.Z, size });
    }

    /// <summary>
    /// ЦИЛИНДР И ЛЮБАЯ ПОВЕРХНОСТЬ ВЫДАВЛИВАНИЯ (122): образующая кривая и вектор переноса.
    /// Цилиндр записывается выдавливанием окружности — именно так его и хранит наше ядро.
    /// </summary>
    public int AddExtruded(int baseCurve, Vector3d dir) =>
        Add(TypeTabulatedCylinder, new double[] { baseCurve, dir.X, dir.Y, dir.Z });

    /// <summary>
    /// ПОВЕРХНОСТЬ ВРАЩЕНИЯ (120): ось, образующая и углы начала и конца. Так записываются шар,
    /// тор и конус.
    /// </summary>
    public int AddRevolved(int axisLine, int generatrix, double a0, double a1) =>
        Add(TypeSurfaceOfRevolution, new double[] { axisLine, generatrix, a0, a1 });

    /// <summary>
    /// СПЛАЙНОВАЯ ПОВЕРХНОСТЬ (128): степени, число точек по каждому направлению, узлы, веса и
    /// сетка опорных точек.
    /// </summary>
    public int AddSplineSurface(Surfaces.NurbsSurface s)
    {
        int k1 = s.CountU - 1, k2 = s.CountV - 1;
        int m1 = s.DegreeU, m2 = s.DegreeV;

        var data = new List<double> { k1, k2, m1, m2, 0, 0, 0, 0, 0 };

        foreach (double t in s.KnotsU) data.Add(t);
        foreach (double t in s.KnotsV) data.Add(t);

        for (int j = 0; j <= k2; j++)
            for (int i = 0; i <= k1; i++) data.Add(s.Weight(i, j));

        for (int j = 0; j <= k2; j++)
            for (int i = 0; i <= k1; i++)
            {
                var p = s.ControlPoint(i, j);
                data.Add(p.X); data.Add(p.Y); data.Add(p.Z);
            }

        data.Add(s.KnotsU[m1]); data.Add(s.KnotsU[s.KnotsU.Count - m1 - 1]);
        data.Add(s.KnotsV[m2]); data.Add(s.KnotsV[s.KnotsV.Count - m2 - 1]);

        return Add(TypeSplineSurface, data);
    }

    /// <summary>
    /// СОСТАВНОЙ КОНТУР (102): список кривых, идущих подряд. Без него у контура грани сохранялась
    /// бы только первая кривая — то есть от четырёхугольной грани оставался бы один отрезок.
    /// </summary>
    public int AddComposite(IReadOnlyList<int> curves)
    {
        var data = new List<double> { curves.Count };
        foreach (int c in curves) data.Add(c);
        return Add(TypeComposite, data);
    }

    /// <summary>
    /// ОБРЕЗАННАЯ ПОВЕРХНОСТЬ (144): сама поверхность, её внешний контур и отверстия. Только она
    /// и делает поверхность гранью тела — без обрезки поверхность бесконечна.
    /// </summary>
    public int AddTrimmed(int surface, int outerLoop, IReadOnlyList<int>? holes = null)
    {
        // K10: границы обрезанной поверхности по IGES — сущности 142 «кривая на поверхности»
        // (с кривой в пространстве модели), а не сами кривые; N1 = 1, когда внешний контур задан.
        int Cos(int curve) => curve == 0 ? 0 : Add(TypeCurveOnSurface, new double[] { 1, surface, 0, curve, 2 });
        var data = new List<double> { surface, outerLoop == 0 ? 0 : 1, holes?.Count ?? 0, Cos(outerLoop) };
        if (holes != null) foreach (int h in holes) data.Add(Cos(h));
        return Add(TypeTrimmedSurface, data);
    }

    /// <summary>
    /// m.x2: ОБРЕЗАННАЯ ПОВЕРХНОСТЬ С КРИВЫМИ В ПАРАМЕТРАХ: у каждой границы — и кривая в пространстве модели, и её образ в
    /// параметрах поверхности (142: BPTR — параметры, CPTR — модель, PREF = 3 — равноценны), как пишет OpenCascade. Без
    /// параметров читатель строил их проекцией и на периодических поверхностях (шов) ошибался.
    /// </summary>
    public int AddTrimmedParam(int surface, (int Model, int Param) outer, IReadOnlyList<(int Model, int Param)> holes)
    {
        int Cos((int Model, int Param) c) => c.Model == 0 ? 0 : Add(TypeCurveOnSurface, new double[] { 0, surface, c.Param, c.Model, 3 });
        var data = new List<double> { surface, outer.Model == 0 ? 0 : 1, holes.Count, Cos(outer) };
        foreach (var h in holes) data.Add(Cos(h));
        return Add(TypeTrimmedSurface, data);
    }

    /// <summary>
    /// Кривая в параметрах (u, v) по точкам: на прямой (изолиния — окружность пояса, шов) — отрезок (110, z = 0); иначе —
    /// кубический сплайн, проходящий через точки (126; гладкий: у ломаной в каждой вершине C0, и читатель делил ребро по ним).
    /// </summary>
    public int AddParamPolyline(IReadOnlyList<Vector2d> uv, bool cubic = true)
    {
        var a = uv[0]; var b = uv[^1]; var ab = b - a; double L = ab.Length;
        bool straight = L > 1e-12 && uv.All(q => Math.Abs((q.X - a.X) * ab.Y - (q.Y - a.Y) * ab.X) / L < 1e-9);
        if (straight) return Add(TypeLine, new[] { a.X, a.Y, 0, b.X, b.Y, 0 });
        if (uv.Count >= 4 && !PolyOld && cubic) { int sp = ParamCubic(uv); if (sp != 0) return sp; }
        return ParamPolyline1(uv);
    }
    static readonly bool PolyOld = Environment.GetEnvironmentVariable("P3D_IGESPOLY") == "old";

    int ParamCubic(IReadOnlyList<Vector2d> uv)
    {
        int m = uv.Count, deg = 3;
        var acc = new double[m]; for (int i = 1; i < m; i++) acc[i] = acc[i - 1] + (uv[i] - uv[i - 1]).Length;
        if (!(acc[^1] > 0)) return 0;
        var t = acc.Select(x => x / acc[^1]).ToArray();
        var knots = new double[m + deg + 1];
        for (int i = 0; i <= deg; i++) { knots[i] = 0; knots[^(i + 1)] = 1; }
        for (int j = 1; j < m - deg; j++) knots[j + deg] = t.Skip(j).Take(deg).Average();
        var A = new double[m, m];
        for (int i = 0; i < m; i++) { var nb = Surfaces.BandNurbs.Basis(knots, deg, t[i], m); for (int j = 0; j < m; j++) A[i, j] = nb[j]; }
        var rhs = uv.Select(q => new Vector4d(q.X, q.Y, 0, 0)).ToArray();
        var sol = Surfaces.BandNurbs.Solve(A, rhs, m);
        if (sol == null) return 0;
        var data = new List<double> { m - 1, deg, 1, 0, 1, 0 };
        data.AddRange(knots);
        for (int i = 0; i < m; i++) data.Add(1);
        foreach (var q in sol) { data.Add(q.X); data.Add(q.Y); data.Add(0); }
        data.Add(0); data.Add(1); data.Add(0); data.Add(0); data.Add(1);
        return Add(TypeSpline, data);
    }

    int ParamPolyline1(IReadOnlyList<Vector2d> uv)
    {
        int n = uv.Count - 1;
        var data = new List<double> { n, 1, 1, 0, 1, 0 };     // плоская, не замкнутая, полиномиальная, не периодическая
        // узлы: 0, 0, равномерно по длине в параметрах, 1, 1
        var acc = new double[uv.Count]; for (int i = 1; i < uv.Count; i++) acc[i] = acc[i - 1] + (uv[i] - uv[i - 1]).Length;
        double tot = acc[^1] > 0 ? acc[^1] : 1;
        data.Add(0); for (int i = 0; i <= n; i++) data.Add(acc[i] / tot); data.Add(1);
        for (int i = 0; i <= n; i++) data.Add(1);
        foreach (var q in uv) { data.Add(q.X); data.Add(q.Y); data.Add(0); }
        data.Add(0); data.Add(1); data.Add(0); data.Add(0); data.Add(1);
        return Add(TypeSpline, data);
    }

    /// <summary>
    /// m.x2: ДУГА В ЛЮБОЙ ПЛОСКОСТИ — 100 в своей системе плюс матрица 124 (столбцы U, V, W, перенос — центр), обход против
    /// часовой в этой системе (дуга по часовой — система с обращёнными V, W). Как пишет OpenCascade: одна кривая на дугу (у
    /// рационального сплайна из четвертей стыки C0 — читатель делит ребро, и кривые в параметрах перестают совпадать с ним).
    /// </summary>
    public int AddArc(Vector3d centre, Vector3d u, Vector3d v, double r, double a0, double a1)
    {
        var W = Vector3d.Cross(u, v).Normalized(); var U = u.Normalized(); var V = Vector3d.Cross(W, U);
        if (a1 < a0) { V = -V; W = -W; a0 = -a0; a1 = -a1; }
        int m = Add(TypeTransform, new[] { U.X, V.X, W.X, centre.X, U.Y, V.Y, W.Y, centre.Y, U.Z, V.Z, W.Z, centre.Z });
        int id = Add(TypeArc, new[] { 0, 0, 0, r * Math.Cos(a0), r * Math.Sin(a0), r * Math.Cos(a1), r * Math.Sin(a1) });
        _xform[id] = m;
        return id;
    }

    /// <summary>Записать файл целиком.</summary>
    public string Write(string author = "Parametric3D")
    {
        var sb = new StringBuilder();

        void Put(string text, char section, ref int counter)
        {
            sb.Append(text.PadRight(72).Substring(0, 72));
            sb.Append(section);
            sb.Append(counter.ToString().PadLeft(7));
            sb.Append('\n');
            counter++;
        }

        // Раздел приветствия.
        int s = 1;
        Put("Parametric3D exact geometry (IGES 5.3 DE pointers)", 'S', ref s);

        // Общий раздел: разделители, имя, единицы, точность.
        string global = string.Join(",", new[]
        {
            "1H,", "1H;", "4HPART", "4HPART", $"{Quote(author)}", $"{Quote("Parametric3D")}",
            "32", "38", "6", "308", "15", "4HPART", "1.0", "2", "2HMM",
            "1", "0.08", $"{Quote(DateTime.UtcNow.ToString("yyyyMMdd.HHmmss"))}",
            "1E-07", "1000.0", $"{Quote(author)}", $"{Quote("Parametric3D")}", "11", "0"
        }) + ";";

        int g = 1;
        foreach (var chunk in Chunks(global, 72)) Put(chunk, 'G', ref g);

        // Разделы описаний и данных строятся вместе: номера строк должны сойтись.
        var dir = new StringBuilder();
        var par = new StringBuilder();
        int d = 1, p = 1;

        foreach (var (type, data, label) in _entities)
        {
            int parStart = p;
            string body = type.ToString(CultureInfo.InvariantCulture) + "," +
                          string.Join(",", data.Select(N)) + ";";

            var lines = Chunks(body, 64).ToList();
            foreach (var chunk in lines)
            {
                par.Append(chunk.PadRight(64).Substring(0, 64));
                par.Append((d).ToString().PadLeft(8));
                par.Append('P');
                par.Append(p.ToString().PadLeft(7));
                par.Append('\n');
                p++;
            }

            // Две строки описания на сущность.
            // Имя занимает восемь знаков; длинное обрезается, а не переносится — иначе строка
            // перестанет быть длиной в 80 знаков, и файл не примут.
            string shortLabel = label.Length > 8 ? label.Substring(0, 8) : label;

            int xf = _xform.GetValueOrDefault(d);
            string d1 = $"{type,8}{parStart,8}{0,8}{0,8}{0,8}{0,8}{xf,8}{0,8}{"00000000",8}";
            string d2 = $"{type,8}{0,8}{0,8}{lines.Count,8}{0,8}{"",8}{"",8}{shortLabel,8}{d + 1,8}";
            dir.Append(d1.PadRight(72).Substring(0, 72)).Append('D').Append(d.ToString().PadLeft(7)).Append('\n');
            d++;
            dir.Append(d2.PadRight(72).Substring(0, 72)).Append('D').Append(d.ToString().PadLeft(7)).Append('\n');
            d++;
        }

        sb.Append(dir);
        sb.Append(par);

        // Завершающий раздел: сколько строк в каждом разделе.
        string term = $"S{(s - 1),7}G{(g - 1),7}D{(d - 1),7}P{(p - 1),7}";
        sb.Append(term.PadRight(72).Substring(0, 72)).Append('T').Append("      1").Append('\n');

        return sb.ToString();
    }

    private static string Quote(string text) => $"{text.Length}H{text}";

    // m.x2: 15 знаков после запятой (было 9) — как в STEP
    private static string N(double v) => v.ToString(Environment.GetEnvironmentVariable("P3D_IGESDIGITS") == "9" ? "0.#########" : "0.###############", CultureInfo.InvariantCulture);

    private static IEnumerable<string> Chunks(string text, int width)
    {
        // m.x2: строка режется ПОСЛЕ разделителя (',' или ';'), число на две строки не делится: OpenCascade склеивал
        // разрезанное число неверно («0.7071» + «06781»), и грани читались искажёнными. P3D_IGESCHUNK=old — прежнее.
        if (ChunkOld) { for (int i = 0; i < text.Length; i += width) yield return text.Substring(i, Math.Min(width, text.Length - i)); yield break; }
        int at = 0;
        while (at < text.Length)
        {
            int len = Math.Min(width, text.Length - at);
            if (at + len < text.Length)
            {
                int cut = text.LastIndexOfAny(new[] { ',', ';' }, at + len - 1, len);
                if (cut >= at) len = cut - at + 1;
            }
            yield return text.Substring(at, len);
            at += len;
        }
    }
    static readonly bool ChunkOld = Environment.GetEnvironmentVariable("P3D_IGESCHUNK") == "old";
}
