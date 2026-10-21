using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Tess;

/// <summary>
/// Вершина треугольной сетки в том виде, в каком она уходит в GPU.
/// Раскладка совпадает с атрибутами шейдера Tri (см. Render/Shaders.cs),
/// поэтому загрузка — один BufferData без перепаковки.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MeshVertex
{
    public Vector3 P;       // 0  : позиция (float, локальные координаты детали)
    public Vector3 N;       // 12 : нормаль
    public uint FaceId;     // 24 : pick-id грани
    public uint Flags;      // 28 : bit0 hover, bit1 select
    public const int Size = 32;
}

/// <summary>Диапазон вершин/индексов, принадлежащих одной грани — для точечного обновления Flags.</summary>
public struct FaceRange
{
    public int VertexStart, VertexCount;
    public int IndexStart, IndexCount;
}

/// <summary>Результат тесселяции тела: треугольники + рёбра + вершины, всё уже с pick-id.</summary>
public sealed class MeshData
{
    public MeshVertex[] Vertices = Array.Empty<MeshVertex>();
    public int[] Indices = Array.Empty<int>();

    /// <summary>
    /// ГРАНИ, КОТОРЫЕ НЕ УДАЛОСЬ РАЗМЕТИТЬ, с причиной. Сетка без них — не целое тело, и тот,
    /// кто её получил, обязан об этом знать: по дырявой сетке нельзя считать массу, выводить в
    /// обмен или отдавать в расчёт.
    ///
    /// Проверки одним объёмом для этого не хватает: у грани, лежащей в плоскости начала
    /// координат, вклад в объём нулевой, и её пропажа объём не меняет вовсе.
    /// </summary>
    public readonly List<(uint Face, string Why)> BrokenFaces = new();

    /// <summary>Целая ли сетка: все грани тела размечены.</summary>
    public bool Whole => BrokenFaces.Count == 0;
    public readonly Dictionary<uint, FaceRange> FaceRanges = new();

    /// <summary>
    /// g.11: дефекты связности так, как сетку видит потребитель (тождество вершин — совпадение float, как в SolidValidator):
    /// стороны кратности ≠ 2 или с одинаковым обходом, вершины с несвязным звеном.
    /// </summary>
    public static int FloatDefects(MeshData md)
    {
        var id = new Dictionary<Vector3, int>(); int Id(int i) { var p = md.Vertices[i].P; if (!id.TryGetValue(p, out int k)) id[p] = k = id.Count; return k; }
        var dir = new Dictionary<(int, int), int>(); var tris = new List<(int, int, int)>();
        for (int t = 0; t + 2 < md.Indices.Length; t += 3)
        {
            int a = Id(md.Indices[t]), b = Id(md.Indices[t + 1]), c = Id(md.Indices[t + 2]);
            if (a == b || b == c || c == a) continue;
            tris.Add((a, b, c));
            foreach (var e in new[] { (a, b), (b, c), (c, a) }) dir[e] = dir.GetValueOrDefault(e) + 1;
        }
        int bad = 0;
        foreach (var ((a, b), n) in dir)
        {
            int m = dir.GetValueOrDefault((b, a));
            if (a < b || m == 0) { if (n != 1 || m != 1) bad++; }
        }
        var link = new Dictionary<int, List<(int, int)>>();
        foreach (var (a, b, c) in tris)
        {
            (link.TryGetValue(a, out var la) ? la : link[a] = new()).Add((b, c));
            (link.TryGetValue(b, out var lb) ? lb : link[b] = new()).Add((c, a));
            (link.TryGetValue(c, out var lc) ? lc : link[c] = new()).Add((a, b));
        }
        foreach (var (v, l) in link)
        {
            var par = new Dictionary<int, int>();
            int F(int x) { while (par.TryGetValue(x, out int q) && q != x) x = q; return x; }
            foreach (var (x, y) in l) { par.TryAdd(x, x); par.TryAdd(y, y); int rx = F(x), ry = F(y); if (rx != ry) par[rx] = ry; }
            if (par.Keys.Select(F).Distinct().Count() > 1) bad++;
        }
        return bad;
    }

    /// <summary>Ломаные рёбер: для каждого ребра — список точек и его pick-id.</summary>
    public readonly List<(uint EdgeId, Vector3[] Pts)> EdgePolylines = new();
    /// <summary>Вершины тела.</summary>
    public readonly List<(uint VertexId, Vector3 P)> Points = new();

    public Bounds3 Bounds;

    /// <summary>Копия (массивы и списки — свои; ломаные рёбер — общие, они не меняются).</summary>
    public MeshData Clone()
    {
        var c = new MeshData { Vertices = (MeshVertex[])Vertices.Clone(), Indices = (int[])Indices.Clone(), Bounds = Bounds };
        c.BrokenFaces.AddRange(BrokenFaces);
        foreach (var kv in FaceRanges) c.FaceRanges[kv.Key] = kv.Value;
        c.EdgePolylines.AddRange(EdgePolylines);
        c.Points.AddRange(Points);
        return c;
    }
}

/// <summary>Накопитель треугольников. Ведёт учёт диапазонов по граням.</summary>
public sealed class MeshBuilder
{
    private readonly List<MeshVertex> _v = new();
    private readonly List<int> _i = new();

    // КООРДИНАТЫ В DOUBLE — РЯДОМ С FLOAT, А НЕ ВМЕСТО.
    //
    // Для отрисовки float достаточно, и вершинный буфер остаётся float.
    // Но булева брала треугольники отсюда же — и работала на 32-битных
    // координатах: на детали 100 мм шаг float — 7.6e-6 мм, а допуск
    // склейки вершин 1e-6 мм. Склейка не могла срабатывать надёжно.
    // Точному ядру нужна сетка 1e-5 мм, и float её не держит вовсе.
    private readonly List<Vector3d> _pd = new();

    /// <summary>Координаты вершин в двойной точности, в порядке добавления.</summary>
    public IReadOnlyList<Vector3d> PositionsD => _pd;

    /// <summary>
    /// Присоединить готовую сетку: вершины добавляются со сдвигом номеров. Нужна, когда грани
    /// считаются по отдельности (в том числе в несколько потоков), а собираются вместе.
    /// </summary>
    /// <summary>
    /// Присоединить кусок. Номер грани ПРОСТАВЛЯЕТСЯ ЗАНОВО: вершины куска строились своим
    /// построителем, где номер не задавался, и копировать их как есть нельзя — треугольники
    /// оказываются ничьими. Грань после этого не подсвечивается под курсором и не выбирается,
    /// потому что выбор идёт по номеру.
    /// </summary>
    public void Append(MeshBuilder other)
    {
        if (other == null) return;
        int shift = VertexCount;
        for (int i = 0; i < other._v.Count; i++)
        {
            var v = other._v[i];
            v.FaceId = _curFace;
            _v.Add(v);
            _pd.Add(other._pd[i]);
        }
        foreach (int idx in other._i) _i.Add(idx + shift);
    }

    /// <summary>Стороны треугольников — нужны тем, кто строит сетку поверх готовой.</summary>
    public IReadOnlyList<int> IndicesList => _i;
    private readonly Dictionary<uint, FaceRange> _ranges = new();

    private uint _curFace;
    private int _faceV0, _faceI0;

    public void BeginFace(uint faceId)
    {
        _curFace = faceId;
        _faceV0 = _v.Count;
        _faceI0 = _i.Count;
    }

    public void EndFace()
    {
        _ranges[_curFace] = new FaceRange
        {
            VertexStart = _faceV0,
            VertexCount = _v.Count - _faceV0,
            IndexStart = _faceI0,
            IndexCount = _i.Count - _faceI0
        };
    }

    public int AddVertex(Vector3d p, Vector3d n)
    {
        _pd.Add(p);
        _v.Add(new MeshVertex
        {
            P = new Vector3((float)p.X, (float)p.Y, (float)p.Z),
            N = new Vector3((float)n.X, (float)n.Y, (float)n.Z),
            FaceId = _curFace,
            Flags = 0
        });
        return _v.Count - 1;
    }

    public void AddTriangle(int a, int b, int c)
    {
        _i.Add(a); _i.Add(b); _i.Add(c);
    }

    public void AddQuad(int a, int b, int c, int d)
    {
        AddTriangle(a, b, c);
        AddTriangle(a, c, d);
    }

    /// <summary>
    /// Отдать собранное. СХЛОПНУВШИЕСЯ ТРЕУГОЛЬНИКИ ПРИ ЭТОМ ВЫБРАСЫВАЮТСЯ.
    ///
    /// Каждая грань размечается чисто — это проверено отдельно, — но при сборке точки соседних
    /// граней сводятся к общим местам, и треугольник, у которого две вершины сошлись, становится
    /// нулевым. Измерено по ста телам: 772 таких треугольника, у худшего тела 207 из 4050, при
    /// нуле в разметке самих граней.
    ///
    /// Для показа они безвредны (ничего не рисуют), а для расчёта негодны.
    ///
    /// УБИРАТЬ ИХ ЗДЕСЬ НЕЛЬЗЯ — ПРОВЕРЕНО ДВАЖДЫ, ДВУМЯ РАЗНЫМИ СПОСОБАМИ.
    ///
    /// Оба дают заманчивые числа: охват сетки 89 → 96 тел из ста, выродившихся 772 → 20, годных
    /// к расчёту 3 → 5. И оба роняют ОДНИ И ТЕ ЖЕ двадцать одну проверку ядра: у тела с вогнутым
    /// кольцом объём выходит 38.8 или 359.5 вместо 543.3, скругление кромки перестаёт строиться,
    /// у отверстий пропадают полоски стенки.
    ///
    ///   способ 1: выбросить треугольник, у которого две вершины совпали — 21 отказ;
    ///   способ 2: свести совпавшие вершины ОДНОЙ грани в одну, после чего такие треугольники
    ///             уходят сами, а связность сохраняется — те же 21 отказ.
    ///
    /// Раз связность сохранена, а тела всё равно распадаются, дело не в сборке сетки. Эти
    /// треугольники ЧИТАЮТ дальше: по ним строятся операции (скругление кромки, полоски стенки),
    /// и там нулевая площадь, видимо, несёт смысл — например, отмечает место стыка.
    ///
    /// Значит, искать надо не здесь, а у того, кто сетку читает: сперва понять, зачем ему
    /// нулевой треугольник, и лишь потом убирать.
    /// </summary>
    /// <summary>
    /// Отдать собранное. Сведение совпавших вершин — ТОЛЬКО ПО ПРОСЬБЕ: эту же сборку читает
    /// булева, и ей нужны треугольники ровно те, что дала разметка.
    /// </summary>
    public void FillInto(MeshData md, bool weld = false, bool fine = false)
    {
        _fine = fine;
        if (weld && fine && !CollapseOff)
        {
            if (LocalSnapOn) GlobalSnapClean(snapOnly: true);
            using (Diagnostics.Prof.Time("    сетка: стягивание (1)")) CollapseSubFloatEdges();
            using (Diagnostics.Prof.Time("    сетка: щепки (1)")) GlobalSnapClean(snapOnly: false);
            // второй проход: после сшивки щепок условие звена у коротких рёбер часто выполняется (S-ROUNDHOLE: ребро 7.5e-6 мм
            // у сшитой трещины в плоскости z=20 — иначе наложение треугольников на доли шага float). Мутация P3D_SECONDPASS=0.
            if (!SecondPassOff) using (Diagnostics.Prof.Time("    сетка: второй проход")) { CollapseSubFloatEdges(); GlobalSnapClean(snapOnly: false); }
            using (Diagnostics.Prof.Time("    сетка: канон и сварка")) { CanonicalPositions(); WeldWithinFaces(); }
        }
        else if (weld) { CanonicalPositions(); WeldWithinFaces(); }

        md.Vertices = _v.ToArray();
        md.Indices = _i.ToArray();
        md.FaceRanges.Clear();
        foreach (var kv in _ranges) md.FaceRanges[kv.Key] = kv.Value;
    }

    /// <summary>
    /// СВЕСТИ СОВПАВШИЕ ВЕРШИНЫ ОДНОЙ ГРАНИ В ОДНУ, и тогда схлопнувшиеся треугольники уходят
    /// сами: у них повторяется номер вершины.
    ///
    /// Простое выбрасывание таких треугольников пробовалось и ломало тела: на них держится
    /// связность, и без них соседние грани расходятся (двадцать одна проверка ядра падала).
    /// Сведение же вершин связность СОХРАНЯЕТ — стороны начинают сходиться на общем номере.
    ///
    /// Сводятся только вершины ОДНОЙ грани: у соседних граней свои нормали и свой номер для
    /// выбора под курсором, и смешивать их нельзя.
    /// </summary>
    /// <summary>
    /// K09. ОДНА ТОЧКА — ОДНО ЧИСЛО ВО ВСЕХ ГРАНЯХ. Соседние грани вычисляют общую точку кромки
    /// каждая своим путём (поворот рамки, параметр кривой), и результаты расходятся в последних
    /// знаках: −8.9e-16 против −6.4e-16. В float это разные числа, и сетка тела вращения
    /// (сегмент тора) оказывалась открытой: 16 сторон без пары. Точки, совпадающие до 1e-9 мм,
    /// получают одно представление (первое встреченное); нормали и номера граней не трогаются.
    /// </summary>
    bool _fine;
    static readonly bool DebugSnap = Environment.GetEnvironmentVariable("P3D_COLLDBG") is "1" or "2";

    static readonly bool CollapseOff = Environment.GetEnvironmentVariable("P3D_SUBFLOATCOLLAPSE") == "0";

    /// <summary>
    /// g.11: РЁБРА КОРОЧЕ РАЗРЕШЕНИЯ FLOAT СТЯГИВАЮТСЯ С ПРОВЕРКОЙ СВЯЗНОСТИ (условие звена), до перевода во float.
    /// Точная многогранная форма бывает с щепками-гранями шириной 1e-6 мм (конус почти касается цилиндра: серия 9,
    /// случаи 65, 66, 136, 186): в double они верны и замкнуты, но во float их вершины сходятся, и сетка показа
    /// складывалась — сторона кратности 4, разветвлённые вершины; результат признавался негодным. Стягивание ребра
    /// ab с условием звена (общие соседи a и b — ровно вершины напротив ab) многообразие сохраняет; сдвиг точек — меньше
    /// 16 шагов float у этих координат (у детали 100 мм — 2e-4 мм, хорда показа 0.02 мм). Только показ тела с
    /// авторитетной многогранной формой; сама форма не меняется. Мутация P3D_SUBFLOATCOLLAPSE=0.
    /// </summary>
    private void CollapseSubFloatEdges()
    {
        if (_i.Count < 3) return;
        double maxAbs = 1;
        foreach (var q in _pd) maxAbs = Math.Max(maxAbs, Math.Max(Math.Abs(q.X), Math.Max(Math.Abs(q.Y), Math.Abs(q.Z))));
        double eps = maxAbs * 16 * 1.2e-7, eps2 = eps * eps;
        // общая вершина — по точному совпадению double (копии одной точки в разных гранях)
        var gid = new int[_pd.Count]; var byPos = new Dictionary<Vector3d, int>(); var gpos = new List<Vector3d>();
        for (int i = 0; i < _pd.Count; i++)
        {
            if (!byPos.TryGetValue(_pd[i], out int g)) { g = gpos.Count; byPos[_pd[i]] = g; gpos.Add(_pd[i]); }
            gid[i] = g;
        }
        int n = gpos.Count;
        var rep = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (rep[x] != x) x = rep[x] = rep[rep[x]]; return x; }
        int collapsed = 0;
        for (int pass = 0; pass < 8; pass++)
        {
            var tris = new List<(int A, int B, int C)>();
            for (int t = 0; t + 2 < _i.Count; t += 3)
            {
                int a = Find(gid[_i[t]]), b = Find(gid[_i[t + 1]]), c = Find(gid[_i[t + 2]]);
                if (a == b || b == c || c == a) continue;
                tris.Add((a, b, c));
            }
            var nbr = new Dictionary<int, HashSet<int>>();
            var edgeOpp = new Dictionary<(int, int), List<int>>();
            void Link(int x, int y) { if (!nbr.TryGetValue(x, out var h)) nbr[x] = h = new HashSet<int>(); h.Add(y); }
            foreach (var (a, b, c) in tris)
            {
                Link(a, b); Link(b, a); Link(b, c); Link(c, b); Link(c, a); Link(a, c);
                foreach (var (x, y, z) in new[] { (a, b, c), (b, c, a), (c, a, b) })
                {
                    var k = x < y ? (x, y) : (y, x);
                    if (!edgeOpp.TryGetValue(k, out var l)) edgeOpp[k] = l = new List<int>();
                    l.Add(z);
                }
            }
            var shortEdges = edgeOpp.Keys.Where(k => (gpos[k.Item1] - gpos[k.Item2]).LengthSquared < eps2)
                                         .OrderBy(k => (gpos[k.Item1] - gpos[k.Item2]).LengthSquared).ToList();
            if (shortEdges.Count == 0) break;
            var touched = new HashSet<int>(); int done = 0;
            foreach (var (a, b) in shortEdges)
            {
                if (touched.Contains(a) || touched.Contains(b)) continue;
                var opp = edgeOpp[(a, b)];
                if (opp.Count != 2 || opp[0] == opp[1]) continue;           // ребро не многообразное — не трогать
                var common = new HashSet<int>(nbr[a]); common.IntersectWith(nbr[b]);
                if (common.Count != 2 || !common.Contains(opp[0]) || !common.Contains(opp[1])) continue;   // условие звена
                rep[b] = a; done++;
                touched.Add(a); touched.Add(b); foreach (var x in nbr[a]) touched.Add(x); foreach (var x in nbr[b]) touched.Add(x);
            }
            collapsed += done;
            if (Environment.GetEnvironmentVariable("P3D_COLLDBG") == "1") Console.WriteLine($"collapse pass {pass}: short {shortEdges.Count} done {done} nonmanifold {shortEdges.Count(k => edgeOpp[k].Count != 2)}");
            if (done == 0) break;
        }
        if (collapsed == 0) return;
        for (int i = 0; i < _pd.Count; i++)
        {
            int r = Find(gid[i]);
            if (r == gid[i]) continue;
            var p = gpos[r]; _pd[i] = p;
            var v = _v[i]; v.P = new Vector3((float)p.X, (float)p.Y, (float)p.Z); _v[i] = v;
        }
        // треугольники со стянутым ребром — вырождены (две вершины в одной точке); у копий в разных гранях —
        // убираются здесь же (сварка внутри граней ловит только копии одной грани)
        var keep = new List<int>(_i.Count);
        for (int t = 0; t + 2 < _i.Count; t += 3)
        {
            int a = Find(gid[_i[t]]), b = Find(gid[_i[t + 1]]), c = Find(gid[_i[t + 2]]);
            if (a == b || b == c || c == a) continue;
            keep.Add(_i[t]); keep.Add(_i[t + 1]); keep.Add(_i[t + 2]);
        }
        if (keep.Count != _i.Count) { _i.Clear(); _i.AddRange(keep); }
    }

    /// <summary>
    /// g.11: СЕТКА ПОКАЗА — ТАКАЯ, КАКОЙ ЕЁ УВИДИТ ПОТРЕБИТЕЛЬ ВО FLOAT. Вершины, совпадающие во float или ближе 4 шагов
    /// float, сводятся в одну во ВСЕХ гранях; затем: треугольники с повтором вершины уходят; пары-складки (те же три
    /// вершины, обход навстречу) взаимно гасятся; игла (высота меньше шага float) убирается переворотом длиннейшей
    /// стороны с соседом — и через границу граней (обе половины — грани соседа: игла площади не имеет). Щепка шириной
    /// 1e-6 мм у точной формы так становится нулевой, а не складкой сетки. Мутация P3D_SUBFLOATCOLLAPSE=0.
    /// </summary>
    static readonly Vector3d? SnapAt = Environment.GetEnvironmentVariable("P3D_SNAPAT") is { } sv ? new Vector3d(double.Parse(sv.Split(',')[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(sv.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture), double.Parse(sv.Split(',')[2], System.Globalization.CultureInfo.InvariantCulture)) : null;
    static readonly double SnapUlps = double.TryParse(Environment.GetEnvironmentVariable("P3D_SNAPULPS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var su) ? su : 0;
    static readonly bool LocalSnapOn = Environment.GetEnvironmentVariable("P3D_LOCALSNAP") == "1";
    static readonly bool WedgeOff = Environment.GetEnvironmentVariable("P3D_WEDGE") != "1";
    static readonly bool SecondPassOff = Environment.GetEnvironmentVariable("P3D_SECONDPASS") == "0";
    static readonly double ZipK = double.TryParse(Environment.GetEnvironmentVariable("P3D_ZIPK"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var zk) ? zk : 1;
    private void GlobalSnapClean(bool snapOnly)
    {
        int nV = _v.Count; if (_i.Count < 3) return;
        double maxAbs = 1;
        foreach (var q in _pd) maxAbs = Math.Max(maxAbs, Math.Max(Math.Abs(q.X), Math.Max(Math.Abs(q.Y), Math.Abs(q.Z))));
        double eps = maxAbs * 4 * 1.2e-7, ceps = maxAbs * SnapUlps * 1.2e-7;
        // 1. скопления
        var byF = new Dictionary<Vector3, int>(); var pts = new List<Vector3d>(); var gOf = new int[nV];
        for (int i = 0; i < nV; i++)
        {
            if (!byF.TryGetValue(_v[i].P, out int g)) { g = pts.Count; byF[_v[i].P] = g; pts.Add(_pd[i]); }
            gOf[i] = g;
        }
        var par = Enumerable.Range(0, pts.Count).ToArray();
        int F(int x) { while (par[x] != x) x = par[x] = par[par[x]]; return x; }
        var cells = new Dictionary<(long, long, long), List<int>>();
        for (int g = 0; g < pts.Count && ceps > 0; g++)
        {
            var p = pts[g];
            long cx = (long)Math.Floor(p.X / ceps), cy = (long)Math.Floor(p.Y / ceps), cz = (long)Math.Floor(p.Z / ceps);
            for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -1; dz <= 1; dz++)
                if (cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var l))
                    foreach (int h in l) if ((pts[h] - p).LengthSquared <= ceps * ceps) { int a = F(g), b = F(h); if (a != b) par[Math.Max(a, b)] = Math.Min(a, b); }
            if (!cells.TryGetValue((cx, cy, cz), out var own)) cells[(cx, cy, cz)] = own = new List<int>();
            own.Add(g);
        }
        int G(int i) => F(gOf[i]);
        Vector3d Pos(int g) => pts[g];
        // 2. треугольники в общих номерах
        var tris = new List<(int A, int B, int C, uint Face, int Src)>();
        for (int t = 0; t + 2 < _i.Count; t += 3)
        {
            int a = G(_i[t]), b = G(_i[t + 1]), c = G(_i[t + 2]);
            if (a == b || b == c || c == a) continue;
            tris.Add((a, b, c, _v[_i[t]].FaceId, _i[t]));
        }
        // вершины игл (щепок) ближе 4 шагов float сводятся — только они: во float стороны щепки иначе перекрещиваются на
        // доли шага, и сшитая трещина перекрывает соседей (точная проверка самопересечений: S-ROUNDHOLE, плоскость z=20).
        // Сведение по всей сетке ломало соседство микрорёбер вне щепок (K09 вершина N=4) — поэтому только вершины игл.
        if (snapOnly)
        {
            double leps = maxAbs * 4 * 1.2e-7, Tn = ZipK * eps;
            // иглы и их связные группы (по общим сторонам): сводятся только точки ОДНОЙ группы — сведение несмежных
            // точек разных мест давало касание тела в вершине (серия 9, случай 186)
            var needleT = new List<int>();
            for (int t = 0; t < tris.Count; t++)
            {
                var (a, b, c, _, _) = tris[t];
                var pa = Pos(a); var pb = Pos(b); var pc = Pos(c);
                double L = Math.Sqrt(Math.Max((pb - pa).LengthSquared, Math.Max((pc - pb).LengthSquared, (pa - pc).LengthSquared)));
                if (L > 0 && Vector3d.Cross(pb - pa, pc - pa).Length / L <= Tn) needleT.Add(t);
            }
            var gpar = Enumerable.Range(0, needleT.Count).ToArray();
            int GF(int x) { while (gpar[x] != x) x = gpar[x] = gpar[gpar[x]]; return x; }
            var firstByEdge = new Dictionary<(int, int), int>();
            for (int k = 0; k < needleT.Count; k++)
            {
                var (a, b, c, _, _) = tris[needleT[k]];
                foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
                {
                    var e = x < y ? (x, y) : (y, x);
                    if (firstByEdge.TryGetValue(e, out int o)) { int r1 = GF(k), r2 = GF(o); if (r1 != r2) gpar[r1] = r2; } else firstByEdge[e] = k;
                }
            }
            var groupsOf = new Dictionary<int, HashSet<int>>();
            for (int k = 0; k < needleT.Count; k++)
            {
                var (a, b, c, _, _) = tris[needleT[k]]; int gr = GF(k);
                foreach (int v in new[] { a, b, c }) { if (!groupsOf.TryGetValue(v, out var hs)) groupsOf[v] = hs = new HashSet<int>(); hs.Add(gr); }
            }
            var nv = groupsOf.Keys.ToList();
            var lc = new Dictionary<(long, long, long), List<int>>(); int unions = 0;
            foreach (int g in nv)
            {
                var p = Pos(g);
                long cx = (long)Math.Floor(p.X / leps), cy = (long)Math.Floor(p.Y / leps), cz = (long)Math.Floor(p.Z / leps);
                for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -1; dz <= 1; dz++)
                    if (lc.TryGetValue((cx + dx, cy + dy, cz + dz), out var l))
                        foreach (int h in l) if ((Pos(h) - p).LengthSquared <= leps * leps && groupsOf[g].Overlaps(groupsOf[h])) { int a = F(g), b = F(h); if (a != b) { par[Math.Max(a, b)] = Math.Min(a, b); unions++; } }
                if (!lc.TryGetValue((cx, cy, cz), out var own)) lc[(cx, cy, cz)] = own = new List<int>();
                own.Add(g);
            }
            if (unions > 0)
            {
                tris.Clear();
                for (int t = 0; t + 2 < _i.Count; t += 3)
                {
                    int a = G(_i[t]), b = G(_i[t + 1]), c = G(_i[t + 2]);
                    if (a == b || b == c || c == a) continue;
                    tris.Add((a, b, c, _v[_i[t]].FaceId, _i[t]));
                }
            }
        }
        var dead = new bool[tris.Count];
        bool changed = false;
        if (snapOnly) goto Positions;
        // складки
        var bySet = new Dictionary<(int, int, int), List<int>>();
        for (int t = 0; t < tris.Count; t++)
        {
            var arr = new[] { tris[t].A, tris[t].B, tris[t].C }; Array.Sort(arr);
            var k = (arr[0], arr[1], arr[2]);
            if (!bySet.TryGetValue(k, out var l)) bySet[k] = l = new List<int>();
            l.Add(t);
        }
        bool Even(int t) { var (a, b, c, _, _) = tris[t]; int m = Math.Min(a, Math.Min(b, c)); return m == a ? b < c : m == b ? c < a : a < b; }
        foreach (var l in bySet.Values)
        {
            if (l.Count < 2) continue;
            var pos = l.Where(Even).ToList(); var neg = l.Where(t => !Even(t)).ToList();
            for (int k = 0; k < Math.Min(pos.Count, neg.Count); k++) { dead[pos[k]] = true; dead[neg[k]] = true; }
        }
        // иглы (высота меньше T) — связными группами: группа удаляется, её соседи по сторонам (треугольники с основанием
        // на группе) делятся во всех точках группы, лежащих на их основании; так обе стороны щепки получают одни и те же
        // вершины, и стороны сходятся парами. Группа, после которой окрестность не многообразна, возвращается как была.
        changed = dead.Any(d => d) || tris.Count * 3 != _i.Count;
        double T = ZipK * eps;
        double Height(int m, out int p, out int q, out int r)
        {
            var (a, b, c, _, _) = tris[m];
            var pa = Pos(a); var pb = Pos(b); var pc = Pos(c);
            double ab = (pb - pa).LengthSquared, bc = (pc - pb).LengthSquared, ca = (pa - pc).LengthSquared;
            if (ab >= bc && ab >= ca) { p = a; q = b; r = c; } else if (bc >= ab && bc >= ca) { p = b; q = c; r = a; } else { p = c; q = a; r = b; }
            double L = Math.Sqrt(Math.Max(ab, Math.Max(bc, ca)));
            return L == 0 ? 0 : Vector3d.Cross(pb - pa, pc - pa).Length / L;
        }
        _snapTris = tris; _snapPos = Pos; _snapT = T;
        var isNeedle = new bool[tris.Count];
        for (int t = 0; t < tris.Count; t++) if (!dead[t]) isNeedle[t] = Height(t, out _, out _, out _) <= T;
        if (isNeedle.Any(x => x))
        {
            var byEdge = new Dictionary<(int, int), List<int>>();
            (int, int) U(int x, int y) => x < y ? (x, y) : (y, x);
            for (int t = 0; t < tris.Count; t++)
            {
                if (dead[t]) continue;
                var (a, b, c, _, _) = tris[t];
                foreach (var e in new[] { U(a, b), U(b, c), U(c, a) }) { if (!byEdge.TryGetValue(e, out var l)) byEdge[e] = l = new List<int>(); l.Add(t); }
            }
            var seen = new bool[tris.Count]; int nT0 = tris.Count; int fixedGroups = 0, failedGroups = 0;
            for (int s0 = 0; s0 < nT0; s0++)
            {
                if (dead[s0] || !isNeedle[s0] || seen[s0]) continue;
                // группа игл
                var grp = new List<int>(); var st = new Stack<int>(); st.Push(s0); seen[s0] = true;
                while (st.Count > 0)
                {
                    int t = st.Pop(); grp.Add(t);
                    var (a, b, c, _, _) = tris[t];
                    foreach (var e in new[] { U(a, b), U(b, c), U(c, a) })
                        foreach (int m in byEdge[e]) if (!dead[m] && isNeedle[m] && !seen[m]) { seen[m] = true; st.Push(m); }
                }
                var gset = new HashSet<int>(grp);
                // треугольник, со всех сторон окружённый иглами группы (дырка в щепке), — внутри щепки: тоже прочь
                for (bool more = true; more;)
                {
                    more = false;
                    foreach (int t in grp.ToList())
                    {
                        var (a, b, c, _, _) = tris[t];
                        foreach (var e in new[] { U(a, b), U(b, c), U(c, a) })
                            foreach (int m in byEdge[e])
                            {
                                if (dead[m] || gset.Contains(m)) continue;
                                var tm = tris[m];
                                if (new[] { U(tm.A, tm.B), U(tm.B, tm.C), U(tm.C, tm.A) }.All(f => byEdge[f].Any(o => o != m && gset.Contains(o))))
                                { gset.Add(m); grp.Add(m); seen[m] = true; more = true; }
                            }
                    }
                }
                var S = grp.SelectMany(t => new[] { tris[t].A, tris[t].B, tris[t].C }).Distinct().ToList();
                // стороны: не-иглы с общим ребром группы
                var sides = new List<(int T, int A, int B)>();
                foreach (int t in grp)
                {
                    var (a, b, c, _, _) = tris[t];
                    foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
                        foreach (int m in byEdge[U(x, y)])
                            if (!dead[m] && !gset.Contains(m) && !sides.Any(sd => sd.T == m))
                            {
                                var tm = tris[m];
                                // основание в обходе соседа
                                var (u, v) = (tm.A == y && tm.B == x) || (tm.A == x && tm.B == y) ? (tm.A, tm.B) : (tm.B == y && tm.C == x) || (tm.B == x && tm.C == y) ? (tm.B, tm.C) : (tm.C, tm.A);
                                sides.Add((m, u, v));
                            }
                }
                // новая окрестность: точки деления основания стороны — (1) по параметру вдоль «хребта» щепки (длина дуги
                // по одной стороне контура группы; годится и для изогнутой полосы вдоль кривой пересечения), иначе
                // (2) по проекции на прямую основания
                var kill = new HashSet<int>(grp);
                List<(int A, int B, int C, uint Face, int Src)>? Build(Func<int, int, List<int>?> between)
                {
                    var add = new List<(int A, int B, int C, uint Face, int Src)>();
                    foreach (var (m, u, v) in sides)
                    {
                        var tm = tris[m];
                        int apex = tm.A != u && tm.A != v ? tm.A : tm.B != u && tm.B != v ? tm.B : tm.C;
                        var mid = between(u, v); if (mid == null) return null;
                        var chain = new List<int> { u }; chain.AddRange(mid); chain.Add(v);
                        for (int k = 0; k + 1 < chain.Count; k++) add.Add((chain[k], chain[k + 1], apex, tm.Face, tm.Src));
                    }
                    // проверка: стороны новых треугольников и прочих треугольников при них — парны
                    var cnt = new Dictionary<(int, int), int>();
                    void Count(int a, int b) { var k = (a, b); cnt[k] = cnt.GetValueOrDefault(k) + 1; }
                    foreach (var (a, b, c, _, _) in add) { Count(a, b); Count(b, c); Count(c, a); }
                    var others = new HashSet<int>();
                    foreach (var tri in add) foreach (var e in new[] { U(tri.A, tri.B), U(tri.B, tri.C), U(tri.C, tri.A) }) if (byEdge.TryGetValue(e, out var l)) foreach (int m in l) if (!dead[m] && !kill.Contains(m) && !sides.Any(sd => sd.T == m)) others.Add(m);
                    foreach (int m in others) { var (a, b, c, _, _) = tris[m]; Count(a, b); Count(b, c); Count(c, a); }
                    foreach (var tri in add)
                        foreach (var (x, y) in new[] { (tri.A, tri.B), (tri.B, tri.C), (tri.C, tri.A) })
                            if (cnt.GetValueOrDefault((x, y)) != 1 || cnt.GetValueOrDefault((y, x)) != 1) return null;
                    return add;
                }
                List<int>? Straight(int u, int v)
                {
                    var pu = Pos(u); var d = Pos(v) - pu; double L2 = d.LengthSquared; if (L2 == 0) return null;
                    var on = new List<(double S, int V)>();
                    foreach (int w in S)
                    {
                        if (w == u || w == v) continue;
                        double sp = Vector3d.Dot(Pos(w) - pu, d) / L2;
                        if (sp <= 0 || sp >= 1) continue;
                        if ((pu + d * sp - Pos(w)).Length <= 2 * T) on.Add((sp, w));
                    }
                    on.Sort((x, y) => x.S.CompareTo(y.S));
                    return on.Select(o => o.V).ToList();
                }
                var spine = SpineParams(grp, S);
                List<int>? BySpine(int u, int v)
                {
                    if (spine == null || !spine.TryGetValue(u, out double su) || !spine.TryGetValue(v, out double sv)) return null;
                    double lo = Math.Min(su, sv), hi = Math.Max(su, sv);
                    var on = S.Where(w => w != u && w != v && spine[w] > lo && spine[w] < hi).ToList();
                    on.Sort((x, y) => spine[x] != spine[y] ? spine[x].CompareTo(spine[y]) : x.CompareTo(y));
                    if (su > sv) on.Reverse();
                    return on;
                }
                var addSel = Build(BySpine) ?? Build(Straight);
                bool bad = addSel == null;
                var add = addSel ?? new List<(int A, int B, int C, uint Face, int Src)>();
                foreach (var sd in sides) kill.Add(sd.T);
                if (bad)
                {
                    failedGroups++;
                    if (Environment.GetEnvironmentVariable("P3D_COLLDBG") == "2")
                    {
                        Console.WriteLine($"failed group: needles {grp.Count} points {S.Count} sides {sides.Count} add {add.Count} spine {(spine == null ? _spineWhy : "ok")}");
                        foreach (int t in grp) Console.WriteLine($"   needle {tris[t].A},{tris[t].B},{tris[t].C} h {Height(t, out _, out _, out _):E2}");
                        foreach (var sd in sides) { var tm = tris[sd.T]; Console.WriteLine($"   side {tm.A},{tm.B},{tm.C} base {sd.A}->{sd.B} h {Height(sd.T, out _, out _, out _):E2}"); }
                        foreach (int w in S) Console.WriteLine($"   pt {w} {Pos(w).X:R} {Pos(w).Y:R} {Pos(w).Z:R}");
                    }
                    continue;
                }
                foreach (int m in kill) dead[m] = true;
                foreach (var tri in add)
                {
                    tris.Add(tri); Array.Resize(ref dead, tris.Count); Array.Resize(ref isNeedle, tris.Count); Array.Resize(ref seen, tris.Count);
                    int id = tris.Count - 1; seen[id] = true;
                    foreach (var e in new[] { U(tri.A, tri.B), U(tri.B, tri.C), U(tri.C, tri.A) }) { if (!byEdge.TryGetValue(e, out var l)) byEdge[e] = l = new List<int>(); l.Add(id); }
                }
                fixedGroups++; changed = true;
            }
            if (Environment.GetEnvironmentVariable("P3D_COLLDBG") == "1") Console.WriteLine($"needle groups: fixed {fixedGroups} failed {failedGroups}");
        }
        Positions:
        if (snapOnly) changed = tris.Count * 3 != _i.Count;
        // 3. позиции — общие у скопления
        for (int i = 0; i < nV; i++)
        {
            var pp = Pos(G(i));
            if (_pd[i] != pp) { _pd[i] = pp; var v = _v[i]; v.P = new Vector3((float)pp.X, (float)pp.Y, (float)pp.Z); _v[i] = v; }
        }
        if (!changed) return;
        // 4. номера вершин по (грань, скопление)
        var idx = new Dictionary<(uint, int), int>();
        for (int i = 0; i < nV; i++) idx.TryAdd((_v[i].FaceId, G(i)), i);
        var anyOf = new Dictionary<int, int>();
        for (int i = 0; i < nV; i++) anyOf.TryAdd(G(i), i);
        int V(uint face, int g, Vector3d nrm)
        {
            if (idx.TryGetValue((face, g), out int i)) return i;
            var v = _v[anyOf[g]]; v.FaceId = face; v.N = new Vector3((float)nrm.X, (float)nrm.Y, (float)nrm.Z); _v.Add(v); _pd.Add(_pd[anyOf[g]]);
            idx[(face, g)] = _v.Count - 1; return _v.Count - 1;
        }
        _i.Clear();
        for (int t = 0; t < tris.Count; t++)
        {
            if (dead[t]) continue;
            var (a, b, c, face, _) = tris[t];
            var fn = Vector3d.Cross(Pos(b) - Pos(a), Pos(c) - Pos(a)); fn = fn.LengthSquared > 0 ? fn.Normalized() : Vector3d.UnitZ;
            _i.Add(V(face, a, fn)); _i.Add(V(face, b, fn)); _i.Add(V(face, c, fn));
        }
    }

    /// <summary>
    /// Параметр «хребта» щепки: контур группы игл (направленные стороны без пары внутри группы) — одна петля; концы щепки —
    /// места, где петля разворачивается (угол между соседними сторонами длиннее поперечника — больше 135°); сторона A — путь
    /// от одного конца до другого; параметр точки — длина дуги по стороне A до ближайшей точки на ней. null — строение не
    /// такое (две петли, ветвление, не два конца).
    /// </summary>
    Dictionary<int, double>? SpineParams(List<int> grp, List<int> S)
    {
        // направленные стороны группы → треугольник; обход края с поворотом внутри группы у каждой вершины (у щепки,
        // стянутой в точку посередине, вершина края встречается дважды)
        var owner = new Dictionary<(int, int), int>();
        foreach (int t in grp) { var (a, b, c, _, _) = _snapTris![t]; owner[(a, b)] = t; owner[(b, c)] = t; owner[(c, a)] = t; }
        var bnd = owner.Keys.Where(e => !owner.ContainsKey((e.Item2, e.Item1))).ToList();
        if (bnd.Count < 3) { _spineWhy = "r1"; return null; }
        int Third(int t, int x, int y) { var (a, b, c, _, _) = _snapTris![t]; return a != x && a != y ? a : b != x && b != y ? b : c; }
        (int, int) NextB((int X, int Y) e)
        {
            // из y: сторона (y, z) того же треугольника; пока у неё есть пара в группе — переход в соседний треугольник
            int t = owner[e]; int y = e.Y; int z = Third(t, e.X, y);
            for (int guard = 0; guard < 64; guard++)
            {
                if (!owner.ContainsKey((z, y))) return (y, z);
                t = owner[(z, y)]; int w = Third(t, z, y); z = w;
            }
            return (-1, -1);
        }
        var used = new HashSet<(int, int)>(); var loops = new List<List<int>>();
        foreach (var e0 in bnd)
        {
            if (used.Contains(e0)) continue;
            var lp = new List<int>(); var e = e0;
            for (int guard = 0; guard <= bnd.Count; guard++)
            {
                if (!used.Add(e)) break;
                lp.Add(e.Item1); e = NextB(e); if (e.Item1 < 0) { _spineWhy = "walk"; return null; }
            }
            if (e != e0) { _spineWhy = "open walk"; return null; }
            loops.Add(lp);
        }
        if (loops.Count != 1) { _spineWhy = "loops " + loops.Count + " [" + string.Join(",", loops.Select(l => l.Count)) + "]"; return null; }
        var loop = loops[0];
        int n = loop.Count;
        Vector3d P(int i) => _snapPos!(loop[((i % n) + n) % n]);
        // поперечник: стороны короче cap — поперёк щепки
        double cap = 4 * _snapT;
        var turn = new double[n];
        for (int i = 0; i < n; i++)
        {
            int j = i; Vector3d din = Vector3d.Zero;
            for (int k = 0; k < n && din == Vector3d.Zero; k++, j--) { var e = P(j) - P(j - 1); if (e.Length > cap) din = e.Normalized(); }
            j = i; Vector3d dout = Vector3d.Zero;
            for (int k = 0; k < n && dout == Vector3d.Zero; k++, j++) { var e = P(j + 1) - P(j); if (e.Length > cap) dout = e.Normalized(); }
            turn[i] = Vector3d.Dot(din, dout);
        }
        // места разворота — группы подряд идущих вершин с turn < −0.7; конец — вершина с наименьшим turn в группе
        var ends = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (turn[i] >= -0.7) continue;
            if (turn[(i - 1 + n) % n] < -0.7 && i != 0) continue;     // не начало группы
            int k = i, best = i;
            while (turn[k % n] < -0.7 && k < i + n) { if (turn[k % n] < turn[best % n]) best = k; k++; }
            ends.Add(best % n);
        }
        // группа, начавшаяся до 0 и продолженная с 0, считается дважды — свести
        ends = ends.Distinct().ToList();
        if (ends.Count == 3 && turn[0] < -0.7 && turn[n - 1] < -0.7) ends.RemoveAt(0);
        // клин (иглы веером от общей вершины): острый конец один — второй конец берётся на половине длины петли
        if (ends.Count == 1 && !WedgeOff)
        {
            double per = 0; var cum = new double[n + 1];
            for (int i = 0; i < n; i++) { cum[i + 1] = cum[i] + (P(ends[0] + i + 1) - P(ends[0] + i)).Length; }
            per = cum[n]; int k = 0; while (k < n && cum[k] < per / 2) k++;
            ends.Add((ends[0] + k) % n);
        }
        if (ends.Count != 2) { _spineWhy = "ends " + ends.Count + " n " + n + " r5"; return null; }
        int e1 = ends[0], e2 = ends[1];
        var side = new List<int>(); for (int i = e1; ; i = (i + 1) % n) { side.Add(loop[i]); if (i == e2) break; }
        var arc = new double[side.Count];
        for (int i = 1; i < side.Count; i++) arc[i] = arc[i - 1] + (_snapPos!(side[i]) - _snapPos(side[i - 1])).Length;
        var res = new Dictionary<int, double>();
        for (int i = 0; i < side.Count; i++) res[side[i]] = arc[i];
        foreach (int w in S)
        {
            if (res.ContainsKey(w)) continue;
            var p = _snapPos!(w); double bestD = double.MaxValue, bestS = 0;
            for (int i = 0; i + 1 < side.Count; i++)
            {
                var a = _snapPos(side[i]); var d = _snapPos(side[i + 1]) - a; double L2 = d.LengthSquared; if (L2 == 0) continue;
                double sp = Math.Clamp(Vector3d.Dot(p - a, d) / L2, 0, 1);
                double dist = (a + d * sp - p).Length;
                if (dist < bestD) { bestD = dist; bestS = arc[i] + sp * Math.Sqrt(L2); }
            }
            if (bestD > Math.Max(4 * _snapT, WedgeOff ? 0 : 0.1 * arc[^1])) { _spineWhy = "r6"; return null; }
            res[w] = bestS;
        }
        return res;
    }
    public string? _spineWhy;
    List<(int A, int B, int C, uint Face, int Src)>? _snapTris; Func<int, Vector3d>? _snapPos; double _snapT;

    private void CanonicalPositions()
    {
        // Порог — несколько шагов float у этих координат: точки, которые во float всё равно
        // почти неразличимы, получают одно значение во всех гранях. Иначе микроребро (6e-6 мм
        // у осколка повёрнутого скругления) оставалось и внутренним ребром одной грани, и общим
        // ребром двух других — сторона кратности 4. Сводится ДО сварки граней: сведённые точки
        // внутри грани затем свариваются, вырожденные треугольники уходят.
        double maxAbs = 1;
        foreach (var q in _pd) maxAbs = Math.Max(maxAbs, Math.Max(Math.Abs(q.X), Math.Max(Math.Abs(q.Y), Math.Abs(q.Z))));
        double eps = Math.Max(1e-9, maxAbs * (_fine ? 1 : 4) * 1.2e-7);
        var cells = new Dictionary<(long, long, long), List<int>>();
        for (int i = 0; i < _pd.Count; i++)
        {
            var p = _pd[i];
            long cx = (long)Math.Floor(p.X / eps), cy = (long)Math.Floor(p.Y / eps), cz = (long)Math.Floor(p.Z / eps);
            int rep = -1;
            for (long dx = -1; dx <= 1 && rep < 0; dx++)
                for (long dy = -1; dy <= 1 && rep < 0; dy++)
                    for (long dz = -1; dz <= 1 && rep < 0; dz++)
                        if (cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
                            foreach (int j in list)
                                if ((_pd[j] - p).LengthSquared <= eps * eps) { rep = j; break; }
            if (rep >= 0 && _pd[rep] != p)
            {
                _pd[i] = _pd[rep];
                var v = _v[i]; v.P = _v[rep].P; _v[i] = v;
            }
            else if (rep < 0)
            {
                if (!cells.TryGetValue((cx, cy, cz), out var own)) cells[(cx, cy, cz)] = own = new List<int>();
                own.Add(i);
            }
        }
    }

    private void WeldWithinFaces()
    {
        double same = _fine ? 1e-9 : Math.Max(Core.Tol.Weld * 0.5, 1e-9);
        double scale = 1 / same;

        var first = new Dictionary<(uint, long, long, long), int>();
        // K09. ТАКЖЕ СВОДЯТСЯ ВЕРШИНЫ, СОВПАВШИЕ В FLOAT. Сетка уходит потребителю во float: две
        // различные в double точки (у осколков точной булевой — ближе 1e-6 мм), попавшие в одно
        // float-значение, иначе дают в одной грани две вершины в одном месте, и сторона между
        // ними и соседями оказывалась кратности 4 (повёрнутое скругление A06, поворот 3D 0.9).
        // Округлённый ключ этого не ловит: близкие точки могут лечь по разные стороны границы ячейки.
        var firstFloat = new Dictionary<(uint, float, float, float), int>();
        var map = new int[_v.Count];

        for (int i = 0; i < _v.Count; i++)
        {
            var key = (_v[i].FaceId,
                       (long)Math.Round(_pd[i].X * scale),
                       (long)Math.Round(_pd[i].Y * scale),
                       (long)Math.Round(_pd[i].Z * scale));
            var fkey = (_v[i].FaceId, _v[i].P.X, _v[i].P.Y, _v[i].P.Z);

            if (firstFloat.TryGetValue(fkey, out int atF)) { map[i] = map[atF]; continue; }
            if (!first.TryGetValue(key, out int at)) first[key] = at = i;
            map[i] = at;
            firstFloat[fkey] = i;
        }

        // K01. ПРЕЖДЕ ЗДЕСЬ ВЫБРАСЫВАЛСЯ ЛЮБОЙ ТРЕУГОЛЬНИК, ПРИЗНАННЫЙ ВЫРОЖДЕННЫМ, — и с тремя
        // разными вершинами тоже. Но у замкнутой сетки каждая сторона принадлежит ровно двум
        // треугольникам: удаление треугольника с тремя различными вершинами всегда оставляет три
        // стороны без пары, то есть дыру. Так открывалась грань z=20 сферического выреза: два
        // годных треугольника-иглы (1.8·10⁻⁷ мм²) мера угла ошибочно сочла нулевыми.
        //
        // Теперь:
        //  • треугольник со сведёнными вершинами (повтор номера) удаляется — его стороны
        //    взаимно гасятся, и связность не меняется;
        //  • треугольник с тремя разными вершинами, лежащими на одной прямой (нулевая площадь
        //    в выходных float-координатах), убирается ПЕРЕВОРОТОМ длиннейшей стороны с соседом
        //    той же грани: область не меняется, стороны остаются парными;
        //  • если соседа нет (сторона на краю грани) или переворот дал бы плохой результат,
        //    треугольник ОСТАЁТСЯ и учитывается мерой качества как выродившийся. Закрытость
        //    оболочки важнее чистоты показной сетки.
        var tris = new List<(int A, int B, int C)>(_i.Count / 3);
        for (int t = 0; t + 2 < _i.Count; t += 3)
        {
            int a = map[_i[t]], b = map[_i[t + 1]], c = map[_i[t + 2]];
            if (a == b || b == c || c == a) continue;
            tris.Add((a, b, c));
        }

        RemoveFolds(tris);
        if (!(_fine && Environment.GetEnvironmentVariable("P3D_FINEFLIP") == "0")) RemoveCollinearByFlips(tris);

        bool changed = tris.Count * 3 != _i.Count || map.Where((m, i) => m != i).Any();
        if (!changed)
            for (int t = 0, k = 0; t < tris.Count; t++, k += 3)
                if (_i[k] != tris[t].A || _i[k + 1] != tris[t].B || _i[k + 2] != tris[t].C) { changed = true; break; }
        if (!changed) return;

        _i.Clear();
        foreach (var (a, b, c) in tris) { _i.Add(a); _i.Add(b); _i.Add(c); }
    }

    /// <summary>Площадь треугольника нулевая в тех float-координатах, что уйдут потребителю.</summary>
    private bool ZeroAreaOut(int a, int b, int c)
    {
        var qa = new Vector3d(_v[a].P.X, _v[a].P.Y, _v[a].P.Z);
        var qb = new Vector3d(_v[b].P.X, _v[b].P.Y, _v[b].P.Z);
        var qc = new Vector3d(_v[c].P.X, _v[c].P.Y, _v[c].P.Z);
        return Vector3d.Cross(qb - qa, qc - qa).LengthSquared == 0;
    }

    private Vector3d OutP(int i) => new(_v[i].P.X, _v[i].P.Y, _v[i].P.Z);

    /// <summary>
    /// K09. СКЛАДКА: два треугольника на одних и тех же трёх вершинах с противоположным обходом.
    /// Так выглядит игла осколка точной булевой, разбитая веером с двух сторон (три почти
    /// коллинеарные точки у повёрнутого скругления): каждая её сторона встречалась четырежды.
    /// Пара складки удаляется целиком — её стороны гасят друг друга, связность не меняется.
    /// </summary>
    private static void RemoveFolds(List<(int A, int B, int C)> tris)
    {
        var bySet = new Dictionary<(int, int, int), List<int>>();
        for (int t = 0; t < tris.Count; t++)
        {
            var (a, b, c) = tris[t];
            var arr = new[] { a, b, c }; Array.Sort(arr);
            var key = (arr[0], arr[1], arr[2]);
            if (!bySet.TryGetValue(key, out var l)) bySet[key] = l = new List<int>();
            l.Add(t);
        }
        static int Parity((int A, int B, int C) t)
        {
            // Обход относительно возрастающего порядка вершин: +1 — тот же, −1 — обратный.
            int[] v = { t.A, t.B, t.C };
            int inv = (v[0] > v[1] ? 1 : 0) + (v[0] > v[2] ? 1 : 0) + (v[1] > v[2] ? 1 : 0);
            return inv % 2 == 0 ? 1 : -1;
        }
        var dead = new HashSet<int>();
        foreach (var list in bySet.Values)
        {
            if (list.Count < 2) continue;
            var pos = list.Where(t => Parity(tris[t]) > 0).ToList();
            var neg = list.Where(t => Parity(tris[t]) < 0).ToList();
            for (int i = 0; i < Math.Min(pos.Count, neg.Count); i++) { dead.Add(pos[i]); dead.Add(neg[i]); }
        }
        if (dead.Count == 0) return;
        var keep = new List<(int, int, int)>(tris.Count - dead.Count);
        for (int t = 0; t < tris.Count; t++) if (!dead.Contains(t)) keep.Add(tris[t]);
        tris.Clear(); tris.AddRange(keep);
    }

    /// <summary>
    /// Убрать треугольники нулевой площади с тремя разными вершинами переворотом ребра.
    /// Вершины разных граней имеют разные номера, поэтому сосед по стороне — всегда той же грани.
    /// </summary>
    private void RemoveCollinearByFlips(List<(int A, int B, int C)> tris)
    {
        if (tris.Count == 0) return;

        var zero = new List<int>();
        for (int t = 0; t < tris.Count; t++)
            if (ZeroAreaOut(tris[t].A, tris[t].B, tris[t].C)) zero.Add(t);
        if (zero.Count == 0) return;

        // Направленная сторона → треугольник. В правильной сетке направленная сторона одна.
        var owner = new Dictionary<(int, int), int>(tris.Count * 3);
        void Own(int t, bool on)
        {
            var (a, b, c) = tris[t];
            foreach (var k in new[] { (a, b), (b, c), (c, a) })
                if (on) owner[k] = t; else if (owner.TryGetValue(k, out int o) && o == t) owner.Remove(k);
        }
        for (int t = 0; t < tris.Count; t++) Own(t, true);

        var dead = new bool[tris.Count];
        // Несколько проходов: после переворота соседняя игла может получить соседа.
        for (int pass = 0; pass < 4 && zero.Count > 0; pass++)
        {
            var still = new List<int>();
            foreach (int t in zero)
            {
                if (dead[t]) continue;
                var (a, b, c) = tris[t];
                if (!ZeroAreaOut(a, b, c)) continue;

                // Длиннейшая сторона p→q, средняя точка r: обход p→q→r.
                double ab = (OutP(b) - OutP(a)).LengthSquared, bc = (OutP(c) - OutP(b)).LengthSquared,
                       ca = (OutP(a) - OutP(c)).LengthSquared;
                int p, q, r;
                if (ab >= bc && ab >= ca) { p = a; q = b; r = c; }
                else if (bc >= ab && bc >= ca) { p = b; q = c; r = a; }
                else { p = c; q = a; r = b; }

                if (!owner.TryGetValue((q, p), out int n) || n == t || dead[n]) { still.Add(t); continue; }

                var (na, nb, nc) = tris[n];
                int d = na != q && na != p ? na : nb != q && nb != p ? nb : nc;
                if (d == r || ZeroAreaOut(p, d, r) || ZeroAreaOut(d, q, r)) { still.Add(t); continue; }
                // Сторона r–d уже есть — переворот создал бы ребро кратности больше двух.
                if (owner.ContainsKey((d, r)) || owner.ContainsKey((r, d))) { still.Add(t); continue; }

                // Ориентация новых треугольников согласуется с прежней: оба смотрят как сосед.
                var before = Vector3d.Cross(OutP(p) - OutP(q), OutP(d) - OutP(q));
                var n1 = Vector3d.Cross(OutP(d) - OutP(p), OutP(r) - OutP(p));
                var n2 = Vector3d.Cross(OutP(q) - OutP(d), OutP(r) - OutP(d));
                if (Vector3d.Dot(before, n1) <= 0 || Vector3d.Dot(before, n2) <= 0) { still.Add(t); continue; }

                Own(t, false); Own(n, false);
                tris[t] = (p, d, r);
                tris[n] = (d, q, r);
                Own(t, true); Own(n, true);
            }
            if (still.Count == zero.Count) break;
            zero = still;
        }
    }

    public int VertexCount => _v.Count;
}
