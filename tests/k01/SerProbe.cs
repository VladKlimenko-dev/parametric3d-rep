using OpenTK.Mathematics;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    static void SerProbe()
    {
        var p = new Part("ser"); var ex = TestGeometry.AddCube(p, 60);
        p.AddFeature(new LinearPatternFeature("P") { Step = new Vector3d(100, 0, 0), Count = new ValueRef(2) });
        p.Rebuild(null);
        string json = DocumentFile.Save(p);
        void Try(string name, string text)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (q, t) = DocumentFile.Load(text);
                var r = q.Compute(t, null, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token); q.Apply(r);
                Console.WriteLine($"{name}: loaded; build errors [{string.Join(" | ", q.Features.Where(f => f.Error != null).Select(f => f.Error))}], V={q.Volume:G6}, {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception e) { Console.WriteLine($"{name}: {e.GetType().Name}: {e.Message[..Math.Min(200, e.Message.Length)]}"); }
        }
        Console.WriteLine(json[..Math.Min(1500, json.Length)]);
        Try("baseline", json);
        Try("plane of 3 numbers", System.Text.RegularExpressions.Regex.Replace(json, "\"Plane\": \\[[^\\]]*\\]", "\"Plane\": [0, 0, 0]"));
        Try("plane empty", System.Text.RegularExpressions.Regex.Replace(json, "\"Plane\": \\[[^\\]]*\\]", "\"Plane\": []"));
        Try("plane collinear axes", System.Text.RegularExpressions.Regex.Replace(json, "\"Plane\": \\[[^\\]]*\\]", "\"Plane\": [0,0,0, 1,0,0, 1,0,0]"));
        Try("cube size 1e300", json.Replace("\"Distance\": 60,", "\"Distance\": 1e300,"));
        Try("cube size NaN string", json.Replace("\"Distance\": 60,", "\"Distance\": \"NaN\","));
        Try("negative pattern count", json.Replace("\"Distance\": 2,", "\"Distance\": -5,"));
        Try("point coordinate 1e300", json.Replace("\"X\": 30", "\"X\": 1e300"));
        Try("pattern count 1e9", json.Replace("\"Kind\": \"linearPattern\"", "\"Kind\": \"linearPattern\"").Replace("\"Distance\": 2,", "\"Distance\": 1000000000,"));
    }
}
