using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// Многоугольник для булевых операций: контур, его плоскость и метка
/// исходной грани.
///
/// Метка переносится через все разрезы, поэтому в результате известно,
/// из какой грани какого тела получился каждый кусок. Без неё результат
/// был бы безликой сеткой: нельзя было бы ни сохранить аналитические
/// поверхности, ни восстановить рёбра по стыкам граней.
/// </summary>
public sealed class CsgPolygon
{
    public List<Vector3d> V;
    public Vector3d N;
    public double W;
    public uint Tag;

    public CsgPolygon(List<Vector3d> v, uint tag)
    {
        V = v;
        Tag = tag;
        N = Vector3d.Cross(v[1] - v[0], v[2] - v[0]);
        double len = N.Length;
        N = len > 1e-300 ? N / len : new Vector3d(0, 0, 1);
        W = Vector3d.Dot(N, v[0]);
    }

    private CsgPolygon(List<Vector3d> v, uint tag, Vector3d n, double w)
    {
        V = v; Tag = tag; N = n; W = w;
    }

    /// <summary>k113: исходный многоугольник, от которого отрезан этот кусок (null — сам исходный): нормали сетки показа.</summary>
    public CsgPolygon? Origin;

    public CsgPolygon Clone() => new(new List<Vector3d>(V), Tag, N, W) { Origin = Origin ?? this };

    public CsgPolygon Split(List<Vector3d> v) => new(v, Tag, N, W) { Origin = Origin ?? this };

    public void Flip()
    {
        V.Reverse();
        N = -N;
        W = -W;
    }
}

/// <summary>
/// Булевы операции над телами методом двоичного разбиения пространства
/// (BSP), классический приём конструктивной геометрии.
///
/// ПОЧЕМУ ИМЕННО ТАК. Точное булево над аналитическими поверхностями —
/// это пересечение поверхностей, разрезание рёбер, перестройка контуров
/// граней и разбор десятков особых случаев. Такой код нельзя написать
/// «на глаз»: он либо проверен на сотнях моделей, либо не работает.
///
/// Разбиение пространства работает с гранями, разбитыми на плоские
/// многоугольники. Оно проще, устойчиво к совпадающим плоскостям
/// и даёт верный результат — что и проверено на объёмах для двенадцати
/// расположений тел, включая соприкосновение гранями и сквозной паз.
///
/// Плата за это — результат ГРАНЁНЫЙ: кривые поверхности после операции
/// остаются набором плоских кусков. Метки исходных граней позволяют
/// сохранить аналитическую поверхность у граней, которые операция
/// не разрезала, поэтому плоскую грань после выреза по-прежнему можно
/// взять как базис эскиза.
/// </summary>
public static class Csg
{
    /// <summary>
    /// Допуск отнесения точки к плоскости.
    ///
    /// Величина абсолютная, в единицах модели (миллиметрах). Так и должно
    /// быть: разбиение сравнивает РАССТОЯНИЯ до плоскости, и относительный
    /// допуск здесь смысла не имеет.
    /// </summary>
    public const double Eps = 1e-7;

    private const int Coplanar = 0, Front = 1, Back = 2, Spanning = 3;

    public static List<CsgPolygon> Unite(List<CsgPolygon> a, List<CsgPolygon> b)
    {
        var na = new Node(Clone(a));
        var nb = new Node(Clone(b));

        na.ClipTo(nb);
        nb.ClipTo(na);

        // Совпадающие участки границы убираются выворотом: после него
        // внутренние стенки обоих тел взаимно отсекаются, и в результате
        // остаётся только внешняя оболочка.
        nb.Invert();
        nb.ClipTo(na);
        nb.Invert();

        na.Build(nb.AllPolygons());
        return na.AllPolygons();
    }

    public static List<CsgPolygon> Subtract(List<CsgPolygon> a, List<CsgPolygon> b)
    {
        var na = new Node(Clone(a));
        var nb = new Node(Clone(b));

        na.Invert();
        na.ClipTo(nb);
        nb.ClipTo(na);
        nb.Invert();
        nb.ClipTo(na);
        nb.Invert();

        na.Build(nb.AllPolygons());
        na.Invert();
        return na.AllPolygons();
    }

    public static List<CsgPolygon> Intersect(List<CsgPolygon> a, List<CsgPolygon> b)
    {
        var na = new Node(Clone(a));
        var nb = new Node(Clone(b));

        na.Invert();
        nb.ClipTo(na);
        nb.Invert();
        na.ClipTo(nb);
        nb.ClipTo(na);

        na.Build(nb.AllPolygons());
        na.Invert();
        return na.AllPolygons();
    }

    private static List<CsgPolygon> Clone(List<CsgPolygon> src)
    {
        var r = new List<CsgPolygon>(src.Count);
        foreach (var p in src) r.Add(p.Clone());
        return r;
    }

    /// <summary>Знаковый объём набора многоугольников. Для проверок.</summary>
    public static double Volume(List<CsgPolygon> polys)
    {
        double v = 0;
        foreach (var p in polys)
            for (int i = 1; i < p.V.Count - 1; i++)
                v += Vector3d.Dot(p.V[0], Vector3d.Cross(p.V[i], p.V[i + 1])) / 6.0;
        return v;
    }

    // ------------------------------------------------------------ разбиение

    /// <summary>
    /// Разрезает многоугольник плоскостью, раскладывая куски по четырём
    /// приёмникам.
    ///
    /// Совпадающие с плоскостью многоугольники идут в отдельные приёмники
    /// по направлению нормали — именно на них держится обработка тел,
    /// соприкасающихся гранью. Свали их вместе с передними или задними,
    /// и общая стенка либо исчезнет, либо останется вдвойне.
    /// </summary>
    private static void Split(Vector3d pn, double pw, CsgPolygon poly,
                              List<CsgPolygon> coFront, List<CsgPolygon> coBack,
                              List<CsgPolygon> front, List<CsgPolygon> back)
    {
        int polyType = 0;
        var types = new int[poly.V.Count];

        for (int i = 0; i < poly.V.Count; i++)
        {
            double t = Vector3d.Dot(pn, poly.V[i]) - pw;
            int type = t < -Eps ? Back : (t > Eps ? Front : Coplanar);
            polyType |= type;
            types[i] = type;
        }

        switch (polyType)
        {
            case Coplanar:
                (Vector3d.Dot(pn, poly.N) > 0 ? coFront : coBack).Add(poly);
                return;

            case Front:
                front.Add(poly);
                return;

            case Back:
                back.Add(poly);
                return;
        }

        var f = new List<Vector3d>();
        var b = new List<Vector3d>();
        int n = poly.V.Count;

        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            int ti = types[i], tj = types[j];
            Vector3d vi = poly.V[i], vj = poly.V[j];

            if (ti != Back) f.Add(vi);
            if (ti != Front) b.Add(vi);

            if ((ti | tj) == Spanning)
            {
                double denom = Vector3d.Dot(pn, vj - vi);
                if (Math.Abs(denom) < 1e-300) continue;

                double t = (pw - Vector3d.Dot(pn, vi)) / denom;
                Vector3d v = vi + (vj - vi) * t;
                f.Add(v);
                b.Add(v);
            }
        }

        if (f.Count >= 3) front.Add(poly.Split(f));
        if (b.Count >= 3) back.Add(poly.Split(b));
    }

    /// <summary>Узел дерева разбиения: плоскость, свои многоугольники и две ветви.</summary>
    private sealed class Node
    {
        private bool _hasPlane;
        private Vector3d _n;
        private double _w;
        private Node? _front, _back;
        private readonly List<CsgPolygon> _polys = new();

        public Node() { }

        public Node(List<CsgPolygon> polys) => Build(polys);

        public void Invert()
        {
            foreach (var p in _polys) p.Flip();

            if (_hasPlane) { _n = -_n; _w = -_w; }

            _front?.Invert();
            _back?.Invert();

            (_front, _back) = (_back, _front);
        }

        /// <summary>Оставляет от набора только то, что лежит ВНЕ этого тела.</summary>
        public List<CsgPolygon> ClipPolygons(List<CsgPolygon> polys)
        {
            if (!_hasPlane) return new List<CsgPolygon>(polys);

            var front = new List<CsgPolygon>();
            var back = new List<CsgPolygon>();

            foreach (var p in polys) Split(_n, _w, p, front, back, front, back);

            if (_front != null) front = _front.ClipPolygons(front);

            // Нет задней ветви — значит за плоскостью пусто, и всё, что там
            // оказалось, находится внутри тела. Оно отбрасывается.
            back = _back != null ? _back.ClipPolygons(back) : new List<CsgPolygon>();

            front.AddRange(back);
            return front;
        }

        public void ClipTo(Node other)
        {
            var clipped = other.ClipPolygons(_polys);
            _polys.Clear();
            _polys.AddRange(clipped);

            _front?.ClipTo(other);
            _back?.ClipTo(other);
        }

        public List<CsgPolygon> AllPolygons()
        {
            var r = new List<CsgPolygon>(_polys);
            if (_front != null) r.AddRange(_front.AllPolygons());
            if (_back != null) r.AddRange(_back.AllPolygons());
            return r;
        }

        public void Build(List<CsgPolygon> polys)
        {
            if (polys.Count == 0) return;

            if (!_hasPlane)
            {
                _hasPlane = true;
                _n = polys[0].N;
                _w = polys[0].W;
            }

            var front = new List<CsgPolygon>();
            var back = new List<CsgPolygon>();

            foreach (var p in polys) Split(_n, _w, p, _polys, _polys, front, back);

            if (front.Count > 0)
            {
                _front ??= new Node();
                _front.Build(front);
            }

            if (back.Count > 0)
            {
                _back ??= new Node();
                _back.Build(back);
            }
        }
    }
}
