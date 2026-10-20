using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Tess;

/// <summary>
/// Превращает B-Rep в данные для GPU.
///
/// Важно: рёбра берутся ИЗ ТОПОЛОГИИ, а не восстанавливаются из треугольников.
/// Поэтому каркас рисуется один раз на ребро, без «лесенки» из диагоналей
/// треугольников и без дублирования на стыке двух граней.
/// </summary>
public static class Tessellator
{
    /// <summary>
    /// Сколько потоков использовать на расчёте граней. По умолчанию по числу ядер; на одном
    /// ядре раскладка не включается. Задаётся и переключателем P3D_THREADS.
    /// </summary>
    private static int _gThreads =
        int.TryParse(Environment.GetEnvironmentVariable("P3D_THREADS"), out int t) && t > 0
            ? t : Environment.ProcessorCount;
    /// <summary>K04: значение контекста вычисления, если он задан.</summary>
    public static int Threads
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.MeshThreads : _gThreads;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gThreads, value, "Tessellator.Threads");
    }

    [ThreadStatic] private static TSolid? LastSolid;
    internal static void ReleaseThreadState() { LastSolid = null; }
    [ThreadStatic] private static bool LastFacets;

    /// <summary>
    /// Нужна ли этой грани ТОЧНАЯ сетка. Обычные построители поверхностей гранят грань целиком
    /// и контуров не смотрят: у цилиндра с вырезом по кривой стыка сетка накрывала бы и вырез
    /// (измерено: объём вала с поперечным отверстием выходил на 3.6% больше расчётного).
    /// Построитель точной сетки такие контуры умеет — проверено, он принимает и вал с двумя
    /// вырезами (3036 треугольников), и стенку отверстия (96).
    /// </summary>
    private static bool NeedsExactMesher(TFace f)
    {
        // Условие узкое СОЗНАТЕЛЬНО. Кривые пересечения встречаются и у обычных тел после
        // булевой, и там прежнее гранение верно: широкое условие сломало проверку кромки
        // короткого отверстия. Точная сетка нужна лишь там, где грань имеет ВЫРЕЗ (больше
        // одного контура), ограниченный такой кривой, — то есть у вала с поперечным отверстием.
        if (f.Loops.Count < 2) return false;

        foreach (var loop in f.Loops)
        {
            if (loop.IsOuter) continue;
            foreach (var ce in loop.CoEdges)
                if (ce.Edge?.Curve is Curves.IntersectionCurve3) return true;
        }
        return false;
    }

    /// <summary>
    /// Сетка грани точным построителем. Обход треугольников согласуется с нормалью поверхности:
    /// построитель не обязан выдавать их в одну сторону, а знаковый объём к этому чувствителен.
    /// </summary>
    private static bool ExactInto(TFace f, MeshBuilder mb)
    {
        var res = ExactFaceMesher.Mesh(f, out _);
        if (res == null) return false;

        var (P, T) = res.Value;
        if (T.Count < 3) return false;

        int start = mb.VertexCount;
        foreach (var p in P)
        {
            Vector3d n = f.Surface.NormalAt(f.Surface.ProjectToUV(p));
            if (f.Reversed) n = -n;
            mb.AddVertex(p, n);
        }

        for (int i = 0; i + 2 < T.Count; i += 3)
        {
            Vector3d a = P[T[i]], b = P[T[i + 1]], c = P[T[i + 2]];
            Vector3d tn = Vector3d.Cross(b - a, c - a);
            if (tn.LengthSquared < 1e-24) continue;

            Vector3d g = (a + b + c) / 3;
            Vector3d want = f.Surface.NormalAt(f.Surface.ProjectToUV(g));
            if (f.Reversed) want = -want;

            if (Vector3d.Dot(tn, want) < 0) mb.AddTriangle(start + T[i], start + T[i + 2], start + T[i + 1]);
            else mb.AddTriangle(start + T[i], start + T[i + 1], start + T[i + 2]);
        }

        return true;
    }

    public static MeshData Tessellate(TSolid solid) => Tessellate(solid, facets: false);
    static readonly bool PolyDisplayOff = Environment.GetEnvironmentVariable("P3D_POLYDISPLAY") == "0";
    // g.11: тело с авторитетной многогранной формой показывается САМИМИ фасетами формы, без видимой сетки граней
    // (DisplaySnapshot): видимая сетка размечается по граням отдельно и у соседних граней не согласована — 24 из 600
    // случайных цепочек давали стороны кратности 3 (повтор треугольников). Показ = геометрия. Мутация P3D_POLYFACETS=0.
    static readonly bool PolyWindingOld = Environment.GetEnvironmentVariable("P3D_POLYWINDING") == "0";
    // g.11: сетка показа многогранного тела сводится без допуска сварки (только совпавшее во float и ближе 4 шагов float),
    // с разбором щепок шириной меньше шага float (MeshBuilder.GlobalSnapClean): сварка по допуску 1e-5 мм сводила
    // различные вершины точной формы и складывала сетку (серия 8, случай 82). Мутация P3D_POLYFINE=0.
    static readonly bool PolyFine = Environment.GetEnvironmentVariable("P3D_POLYFINE") != "0";
    static readonly bool PolyFacetsOnly = Environment.GetEnvironmentVariable("P3D_POLYFACETS") != "0";

    /// <summary>
    /// Сетка тела; facets — по треугольникам разбиения, а не по видимой сетке (этап 3): для
    /// проверок, которые спрашивают точное тело (точка внутри ли, объём среза). Видимая сетка
    /// ближе к настоящей поверхности и отходит от тела из хорд на сотые миллиметра — пробные
    /// точки проверки замысла среза (0.02 мм от граней) меняли ответ.
    /// </summary>
    /// <summary>Сколько раз звали гранение и сколько из них — повторно для того же тела.</summary>
    public static long Calls, Repeats;

    // g.11: ДВА ИСТОЧНИКА ПОКАЗА МНОГОГРАННОГО ТЕЛА — ВЫБОР ПО ГОДНОСТИ. Фасеты формы — по умолчанию; если в их сетке остались
    // дефекты связности во float, строится и видимая сетка граней (DisplaySnapshot), и берётся та, где дефектов меньше
    // (серия 9, случай 50: фасеты плоской грани x = 6.43 после второй булевой не согласованы с соседями — 45 сторон без
    // пары, видимая сетка годна). Мутация P3D_POLYCHOICE=0.
    static readonly bool PolyChoiceOff = Environment.GetEnvironmentVariable("P3D_POLYCHOICE") == "0";
    // g.11: ТРЕТИЙ ИСТОЧНИК — САМИ ТОЧНЫЕ МНОГОУГОЛЬНИКИ ТЕЛА (ExactAdapter.ToCsg: кольца без T-стыков, вершины реестра
    // общие): фасеты граней бывают переразмечены (плоская грань после восстановления дуг — брусок минус тор: 159 сторон без
    // пары и у фасет, и у видимой сетки). Порядок: многоугольники → фасеты → видимая сетка; берётся первая без дефектов,
    // иначе — с наименьшим их числом. Мутация P3D_POLYFROMEXACT=0.
    static readonly bool PolyFromExactOff = Environment.GetEnvironmentVariable("P3D_POLYFROMEXACT") == "0";
    public static MeshData Tessellate(TSolid solid, bool facets)
    {
        bool poly = !facets && !PolyDisplayOff && PolyFacetsOnly && solid.Exact != null && solid.ExactAuthoritative;
        if (!poly) return TessellateCore(solid, facets, 0);
        MeshData? best = null; int bestD = int.MaxValue;
        foreach (int src in PolyFromExactOff ? new[] { 0, 1 } : new[] { 2, 0, 1 })
        {
            if (src == 1 && PolyChoiceOff) continue;
            MeshData md;
            try { md = TessellateCore(solid, facets, src); }
            catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { continue; }
            if (md.Indices.Length == 0) continue;
            int d = md.BrokenFaces.Count > 0 ? int.MaxValue - 1 : MeshData.FloatDefects(md);   // повреждённая грань хуже любых дефектов
            if (d < bestD) { best = md; bestD = d; }
            if (d == 0 || (PolyChoiceOff && src == 0)) break;
        }
        return best ?? TessellateCore(solid, facets, 0);
    }

    static MeshData TessellateCore(TSolid solid, bool facets, int source)
    {
        bool forceSnapshot = source == 1, fromExact = source == 2;
        bool PolyFacetsOnly = Tessellator.PolyFacetsOnly && !forceSnapshot;
        Calls++;
        if (LastSolid != null && ReferenceEquals(LastSolid, solid) && LastFacets == facets) Repeats++;
        LastSolid = solid; LastFacets = facets;

        var md = new MeshData();
        var mb = new MeshBuilder();

        // ГРАНИ СЧИТАЮТСЯ ЗАРАНЕЕ И В НЕСКОЛЬКО ПОТОКОВ. Каждая грань независима, а сборка
        // общей сетки — нет: она ведёт общий список вершин и должна идти по порядку, иначе
        // номера вершин перемешаются. Поэтому в потоках делается дорогая часть (расчёт), а
        // сборка остаётся последовательной.
        // ТЕЛО ПО КВАДРИКЕ: рёбра размечаются один раз и берутся обеими гранями, иначе сетка не
        // сходится (измерено: 316 сторон без пары из 611).
        // РАЗМЕТКА С ОБЩИМИ РЁБРАМИ ИДЁТ ТЕМ ЖЕ ПУТЁМ ЗАВЕРШЕНИЯ, ЧТО И ОСТАЛЬНЫЕ. Прежде она
        // возвращала сетку сразу после заполнения и обходила всё, что делается дальше: диапазоны
        // граней, габарит, рёбра, приведение обхода. В сетке оказывались верные номера граней, но
        // ни одного диапазона, а габарит выходил нулевым — тело нельзя было ни выбрать по грани,
        // ни поместить в кадр.
        MeshBuilder? sharedParts = null;

        if (QMesher.Enabled && !facets && QMesher.Fits(solid))
        {
            var byFace = new Dictionary<TFace, MeshBuilder>();
            if (QMesher.TessellatePerFace(solid, byFace))
            {
                QMesher.Debug?.Invoke("общая разметка применена");
                sharedParts = new MeshBuilder();

                foreach (var f in solid.Faces)
                {
                    if (!byFace.TryGetValue(f, out var own)) continue;
                    mb.BeginFace(f.Id);
                    mb.Append(own);
                    mb.EndFace();
                }
            }
            else QMesher.Debug?.Invoke("общая разметка не применена");
        }

        var faces = sharedParts != null ? new List<TFace>() : solid.Faces.ToList();
        // g.05/g.11: ТЕЛО С АВТОРИТЕТНОЙ МНОГОГРАННОЙ ФОРМОЙ показывается своими треугольниками. Грань с вырезом по кривой
        // пересечения прежде переразмечалась по контурам (ExactFaceMesher), а соседние — нет: сетка многогранного результата
        // (брусок минус шар, касающийся ребра) — 201 сторона без пары при замкнутых фасетах; результат принимался.
        // Мутация P3D_POLYDISPLAY=0.
        bool polyAuth = !PolyDisplayOff && solid.Exact != null && solid.ExactAuthoritative;
        var parts = new MeshBuilder[faces.Count];
        var broke = new System.Collections.Concurrent.ConcurrentBag<(uint Face, string Why)>();

        void One(int i)
        {
            var own = new MeshBuilder();
            try
            {
                KernelContext.Checkpoint("mesh.face");
                FaultInjection.Hit("mesh.face");
                if (facets && faces[i].Surface is Surfaces.FacetedSurface fs) fs.TessellateFacetsFlat(faces[i], own);
                else if (polyAuth && PolyFacetsOnly && faces[i].Surface is Surfaces.FacetedSurface pf) pf.TessellateFacets(faces[i], own);
                else if (!polyAuth && NeedsExactMesher(faces[i]) && ExactInto(faces[i], own)) { }
                else if (faces[i].Surface is Surfaces.ExtrudedSurface exd) exd.TessellateDisplay(faces[i], own);   // KRN-09: одна полоса (P3D_DISPLAYONEROW)
                else faces[i].Surface.TessellateFace(faces[i], own);
            }
            catch (Exception ex) when (!KernelContext.IsCancellation(ex))
            {
                // ПОВРЕЖДЁННАЯ ГРАНЬ НАЗЫВАЕТСЯ, А НЕ ПРОПАДАЕТ МОЛЧА. Прежде здесь стоял пустой
                // перехват: сетка выходила без этой грани, а тело считалось построенным. Объём
                // при этом мог не измениться вовсе — у грани, лежащей в плоскости начала
                // координат, вклад в объём нулевой, — и проверка объёмом такую дыру не ловит.
                broke.Add((faces[i].Id, ex.Message));
            }

            parts[i] = own;
        }

        if (fromExact && polyAuth)
        {
            // многоугольники точной формы по меткам → грани (по исходной метке грани)
            var csg = Exact.ExactAdapter.ToCsg(solid.Exact!.Solid);
            var byTag = new Dictionary<uint, List<int>>();
            for (int i = 0; i < faces.Count; i++) if (faces[i].SourceTag != 0) { if (!byTag.TryGetValue(faces[i].SourceTag, out var l)) byTag[faces[i].SourceTag] = l = new(); l.Add(i); }
            for (int i = 0; i < faces.Count; i++)
            {
                parts[i] = new MeshBuilder();
                // тот же этап, что и разметка грани: прерывание и внесённый сбой видны и здесь (K03-FAULT-PART mesh.face)
                try { KernelContext.Checkpoint("mesh.face"); FaultInjection.Hit("mesh.face"); }
                catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { broke.Add((faces[i].Id, ex.Message)); }
            }
            // несколько граней с одной меткой (грань разделена на области) — многоугольник к той, чей треугольник разбиения ближе
            var cents = new Dictionary<int, List<Vector3d>>();
            List<Vector3d> CentsOf(int i) { if (!cents.TryGetValue(i, out var l)) { l = new(); if (faces[i].Surface is Surfaces.FacetedSurface fz) foreach (var (a, b, c) in fz.Facets.ReadView) l.Add((a + b + c) / 3); cents[i] = l; } return l; }
            foreach (var cp in csg)
            {
                if (!byTag.TryGetValue(cp.Tag, out var cand)) return new MeshData();      // метка без грани — источник непригоден
                int fi = cand[0];
                if (cand.Count > 1)
                {
                    var pc = cp.V.Aggregate(Vector3d.Zero, (a, b) => a + b) / cp.V.Count; double bd = double.MaxValue;
                    foreach (int ci in cand) foreach (var q in CentsOf(ci)) { double d = (q - pc).LengthSquared; if (d < bd) { bd = d; fi = ci; } }
                }
                var face = faces[fi]; var mbF = parts[fi];
                var srcS = face.Surface is Surfaces.FacetedSurface fsx ? fsx.Source : face.Surface;
                Vector3d Nrm(Vector3d p)
                {
                    if (srcS == null || srcS.IsPlanar) return cp.N;
                    try { var n = srcS.NormalAt(srcS.ProjectToUV(p)); if (n.LengthSquared < 1e-24) return cp.N; n = n.Normalized(); return Vector3d.Dot(n, cp.N) >= 0.866 ? n : Vector3d.Dot(-n, cp.N) >= 0.866 ? -n : cp.N; } catch { return cp.N; }
                }
                for (int k = 1; k + 1 < cp.V.Count; k++)
                {
                    var va = cp.V[0]; var vb = cp.V[k]; var vc = cp.V[k + 1];
                    if (va == vb || vb == vc || vc == va) continue;
                    mbF.AddTriangle(mbF.AddVertex(va, Nrm(va)), mbF.AddVertex(vb, Nrm(vb)), mbF.AddVertex(vc, Nrm(vc)));
                }
            }
        }
        else if (Threads > 1 && Environment.ProcessorCount > 1 && faces.Count > 1)
            System.Threading.Tasks.Parallel.For(0, faces.Count,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Threads }, One);
        else
            for (int i = 0; i < faces.Count; i++) One(i);

        for (int i = 0; i < faces.Count; i++)
        {
            mb.BeginFace(faces[i].Id);
            mb.Append(parts[i]);
            mb.EndFace();
        }
        // ПОКАЗНАЯ И РАСЧЁТНАЯ СЕТКА ЧИСТИТСЯ, А СЕТКА ДЛЯ БУЛЕВОЙ — НЕТ. Схлопнувшиеся
        // треугольники появляются при сведении точек соседних граней; для показа и расчёта они
        // вредны, а булева читает те же треугольники и без них распадается — измерено, двадцать
        // одна проверка ядра. Поэтому чистка стоит здесь, а не в самой сборке.
        mb.FillInto(md, weld: true, fine: polyAuth && PolyFacetsOnly && PolyFine);

        // ОХВАТ СЕТКИ: 89 тел из ста вместо 90. Проверено, что ни общая разметка (P3D_QMESH=0),
        // ни запас габарита тут ни при чём — число то же. Значит, одно тело изменилось от
        // согласованного завершения разметки: прежде её путь обходил приведение обхода и
        // знаковый объём, и у одного тела это, видимо, скрывало расхождение. Разбирать это надо
        // отдельно; выдавать сетку в обход завершения нельзя в любом случае.
        //
        // Список граней, которые не размечены: он идёт с сеткой и не даёт выдать её за целую.
        foreach (var (face, why) in broke) md.BrokenFaces.Add((face, why));

        // Ориентация приводится в порядок в два шага, и порядок важен.
        //
        // 1. Намотка каждого треугольника согласуется с его собственной
        //    нормалью. Это локальная проверка: она чинит грань, у которой
        //    обход и нормаль разошлись, не трогая соседей.
        //
        // 2. Только после этого имеет смысл знаковый объём. Он ловит случай,
        //    когда вся оболочка вывернута целиком, и разворачивает её.
        //
        // Раньше был только шаг 2, и это оказалось ловушкой: при ЧАСТИЧНО
        // неверной оболочке (боковые грани намотаны внутрь, крышки наружу)
        // объём выходил отрицательным, и «защита» выворачивала правильные
        // крышки вместо того, чтобы починить боковые грани.
        // g.11: у тела с авторитетной многогранной формой обход задан самой формой (многоугольники точного ядра ориентированы);
        // «исправление по нормалям» переворачивало участки граней с несогласованной гладкой нормалью (серия 9, случай 65:
        // 141 сторона с одинаковым обходом). Мутация P3D_POLYWINDING=0.
        if (!(polyAuth && PolyFacetsOnly && PolyFine && !PolyWindingOld)) FixWindingByNormals(md);

        double vol = MassProps.SignedVolume(md);
        if (vol < 0) FlipAll(md);

        var b = Bounds3.Empty;
        foreach (var v in md.Vertices) b.Add(new Vector3d(v.P.X, v.P.Y, v.P.Z));

        var tmp = new List<Vector3d>();
        foreach (var e in solid.Edges)
        {
            if (e.Hidden) continue;           // гладкий стык: в каркасе не рисуется

            tmp.Clear();
            e.Curve.Tessellate(tmp, true);
            if (tmp.Count < 2) continue;
            var arr = new Vector3[tmp.Count];
            for (int i = 0; i < tmp.Count; i++)
            {
                arr[i] = new Vector3((float)tmp[i].X, (float)tmp[i].Y, (float)tmp[i].Z);
                b.Add(tmp[i]);
            }
            md.EdgePolylines.Add((e.Id, arr));
        }

        // Вершины — только на видимых рёбрах.
        //
        // Тело может нести вершины, которых на нём не видно: у куба это
        // центры крышек и стыки секторов, оставшиеся от разбиения крышки
        // на четыре куска. Рёбра там спрятаны как гладкие, а точки
        // высыпались бы поверх ровных граней.
        var shown = new HashSet<TVertex>();
        foreach (var e in solid.Edges)
        {
            if (e.Hidden) continue;
            shown.Add(e.V0);
            shown.Add(e.V1);
        }

        foreach (var v in solid.Vertices)
        {
            if (!shown.Contains(v)) continue;
            md.Points.Add((v.Id, new Vector3((float)v.P.X, (float)v.P.Y, (float)v.P.Z)));
        }

        md.Bounds = b;
        return md;
    }

    /// <summary>
    /// Разворачивает треугольники, у которых обход вершин противоречит
    /// их нормали. Нормаль здесь — источник истины: её задаёт поверхность,
    /// зная, где у грани наружная сторона.
    /// </summary>
    private static void FixWindingByNormals(MeshData md)
    {
        var idx = md.Indices;
        var vt = md.Vertices;

        // K01. ОБХОД ПО НОРМАЛИ РЕШАЕТСЯ ТОЛЬКО ТАМ, ГДЕ ОРИЕНТАЦИЮ ТРЕУГОЛЬНИКА МОЖНО ПРОЧЕСТЬ
        // ПО FLOAT-КООРДИНАТАМ. У треугольника со сторонами в одну-две единицы последнего
        // разряда float знак векторного произведения задаёт округление, а не геометрия: на
        // сферическом вырезе два таких треугольника переворачивались «по нормали» и получали
        // обход, совпадающий с соседями (шесть сторон без встречного обхода). Такие
        // треугольники ориентируются по соседям той же грани — по связности, а не по шуму.
        var unreliable = new List<int>();

        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            int i0 = idx[i], i1 = idx[i + 1], i2 = idx[i + 2];

            Vector3 e1 = vt[i1].P - vt[i0].P, e2 = vt[i2].P - vt[i0].P;
            Vector3 geom = Vector3.Cross(e1, e2);
            if (geom.LengthSquared < 1e-20f) { unreliable.Add(i); continue; }   // вырожденный треугольник

            Vector3 shading = vt[i0].N + vt[i1].N + vt[i2].N;
            if (shading.LengthSquared < 1e-20f) continue;   // нормали не заданы

            // Погрешность векторного произведения от округления координат в float.
            float scale = MathF.Max(MathF.Max(MaxAbs(vt[i0].P), MaxAbs(vt[i1].P)), MaxAbs(vt[i2].P));
            float edge = MathF.Max(MathF.Max(e1.Length, e2.Length), (vt[i2].P - vt[i1].P).Length);
            if (geom.Length <= 16 * 1.2e-7f * scale * edge) { unreliable.Add(i); continue; }

            if (Vector3.Dot(geom, shading) < 0)
                (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
        }

        if (unreliable.Count == 0) return;

        // Направленные стороны надёжных треугольников. Номера вершин у разных граней разные,
        // поэтому соседство ищется только внутри грани.
        var bad = new HashSet<int>(unreliable);
        var directed = new HashSet<(int, int)>();
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            if (bad.Contains(i)) continue;
            directed.Add((idx[i], idx[i + 1])); directed.Add((idx[i + 1], idx[i + 2])); directed.Add((idx[i + 2], idx[i]));
        }

        for (int pass = 0; pass < 3; pass++)
        {
            bool any = false;
            foreach (int i in unreliable)
            {
                int a = idx[i], b = idx[i + 1], c = idx[i + 2];
                // Сосед с той же направленной стороной — обход противоречит ему; со встречной — согласован.
                int same = 0, opposite = 0;
                foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                {
                    if (directed.Contains((u, v))) same++;
                    if (directed.Contains((v, u))) opposite++;
                }
                if (same > opposite)
                {
                    (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
                    (b, c) = (c, b);
                    any = true;
                }
                if (same != opposite)
                {
                    directed.Add((a, b)); directed.Add((b, c)); directed.Add((c, a));
                }
            }
            if (!any) break;
        }
    }

    private static float MaxAbs(Vector3 p) => MathF.Max(MathF.Abs(p.X), MathF.Max(MathF.Abs(p.Y), MathF.Abs(p.Z)));

    private static void FlipAll(MeshData md)
    {
        for (int i = 0; i < md.Indices.Length; i += 3)
            (md.Indices[i + 1], md.Indices[i + 2]) = (md.Indices[i + 2], md.Indices[i + 1]);
        for (int i = 0; i < md.Vertices.Length; i++)
            md.Vertices[i].N = -md.Vertices[i].N;
    }
}

/// <summary>Масс-инерционные характеристики по треугольной оболочке (теорема о дивергенции).</summary>
public static class MassProps
{
    /// <summary>
    /// KRN-02: опорная точка интегрирования — первая вершина сетки. Объём от неё не зависит (оболочка замкнута), а в
    /// абсолютных координатах во float слагаемые ~|x|³: деталь 60 мм, сдвинутая на 1e5 мм, получала объём со случайным
    /// знаком, и тесселятор выворачивал правильную сетку (FlipAll) — валидатор сообщал «inside-out».
    /// </summary>
    static Vector3d RefPoint(MeshData md) =>
        md.Vertices.Length == 0 ? Vector3d.Zero : new Vector3d(md.Vertices[0].P.X, md.Vertices[0].P.Y, md.Vertices[0].P.Z);
    static Vector3d At(MeshData md, int i, Vector3d o) { var p = md.Vertices[md.Indices[i]].P; return new Vector3d(p.X, p.Y, p.Z) - o; }

    public static double SignedVolume(MeshData md)
    {
        double v = 0; var o = RefPoint(md);
        for (int i = 0; i + 2 < md.Indices.Length; i += 3)
        {
            Vector3d a = At(md, i, o), b = At(md, i + 1, o), c = At(md, i + 2, o);
            v += Vector3d.Dot(a, Vector3d.Cross(b, c));
        }
        return v / 6.0;
    }

    public static double Volume(MeshData md) => Math.Abs(SignedVolume(md));

    /// <summary>
    /// МОМЕНТЫ ИНЕРЦИИ относительно центра тяжести, в кг·мм². Плотность задаётся в кг/м³, как в
    /// справочниках, и приводится к миллиметрам здесь же, чтобы не ошибиться множителем в
    /// вызывающем коде.
    ///
    /// Возвращается симметричная матрица: диагональ — Ixx, Iyy, Izz, вне её — произведения
    /// инерции Ixy, Ixz, Iyz.
    /// </summary>
    public static (double Ixx, double Iyy, double Izz, double Ixy, double Ixz, double Iyz)
        Inertia(MeshData md, double densityKgM3)
    {
        // Интегралы по объёму считаются по тетраэдрам: для каждого берётся его вклад в
        // произведения координат, затем всё сносится в центр тяжести.
        var idx = md.Indices;
        var o = RefPoint(md);                 // KRN-02: от опорной точки (перенос Штейнера — без потери точности вдали)
        Vector3d P(int i) => At(md, i, o);

        double vol = 0;
        double xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
        Vector3d acc = Vector3d.Zero;

        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            Vector3d a = P(i), b = P(i + 1), c = P(i + 2);
            double v = Vector3d.Dot(a, Vector3d.Cross(b, c)) / 6;
            vol += v;
            acc += (a + b + c) * (v / 4);

            // Средние произведения координат по тетраэдру с вершиной в начале координат.
            double S(double a1, double b1, double c1) => a1 + b1 + c1;
            double Sq(double a1, double b1, double c1) => a1 * a1 + b1 * b1 + c1 * c1 + a1 * b1 + a1 * c1 + b1 * c1;
            double Pr(double a1, double b1, double c1, double a2, double b2, double c2) =>
                2 * (a1 * a2 + b1 * b2 + c1 * c2) + a1 * b2 + b1 * a2 + a1 * c2 + c1 * a2 + b1 * c2 + c1 * b2;

            xx += v * Sq(a.X, b.X, c.X) / 10;
            yy += v * Sq(a.Y, b.Y, c.Y) / 10;
            zz += v * Sq(a.Z, b.Z, c.Z) / 10;
            xy += v * Pr(a.X, b.X, c.X, a.Y, b.Y, c.Y) / 20;
            xz += v * Pr(a.X, b.X, c.X, a.Z, b.Z, c.Z) / 20;
            yz += v * Pr(a.Y, b.Y, c.Y, a.Z, b.Z, c.Z) / 20;
            _ = S(0, 0, 0);
        }

        if (Math.Abs(vol) < 1e-18) return (0, 0, 0, 0, 0, 0);

        Vector3d g = acc / vol;
        double volAbs = Math.Abs(vol);

        // Перенос в центр тяжести (теорема Гюйгенса — Штейнера).
        xx = Math.Abs(xx) - volAbs * g.X * g.X;
        yy = Math.Abs(yy) - volAbs * g.Y * g.Y;
        zz = Math.Abs(zz) - volAbs * g.Z * g.Z;
        xy = xy * Math.Sign(vol) - volAbs * g.X * g.Y;
        xz = xz * Math.Sign(vol) - volAbs * g.X * g.Z;
        yz = yz * Math.Sign(vol) - volAbs * g.Y * g.Z;

        // Плотность из кг/м³ в кг/мм³; момент инерции вокруг оси — сумма двух других интегралов.
        double k = densityKgM3 * 1e-9;
        return ((yy + zz) * k, (xx + zz) * k, (xx + yy) * k, -xy * k, -xz * k, -yz * k);
    }

    public static double Area(MeshData md)
    {
        double s = 0;
        var idx = md.Indices;
        var vt = md.Vertices;
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            Vector3 a = vt[idx[i]].P, b = vt[idx[i + 1]].P, c = vt[idx[i + 2]].P;
            s += Vector3.Cross(b - a, c - a).Length * 0.5;
        }
        return s;
    }

    public static Vector3d Centroid(MeshData md)
    {
        double vol = 0;
        Vector3d c = Vector3d.Zero;
        var o = RefPoint(md);                 // KRN-02: от опорной точки
        for (int i = 0; i + 2 < md.Indices.Length; i += 3)
        {
            Vector3d a = At(md, i, o), b = At(md, i + 1, o), d = At(md, i + 2, o);
            double t = Vector3d.Dot(a, Vector3d.Cross(b, d)) / 6.0;
            vol += t;
            c += (a + b + d) * (t / 4.0);
        }
        return Math.Abs(vol) < 1e-15 ? Vector3d.Zero : o + c / vol;
    }

    /// <summary>Площадь одной грани по её диапазону в сетке.</summary>
    public static double FaceArea(MeshData md, uint faceId)
    {
        if (!md.FaceRanges.TryGetValue(faceId, out var r)) return 0;
        double s = 0;
        for (int i = r.IndexStart; i < r.IndexStart + r.IndexCount; i += 3)
        {
            Vector3 a = md.Vertices[md.Indices[i]].P;
            Vector3 b = md.Vertices[md.Indices[i + 1]].P;
            Vector3 c = md.Vertices[md.Indices[i + 2]].P;
            s += Vector3.Cross(b - a, c - a).Length * 0.5;
        }
        return s;
    }
}
