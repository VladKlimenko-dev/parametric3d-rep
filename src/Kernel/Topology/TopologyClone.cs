namespace Parametric3D.Kernel.Topology;

/// <summary>
/// ПОСТАВКА 2. СТРУКТУРНАЯ КОПИЯ ТЕЛА — копирование при записи для опубликованных (неизменяемых) тел.
///
/// Копируется граф B-rep (оболочки, грани, контуры, полурёбра, рёбра, вершины) с новыми номерами сущностей;
/// неизменяемые объекты геометрии (кривые, поверхности) и точная форма разделяются с оригиналом. Копия не
/// опубликована: её можно править (уточнение рёбер для записи STEP, разбор в проверках).
///
/// Прежде «копией» служил TransformOp.Copy со сдвигом на ноль — а он ПЕРЕСОБИРАЕТ тело из треугольников
/// гранения: получается другое, многогранное тело (у 4 отверстий уточнение рёбер на нём давало объём 1 134
/// вместо 45 738 мм³).
/// </summary>
public static class TopologyClone
{
    /// <summary>g.05: копия несёт точку грани (InteriorHint). P3D_CLONEHINT=0 — прежнее (мутация).</summary>
    static readonly bool CloneHintOff = Environment.GetEnvironmentVariable("P3D_CLONEHINT") == "0";

    public static TSolid Deep(TSolid src)
    {
        var vmap = new Dictionary<TVertex, TVertex>(ReferenceEqualityComparer.Instance);
        var emap = new Dictionary<TEdge, TEdge>(ReferenceEqualityComparer.Instance);
        TVertex V(TVertex v)
        {
            if (vmap.TryGetValue(v, out var c)) return c;
            c = new TVertex(v.P) { Tolerance = v.Tolerance, Attr = v.Attr };
            c.Tag = v.Tag;
            return vmap[v] = c;
        }
        TEdge E(TEdge e)
        {
            if (emap.TryGetValue(e, out var c)) return c;
            c = new TEdge { V0 = V(e.V0), V1 = V(e.V1), Curve = e.Curve, Tolerance = e.Tolerance, Hidden = e.Hidden, Attr = e.Attr,
                            PolylineCache = e.PolylineCache == null ? null : new List<OpenTK.Mathematics.Vector3d>(e.PolylineCache),
                            PolylineQuality = e.PolylineQuality, PolylineSource = e.PolylineSource };
            c.Tag = e.Tag;
            return emap[e] = c;
        }

        var cmap = new Dictionary<TCoEdge, TCoEdge>(ReferenceEqualityComparer.Instance);
        var dst = new TSolid { QExact = src.QExact, Guarantee = src.Guarantee, TopologyConvention = src.TopologyConvention, Attr = src.Attr };
        dst.Tag = src.Tag;
        foreach (var sh in src.Shells)
        {
            var nsh = new TShell { Solid = dst, IsClosed = sh.IsClosed, Attr = sh.Attr };
            nsh.Tag = sh.Tag;
            dst.Shells.Add(nsh);
            foreach (var f in sh.Faces)
            {
                var nf = new TFace { Surface = f.Surface, Reversed = f.Reversed, MaterialSlot = f.MaterialSlot, SourceTag = f.SourceTag, Attr = f.Attr, InteriorHint = CloneHintOff ? null : f.InteriorHint };
                nf.Tag = f.Tag;
                nsh.Add(nf);
                foreach (var l in f.Loops)
                {
                    var nl = new TLoop { Face = nf, IsOuter = l.IsOuter, Attr = l.Attr };
                    nl.Tag = l.Tag;
                    nf.Loops.Add(nl);
                    foreach (var ce in l.CoEdges)
                    {
                        var ne = E(ce.Edge);
                        var nce = new TCoEdge { Edge = ne, Reversed = ce.Reversed, Loop = nl, PCurve = ce.PCurve, PCurveDeviation = ce.PCurveDeviation };
                        nl.CoEdges.Add(nce);
                        cmap[ce] = nce;
                    }
                }
            }
        }
        // Порядок как у оригинала: FaceA/FaceB ребра — по месту полуребра, рёбра вершины — по месту в списке.
        foreach (var (e, ne) in emap)
            foreach (var ce in e.CoEdges) if (cmap.TryGetValue(ce, out var nce)) ne.CoEdges.Add(nce);
        foreach (var (v, nv) in vmap)
            foreach (var e in v.Edges) if (emap.TryGetValue(e, out var ne)) nv.Edges.Add(ne);
        dst.CopyRepresentationStateFrom(src);
        return dst;
    }
}
