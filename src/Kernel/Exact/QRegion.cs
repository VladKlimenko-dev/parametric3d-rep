using System.Numerics;
using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Exact;

/// <summary>
/// ОБЛАСТЬ ТОЧНОГО ЯДРА ОБЩЕГО ВИДА: объединение выпуклых кусков, у каждого свои вырезы.
///
/// Выпуклой части хватало, чтобы вырезать цилиндр из бруска, но настоящая деталь невыпукла:
/// уголок, ступенька, ребро жёсткости. Здесь тело описывается объединением выпуклых кусков —
/// этого достаточно для любого тела, которое строит наше дерево операций, и при этом сохраняется
/// главное: ответ «где точка» точен, потому что каждый кусок — пересечение полупространств, а
/// они проверяются целой арифметикой.
///
/// Почему не дерево ветвления. Дерево нужно, когда тело задано россыпью граней и надо
/// восстановить, что внутри. У нас тело СТРОИТСЯ операциями, и на каждом шаге известно, что
/// прибавляется и что убавляется, — объединение кусков это выражает прямо и без догадок.
/// </summary>
public sealed class QRegion
{
    /// <summary>Выпуклый кусок: пересечение полупространств минус вырезы.</summary>
    public sealed class Piece
    {
        public readonly List<ExactSurfaceRef> Inside = new();
        public readonly List<List<ExactSurfaceRef>> Holes = new();
    }

    public readonly PlaneRegistry Planes;
    public readonly QuadricRegistry Quadrics;
    public readonly List<Piece> Pieces = new();

    private readonly QSolidTester _tester;

    public QRegion(PlaneRegistry planes, QuadricRegistry quadrics)
    {
        Planes = planes;
        Quadrics = quadrics;
        _tester = new QSolidTester(planes, quadrics);
    }

    public bool HasQuadric =>
        Pieces.Any(p => p.Inside.Any(s => s.IsQuadric) || p.Holes.Any(h => h.Any(s => s.IsQuadric)));

    /// <summary>Добавить выпуклое тело как ещё один кусок объединения.</summary>
    public QRegion Add(QSolid body)
    {
        var piece = new Piece();
        foreach (var f in body.Faces) piece.Inside.Add(f.Support);
        Pieces.Add(piece);
        return this;
    }

    /// <summary>
    /// Вычесть выпуклое тело ИЗ ВСЕХ кусков. Вычитание применяется к каждому: убрать материал
    /// нужно всюду, где он есть, а не только в последнем добавленном.
    /// </summary>
    public QRegion Subtract(QSolid tool)
    {
        var hole = tool.Faces.Select(f => f.Support).ToList();
        foreach (var p in Pieces) p.Holes.Add(hole);
        return this;
    }

    /// <summary>
    /// Где точка: −1 внутри, 0 на поверхности, +1 снаружи. Точка внутри области, если она внутри
    /// ХОТЯ БЫ ОДНОГО куска; на поверхности — если лежит на границе куска и при этом не внутри
    /// другого (иначе граница окажется внутри материала и видимой поверхностью не является).
    /// </summary>
    public int Where(BigInteger x, BigInteger y, BigInteger z, BigInteger w)
    {
        bool onSurface = false;

        foreach (var piece in Pieces)
        {
            int side = WhereIn(piece, x, y, z, w);
            if (side < 0) return -1;                    // строго внутри куска — внутри тела
            if (side == 0) onSurface = true;
        }

        return onSurface ? 0 : 1;
    }

    public int Where(Vector3d p, long scale = 1_000_000)
    {
        BigInteger X = new(Math.Round(p.X * scale));
        BigInteger Y = new(Math.Round(p.Y * scale));
        BigInteger Z = new(Math.Round(p.Z * scale));
        return Where(X, Y, Z, new BigInteger(scale));
    }

    /// <summary>Где точка относительно одного куска.</summary>
    private int WhereIn(Piece piece, BigInteger x, BigInteger y, BigInteger z, BigInteger w)
    {
        bool onSurface = false;

        foreach (var s in piece.Inside)
        {
            int side = _tester.Side(x, y, z, w, s);
            if (side > 0) return 1;
            if (side == 0) onSurface = true;
        }

        foreach (var hole in piece.Holes)
        {
            bool allInside = true, touching = false;
            foreach (var s in hole)
            {
                int side = _tester.Side(x, y, z, w, s);
                if (side > 0) { allInside = false; break; }
                if (side == 0) touching = true;
            }

            if (!allInside) continue;
            if (touching) { onSurface = true; continue; }
            return 1;
        }

        return onSurface ? 0 : -1;
    }

    /// <summary>
    /// ИЗВЛЕЧЕНИЕ ГРАНИЦЫ: описание («внутри этих поверхностей, кроме этих вырезов») переводится
    /// обратно в грани. Без этого тело нельзя ни показать, ни сохранить, ни подать в следующую
    /// операцию — оно существовало бы только как ответ на вопрос «где точка».
    ///
    /// Грань получается из каждой поверхности куска: она сама несущая, а ограничивают её
    /// ОСТАЛЬНЫЕ поверхности того же куска и стенки его вырезов (вывернутые: материал снаружи
    /// выреза). Стенка выреза тоже даёт грань — ограниченную поверхностями куска.
    ///
    /// ОГОВОРКА, ВАЖНАЯ ДЛЯ ЧЕСТНОСТИ: грани не обрезаются по СОСЕДНИМ КУСКАМ. У двух брусков
    /// встык общая грань попадёт в границу дважды, хотя поверхностью тела не является. Убрать
    /// это можно, проверяя каждую грань на попадание внутрь других кусков, — но проверка нужна
    /// не в точке, а по области, и это отдельная работа. Пока граница верна для тела из одного
    /// куска с вырезами — то есть для всего, что строит операция «отверстие».
    /// </summary>
    public List<QPolygon> Boundary()
    {
        var faces = new List<QPolygon>();

        foreach (var piece in Pieces)
        {
            // Поверхности самого куска.
            for (int i = 0; i < piece.Inside.Count; i++)
            {
                var bounds = new List<ExactSurfaceRef>();
                for (int j = 0; j < piece.Inside.Count; j++)
                    if (j != i) bounds.Add(piece.Inside[j]);

                // Вырез ограничивает грань: внутри выреза материала нет.
                foreach (var hole in piece.Holes)
                    foreach (var h in hole) bounds.Add(h.Flipped);

                faces.Add(new QPolygon(piece.Inside[i], bounds.ToArray(), (uint)faces.Count + 1));
            }

            // Стенки вырезов: несущая — поверхность выреза, вывернутая наружу.
            foreach (var hole in piece.Holes)
                for (int i = 0; i < hole.Count; i++)
                {
                    var bounds = new List<ExactSurfaceRef>(piece.Inside);
                    for (int j = 0; j < hole.Count; j++)
                        if (j != i) bounds.Add(hole[j]);

                    faces.Add(new QPolygon(hole[i].Flipped, bounds.ToArray(), (uint)faces.Count + 1));
                }
        }

        return faces;
    }

    /// <summary>Объём счётом точек, с оценкой погрешности — как и у выпуклого случая.</summary>
    public (double Volume, double Error) MeasureVolume(Vector3d lo, Vector3d hi,
                                                       int samples = 200_000, int seed = 12345)
    {
        var rnd = new Random(seed);
        Vector3d span = hi - lo;
        double boxVolume = span.X * span.Y * span.Z;

        int inside = 0;
        for (int i = 0; i < samples; i++)
        {
            var p = new Vector3d(lo.X + span.X * rnd.NextDouble(),
                                 lo.Y + span.Y * rnd.NextDouble(),
                                 lo.Z + span.Z * rnd.NextDouble());
            if (Where(p) < 0) inside++;
        }

        double fraction = inside / (double)samples;
        return (fraction * boxVolume,
                boxVolume * Math.Sqrt(Math.Max(fraction * (1 - fraction), 1e-12) / samples) * 3);
    }

    public override string ToString() =>
        $"область: кусков {Pieces.Count}" + (HasQuadric ? ", с квадриками" : "");
}
