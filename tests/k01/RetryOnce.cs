using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>
    /// ПОВТОР СБОЙНОЙ ОПЕРАЦИИ — ОДИН РАЗ (кнопка «Тест», раздел «Нагрузка», 2026-10-20). Операция с постоянным внутренним
    /// сбоем (InternalFailure) в середине дерева: прежде каждая следующая правка в конце дерева пересчитывала всё от неё —
    /// время правки росло с числом правок (на детали из 12 операций и 24 скруглений — с 0.2 до 17 с). Теперь: первый расчёт —
    /// сбой, следующий — один повтор (с сбойной), дальше правка в конце выполняет только новую операцию; операция,
    /// исправленная правкой, снова повторяется при сбое. Мутация P3D_RETRYONCE=0 — сбойная выполняется при каждой правке.
    /// </summary>
    static void RetryOnceChecks()
    {
        // ОШИБКА ОПЕРАЦИИ ВЫШЕ ПО ДЕРЕВУ ПЕРЕЖИВАЕТ ПРАВКУ НИЖЕ: пересчёт с сохранённого состояния переносит итог операций до
        // него (ошибка, исход, созданные грани, история) из их прежнего расчёта. Прежде — обнулял: отказ (InvalidInput)
        // пропадал из дерева после любой правки ниже. Мутация P3D_KEEPRECORDS=0.
        Check("KRN01-ERROR-SURVIVES-LATER-EDIT", () =>
        {
            var part = new Part("keep");
            part.AddFeature(new BoxFeatureT(Box(0, 0, 0, 60, 40, 20)));
            part.AddFeature(new HoleFeatureT(10));
            var bad = new RefusingFeature(); part.AddFeature(bad);
            part.AddFeature(new HoleFeatureT(20));
            part.Rebuild(null);
            Require(bad.Error != null, "the refusing feature did not fail");
            var created1 = part.Features[1].CreatedFaceIds.ToList();
            for (int i = 0; i < 3; i++) { part.AddFeature(new HoleFeatureT(30 + i * 6)); part.Rebuild(null); }
            Log(new { test = "KRN01-ERROR-SURVIVES-LATER-EDIT", error = bad.Error, created1 = created1.Count, createdAfter = part.Features[1].CreatedFaceIds.Count });
            Require(bad.Error != null, "the refused feature lost its error after edits further down the tree");
            Require(part.Features[1].CreatedFaceIds.SequenceEqual(created1), "faces created by an earlier feature were forgotten");
        });
        Check("KRN01-RETRY-FAILURE-ONCE", () =>
        {
            var part = new Part("retry");
            part.AddFeature(new BoxFeatureT(Box(0, 0, 0, 60, 40, 20)));
            var bad = new ThrowingFeature(); part.AddFeature(bad);
            part.AddFeature(new HoleFeatureT(10)); 
            part.Rebuild(null);
            Require(part.Features[1].Error != null, "the failing feature did not fail");
            int c0 = bad.Calls;
            var runs = new List<int>();
            for (int i = 0; i < 6; i++)
            {
                part.AddFeature(new HoleFeatureT(20 + i * 5));
                int before = bad.Calls; part.Rebuild(null); runs.Add(bad.Calls - before);
            }
            Log(new { test = "KRN01-RETRY-FAILURE-ONCE", firstCalls = c0, perEdit = runs });
            Require(runs.Skip(1).All(x => x == 0), $"the failing feature ran again on later edits: {string.Join(",", runs)}");
            Require(runs.Sum() == 1, $"the failing feature was retried {runs.Sum()} times (one retry expected)");
            Require(part.Features.Skip(2).All(f => f.Error == null) && part.Bodies.Count == 1, "later features failed");
            Require(part.Features[1].Error != null, "the failing feature lost its error after edits further down the tree");
        });
    }

    sealed class RefusingFeature : Feature
    {
        public override void Execute(BuildContext c) => throw new Parametric3D.Core.RebuildException(OperationOutcome.InvalidInput, "refused (test): invalid input");
    }
    sealed class ThrowingFeature : Feature
    {
        public int Calls;
        public override void Execute(BuildContext c) { Calls++; throw new InvalidOperationException("injected internal failure (test)"); }
    }
    sealed class BoxFeatureT(TSolid box) : Feature
    {
        public override void Execute(BuildContext c) => c.Bodies.Add(box);
    }
    sealed class HoleFeatureT(double x) : Feature
    {
        public override void Execute(BuildContext c) => BooleanOp.Subtract(c.Bodies, PrimitiveOp.Cylinder(new Vector3d(x, 20, -5), 2, 30), c.Warnings);
    }
}

static partial class Program
{
    /// <summary>
    /// ОТМЕНА БЕЗ ПЕРЕСЧЁТА ВСЕГО ДЕРЕВА (u-нагрузка, 2026-10-20): деталь из снимка перенимает у текущей сохранённые состояния
    /// общей начальной части дерева. Деталь последовательности ручками (8 операций): правка последнего скругления, затем
    /// «отменить» и «вернуть» — пересчёт начинается с последней операции (StartedAt = 7), объём и грани — как у расчёта того
    /// же снимка с нуля. Мутация P3D_ADOPT=0 — пересчёт с нуля (StartedAt = 0).
    /// </summary>
    static void AdoptChecks()
    {
        // «Отмена» новой операции (удаление последней) — пересчёт с её места, а не с нуля. Прежде удаление из дерева детали
        // извещало «изменилась сама деталь», и все сохранённые состояния забывались. Мутация P3D_REMOVEMISSING=0.
        Check("KRN08-REMOVE-FEATURE-KEEPS-CHECKPOINTS", () =>
        {
            var (part, _) = DocumentFile.Load(File.ReadAllText(Fixture("seq_handles_k75.p3d")));
            ((RoundFeature)part.Features[7]).Radius = new ValueRef(1.5);
            part.Rebuild(null);
            double v = part.Volume;
            var extra = new RoundFeature { Radius = new ValueRef(0.5) };
            extra.Edges.AddRange(((RoundFeature)part.Features[5]).Edges);
            part.AddFeature(extra); part.Rebuild(null);
            part.RemoveFeature(extra);
            var r = part.Compute(null); part.Apply(r);
            Log(new { test = "KRN08-REMOVE-FEATURE-KEEPS-CHECKPOINTS", startedAt = r.StartedAt, executed = r.Executed, v, after = part.Volume });
            Require(r.StartedAt == 8 && r.Executed == 0, $"removing the last feature rebuilt from operation {r.StartedAt} ({r.Executed} executed)");
            Require(Math.Abs(part.Volume - v) <= 1e-9 * v, $"volume {part.Volume:R} vs {v:R}");
        });
        Check("KRN08-UNDO-ADOPTS-CHECKPOINTS", () =>
        {
            var (part, pars) = DocumentFile.Load(File.ReadAllText(Fixture("seq_handles_k75.p3d")));
            ((RoundFeature)part.Features[7]).Radius = new ValueRef(1.5);
            part.Rebuild(null);
            var h = new UndoHistory();
            h.Record(part, pars);
            ((RoundFeature)part.Features[7]).Radius = new ValueRef(1.2); part.MarkDirtyFrom(part.Features[7]);
            part.Rebuild(null);
            double v12 = part.Volume;
            var u = h.Undo(part, pars, rebuild: false)!.Value;
            var r1 = u.Part.Compute(null); u.Part.Apply(r1);
            var refU = DocumentFile.Load(DocumentFile.Save(u.Part, pars)).Part; refU.Rebuild(null);
            var rd = h.Redo(u.Part, pars, rebuild: false)!.Value;
            var r2 = rd.Part.Compute(null); rd.Part.Apply(r2);
            Log(new { test = "KRN08-UNDO-ADOPTS-CHECKPOINTS", undoStartedAt = r1.StartedAt, redoStartedAt = r2.StartedAt, adopted = h.LastAdopted, vUndo = u.Part.Volume, vRef = refU.Volume, vRedo = rd.Part.Volume, v12 });
            Require(r1.StartedAt == 7 && r2.StartedAt == 7, $"undo/redo rebuilt from operation {r1.StartedAt}/{r2.StartedAt} (7 expected)");
            Require(Math.Abs(u.Part.Volume - refU.Volume) <= 1e-9 * refU.Volume, $"undo volume {u.Part.Volume:R} vs from scratch {refU.Volume:R}");
            Require(Math.Abs(rd.Part.Volume - v12) <= 1e-9 * v12, $"redo volume {rd.Part.Volume:R} vs {v12:R}");
            Require(u.Part.Features.All(f => f.Error == null) && rd.Part.Features.All(f => f.Error == null), "errors after undo/redo");
            Require(u.Part.Bodies.Single().Faces.Count() == refU.Bodies.Single().Faces.Count(), "face count differs from the rebuild from scratch");
        });
    }
}
