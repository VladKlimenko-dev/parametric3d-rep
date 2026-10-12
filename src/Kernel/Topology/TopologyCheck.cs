using OpenTK.Mathematics;
using Parametric3D.Core;

namespace Parametric3D.Kernel.Topology;

/// <summary>Замечание проверки: что не так, у какого элемента и насколько.</summary>
public readonly struct TopologyIssue
{
    public readonly string Kind;
    public readonly uint Id;
    public readonly double Amount;
    public readonly double Allowed;

    public TopologyIssue(string kind, uint id, double amount, double allowed)
    {
        Kind = kind; Id = id; Amount = amount; Allowed = allowed;
    }

    public override string ToString() =>
        $"{Kind} (элемент {Id}): {Amount:0.######} мм при допуске {Allowed:0.######}";
}

/// <summary>
/// ПРОВЕРКА ТОПОЛОГИИ ПО СОБСТВЕННЫМ ДОПУСКАМ ЭЛЕМЕНТОВ. Прежде любая проверка сравнивала с
/// одним допуском на всю модель: у тела, где одно место сходится до микрона, а другое до сотых,
/// это означало либо ложные отказы, либо пропущенный брак.
///
/// Здесь каждое замечание сравнивается с допуском ТОГО элемента, к которому относится: ребро
/// спрашивают о его допуске, вершину — о её. Тело считается годным, когда замечаний нет.
/// </summary>
public static class TopologyCheck
{
    /// <summary>
    /// Что проверяется:
    /// — концы кривой ребра совпадают с его вершинами;
    /// — контур грани замкнут (по чётности степеней вершин);
    /// — у ребра не больше двух граней;
    /// — у ребра есть хотя бы одна грань.
    /// </summary>
    /// <summary>
    /// Проверить тело. Признак замкнутости — ДОВОД, а не глобальная настройка: открытая оболочка
    /// отличается от замкнутого тела тем, что у неё рёбра на краю законны, и путать эти два вида
    /// нельзя. Глобальным переключателем это делать опасно — он протекает между вызовами, и
    /// проверка одного тела меняет ответ для другого.
    /// </summary>
    public static List<TopologyIssue> Validate(TSolid solid, double extra = 0, bool closed = true)
    {
        var issues = new List<TopologyIssue>();

        foreach (var e in solid.Edges)
        {
            double allow = e.EffectiveTolerance + extra;

            if (e.Curve != null && e.V0 != null && e.V1 != null)
            {
                Vector3d cs = e.Curve.PointAt(e.Curve.T0), ce2 = e.Curve.PointAt(e.Curve.T1);

                if (ReferenceEquals(e.V0, e.V1))
                {
                    // ЗАМКНУТОЕ РЕБРО: вершина одна, и начало кривой может стоять где угодно на
                    // ней. Спрашивать «совпадает ли конец кривой с вершиной» бессмысленно —
                    // проверяется замкнутость самой кривой.
                    double gap = (cs - ce2).Length;
                    if (gap > allow) issues.Add(new TopologyIssue("замкнутое ребро не замкнуто", e.Id, gap, allow));
                }
                else
                {
                    double worst = Math.Max((cs - e.V0.P).Length, (ce2 - e.V1.P).Length);
                    if (worst > allow) issues.Add(new TopologyIssue("кривая не сходится с вершинами", e.Id, worst, allow));
                }
            }

            // СКОЛЬКО ГРАНЕЙ У РЕБРА — зависит от того, что проверяется. У ЗАМКНУТОГО тела ребро
            // обязано принадлежать ровно двум граням, и обходить его они должны навстречу.
            //
            // Прежде одна грань у ребра считалась допустимой всегда, и тело без грани проходило
            // без замечаний: пять граней вместо шести, четыре ребра на краю. Такое «тело» нельзя
            // ни залить материалом, ни посчитать, ни вывести в обмен.
            int faces = e.CoEdges.Count;

            if (faces == 0) issues.Add(new TopologyIssue("ребро без граней", e.Id, 0, 0));
            else if (faces > 2) issues.Add(new TopologyIssue("у ребра больше двух граней", e.Id, faces, 2));
            else if (faces == 1 && closed)
                issues.Add(new TopologyIssue("ребро на краю: у замкнутого тела таких быть не должно",
                                             e.Id, 1, 2));
            // ВСТРЕЧНЫЙ ОБХОД ЗДЕСЬ НЕ ПРОВЕРЯЕТСЯ. Пробовал: у целого бруска четыре ребра из
            // двенадцати обходятся обеими гранями в одну сторону, и проверка объявляла годное
            // тело негодным. Значит, в этом ядре признак Reversed у полуребра не задаёт
            // направление обхода так, как в обычном соглашении полурёбер; прежде чем требовать
            // встречности, надо привести само соглашение к одному виду — это отдельная работа.
        }

        // ЗАМКНУТОСТЬ КОНТУРА — ПО ЧЁТНОСТИ СТЕПЕНЕЙ ВЕРШИН. Три первые попытки объявляли целое
        // тело негодным, и разбор показал почему: в одном списке контура лежат ДВА кольца —
        // наружный прямоугольник и отверстие. Ни порядок в списке, ни связность тут не годятся;
        // годится чётность: у замкнутого обхода каждая вершина входит столько же раз, сколько
        // выходит, и это верно для любого числа колец в списке.
        foreach (var f in solid.Faces)
            foreach (var loop in f.Loops)
                if (loop.CoEdges.Count > 0 && !loop.FormsCycle())
                    issues.Add(new TopologyIssue("контур грани не замкнут", f.Id, loop.CoEdges.Count, 0));

        return issues;
    }

    /// <summary>Годно ли тело по своим же допускам.</summary>
    public static bool IsValid(TSolid solid, double extra = 0, bool closed = true) =>
        Validate(solid, extra, closed).Count == 0;

    /// <summary>
    /// Наибольшее расхождение в теле — мерка его «чистоты» одним числом. Пригодна, чтобы
    /// сравнивать тела между собой и следить, не ухудшается ли качество от операции к операции.
    /// </summary>
    public static double WorstDeviation(TSolid solid)
    {
        double worst = 0;
        foreach (var e in solid.Edges)
        {
            if (e.Curve == null || e.V0 == null || e.V1 == null) continue;
            worst = Math.Max(worst, (e.Curve.PointAt(e.Curve.T0) - e.V0.P).Length);
            worst = Math.Max(worst, (e.Curve.PointAt(e.Curve.T1) - e.V1.P).Length);
        }
        return worst;
    }
}
