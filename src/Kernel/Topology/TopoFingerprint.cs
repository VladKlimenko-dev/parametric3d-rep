using System.Runtime.CompilerServices;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// u11: ОТПЕЧАТОК СОСТАВА ТЕЛА для памяти проверок и объёма. Ревизия растёт только на открытой правке (BeginEdit), а
/// построители правят ещё не опубликованное тело на месте и без неё (соглашение, разметка, p-кривые). Отпечаток — ссылки
/// поверхностей, кривых, p-кривых, флаги обхода, точки вершин побитово и число граней сетки: любая такая правка меняет его.
/// Стоит O(n) по B-rep — микросекунды против сотен миллисекунд проверки.
/// </summary>
public static class TopoFingerprint
{
    public static ulong Of(TSolid s)
    {
        ulong h = 1469598103934665603UL;
        void M(ulong x) { h ^= x + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2); h *= 1099511628211UL; }
        void R(object? o) => M(o == null ? 0UL : (ulong)(uint)RuntimeHelpers.GetHashCode(o) | ((ulong)o.GetType().GetHashCode() << 32));
        void D(double d) => M((ulong)BitConverter.DoubleToInt64Bits(d));
        M((ulong)s.Shells.Count);
        foreach (var sh in s.Shells)
        {
            M((ulong)sh.Faces.Count); M(sh.IsClosed ? 1UL : 2UL);
            foreach (var f in sh.Faces)
            {
                R(f); R(f.Surface); M(f.Reversed ? 3UL : 4UL);
                if (f.Surface is FacetedSurface fs) { M((ulong)fs.Facets.Count); R(fs.Display); }
                M((ulong)f.Loops.Count);
                foreach (var l in f.Loops)
                {
                    M(l.IsOuter ? 5UL : 6UL); M((ulong)l.CoEdges.Count);
                    foreach (var ce in l.CoEdges)
                    {
                        R(ce.Edge); M(ce.Reversed ? 7UL : 8UL); R(ce.PCurve);
                        var e = ce.Edge; R(e.Curve); R(e.V0); R(e.V1);
                        D(e.V0.P.X); D(e.V0.P.Y); D(e.V0.P.Z); D(e.V1.P.X); D(e.V1.P.Y); D(e.V1.P.Z);
                    }
                }
            }
        }
        return h;
    }
}
