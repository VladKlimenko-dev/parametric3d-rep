using System.Text.Json;
using OpenTK.Mathematics;

namespace Parametric3D.Model.Threads;

// Разбор threads_db.json: threads[] с sweep_ext/sweep_int.profile_zr.
public static partial class ThreadDb
{
    public static List<ThreadSpec> ParseJson(string text)
    {
        var res = new List<ThreadSpec>();
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("threads", out var arr) || arr.ValueKind != JsonValueKind.Array) return res;
        foreach (var e in arr.EnumerateArray())
        {
            var t = new ThreadSpec
            {
                Family = Str(e, "family"), Profile = Str(e, "profile"), Designation = Str(e, "designation"), Series = Str(e, "series"),
                DMajor = Num(e, "d_major") ?? 0, P = Num(e, "P") ?? 0,
                Starts = (int)(Num(e, "starts") ?? 1), StdLeft = Str(e, "hand") == "LH",
                TapDrill = Num(e, "tap_drill"), D1 = Num(e, "D1"), D3 = Num(e, "d3"),
            };
            t.Lead = Num(e, "lead") ?? t.P * Math.Max(1, t.Starts);
            if (e.TryGetProperty("taper", out var tp) && tp.ValueKind == JsonValueKind.Object)
            {
                t.Taper = true;
                t.HalfConeDeg = Num(tp, "half_cone_deg") ?? 1.7899;
            }
            (t.ExtZr, t.ExtStartR) = Sweep(e, "sweep_ext");
            (t.IntZr, t.IntStartR) = Sweep(e, "sweep_int");
            if (t.P > 0 && t.DMajor > 0 && t.ExtZr.Length >= 3 && t.IntZr.Length >= 3) res.Add(t);
        }
        return res;
    }

    private static (Vector2d[], double) Sweep(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var s) || s.ValueKind != JsonValueKind.Object) return (Array.Empty<Vector2d>(), 0);
        var pts = new List<Vector2d>();
        if (s.TryGetProperty("profile_zr", out var zr) && zr.ValueKind == JsonValueKind.Array)
            foreach (var p in zr.EnumerateArray())
                if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2) pts.Add(new Vector2d(p[0].GetDouble(), p[1].GetDouble()));
        return (pts.ToArray(), Num(s, "start_radius") ?? 0);
    }

    private static double? Num(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
