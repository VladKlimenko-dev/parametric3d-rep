using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Export;

/// <summary>
/// РАЗБОР STEP НА СУЩНОСТИ И ТОЧНУЮ ГЕОМЕТРИЮ.
///
/// Гранёный STEP несёт только треугольники: цилиндр в нём возвращается многогранником. Точный
/// STEP хранит сами поверхности и кривые, и все они у нас в ядре уже есть — плоскость,
/// выдавливание по дуге (цилиндр), вращение, сплайн. Поэтому чтение сводится к разбору файла
/// и сопоставлению.
///
/// Здесь — нижний слой: строки файла превращаются в записи «номер → тип и доводы», а ссылки
/// разрешаются по требованию. Сборка тела идёт отдельно.
/// </summary>
public sealed class StepParser
{
    public readonly Dictionary<int, (string Type, List<string> Args)> Entities = new();

    /// <summary>
    /// ЕДИНИЦА УГЛА В ФАЙЛЕ. Углы в STEP задаются в единице из описания: многие системы пишут
    /// ГРАДУСЫ (у полуугла конуса стояло 45 и 30). Приняв их за радианы, мы строили конус с
    /// огромным раствором: поперечник детали выходил вдвое больше чертёжного.
    /// </summary>
    public double AngleScale = 1.0;

    /// <summary>
    /// K10. ЕДИНИЦА ДЛИНЫ ФАЙЛА → МИЛЛИМЕТРЫ. Прежде любые числа считались миллиметрами: файл в
    /// дюймах читался в 25.4 раза меньше, в метрах — в 1000 раз меньше. Множитель применяется к
    /// точкам и ко всем длинам (радиусы, полуоси, длины векторов).
    /// </summary>
    public double LengthScale = 1.0;

    /// <summary>Веса рациональных B-сплайнов из составных экземпляров (строка списка весов).</summary>
    public readonly Dictionary<int, string> Weights1 = new(), Weights2 = new();

    /// <summary>Части составного экземпляра: тип → доводы.</summary>
    private static Dictionary<string, List<string>>? Complex(string body)
    {
        var res = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string inner = body.Trim();
        if (!inner.StartsWith("(") || !inner.EndsWith(")")) return null;
        inner = inner[1..^1];
        int i = 0;
        while (i < inner.Length)
        {
            while (i < inner.Length && char.IsWhiteSpace(inner[i])) i++;
            int start = i;
            while (i < inner.Length && (char.IsLetterOrDigit(inner[i]) || inner[i] == '_')) i++;
            string type = inner[start..i].ToUpperInvariant();
            while (i < inner.Length && char.IsWhiteSpace(inner[i])) i++;
            if (type.Length == 0 || i >= inner.Length || inner[i] != '(') return res.Count > 0 ? res : null;
            int depth = 0, argStart = i + 1; bool q = false;
            for (; i < inner.Length; i++)
            {
                char ch = inner[i];
                if (ch == '\'') q = !q;
                if (q) continue;
                if (ch == '(') depth++;
                else if (ch == ')') { depth--; if (depth == 0) break; }
            }
            res[type] = SplitArgs(inner[argStart..i]);
            i++;
        }
        return res;
    }
    public string? LengthUnitNote;

    /// <summary>Операторы файла: «;» внутри строк в апострофах не разделяет.</summary>
    private static IEnumerable<string> Statements(string s)
    {
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char ch in s)
        {
            if (ch == '\'') quoted = !quoted;
            if (ch == ';' && !quoted) { yield return sb.ToString(); sb.Clear(); continue; }
            sb.Append(ch);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static double SiPrefix(string prefix) => prefix.Trim('.', ' ').ToUpperInvariant() switch
    {
        "MILLI" => 1, "CENTI" => 10, "DECI" => 100, "KILO" => 1e6, "MICRO" => 1e-3, "NANO" => 1e-6, "" or "$" => 1000,
        _ => double.NaN
    };

    /// <summary>
    /// Множитель единицы длины: SI_UNIT(приставка, .METRE.) или CONVERSION_BASED_UNIT('имя', мера),
    /// где мера — LENGTH_MEASURE_WITH_UNIT(LENGTH_MEASURE(x), ссылка на базовую единицу).
    /// Неизвестная единица — 1 и пометка: молча подставлять миллиметры нельзя.
    /// </summary>
    private static double LengthUnitScale(StepParser p, string text, out string? note)
    {
        note = null;
        var units = new Dictionary<int, string>();
        foreach (string raw in Statements(text))
        {
            string st = raw.Replace("\r", " ").Replace("\n", " ").Trim();
            if (st.Length < 2 || st[0] != '#') continue;
            int eq = st.IndexOf('=');
            if (eq < 0 || !int.TryParse(st[1..eq].Trim(), out int id)) continue;
            units[id] = st[(eq + 1)..].Replace(" ", "");
        }
        double Factor(int id, int depth)
        {
            if (depth > 8 || !units.TryGetValue(id, out var body)) return double.NaN;
            var si = System.Text.RegularExpressions.Regex.Match(body, @"SI_UNIT\(([^,]*),\.METRE\.\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (si.Success && body.Contains("LENGTH_UNIT", StringComparison.OrdinalIgnoreCase)) return SiPrefix(si.Groups[1].Value);
            var cb = System.Text.RegularExpressions.Regex.Match(body, @"CONVERSION_BASED_UNIT\('[^']*',#(\d+)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (cb.Success && units.TryGetValue(int.Parse(cb.Groups[1].Value), out var measure))
            {
                var mm = System.Text.RegularExpressions.Regex.Match(measure, @"MEASURE_WITH_UNIT\((?:LENGTH_MEASURE|POSITIVE_LENGTH_MEASURE)\(([-+0-9.EeDd]+)\),#(\d+)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (mm.Success) return Num(mm.Groups[1].Value) * Factor(int.Parse(mm.Groups[2].Value), depth + 1);
            }
            return double.NaN;
        }
        // Единица, назначенная контексту представления (GLOBAL_UNIT_ASSIGNED_CONTEXT), главнее:
        // дюймовый файл объявляет и миллиметр — как базу пересчёта дюйма.
        var assigned = new HashSet<int>();
        foreach (var body in units.Values)
        {
            var g = System.Text.RegularExpressions.Regex.Match(body, @"GLOBAL_UNIT_ASSIGNED_CONTEXT\(\(([^)]*)\)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (g.Success)
                foreach (System.Text.RegularExpressions.Match r in System.Text.RegularExpressions.Regex.Matches(g.Groups[1].Value, @"#(\d+)"))
                    assigned.Add(int.Parse(r.Groups[1].Value));
        }
        var candidates = units.Where(kv => kv.Value.Contains("LENGTH_UNIT", StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Any(kv => assigned.Contains(kv.Key))) candidates = candidates.Where(kv => assigned.Contains(kv.Key)).ToList();
        var found = candidates.Select(kv => Factor(kv.Key, 0)).Where(x => !double.IsNaN(x)).Distinct().ToList();
        if (found.Count == 0)
        {
            if (units.Values.Any(b => b.Contains("LENGTH_UNIT", StringComparison.OrdinalIgnoreCase)))
                note = "STEP: the length unit is not recognised; millimetres assumed.";
            return 1.0;
        }
        if (found.Count > 1) note = $"STEP: several length units ({string.Join(", ", found)} mm); the first is used.";
        else if (found[0] != 1) note = $"STEP: lengths are scaled by {found[0]} to millimetres.";
        return found[0];
    }

    /// <summary>Разбор текста файла. Строки склеиваются до точки с запятой: перенос строк в STEP произволен.</summary>
    public static StepParser Parse(string text)
    {
        var p = new StepParser();
        int data = text.IndexOf("DATA;", StringComparison.Ordinal);
        if (data < 0) data = 0;

        foreach (string raw in Statements(text[data..]))
        {
            string s = raw.Trim().Replace("\r", " ").Replace("\n", " ");
            if (s.Length < 3 || s[0] != '#') continue;
            int eq = s.IndexOf('=');
            if (eq < 0) continue;
            if (!int.TryParse(s[1..eq].Trim(), out int id)) continue;

            string body = s[(eq + 1)..].Trim();
            // K10. СОСТАВНОЙ ЭКЗЕМПЛЯР «(ТИП(...)ТИП(...)...)». Так другие системы пишут
            // рациональные B-сплайны (вес — в части RATIONAL_B_SPLINE_*): прежде такие сущности
            // не распознавались вовсе, и грань или ребро из чужого файла пропадали.
            if (body.StartsWith("(") && Complex(body) is { } parts)
            {
                if (parts.TryGetValue("B_SPLINE_SURFACE_WITH_KNOTS", out var sk) && parts.TryGetValue("B_SPLINE_SURFACE", out var sb2))
                {
                    // Простая форма: имя, du, dv, сетка, вид, замкн. u, v, самопересеч., кратн. u, v, узлы u, v, вид узлов.
                    var args = new List<string> { "''" };
                    args.AddRange(sb2); args.AddRange(sk);
                    if (parts.TryGetValue("RATIONAL_B_SPLINE_SURFACE", out var rw)) p.Weights2[id] = rw[0];
                    p.Entities[id] = ("B_SPLINE_SURFACE_WITH_KNOTS", args);
                    continue;
                }
                if (parts.TryGetValue("B_SPLINE_CURVE_WITH_KNOTS", out var ck) && parts.TryGetValue("B_SPLINE_CURVE", out var cb2))
                {
                    var args = new List<string> { "''" };
                    args.AddRange(cb2); args.AddRange(ck);
                    if (parts.TryGetValue("RATIONAL_B_SPLINE_CURVE", out var rw)) p.Weights1[id] = rw[0];
                    p.Entities[id] = ("B_SPLINE_CURVE_WITH_KNOTS", args);
                    continue;
                }
            }
            int open = body.IndexOf('(');
            if (open < 0) continue;
            string type = body[..open].Trim().ToUpperInvariant();
            string inside = body[(open + 1)..^(body.EndsWith(")") ? 1 : 0)];
            p.Entities[id] = (type, SplitArgs(inside));
        }

        p.LengthScale = LengthUnitScale(p, text, out p.LengthUnitNote);

        // Единица угла: градус объявляется через CONVERSION_BASED_UNIT с именем DEGREE.
        bool degrees = text.Contains("'DEGREE'", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("PLANE_ANGLE_UNIT()) CONVERSION_BASED_UNIT", StringComparison.OrdinalIgnoreCase);
        if (degrees) p.AngleScale = Math.PI / 180;
        return p;
    }

    /// <summary>Разделение доводов по запятым верхнего уровня: вложенные скобки и строки не трогаются.</summary>
    public static List<string> SplitArgs(string s)
    {
        var res = new List<string>();
        int depth = 0;
        bool str = false;
        var cur = new System.Text.StringBuilder();
        foreach (char c in s)
        {
            if (str) { cur.Append(c); if (c == '\'') str = false; continue; }
            switch (c)
            {
                case '\'': str = true; cur.Append(c); break;
                case '(': depth++; cur.Append(c); break;
                case ')': depth--; cur.Append(c); break;
                case ',' when depth == 0: res.Add(cur.ToString().Trim()); cur.Clear(); break;
                default: cur.Append(c); break;
            }
        }
        if (cur.Length > 0) res.Add(cur.ToString().Trim());
        return res;
    }

    private static int Ref(string s) => int.TryParse(s.TrimStart('#'), out int id) ? id : -1;

    private static double Num(string s) =>
        double.Parse(s.Replace("D", "E").TrimEnd('.') is { Length: > 0 } t ? t : "0",
                     System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Точка по ссылке.</summary>
    public Vector3d Point(string arg)
    {
        var (type, a) = Entities[Ref(arg)];
        if (type != "CARTESIAN_POINT") throw new InvalidDataException($"{arg}: ожидалась точка, получено {type}");
        var xs = SplitArgs(a[1].Trim('(', ')'));
        return new Vector3d(Num(xs[0]), Num(xs[1]), xs.Count > 2 ? Num(xs[2]) : 0) * LengthScale;
    }

    /// <summary>Направление по ссылке.</summary>
    public Vector3d Direction(string arg)
    {
        var (type, a) = Entities[Ref(arg)];
        if (type != "DIRECTION") throw new InvalidDataException($"{arg}: ожидалось направление, получено {type}");
        var xs = SplitArgs(a[1].Trim('(', ')'));
        return new Vector3d(Num(xs[0]), Num(xs[1]), xs.Count > 2 ? Num(xs[2]) : 0);
    }

    /// <summary>Система координат: начало, ось Z и ось X (вторая может отсутствовать).</summary>
    public Frame3 Placement(string arg)
    {
        var (type, a) = Entities[Ref(arg)];
        if (type != "AXIS2_PLACEMENT_3D") throw new InvalidDataException($"{arg}: ожидалась система координат, получено {type}");
        Vector3d o = Point(a[1]);
        Vector3d w = a.Count > 2 && a[2] != "$" ? Direction(a[2]).Normalized() : Vector3d.UnitZ;
        Vector3d u = a.Count > 3 && a[3] != "$" ? Direction(a[3]).Normalized() : Any(w);
        u = (u - w * Vector3d.Dot(u, w)).Normalized();
        return new Frame3(o, u, Vector3d.Cross(w, u));
    }

    private static Vector3d Any(Vector3d w) =>
        Math.Abs(w.Z) < 0.9 ? Vector3d.Cross(w, Vector3d.UnitZ).Normalized() : Vector3d.UnitX;

    /// <summary>Поверхность по ссылке: плоскость, цилиндр, конус, сфера, тор.</summary>
    public ISurface3? Surface(string arg)
    {
        int id = Ref(arg);
        if (!Entities.TryGetValue(id, out var e)) return null;
        var (type, a) = e;
        switch (type)
        {
            case "PLANE":
                return new PlaneSurface(Placement(a[1]));

            case "CYLINDRICAL_SURFACE":
            {
                Frame3 f = Placement(a[1]);
                double r = Num(a[2]) * LengthScale;
                // Цилиндр — выдавливание окружности вдоль оси.
                return new ExtrudedSurface(new ArcCurve3(f, r, 0, 2 * Math.PI), f.W);
            }

            case "CONICAL_SURFACE":
            {
                // Конус задан радиусом у начала системы координат и полууглом при вершине;
                // у нас это вращение прямой образующей вокруг оси.
                Frame3 f = Placement(a[1]);
                double r = Num(a[2]) * LengthScale, ang = Num(a[3]) * AngleScale;
                double h = 1.0;                                  // длина образующей задаётся гранью
                Vector3d p0 = f.Origin + f.U * r;
                Vector3d p1 = p0 + f.W * h + f.U * (h * Math.Tan(ang));
                return new RevolvedSurface(new LineCurve3(p0, p1), f, 0, 2 * Math.PI);
            }

            case "SURFACE_OF_LINEAR_EXTRUSION":
            {
                // Протяжка кривой по прямой: у нас это выдавливание.
                var bc = Curve(a[1]);
                if (bc == null) return null;
                var (vt2, va2) = Entities[Ref(a[2])];
                if (vt2 != "VECTOR") return null;
                return new ExtrudedSurface(bc, Direction(va2[1]) * (Num(va2[2]) * LengthScale));
            }

            case "B_SPLINE_SURFACE_WITH_KNOTS":
            case "RATIONAL_B_SPLINE_SURFACE":
            {
                // Доводы: имя, степень по u, степень по v, сетка опорных точек, вид, замкнутость
                // по u и по v, самопересечение, кратности узлов по u и по v, узлы по u и по v.
                int du = (int)Num(a[1]), dv = (int)Num(a[2]);

                // Сетка записана списком строк, каждая — список ссылок на точки. Снимается РОВНО
                // одна пара скобок: Trim снял бы и внутренние, и строка порвалась бы.
                static string Inner(string x)
                {
                    x = x.Trim();
                    if (x.StartsWith("(")) x = x[1..];
                    if (x.EndsWith(")")) x = x[..^1];
                    return x;
                }

                var rows = SplitArgs(Inner(a[3]));
                int nu = rows.Count;
                if (nu < 2) return null;
                var first = SplitArgs(Inner(rows[0]));
                int nv = first.Count;
                if (nv < 2) return null;

                var cp = new Vector3d[nu, nv];
                for (int i = 0; i < nu; i++)
                {
                    var cols = SplitArgs(Inner(rows[i]));
                    if (cols.Count != nv) return null;
                    for (int j = 0; j < nv; j++) cp[i, j] = Point(cols[j]);
                }

                double[] Knots(string mult, string vals)
                {
                    var m = SplitArgs(Inner(mult)).Select(x => (int)Num(x)).ToList();
                    var k = SplitArgs(Inner(vals)).Select(Num).ToList();
                    var res = new List<double>();
                    for (int i = 0; i < k.Count && i < m.Count; i++)
                        for (int r = 0; r < m[i]; r++) res.Add(k[i]);
                    return res.ToArray();
                }

                double[]? ku = a.Count > 9 ? Knots(a[8], a[10]) : null;
                double[]? kv = a.Count > 11 ? Knots(a[9], a[11]) : null;
                if (ku != null && ku.Length != nu + du + 1) ku = null;
                if (kv != null && kv.Length != nv + dv + 1) kv = null;

                // Узлы нормируются к 0…1: наша поверхность работает в этих пределах.
                static double[]? Norm(double[]? k)
                {
                    if (k == null || k.Length == 0) return k;
                    double lo = k[0], hi = k[^1];
                    if (hi - lo <= 0) return null;
                    return k.Select(x => (x - lo) / (hi - lo)).ToArray();
                }

                double[,]? wts = null;
                if (Weights2.TryGetValue(id, out var wtxt))
                {
                    var wr = SplitArgs(Inner(wtxt));
                    if (wr.Count == nu)
                    {
                        wts = new double[nu, nv];
                        for (int i = 0; i < nu; i++)
                        {
                            var wc = SplitArgs(Inner(wr[i]));
                            if (wc.Count != nv) { wts = null; break; }
                            for (int j = 0; j < nv; j++) wts[i, j] = Num(wc[j]);
                        }
                    }
                }
                try { return new NurbsSurface(cp, du, dv, wts, Norm(ku), Norm(kv)); }
                catch { return null; }
            }

            case "SPHERICAL_SURFACE":
            {
                Frame3 f = Placement(a[1]);
                double r = Num(a[2]) * LengthScale;
                // Шар — вращение полукруга вокруг оси.
                var half = new ArcCurve3(new Frame3(f.Origin, f.U, f.W), r, -Math.PI / 2, Math.PI / 2);
                return new RevolvedSurface(half, f, 0, 2 * Math.PI);
            }

            case "TOROIDAL_SURFACE":
            case "DEGENERATE_TOROIDAL_SURFACE":
            {
                Frame3 f = Placement(a[1]);
                double ring = Num(a[2]) * LengthScale, tube = Num(a[3]) * LengthScale;
                var circle = new ArcCurve3(new Frame3(f.Origin + f.U * ring, f.U, f.W), tube, 0, 2 * Math.PI);
                return new RevolvedSurface(circle, f, 0, 2 * Math.PI);
            }
        }
        return null;
    }

    /// <summary>Вершина по ссылке.</summary>
    public Vector3d Vertex(string arg)
    {
        var (type, a) = Entities[Ref(arg)];
        if (type != "VERTEX_POINT") throw new InvalidDataException($"{arg}: ожидалась вершина, получено {type}");
        return Point(a[1]);
    }

    /// <summary>
    /// Грань ADVANCED_FACE: точная поверхность и её контуры, каждый — список кривых по обходу.
    /// Ребро EDGE_CURVE даёт кривую и две вершины; направление берётся из ORIENTED_EDGE.
    /// </summary>
    /// <summary>Концы каждой кривой контура по вершинам файла (в направлении обхода).</summary>
    public readonly Dictionary<ICurve3, (Vector3d Start, Vector3d End)> LastEnds = new(ReferenceEqualityComparer.Instance);

    public (ISurface3 Surface, List<List<ICurve3>> Loops, bool SameSense)? Face(string arg)
    {
        int id = Ref(arg);
        if (!Entities.TryGetValue(id, out var e) || e.Type != "ADVANCED_FACE") return null;

        var surf = Surface(e.Args[2]);
        if (surf == null) return null;
        bool faceSame = e.Args.Count > 3 && e.Args[3].Contains("T");

        var loops = new List<List<ICurve3>>();
        foreach (string boundRef in SplitArgs(e.Args[1].Trim('(', ')')))
        {
            if (!Entities.TryGetValue(Ref(boundRef), out var b)) continue;
            if (b.Type is not ("FACE_OUTER_BOUND" or "FACE_BOUND")) continue;
            if (!Entities.TryGetValue(Ref(b.Args[1]), out var lp) || lp.Type != "EDGE_LOOP") continue;

            var curves = new List<ICurve3>();
            foreach (string oeRef in SplitArgs(lp.Args[1].Trim('(', ')')))
            {
                if (!Entities.TryGetValue(Ref(oeRef), out var oe) || oe.Type != "ORIENTED_EDGE") continue;
                bool same = oe.Args[^1].Contains("T");
                if (!Entities.TryGetValue(Ref(oe.Args[3]), out var ec) || ec.Type != "EDGE_CURVE") continue;

                Vector3d v0 = Vertex(ec.Args[1]), v1 = Vertex(ec.Args[2]);
                bool ecSame = ec.Args[^1].Contains("T");
                ICurve3? c = Curve(ec.Args[3]);
                if (c == null) continue;

                // K08/K10. КРИВАЯ РЕБРА ОТ V0 К V1 С УЧЁТОМ same_sense ребра. Прежде дуга всегда
                // строилась против часовой стрелки от V0 к V1, а признак ребра лишь разворачивал
                // её: при .F. (кривая идёт от V1 к V0) получалась ДОПОЛНИТЕЛЬНАЯ дуга — у отверстия
                // r = 3 кромка выходила вдвое длиннее (37.7 вместо 18.8), отверстия в грани
                // прибавлялись к площади (3 136 вместо 2 287 мм²).
                ICurve3 edgeCurve = c switch
                {
                    LineCurve3 => new LineCurve3(v0, v1),
                    ArcCurve3 arc => ecSame ? ArcBetween(arc, v0, v1) : ArcBetween(arc, v1, v0).Reversed(),
                    EllipseCurve3 el => ecSame ? EllipseBetween(el, v0, v1) : EllipseBetween(el, v1, v0).Reversed(),
                    _ => ecSame ? c : c.Reversed()
                };
                // Обход контура: ORIENTED_EDGE .T. — по ребру, .F. — против.
                curves.Add(same ? edgeCurve : edgeCurve.Reversed());
                // Концы — из ВЕРШИН файла, а не из кривой: у дуги, подогнанной к точкам, конец
                // отходит от вершины на 1e-5 мм, и склейка по допуску 1e-6 разрывала контур.
                LastEnds[curves[^1]] = same ? (v0, v1) : (v1, v0);
            }
            if (curves.Count > 0) loops.Add(curves);
        }

        // Флаг совпадения с нормалью поверхности отдаётся наружу: без него тело собиралось с
        // вывернутыми гранями (у конуса объём выходил 58 864 вместо 82 467).
        return loops.Count == 0 ? null : (surf, loops, faceSame);
    }

    /// <summary>Дуга окружности между двумя её точками (по углам этих точек).</summary>
    private static ICurve3 ArcBetween(ArcCurve3 full, Vector3d v0, Vector3d v1)
    {
        double A(Vector3d p)
        {
            Vector3d d = p - full.Frame.Origin;
            return Math.Atan2(Vector3d.Dot(d, full.Frame.V), Vector3d.Dot(d, full.Frame.U));
        }
        double a0 = A(v0), a1 = A(v1);
        if (Math.Abs(a1 - a0) < 1e-9) a1 = a0 + 2 * Math.PI;      // замкнутое ребро
        if (a1 < a0) a1 += 2 * Math.PI;
        return new ArcCurve3(full.Frame, full.Radius, a0, a1);
    }

    /// <summary>Дуга эллипса между двумя его точками, против часовой стрелки по параметру.</summary>
    private static ICurve3 EllipseBetween(EllipseCurve3 full, Vector3d v0, Vector3d v1)
    {
        double A(Vector3d p)
        {
            Vector3d d = p - full.Frame.Origin;
            return Math.Atan2(Vector3d.Dot(d, full.Frame.V) / full.RV, Vector3d.Dot(d, full.Frame.U) / full.RU);
        }
        double a0 = A(v0), a1 = A(v1);
        if (Math.Abs(a1 - a0) < 1e-9) a1 = a0 + 2 * Math.PI;
        if (a1 < a0) a1 += 2 * Math.PI;
        return new EllipseCurve3(full.Frame, full.RU, full.RV, a0, a1);
    }

    /// <summary>Кривая по ссылке: прямая или окружность (дуга задаётся отдельно рёбрами).</summary>
    public ICurve3? Curve(string arg)
    {
        int id = Ref(arg);
        if (!Entities.TryGetValue(id, out var e)) return null;
        var (type, a) = e;
        switch (type)
        {
            case "LINE":
            {
                Vector3d o = Point(a[1]);
                var (vt, va) = Entities[Ref(a[2])];
                if (vt != "VECTOR") return null;
                Vector3d d = Direction(va[1]) * (Num(va[2]) * LengthScale);
                return new LineCurve3(o, o + d);
            }
            case "B_SPLINE_CURVE_WITH_KNOTS":
            {
                // Доводы: степень, опорные точки, вид, замкнутость, самопересечение,
                // кратности узлов, узлы, вид параметризации.
                // Первым доводом идёт ИМЯ, поэтому степень — второй довод, точки — третий.
                int degree = (int)Num(a[1]);
                var cps = SplitArgs(a[2].Trim('(', ')')).Select(Point).ToList();
                var mult = SplitArgs(a[6].Trim('(', ')')).Select(x => (int)Num(x)).ToList();
                var kn = SplitArgs(a[7].Trim('(', ')')).Select(Num).ToList();

                // Узловой вектор разворачивается из пар «узел и его кратность».
                var knots = new List<double>();
                for (int i = 0; i < kn.Count && i < mult.Count; i++)
                    for (int m = 0; m < mult[i]; m++) knots.Add(kn[i]);

                if (cps.Count < 2) return null;
                if (knots.Count != cps.Count + degree + 1) knots = null!;   // пусть построится свой
                List<double>? wts = null;
                if (Weights1.TryGetValue(id, out var wtxt))
                {
                    wts = SplitArgs(wtxt.Trim('(', ')')).Select(Num).ToList();
                    if (wts.Count != cps.Count) wts = null;
                }
                return new SplineCurve3(cps, degree, wts, knots);
            }

            case "CIRCLE":
            {
                Frame3 f = Placement(a[1]);
                return new ArcCurve3(f, Num(a[2]) * LengthScale, 0, 2 * Math.PI);
            }
            case "ELLIPSE":
            {
                Frame3 f = Placement(a[1]);
                return new EllipseCurve3(f, Num(a[2]) * LengthScale, Num(a[3]) * LengthScale, 0, 2 * Math.PI);
            }
            case "POLYLINE":
            {
                var pts = SplitArgs(a[1].Trim('(', ')')).Select(Point).ToList();
                return pts.Count < 2 ? null : new PolylineCurve3(pts);
            }
        }
        return null;
    }
}
