namespace Parametric3D.Render;

/// <summary>
/// Все GLSL-исходники приложения. GLSL 3.30 core — минимум, который держит
/// целочисленные вложения кадрового буфера и instancing.
///
/// Каждый фрагментный шейдер пишет ОДИН выход — цвет. Раньше был ещё
/// второй, целочисленный, с идентификатором объекта под пикселем; от него
/// пришлось отказаться (см. Render/SceneFramebuffer.cs), и выбор мышью
/// теперь считается геометрически на процессоре. Заодно исчезла причина,
/// по которой нельзя было включить обычный MSAA.
///
/// ВАЖНО: внутри строк с кодом шейдеров — ТОЛЬКО ASCII, включая комментарии.
/// Кириллица в исходнике GLSL ломает компиляцию: строка уходит в драйвер
/// как UTF-8, каждая русская буква занимает два байта, и длина в символах
/// перестаёт совпадать с длиной в байтах. Исходник обрезается с конца ровно
/// на число «лишних» байт — отсюда сообщения про «pre-mature EOF» и про
/// обрубленный идентификатор вроде 'gl_'. Все пояснения по-русски держим
/// здесь, в комментариях C#, СНАРУЖИ строк.
/// </summary>
public static class Shaders
{
    public const int MaxStyles = 32;

    // ---------------------------------------------------------------- ЛИНИИ
    // Одна программа рисует ВСЕ линии проекта: рёбра тел, эскиз, сетку,
    // размеры, осевые. Тип линии — индекс стиля в атрибуте инстанса.
    // Толщина задаётся в пикселях (экранное расширение четырёхугольника),
    // штрих — по фазе, накопленной вдоль ломаной.
    //
    // Пояснения к коду ниже:
    //   aCorner    — (t, side): t в [0,1] вдоль сегмента, side = -1 либо +1
    //   iS0        — накопленная длина в модельных единицах в точке P0
    //   iFlags     — бит 0 «наведение», бит 1 «выделено»
    //   uDepthBias — сдвиг к камере, чтобы линия не тонула в грани
    //
    // Четырёхугольник расширяется на halfW + 1 пиксель, но покрытие во
    // фрагментном шейдере считается от halfW — настоящей полутолщины.
    // Лишний пиксель нужен только под сглаживание края. Если считать
    // покрытие от расширенного размера, любая линия становится минимум
    // двухпиксельной, и тонкие стили (сетка) выглядят жирными.
    //   uWorldToPx — масштаб для штриховки
    // Отсечение по ближней плоскости сделано вручную (проверки c.w < EPS):
    // без него отрезок, уходящий за камеру, «выворачивается» на экране.

    public const string LineVert = @"
#version 330 core
layout(location = 0) in vec2 aCorner;
layout(location = 1) in vec3 iP0;
layout(location = 2) in vec3 iP1;
layout(location = 3) in float iS0;
layout(location = 4) in int  iStyle;
layout(location = 5) in uint iPick;
layout(location = 6) in uint iFlags;
layout(location = 7) in float iWidthPx;

uniform mat4  uMVP;
uniform vec2  uViewport;
uniform float uDepthBias;
uniform float uWorldToPx;
uniform int   uStyleOverride;
uniform float uWidthScale;

uniform float uWidth[32];
uniform float uWidthHover[32];
uniform float uWidthSel[32];

flat out int   vStyle;
flat out uint  vPick;
flat out uint  vFlags;
out float vOffPx;
out float vHalfW;
out float vPhasePx;

void main()
{
    vec4 c0 = uMVP * vec4(iP0, 1.0);
    vec4 c1 = uMVP * vec4(iP1, 1.0);

    // ОБРЕЗКА ПО БЛИЖНЕЙ ПЛОСКОСТИ (z = -w в пространстве отсечения).
    //
    // ИСПРАВЛЕНО. Отрезок обрезался по w = 1e-5, а не по ближней плоскости. При
    // сильном приближении конец длинного ребра уходил за камеру, и после деления на
    // крошечную w его экранные координаты доходили до сотен миллионов пикселей —
    // точности float не хватало, и вся линия, собранная между концами, съезжала
    // вбок: ребро рисовалось не там, где сходятся грани.
    float d0 = c0.z + c0.w, d1 = c1.z + c1.w;
    if (d0 < 0.0 && d1 < 0.0) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
    if (d0 < 0.0) { c0 = mix(c0, c1, d0 / (d0 - d1)); }
    if (d1 < 0.0) { c1 = mix(c1, c0, d1 / (d1 - d0)); }
    const float EPS = 1e-7;
    c0.w = max(c0.w, EPS); c1.w = max(c1.w, EPS);

    vec2 s0 = ((c0.xy / c0.w) * 0.5 + 0.5) * uViewport;
    vec2 s1 = ((c1.xy / c1.w) * 0.5 + 0.5) * uViewport;

    // ОБРЕЗКА ПО ОКНУ ВЫВОДА (с запасом 64 px).
    //
    // ИСПРАВЛЕНО. Полоса линии строится из уже спроецированных координат (w = 1),
    // и аппаратная обрезка ей не помогает: конец ребра, уходящего к камере и за край
    // экрана, попадал за сотни тысяч пикселей, растеризатор на таких числах терял
    // точность, и видимая часть полосы съезжала — ребро рисовалось не там, где сходятся
    // грани. Отрезок обрезается по окну (Лианг — Барски); глубина и фаза штриха
    // пересчитываются под новые концы.
    float zc0 = c0.z / c0.w, zc1 = c1.z / c1.w;
    float lenFull = length(s1 - s0);
    {
        const float M = 64.0;
        vec2 lo = vec2(-M), hi = uViewport + vec2(M);
        vec2 dd = s1 - s0;
        float ta = 0.0, tb = 1.0;
        bool out_ = false;
        for (int k = 0; k < 2 && !out_; k++)
        {
            float pk[2] = float[2](-dd[k], dd[k]);
            float qk[2] = float[2](s0[k] - lo[k], hi[k] - s0[k]);
            for (int j = 0; j < 2; j++)
            {
                if (abs(pk[j]) < 1e-12) { if (qk[j] < 0.0) out_ = true; continue; }
                float r = qk[j] / pk[j];
                if (pk[j] < 0.0) ta = max(ta, r); else tb = min(tb, r);
            }
            if (ta > tb) out_ = true;
        }
        if (out_) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
        vec2 n0 = s0 + dd * ta, n1 = s0 + dd * tb;
        float nz0 = mix(zc0, zc1, ta), nz1 = mix(zc0, zc1, tb);
        s0 = n0; s1 = n1; zc0 = nz0; zc1 = nz1;
        vPhasePx = iS0 * uWorldToPx + ta * lenFull;       // фаза штриха у нового начала
    }

    vec2 d = s1 - s0;
    float len = length(d);
    vec2 dir = len > 1e-5 ? d / len : vec2(1.0, 0.0);
    vec2 nrm = vec2(-dir.y, dir.x);

    int s = uStyleOverride >= 0 ? uStyleOverride : clamp(iStyle, 0, 31);
    float w = uWidth[s];
    if ((iFlags & 2u) != 0u)      w = uWidthSel[s];
    else if ((iFlags & 1u) != 0u) w = uWidthHover[s];
    // iWidthPx перекрывает толщину из таблицы стилей. Нужно наконечникам
    // стрелок: у них своя ширина, не связанная с толщиной размерных линий.
    if (iWidthPx > 0.0) w = iWidthPx;

    float halfA = w * uWidthScale * 0.5;

    // Флаг 4 сужает полосу к нулю у второго конца: одна такая полоса и есть
    // готовый треугольник со сглаженными краями. Раньше наконечник
    // набирался двумя десятками поперечных полос, и их края накладывались
    // друг на друга — отсюда размытость.
    bool taper = (iFlags & 4u) != 0u;
    float halfB = taper ? 0.0 : halfA;

    float t = aCorner.x;
    float halfHere = mix(halfA, halfB, t);
    float padHere  = halfHere + 1.0;

    // Сужающаяся полоса НЕ удлиняется вдоль оси. У обычной линии торцы
    // выносятся на полуширину, чтобы стыки ломаной не расходились;
    // у наконечника такой вынос делал бы стрелку тем длиннее, чем она
    // шире, — при отношении 1:1 это добавляло к длине половину ширины.
    //
    // ИСПРАВЛЕНО. Вынос шёл и на НАСТОЯЩИХ концах ребра — в вершинах тела, и линия
    // вылезала за угол на полуширину с пикселем при любом масштабе. Теперь выносится
    // только конец-стык внутри ломаной (флаги 8 — начало, 16 — конец): там он нужен,
    // чтобы изломы не расходились. Настоящий конец обрывается ровно в вершине.
    bool joinHere = t < 0.5 ? (iFlags & 8u) != 0u : (iFlags & 16u) != 0u;
    float alongPad = (taper || !joinHere) ? 0.0 : padHere;

    vec2 sp = mix(s0, s1, t) + nrm * (aCorner.y * padHere) + dir * ((t * 2.0 - 1.0) * alongPad);
    float z = mix(zc0, zc1, t) - uDepthBias;

    gl_Position = vec4((sp / uViewport) * 2.0 - 1.0, z, 1.0);

    vStyle   = s;
    vPick    = iPick;
    vFlags   = iFlags;
    // Передаётся ГОТОВОЕ смещение от оси в пикселях, а не множители
    // к нему. Смещение — аффинная функция экранных координат, поэтому
    // интерполируется точно. Произведение же двух интерполируемых величин
    // (сторона на полуширину) квадратично, и два треугольника
    // четырёхугольника считали его по-разному: у сужающейся полосы
    // это давало заметную несимметричность наконечника.
    vOffPx   = aCorner.y * padHere;
    vHalfW   = halfHere;
    vPhasePx += t * len;
}";

    // uPattern   — 16-битная маска штриха: 1 = штрих, 0 = пробел
    // uPatternPx — период маски в пикселях; 0 означает сплошную линию
    //
    // В пробеле штриховки НЕ делается discard: цвет гасится, но id пишется
    // всегда. Иначе по штриховой линии нельзя было бы попасть мышью.

    public const string LineFrag = @"
#version 330 core
flat in int   vStyle;
flat in uint  vPick;
flat in uint  vFlags;
in float vOffPx;
in float vHalfW;
in float vPhasePx;

uniform float uOpacity;
uniform vec4  uColor[32];
uniform vec4  uColorHover[32];
uniform vec4  uColorSel[32];
uniform int   uPattern[32];
uniform float uPatternPx[32];

out vec4 oColor;

void main()
{
    // coverage is measured against the real half width, not the expanded quad
    float dpx = abs(vOffPx);
    float cov = clamp(vHalfW - dpx + 0.5, 0.0, 1.0);
    if (cov <= 0.004) discard;

    // dash pattern: no discard in the gap, only the color is killed
    float per = uPatternPx[vStyle];
    bool gap = false;
    if (per > 0.5)
    {
        float ph = fract(vPhasePx / per);
        int bit = int(clamp(floor(ph * 16.0), 0.0, 15.0));
        gap = ((uPattern[vStyle] >> bit) & 1) == 0;
    }

    if (gap) { oColor = vec4(0.0); return; }

    vec4 col = uColor[vStyle];
    if ((vFlags & 2u) != 0u)      col = uColorSel[vStyle];
    else if ((vFlags & 1u) != 0u) col = uColorHover[vStyle];

    oColor = vec4(col.rgb, col.a * cov * uOpacity);
}";

    // ---------------------------------------------------------------- ТОЧКИ
    // aCorner — угол квадрата: (-1|1, -1|1).

    public const string PointVert = @"
#version 330 core
layout(location = 0) in vec2 aCorner;
layout(location = 1) in vec3 iP;
layout(location = 2) in int  iStyle;
layout(location = 3) in uint iPick;
layout(location = 4) in uint iFlags;

uniform mat4  uMVP;
uniform vec2  uViewport;
uniform float uDepthBias;
uniform float uSize[32];
uniform float uSizeHover[32];
uniform float uSizeSel[32];
uniform int   uOnlyFlagged;

flat out int   vStyle;
flat out uint  vPick;
flat out uint  vFlags;
flat out float vHalfPx;
out vec2 vUV;

void main()
{
    vec4 c = uMVP * vec4(iP, 1.0);
    if (c.w < 1e-5) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }

    // Только подсвеченные: точка без флагов уходит за пределы экрана.
    if (uOnlyFlagged != 0 && iFlags == 0u) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }

    int s = clamp(iStyle, 0, 31);
    float sz = uSize[s];
    if ((iFlags & 2u) != 0u)      sz = uSizeSel[s];
    else if ((iFlags & 1u) != 0u) sz = uSizeHover[s];
    float halfPx = sz * 0.5 + 1.0;

    vec2 sp = ((c.xy / c.w) * 0.5 + 0.5) * uViewport + aCorner * halfPx;
    gl_Position = vec4((sp / uViewport) * 2.0 - 1.0, c.z / c.w - uDepthBias, 1.0);

    vStyle  = s;
    vPick   = iPick;
    vFlags  = iFlags;
    vHalfPx = halfPx;
    vUV     = aCorner;
}";

    // uShape задаёт форму маркера:
    //   0 — заливка, 1 — окружность, 2 — крест, 3 — квадрат, 4 — ромб

    public const string PointFrag = @"
#version 330 core
flat in int   vStyle;
flat in uint  vPick;
flat in uint  vFlags;
flat in float vHalfPx;
in vec2 vUV;

uniform float uOpacity;
uniform vec4  uColor[32];
uniform vec4  uColorHover[32];
uniform vec4  uColorSel[32];
uniform int   uShape[32];

out vec4 oColor;

void main()
{
    float r = length(vUV) * vHalfPx;
    float a = 0.0;
    int sh = uShape[vStyle];

    if (sh == 0)        a = clamp(vHalfPx - 1.0 - r, 0.0, 1.0);
    else if (sh == 1)   a = clamp(1.5 - abs(r - (vHalfPx - 2.0)), 0.0, 1.0);
    else if (sh == 2)   a = max(clamp(1.5 - abs(vUV.x) * vHalfPx, 0.0, 1.0),
                                clamp(1.5 - abs(vUV.y) * vHalfPx, 0.0, 1.0));
    else if (sh == 3)   a = 1.0;
    else                a = clamp(vHalfPx - 1.0 - (abs(vUV.x) + abs(vUV.y)) * vHalfPx, 0.0, 1.0);

    if (a <= 0.004) discard;

    vec4 col = uColor[vStyle];
    if ((vFlags & 2u) != 0u)      col = uColorSel[vStyle];
    else if ((vFlags & 1u) != 0u) col = uColorHover[vStyle];

    oColor = vec4(col.rgb, col.a * a * uOpacity);
}";

    // -------------------------------------------------------- ГРАНИ (ТЕЛА)

    public const string TriVert = @"
#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in uint aFaceId;
layout(location = 3) in uint aFlags;

uniform mat4 uModel;
uniform mat4 uViewProj;
uniform mat3 uNormalMat;

out vec3 vNormal;
out vec3 vWorld;
flat out uint vFaceId;
flat out uint vFlags;

void main()
{
    vec4 wp = uModel * vec4(aPos, 1.0);
    vWorld  = wp.xyz;
    vNormal = normalize(uNormalMat * aNormal);
    vFaceId = aFaceId;
    vFlags  = aFlags;
    gl_Position = uViewProj * wp;
}";

    // uUnlit = 1 даёт плоскую заливку — для базовых плоскостей и прочей
    // вспомогательной геометрии, которую не нужно освещать.
    //
    // Освещение двухточечное и привязано к камере (оси L1 и L2 строятся
    // от вектора взгляда), поэтому деталь читается при любом повороте:
    // грани не проваливаются в чёрное, как бывает при неподвижном свете.

    public const string TriFrag = @"
#version 330 core
in vec3 vNormal;
in vec3 vWorld;
flat in uint vFaceId;
flat in uint vFlags;

uniform vec3  uCamPos;
uniform vec4  uBaseColor;
uniform vec4  uHoverColor;
uniform vec4  uSelectColor;
uniform float uOpacity;
uniform int   uUnlit;
uniform float uSpecular;
uniform float uShininess;

out vec4 oColor;

void main()
{
    // palette (UI_VIEW_PALETTE): selection 45 %, hover 35 % over the material
    vec4 base = uBaseColor;
    if ((vFlags & 2u) != 0u)      base.rgb = mix(base.rgb, uSelectColor.rgb, 0.45);
    else if ((vFlags & 1u) != 0u) base.rgb = mix(base.rgb, uHoverColor.rgb, 0.35);

    if (uUnlit == 1) { oColor = vec4(base.rgb, base.a * uOpacity); return; }

    vec3 N = normalize(vNormal);
    vec3 V = normalize(uCamPos - vWorld);
    if (!gl_FrontFacing) N = -N;

    // camera-anchored two point lighting
    vec3 L1 = normalize(V + vec3(0.35, 0.45, 0.0));
    vec3 L2 = normalize(-V + vec3(-0.4, -0.2, 0.3));

    float d1 = max(dot(N, L1), 0.0);
    float d2 = max(dot(N, L2), 0.0) * 0.28;
    float amb = 0.28;

    vec3 H = normalize(L1 + V);
    float spec = pow(max(dot(N, H), 0.0), uShininess) * uSpecular;

    vec3 c = base.rgb * (amb + d1 * 0.75 + d2) + vec3(spec);
    oColor = vec4(c, base.a * uOpacity);
}";

    // ------------------------------------------------------ ФОН И РЕЗОЛВ
    // Полноэкранный треугольник строится без вершинного буфера: позиции
    // считаются прямо из gl_VertexID при вызове DrawArrays(Triangles, 0, 3).

    public const string FullscreenVert = @"
#version 330 core
out vec2 vUV;
void main()
{
    vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    vUV = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

    public const string BackgroundFrag = @"
#version 330 core
in vec2 vUV;
uniform vec4 uTop;
uniform vec4 uBottom;
out vec4 oColor;
void main()
{
    oColor = mix(uBottom, uTop, vUV.y);
}";

}
