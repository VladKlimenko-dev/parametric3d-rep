namespace Parametric3D.Kernel;

/// <summary>
/// НАСТРОЙКИ ЯДРА НА ВРЕМЯ ОДНОЙ РАБОТЫ.
///
/// Настройки разбросаны по общим изменяемым полям: число потоков у булевой и у сетки,
/// откладывание разметки, привязка точек, пометка операции. Пока расчёт идёт по одной детали
/// подряд, это сходит с рук, но стоит запустить две разом — и они начнут менять настройки друг
/// другу посреди работы.
///
/// Здесь не переустройство, а первый шаг к нему: место, где настройки названы вместе, и способ
/// задать их НА ВРЕМЯ — с непременным возвратом прежних значений, даже если работа оборвалась
/// ошибкой. Прежде каждый вызывающий возвращал их сам, и половина забывала.
///
/// Перепись общих изменяемых полей ядра, составленная при этом (чтобы следующий шаг не начинался
/// с поиска):
///   Ops.BooleanOp:        Threads, DeferMeshing, Audit, AuditDepth, UseExactKernel,
///                         LocalizeBooleans, ExactCylinderCuts, ExactDisplay, HealAfterBoolean,
///                         HealTinyEdges, HealTolerance, OpTag
///   Tess.Tessellator:     Threads
///   Tess.ExactFaceMesher: Snap, Remember, Lenient, пути выгрузки, счётчики уточнения
///   Tess.Cdt:             CheckSides, OldLegalize
///   Exact.ExactBsp:       MaxDepth
///   Exact.PlaneRegistry:  VerifyEveryAnswer
///   Diagnostics.Prof:     Enabled
///
/// Из них на РЕЗУЛЬТАТ влияют Snap, DeferMeshing, UseExactKernel, LocalizeBooleans,
/// ExactCylinderCuts, HealAfterBoolean и допуски; остальное — скорость и разбор.
///
/// Поставка 2 (r6): перепись выше устарела. Все флаги, влияющие на результат, теперь — в KernelSettings
/// (слой модели — ModelSettings); действующая опись того, что осталось вне контекста, — проверка
/// G5-GLOBALS-INVENTORY (tests/k01/Guarantees.cs, режим K01Checks globals).
/// </summary>
public sealed class KernelOptions
{
    public int? Threads;
    public bool? DeferMeshing;
    public bool? LocalizeBooleans;
    public bool? UseExactKernel;
    public string? OpTag;

    /// <summary>
    /// K04. Задать настройки на время работы — ДОЧЕРНИМ КОНТЕКСТОМ, а не правкой общих полей.
    /// Прежде область меняла статические значения и возвращала их по выходе; второй расчёт,
    /// идущий одновременно, видел чужие значения. Теперь изменения видны только в логическом
    /// потоке, который открыл область.
    /// </summary>
    public Scope Apply() => new(this);

    /// <summary>Настройки, действующие до освобождения.</summary>
    public sealed class Scope : IDisposable
    {
        private KernelContext.Scope _inner;
        private bool _closed;

        internal Scope(KernelOptions o)
        {
            var parent = KernelContext.Current ?? new KernelContext();
            var ctx = parent.With(s => s with
            {
                BooleanThreads = o.Threads ?? s.BooleanThreads,
                MeshThreads = o.Threads ?? s.MeshThreads,
                DeferMeshing = o.DeferMeshing ?? s.DeferMeshing,
                LocalizeBooleans = o.LocalizeBooleans ?? s.LocalizeBooleans,
                UseExactKernel = o.UseExactKernel ?? s.UseExactKernel,
                OpTag = o.OpTag ?? s.OpTag,
            });
            _inner = ctx.Enter();
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            _inner.Dispose();
        }
    }
}
