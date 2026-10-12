using Parametric3D.Kernel;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Model;

/// <summary>
/// ЗАГОТОВКИ ДЛЯ ПРОВЕРОК.
///
/// Отладка операции обычно начинается с того, что кто-то рисует квадрат
/// мышью. Каждый раз он выходит чуть другой — другие размеры, другие
/// ограничения, другое положение относительно начала, — и результат
/// операции не с чем сравнить: неизвестно, что изменилось, фигура или код.
///
/// Здесь фигуры строятся ТЕМ ЖЕ ПУТЁМ, каким их построил бы человек:
/// эскиз, кривые, операция дерева построения. Положить тело в деталь
/// мимо дерева было бы короче, но тогда проверялось бы не то, чем
/// пользуются.
/// </summary>
public static class TestGeometry
{
    /// <summary>
    /// Куб со стороной <paramref name="side"/> в начале координат детали.
    ///
    /// Выдавливание СИММЕТРИЧНОЕ, поэтому центр куба попадает в начало,
    /// а грани — на ±side/2. Числа в проверках от этого становятся
    /// круглыми: у стандартной сотни грани при ±50 и объём 1 000 000.
    /// </summary>
    public static ExtrudeFeature AddCube(Part part, double side = 100)
    {
        double h = side / 2;

        var plane = part.PlaneXY;

        var sketch = new Sketch($"Sketch{part.Sketches.Count() + 1}", plane.Frame);
        sketch.SetBasePlane(plane.Frame, plane.Id, plane.Name);
        part.Add(sketch);

        // Углы — ОДНИ И ТЕ ЖЕ точки у соседних отрезков. Четыре независимых
        // отрезка с совпадающими координатами контур не образуют: профиль
        // сшивается по общим концам, а не по близости, и вышел бы пустым.
        var a = new SkPoint(-h, -h);
        var b = new SkPoint(h, -h);
        var c = new SkPoint(h, h);
        var d = new SkPoint(-h, h);

        sketch.AddEntity(new SkLine(a, b));
        sketch.AddEntity(new SkLine(b, c));
        sketch.AddEntity(new SkLine(c, d));
        sketch.AddEntity(new SkLine(d, a));

        var ex = new ExtrudeFeature($"Extrude (cube {side:0.###})")
        {
            SketchId = sketch.Id,
            Symmetric = true
        };

        ex.Side1.End = EndType.Distance;
        ex.Side1.Distance = new ValueRef(side);

        part.AddFeature(ex);
        part.Invalidate();

        return ex;
    }
}
