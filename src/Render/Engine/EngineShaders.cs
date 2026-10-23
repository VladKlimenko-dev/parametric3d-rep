namespace Parametric3D.Render.Engine;

/// <summary>
/// ШЕЙДЕРЫ ДВИЖКА. Общее подмножество OpenGL 3.3 и OpenGL ES 3.0 (заголовок меняется под контекст — ShaderProgram).
///
/// Признаки подсветки (наведение, выбор, ошибка) не лежат в буферах вершин: у каждого объекта сцены есть номер выбора,
/// а признаки — в маленькой текстуре по этим номерам (uFlags, texelFetch). Смена подсветки — одна запись в текстуру,
/// а не перезаливка сеток. Тот же номер в проходе выбора (uPickMode) пишется цветом — под курсором читается один пиксель.
/// </summary>
internal static class EngineShaders
{
    private const string Flags = @"
uniform sampler2D uFlags;
uniform int uFlagsW;
uint flagsOf(uint pick)
{
    if (pick == 0u) return 0u;
    int i = int(pick);
    return uint(texelFetch(uFlags, ivec2(i % uFlagsW, i / uFlagsW), 0).r * 255.0 + 0.5);
}
";

    private const string PickOut = @"
vec4 encodePick(uint p)
{
    return vec4(float(p & 255u), float((p >> 8) & 255u), float((p >> 16) & 255u), 255.0) / 255.0;
}
";

    // ------------------------------------------------------------------ сетки тел

    public const string MeshVert = @"#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNrm;
layout(location = 2) in uint aPick;
uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProj;
uniform vec2 uViewport;
uniform float uOutlinePx;
out vec3 vN;
out vec3 vV;
out vec3 vW;
flat out uint vFlags;
flat out uint vPick;
" + Flags + @"
void main()
{
    vec4 wp = uModel * vec4(aPos, 1.0);
    vW = wp.xyz;
    vec4 vp = uView * wp;
    vec3 n = normalize(mat3(uView * uModel) * aNrm);
    vN = n;
    vV = vp.xyz;
    vec4 clip = uProj * vp;
    if (uOutlinePx > 0.0)
    {
        vec2 d = (uProj * vec4(n, 0.0)).xy;
        float l = length(d);
        if (l > 1e-8) clip.xy += (d / l) * uOutlinePx * 2.0 / uViewport * clip.w;
    }
    gl_Position = clip;
    vPick = aPick;
    vFlags = flagsOf(aPick);
}";

    public const string MeshFrag = @"#version 330 core
in vec3 vN;
in vec3 vV;
in vec3 vW;
flat in uint vFlags;
flat in uint vPick;
uniform vec4 uClip;         // u13: плоскость сечения (n, d): отсекается dot(n, p) + d > 0; n = 0 — нет
uniform int uZebra;         // u13: зебра — полосы отражения
uniform vec4 uBase;
uniform vec4 uHover;
uniform vec4 uSelect;
uniform vec4 uError;
uniform float uOpacity;
uniform int uMode;          // 0 lit, 1 flat colour, 2 pick
uniform vec4 uFlat;
uniform vec4 uMat;          // u10: стиль «Материал» — x сила блика, y резкость, z металл, w включён
out vec4 oColor;
" + PickOut + @"
vec3 toLin(vec3 c) { return pow(c, vec3(2.2)); }

void main()
{
    if (dot(uClip.xyz, uClip.xyz) > 0.0 && dot(uClip.xyz, vW) + uClip.w > 0.0) discard;
    if (uMode == 2) { oColor = encodePick(vPick); return; }
    if (uMode == 1) { oColor = uFlat; return; }

    vec3 base = uBase.rgb;
    if ((vFlags & 16u) != 0u)     base = mix(base, vec3(0.95, 0.72, 0.30), 0.55);   // u13: поверхность (квилт)
    if ((vFlags & 8u) != 0u)      base = mix(base, uError.rgb, 0.55);
    if ((vFlags & 2u) != 0u)      base = mix(base, uSelect.rgb, 0.45);
    else if ((vFlags & 1u) != 0u) base = mix(base, uHover.rgb, 0.35);
    vec3 b = toLin(base);

    vec3 N = normalize(vN);
    if (!gl_FrontFacing) N = -N;
    vec3 V = normalize(-vV);

    // студийный свет, привязанный к камере: верхний ключевой, боковой заполняющий, полусфера неба и пола
    vec3 L1 = normalize(vec3(-0.45, 0.65, 0.62));
    vec3 L2 = normalize(vec3(0.75, 0.10, 0.45));
    vec3 hemi = mix(vec3(0.16, 0.165, 0.18), vec3(0.40, 0.42, 0.46), N.y * 0.5 + 0.5);
    float d1 = max(dot(N, L1), 0.0);
    float d2 = max(dot(N, L2), 0.0);
    if (uMat.w > 0.5)
    {
        // МАТЕРИАЛ: у металла рассеянного света меньше, блик и отражение — цвета материала; у пластика — белый блик
        float metal = uMat.z;
        vec3 Rv = reflect(-V, N);
        // окружение-студия: тёмный пол, светлое небо, яркая полоса у горизонта
        vec3 env = mix(vec3(0.05, 0.05, 0.06), vec3(0.62, 0.66, 0.72), smoothstep(-0.35, 0.55, Rv.y))
                 + vec3(0.55) * exp(-pow((Rv.y - 0.08) * 7.0, 2.0));
        vec3 kd = b * (1.0 - 0.75 * metal);
        vec3 diffM = kd * (hemi + d1 * 0.80 * vec3(1.0, 0.98, 0.95) + d2 * 0.28 * vec3(0.85, 0.90, 1.0));
        vec3 ks = mix(vec3(1.0), b, metal) * uMat.x;
        float sp1 = pow(max(dot(N, normalize(L1 + V)), 0.0), max(uMat.y, 2.0));
        float sp2 = pow(max(dot(N, normalize(L2 + V)), 0.0), max(uMat.y * 0.4, 2.0)) * 0.25;
        float F = 0.04 + 0.96 * pow(1.0 - max(dot(N, V), 0.0), 5.0);
        vec3 refl = env * mix(vec3(F) * uMat.x * 0.6, b * (0.55 + 0.45 * F), metal);
        vec3 cm = diffM + ks * (sp1 + sp2) * 1.4 + refl;
        cm = cm / (1.0 + cm * 0.10);
        oColor = vec4(pow(cm, vec3(1.0 / 2.2)), uBase.a * uOpacity);
        return;
    }
    if (uZebra == 1)
    {
        vec3 Rz = reflect(-V, N);
        float st = smoothstep(0.45, 0.55, abs(fract(Rz.y * 5.0) * 2.0 - 1.0));
        vec3 cz = mix(vec3(0.06), vec3(0.96), st);
        if (!gl_FrontFacing) cz *= 0.4;
        oColor = vec4(cz, uBase.a * uOpacity);
        return;
    }
    if (!gl_FrontFacing && dot(uClip.xyz, uClip.xyz) > 0.0) b *= 0.45;   // u13: внутренность в сечении темнее
    vec3 diff = b * (hemi + d1 * 0.80 * vec3(1.0, 0.98, 0.95) + d2 * 0.28 * vec3(0.85, 0.90, 1.0));
    float s1 = pow(max(dot(N, normalize(L1 + V)), 0.0), 64.0) * 0.30;
    float s2 = pow(max(dot(N, normalize(L2 + V)), 0.0), 24.0) * 0.06;
    float fres = pow(1.0 - max(dot(N, V), 0.0), 4.0) * 0.16;
    vec3 c = diff + vec3(s1 + s2) + fres * vec3(0.80, 0.85, 0.95);
    c = c / (1.0 + c * 0.12);
    oColor = vec4(pow(c, vec3(1.0 / 2.2)), uBase.a * uOpacity);
}";

    // ------------------------------------------------------------------ линии: экземпляры-полосы постоянной ширины

    public const string LineVert = @"#version 330 core
layout(location = 0) in vec3 aA;
layout(location = 1) in vec3 aB;
layout(location = 2) in vec4 aColor;
layout(location = 3) in float aWidth;
layout(location = 4) in float aDash;
layout(location = 5) in uint aPick;
uniform mat4 uView;
uniform mat4 uProj;
uniform vec2 uViewport;
uniform float uScale;
uniform float uNear;        // > 0 — перспектива: отрезок обрезается ближней плоскостью
uniform float uDepthBias;
uniform float uBiasPx;      // k98: > 0 — сдвиг к глазу на столько пикселей (в мировых единицах на глубине точки) вместо uDepthBias
uniform float uBiasWorld;   // k102: > 0 — сдвиг к глазу на столько мировых единиц (не больше uBiasPx пикселей)
uniform float uBonus;
uniform float uWidthOverride;
uniform vec4 uHover;
uniform vec4 uSelect;
uniform vec4 uColorOverride;
uniform float uDashOverride;
out vec3 vW;
out float vAcross;
out float vAlong;
flat out float vHalf;
flat out float vDash;
flat out vec4 vColor;
flat out uint vPick;
" + Flags + @"
void main()
{
    int id = gl_VertexID;
    float side = (id == 0 || id == 2) ? -1.0 : 1.0;
    bool atB = id >= 2;

    vec4 va = uView * vec4(aA, 1.0);
    vec4 vb = uView * vec4(aB, 1.0);
    if (uNear > 0.0)
    {
        float za = -va.z, zb = -vb.z;
        if (za < uNear && zb < uNear) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
        if (za < uNear) va = mix(va, vb, (uNear - za) / (zb - za));
        else if (zb < uNear) vb = mix(vb, va, (uNear - zb) / (za - zb));
    }
    vec4 ca = uProj * va;
    vec4 cb = uProj * vb;
    vec2 sa = (ca.xy / ca.w * 0.5 + 0.5) * uViewport;
    vec2 sb = (cb.xy / cb.w * 0.5 + 0.5) * uViewport;
    vec2 d = sb - sa;
    float len = length(d);
    vec2 dir = len > 1e-4 ? d / len : vec2(1.0, 0.0);
    vec2 nrm = vec2(-dir.y, dir.x);

    uint f = flagsOf(aPick);
    float w = aWidth * uScale;
    vec4 col = aColor;
    if (uColorOverride.a > 0.0) col = uColorOverride;
    if ((f & 2u) != 0u) { col = vec4(uSelect.rgb, max(col.a, 0.9)); w += uBonus * uScale; }
    else if ((f & 1u) != 0u) { col = vec4(uHover.rgb, max(col.a, 0.9)); w += uBonus * uScale; }
    if (uWidthOverride > 0.0) w = uWidthOverride * uScale;

    float half_ = w * 0.5;
    float ext = half_ + 1.0;
    vec2 s = (atB ? sb : sa) + nrm * side * ext + dir * (atB ? 1.0 : -1.0) * 0.5;
    vec4 c = atB ? cb : ca;
    float z = c.z / c.w - uDepthBias;
    if (uBiasPx > 0.0)
    {
        vec4 vv = atB ? vb : va;
        float pxW = (uNear > 0.0 ? -vv.z : 1.0) * 2.0 / (uProj[1][1] * uViewport.y);
        vv.z += uBiasWorld > 0.0 ? min(uBiasWorld, uBiasPx * pxW) : uBiasPx * pxW;
        vec4 cz = uProj * vv;
        z = cz.z / cz.w;
    }
    gl_Position = vec4(s / uViewport * 2.0 - 1.0, z, 1.0);

    vW = atB ? aB : aA;
    vAcross = side * ext;
    vAlong = atB ? len : 0.0;
    vHalf = half_;
    vDash = uDashOverride > 0.0 ? uDashOverride * uScale : aDash * uScale;
    vColor = col;
    vPick = aPick;
}";

    public const string LineFrag = @"#version 330 core
in vec3 vW;
uniform vec4 uClip;
in float vAcross;
in float vAlong;
flat in float vHalf;
flat in float vDash;
flat in vec4 vColor;
flat in uint vPick;
uniform int uPickMode;
out vec4 oColor;
" + PickOut + @"
void main()
{
    if (dot(uClip.xyz, uClip.xyz) > 0.0 && dot(uClip.xyz, vW) + uClip.w > 0.0) discard;
    float d = abs(vAcross);
    if (uPickMode == 1)
    {
        if (d > vHalf) discard;
        oColor = encodePick(vPick);
        return;
    }
    if (vDash > 0.0 && mod(vAlong, vDash) > vDash * 0.62) discard;
    float a = clamp(vHalf + 0.5 - d, 0.0, 1.0);
    if (a <= 0.003) discard;
    oColor = vec4(vColor.rgb, vColor.a * a);
}";

    // ------------------------------------------------------------------ точки

    public const string PointVert = @"#version 330 core
layout(location = 0) in vec3 aP;
layout(location = 1) in vec4 aColor;
layout(location = 2) in float aSize;
layout(location = 3) in uint aShape;
layout(location = 4) in uint aPick;
uniform mat4 uView;
uniform mat4 uProj;
uniform vec2 uViewport;
uniform float uScale;
uniform float uDepthBias;
uniform float uSizeOverride;
uniform float uBonus;
uniform int uOnlyFlagged;
uniform vec4 uHover;
uniform vec4 uSelect;
out vec2 vLocal;
flat out float vHalf;
flat out uint vShape;
flat out vec4 vColor;
flat out uint vPick;
" + Flags + @"
void main()
{
    uint f = flagsOf(aPick);
    if (uOnlyFlagged == 1 && (f & 3u) == 0u) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
    vec4 c = uProj * (uView * vec4(aP, 1.0));
    if (c.w <= 1e-6) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
    float size = aSize * uScale;
    vec4 col = aColor;
    if ((f & 2u) != 0u) { col = vec4(uSelect.rgb, 1.0); size += uBonus * uScale; }
    else if ((f & 1u) != 0u) { col = vec4(uHover.rgb, 1.0); size += uBonus * uScale; }
    if (uSizeOverride > 0.0) size = uSizeOverride * uScale;
    int id = gl_VertexID;
    vec2 corner = vec2((id == 1 || id == 3) ? 1.0 : -1.0, id >= 2 ? 1.0 : -1.0);
    float half_ = size * 0.5;
    vec2 s = (c.xy / c.w * 0.5 + 0.5) * uViewport + corner * (half_ + 1.0);
    gl_Position = vec4(s / uViewport * 2.0 - 1.0, c.z / c.w - uDepthBias, 1.0);
    vLocal = corner * (half_ + 1.0);
    vHalf = half_;
    vShape = aShape;
    vColor = col;
    vPick = aPick;
}";

    public const string PointFrag = @"#version 330 core
in vec2 vLocal;
flat in float vHalf;
flat in uint vShape;
flat in vec4 vColor;
flat in uint vPick;
uniform int uPickMode;
out vec4 oColor;
" + PickOut + @"
void main()
{
    float d;
    if (vShape == 1u) d = max(abs(vLocal.x), abs(vLocal.y));                 // квадрат
    else if (vShape == 2u) d = (abs(vLocal.x) + abs(vLocal.y)) * 0.7071;     // ромб
    else if (vShape == 3u)                                                     // крест
    {
        float ax = abs(vLocal.x), ay = abs(vLocal.y);
        d = max(ax, ay) > vHalf ? 1e3 : min(ax, ay) + vHalf - 1.0;
    }
    else d = length(vLocal);                                                  // круг
    if (uPickMode == 1)
    {
        if (max(abs(vLocal.x), abs(vLocal.y)) > vHalf) discard;
        oColor = encodePick(vPick);
        return;
    }
    float a = clamp(vHalf + 0.5 - d, 0.0, 1.0);
    if (a <= 0.003) discard;
    oColor = vec4(vColor.rgb, vColor.a * a);
}";

    // ------------------------------------------------------------------ заливка плоскостей

    public const string FillVert = @"#version 330 core
layout(location = 0) in vec3 aPos;
uniform mat4 uView;
uniform mat4 uProj;
uniform uint uPick;
flat out uint vFlags;
" + Flags + @"
void main()
{
    gl_Position = uProj * (uView * vec4(aPos, 1.0));
    vFlags = flagsOf(uPick);
}";

    public const string FillFrag = @"#version 330 core
flat in uint vFlags;
uniform vec4 uColor;
uniform vec4 uHover;
uniform vec4 uSelect;
uniform int uPickMode;
uniform uint uPick;
out vec4 oColor;
" + PickOut + @"
void main()
{
    if (uPickMode == 1) { oColor = encodePick(uPick); return; }
    vec4 c = uColor;
    if ((vFlags & 2u) != 0u) c = vec4(uSelect.rgb, max(c.a * 2.2, 0.32));
    else if ((vFlags & 1u) != 0u) c = vec4(uHover.rgb, max(c.a * 1.8, 0.26));
    oColor = c;
}";

    // ------------------------------------------------------------------ k99: крышка сечения

    /// <summary>Четырёхугольник на плоскости сечения (по номеру вершины): центр uO, оси uU, uV, полуразмер uHalf.</summary>
    public const string CapVert = @"#version 330 core
uniform mat4 uView;
uniform mat4 uProj;
uniform vec3 uO;
uniform vec3 uU;
uniform vec3 uV;
uniform float uHalf;
out vec2 vUV;
void main()
{
    vec2 c = vec2((gl_VertexID & 1) == 0 ? -1.0 : 1.0, gl_VertexID >= 2 ? 1.0 : -1.0) * uHalf;
    vUV = c;
    vec3 w = uO + uU * c.x + uV * c.y;
    gl_Position = uProj * (uView * vec4(w, 1.0));
}";

    /// <summary>Заливка цветом материала и штриховка: шаг uStep (мир), угол uAngle, крест — uCross; линия ~1.1 px.</summary>
    public const string CapFrag = @"#version 330 core
in vec2 vUV;
uniform vec4 uFill;
uniform vec4 uHatch;
uniform float uStep;
uniform float uAngle;
uniform int uCross;
out vec4 oColor;
float lineAt(float s)
{
    float t = s / uStep;
    float d = abs(fract(t) - 0.5);           // 0.5 — на линии, 0 — посередине между линиями
    float px = (0.5 - d) * uStep / max(fwidth(s), 1e-6);
    return clamp(1.1 - px, 0.0, 1.0);
}
void main()
{
    float ca = cos(uAngle), sa = sin(uAngle);
    float s1 = vUV.x * ca + vUV.y * sa;
    float a = lineAt(s1);
    if (uCross == 1) a = max(a, lineAt(-vUV.x * sa + vUV.y * ca));
    oColor = vec4(mix(uFill.rgb, uHatch.rgb, a), 1.0);
}";

    // ------------------------------------------------------------------ фон

    public const string BackgroundVert = @"#version 330 core
out float vY;
void main()
{
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    vY = p.y;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

    public const string BackgroundFrag = @"#version 330 core
in float vY;
uniform vec4 uTop;
uniform vec4 uBottom;
out vec4 oColor;
void main()
{
    float t = clamp(vY, 0.0, 1.0);
    t = t * t * (3.0 - 2.0 * t);
    oColor = vec4(mix(uBottom.rgb, uTop.rgb, t), 1.0);
}";
}
