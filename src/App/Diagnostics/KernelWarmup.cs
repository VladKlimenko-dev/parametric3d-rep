using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;

namespace Parametric3D.App.Diagnostics;

/// <summary>
/// 20:17 (скорость): ПРОГРЕВ ЯДРА В ФОНЕ. Первая операция каждого вида в сеансе шла в 2–3 раза дольше повторной: .NET
/// компилирует методы при первом вызове, и первые проходы идут неоптимизированным кодом (скругление ребра цилиндр ∪ шар:
/// 1.9 с первый раз, 0.7 с потом). Через пару секунд после открытия окна в фоне (низкий приоритет, отдельная деталь, не
/// документ пользователя) строится небольшая сцена: заготовки FreeForm, перенос, объединение, скругления и фаски рёбер,
/// эскиз → выдавливание, отверстие — так код ядра скомпилирован и разогрет до первой операции пользователя.
/// Мутация / выключение: P3D_WARMUP=0.
/// </summary>
public static class KernelWarmup
{
    /// <summary>Сборка: оптимизирована ли (Release или Optimize) и запущена ли под отладчиком — для журнала (под отладчиком
    /// Visual Studio по умолчанию отключает оптимизацию кода: ядро медленнее в разы).</summary>
    public static string BuildInfo
    {
        get
        {
            var dbg = typeof(KernelWarmup).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false).OfType<System.Diagnostics.DebuggableAttribute>().FirstOrDefault();
            bool optimized = dbg == null || !dbg.IsJITOptimizerDisabled;
            return $"code {(optimized ? "optimized" : "NOT optimized (Debug)")}{(System.Diagnostics.Debugger.IsAttached ? ", debugger attached (JIT optimization may be suppressed)" : "")}";
        }
    }

    public static bool Enabled => Environment.GetEnvironmentVariable("P3D_WARMUP") != "0";
    /// <summary>Прогрев закончен (время, мс); null — не закончен или не шёл.</summary>
    public static double? DoneMs { get; private set; }

    public static void Start(int delayMs = 1500)
    {
        if (!Enabled) return;
        var t = new Thread(() =>
        {
            try
            {
                Thread.Sleep(delayMs);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Run();
                DoneMs = sw.Elapsed.TotalMilliseconds;
                Core.AppLog.Write($"kernel warm-up done in {DoneMs:0} ms");
            }
            catch (Exception ex) { Core.AppLog.Write("kernel warm-up", ex); }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "kernel warm-up" };
        t.Start();
    }

    /// <summary>Сцена прогрева (синхронно; для проверок): число операций с ошибкой (0 — сцена построилась).</summary>
    public static int Run()
    {
        int errors = 0;
        var part = new Part("warm-up") { FreeFormDoc = true };
        var node = new FreeFormFeature("warm-up");
        part.AddFeature(node);
        void Add(Feature f) { f.FreeForm = node.Key; f.Frozen = true; part.AddFeature(f); part.Rebuild(null); if (f.Error != null) { errors++; Core.AppLog.Write($"warm-up: {f.Name}: {f.Error.Split('\n')[0]}"); } }
        Add(new FFPrimitiveFeature { Shape = FFPrimitive.Cylinder, A = new ValueRef(6), B = new ValueRef(16) });
        Add(new FFPrimitiveFeature { Shape = FFPrimitive.Sphere, A = new ValueRef(8) });
        Add(new FFTransformFeature { Mode = FFXform.Move, BodyNames = { "Тело 1" }, Dx = new ValueRef(3), Dz = new ValueRef(2) });
        Add(new FFBooleanFeature { Op = FFBool.Union, Target = "Тело 1", Tools = { "Тело 2" } });
        Add(new FFPrimitiveFeature { Shape = FFPrimitive.Box, A = new ValueRef(20), B = new ValueRef(14), C = new ValueRef(6), Origin = new Vector3d(30, 0, 0) });
        // скругления и фаски рёбер (кривое и прямое ребро), каждое — отдельной пробой
        foreach (bool round in new[] { true, false })
            foreach (var pick in new Func<TEdge, bool>[] { e => e.Curve is not Kernel.Curves.LineCurve3 && !ReferenceEquals(e.V0, e.V1), e => ReferenceEquals(e.V0, e.V1), e => e.Curve is Kernel.Curves.LineCurve3 })
            {
                TEdge? edge = null; int bodyIndex = -1;
                for (int i = 0; i < part.Bodies.Count && edge == null; i++) { edge = part.Bodies[i].Edges.FirstOrDefault(pick); bodyIndex = i; }
                if (edge == null) continue;
                Feature f;
                if (round) { var rf = new RoundFeature { Radius = new ValueRef(1) }; rf.Edges.Add(EdgeRef.Of(edge)); f = rf; }
                else { var cf = new ChamferFeature { D1 = new ValueRef(0.8), D2 = new ValueRef(0.8) }; cf.Edges.Add(EdgeRef.Of(edge)); f = cf; }
                f.FFBody = part.BodyName(bodyIndex);
                Add(f);
                part.RemoveFeature(f); part.Rebuild(null);
            }
        // 23:50: прочие виды поверхностей и операций (первая операция с тором, конусом, шаром в бруске — тоже без компиляции):
        // своя деталь на каждую, чтобы тела не мешали друг другу
        void Scene(params Feature[] fs)
        {
            var p2 = new Part("warm-up 2") { FreeFormDoc = true }; var n2 = new FreeFormFeature("warm-up 2"); p2.AddFeature(n2);
            foreach (var f in fs) { f.FreeForm = n2.Key; f.Frozen = true; p2.AddFeature(f); p2.Rebuild(null); if (f.Error != null) { errors++; Core.AppLog.Write($"warm-up: {f.Name}: {f.Error.Split('\n')[0]}"); } }
        }
        Scene(new FFPrimitiveFeature { Shape = FFPrimitive.Torus, A = new ValueRef(10), B = new ValueRef(3) },
              new FFPrimitiveFeature { Shape = FFPrimitive.Box, A = new ValueRef(6), B = new ValueRef(6), C = new ValueRef(12), Origin = new Vector3d(10, 0, -6) },
              new FFBooleanFeature { Op = FFBool.Union, Target = "Тело 1", Tools = { "Тело 2" } });
        Scene(new FFPrimitiveFeature { Shape = FFPrimitive.Cone, A = new ValueRef(8), B = new ValueRef(3), C = new ValueRef(15) },
              new FFPrimitiveFeature { Shape = FFPrimitive.Box, A = new ValueRef(20), B = new ValueRef(20), C = new ValueRef(5), Origin = new Vector3d(0, 0, 12) },
              new FFBooleanFeature { Op = FFBool.Subtract, Target = "Тело 1", Tools = { "Тело 2" } });
        Scene(new FFPrimitiveFeature { Shape = FFPrimitive.Box, A = new ValueRef(20), B = new ValueRef(16), C = new ValueRef(10) },
              new FFPrimitiveFeature { Shape = FFPrimitive.Sphere, A = new ValueRef(6), Origin = new Vector3d(10, 8, 10) },
              new FFBooleanFeature { Op = FFBool.Subtract, Target = "Тело 1", Tools = { "Тело 2" } },
              new FFPrimitiveFeature { Shape = FFPrimitive.Cylinder, A = new ValueRef(3), B = new ValueRef(20), Origin = new Vector3d(-4, 0, -5), Cut = true },
              new FFMirrorFeature { BodyNames = { "Тело 1" }, PlaneAt = new Vector3d(15, 0, 0) },
              new FFArrayFeature { BodyNames = { "Тело 1" }, Count = new ValueRef(2), Step = new Vector3d(0, 40, 0) });
        // параметрическая деталь: эскиз → выдавливание, скругление
        var sample = Parametric3D.App.Diagnostics.TestParts.Realistic(1, 2);
        sample.Rebuild(null);
        errors += sample.Features.Count(f => f.Error != null);
        return errors;
    }
}
