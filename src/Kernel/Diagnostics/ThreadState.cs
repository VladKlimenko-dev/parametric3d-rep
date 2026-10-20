namespace Parametric3D.Kernel.Diagnostics;

/// <summary>
/// u-нагрузка (2026-10-20): ПОТОЧНЫЕ ССЫЛКИ НА ТЕЛА ПОСЛЕДНЕЙ ОПЕРАЦИИ ОТПУСКАЮТСЯ В КОНЦЕ РАСЧЁТА ДЕТАЛИ. Поля [ThreadStatic]
/// общей булевой (соответствие граней, слияния, происхождение), разметчика, построителя призм и замены NURBS держали тела
/// последней операции потока до следующей операции в нём: деталь из 36 операций, отпущенная после сборки, оставляла
/// ~80 МБ (тело и его сетки), у приложения — по такому набору на каждый поток расчёта. Part.Compute вызывает Release в
/// конце; «последний отвергнутый результат» (BooleanOp.LastInvalidResult) — слабая ссылка;
/// «последний угол» (DirectRound.LastPatch) остаётся (поверхность угла, не тело). Мутация P3D_THREADRELEASE=0.
/// </summary>
public static class ThreadState
{
    static readonly bool Off = Environment.GetEnvironmentVariable("P3D_THREADRELEASE") == "0";

    public static void Release()
    {
        if (Off) return;
        Analytic.GeneralBoolean.ReleaseThreadState();
        Analytic.PrismBuild.ReleaseThreadState();
        Tess.Tessellator.ReleaseThreadState();
        Tess.ExactFaceMesher.ReleaseThreadState();
        Ops.NurbsProxy.ReleaseThreadState();
    }
}
