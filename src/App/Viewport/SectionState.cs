using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.App.Operations;
using Parametric3D.Kernel;
using Parametric3D.Model;

namespace Parametric3D.App.Viewport;

/// <summary>
/// k99 (замечание 3): НАСТРАИВАЕМАЯ ПЛОСКОСТЬ СЕЧЕНИЯ. Основа — плоскость или плоская грань; начало — начало основы, центр
/// основы (середина плоскости) или указанная точка (проекция на основу); смещение — вдоль нормали основы; поворот — на угол A
/// вокруг первой оси основы (U), затем на угол B вокруг второй (V). Отсекается сторона, куда смотрит нормаль сечения.
/// </summary>
public sealed class SectionState
{
    public enum OriginKind { MidPlane, BasePlane, Point }

    public PickRef? Base;
    /// <summary>Рамка основы, если ссылки нет (сечение по умолчанию) или она потеряна.</summary>
    public Frame3 FallbackFrame = Frame3.XY;
    public OriginKind Origin = OriginKind.MidPlane;
    public Vector3d? OriginPoint;
    public double Offset, AngleA, AngleB;

    public SectionState Clone() => (SectionState)MemberwiseClone();

    /// <summary>Рамка основы (начало — начало основы).</summary>
    public Frame3 BaseFrame() => Base is { } b && Refs.PlaneOf(b) is { } pl ? (b.Target is DatumPlane dp ? dp.Frame : pl.Frame) : FallbackFrame;

    /// <summary>Начало сечения на основе (до смещения): начало основы, её центр или проекция точки.</summary>
    public Vector3d BaseOrigin(Bounds3 model)
    {
        var f = BaseFrame();
        var w = f.W.Normalized();
        Vector3d Proj(Vector3d p) => p - w * Vector3d.Dot(p - f.Origin, w);
        switch (Origin)
        {
            case OriginKind.BasePlane: return f.Origin;
            case OriginKind.Point when OriginPoint is { } q: return Proj(q);
            default:
                if (Base is { Target: DatumPlane dp })
                {
                    var c = dp.DisplayCorners();
                    var m = new Vector3d((c[0].X + c[1].X + c[2].X + c[3].X) / 4, (c[0].Y + c[1].Y + c[2].Y + c[3].Y) / 4, (c[0].Z + c[1].Z + c[2].Z + c[3].Z) / 4);
                    return Proj(m);
                }
                if (Base is { Kind: PickKind.Face, Owner: Part p } fb && Refs.FaceCentre(p, fb.Id) is { } fc) return Proj(fc);
                return model.IsEmpty ? f.Origin : Proj((model.Min + model.Max) / 2);
        }
    }

    /// <summary>Плоскость сечения: точка, нормаль и оси в ней.</summary>
    public (Vector3d At, Vector3d N, Vector3d U, Vector3d V) Compute(Bounds3 model)
    {
        var f = BaseFrame();
        Vector3d u = f.U.Normalized(), v = f.V.Normalized(), w = f.W.Normalized();
        var o = BaseOrigin(model) + w * Offset;
        static Vector3d Rot(Vector3d x, Vector3d axis, double deg)
        {
            double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            return x * c + Vector3d.Cross(axis, x) * s + axis * Vector3d.Dot(axis, x) * (1 - c);
        }
        // поворот A — вокруг U основы, затем B — вокруг V основы (оси основы неподвижны)
        Vector3d nu = Rot(Rot(u, u, AngleA), v, AngleB), nv = Rot(Rot(v, u, AngleA), v, AngleB), nw = Rot(Rot(w, u, AngleA), v, AngleB);
        return (o, nw, nu, nv);
    }
}
