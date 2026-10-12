using OpenTK.Mathematics;
using Parametric3D.Kernel.Tess;

namespace Parametric3D.Kernel.Voxels;

/// <summary>
/// Кубы заполнения в вершинные буферы.
///
/// КУБ РИСУЕТСЯ ЦЕЛИКОМ, СО ВСЕМИ ШЕСТЬЮ ГРАНЯМИ. Отбрасывать грани,
/// прилегающие к соседнему кубу, заманчиво — их не видно, — но после
/// слияния соседи бывают разного размера, и «прилегает» перестаёт быть
/// простым отношением. Лишние треугольники дешевле неверных.
///
/// Каждый куб получает свой pick-id из общего счётчика, поэтому по нему
/// можно будет опознать ячейку под курсором тем же способом, каким
/// опознаётся грань.
/// </summary>
public static class CellMesh
{
    /// <summary>
    /// Зазор между соседними кубами, доля половины ребра.
    ///
    /// Без зазора соседние кубы стыкуются грань в грань, и на рисунке
    /// видна сплошная масса, в которой не разобрать ни размера ячеек,
    /// ни их числа. Небольшой зазор возвращает картинке смысл: видно,
    /// что внутри крупные кубы, а у поверхности мелкие.
    /// </summary>
    public const double DefaultGap = 0.06;

    public static MeshData Build(IReadOnlyList<Cell> cells, double gap = DefaultGap)
    {
        var mb = new MeshBuilder();
        var md = new MeshData();
        var bounds = Bounds3.Empty;

        uint tag = 1;

        foreach (var cell in cells)
        {
            double h = cell.Half * (1 - gap);
            if (h <= 0) continue;

            mb.BeginFace(tag++);
            AddCube(mb, cell.Centre, h);
            mb.EndFace();

            bounds.Add(cell.Centre - new Vector3d(cell.Half));
            bounds.Add(cell.Centre + new Vector3d(cell.Half));
        }

        mb.FillInto(md);
        md.Bounds = bounds;

        return md;
    }

    private static void AddCube(MeshBuilder mb, Vector3d c, double h)
    {
        // Вершины заводятся по граням, а не по углам: у куба в каждом углу
        // сходятся три грани с РАЗНЫМИ нормалями, и общая вершина дала бы
        // усреднённую нормаль — куб выглядел бы надутым.
        Face(mb, c, h, new Vector3d(0, 0, 1), new Vector3d(1, 0, 0), new Vector3d(0, 1, 0));
        Face(mb, c, h, new Vector3d(0, 0, -1), new Vector3d(0, 1, 0), new Vector3d(1, 0, 0));
        Face(mb, c, h, new Vector3d(1, 0, 0), new Vector3d(0, 1, 0), new Vector3d(0, 0, 1));
        Face(mb, c, h, new Vector3d(-1, 0, 0), new Vector3d(0, 0, 1), new Vector3d(0, 1, 0));
        Face(mb, c, h, new Vector3d(0, 1, 0), new Vector3d(0, 0, 1), new Vector3d(1, 0, 0));
        Face(mb, c, h, new Vector3d(0, -1, 0), new Vector3d(1, 0, 0), new Vector3d(0, 0, 1));
    }

    /// <summary>Одна грань куба: нормаль наружу, обход против часовой стрелки.</summary>
    private static void Face(MeshBuilder mb, Vector3d c, double h, Vector3d n, Vector3d u, Vector3d v)
    {
        Vector3d o = c + n * h;

        int a = mb.AddVertex(o - u * h - v * h, n);
        int b = mb.AddVertex(o + u * h - v * h, n);
        int d = mb.AddVertex(o + u * h + v * h, n);
        int e = mb.AddVertex(o - u * h + v * h, n);

        mb.AddQuad(a, b, d, e);
    }
}
