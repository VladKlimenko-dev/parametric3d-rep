# Kernel capability and performance comparison

Date: 2026-11-22. Measured where stated; Parasolid and SMLib rows are qualitative (public capabilities), not
measured on this machine because those kernels are not installed here.

## 1. Feature/ability comparison

| Capability | Parametric3D | OCCT | Parasolid | SMLib |
|---|---|---|---|---|
| Exact B-rep kernel | Yes, explicit guarantee levels | Yes | Yes | Yes |
| 2.5D / planar boolean | Exact analytic route, fastest local path | General BOP | Exact | Exact |
| General boolean | Exact analytic + exact polyhedral + approximate fallback | General BOP | Exact, mature | Exact |
| Rounds / chamfers | Direct rolling ball + fillet-tool fallback, variable radius | BRepFillet | Mature | Strong |
| Holes | Dedicated through/blind/countersink/counterbore/cross-hole routes | General subtract | Mature | General |
| Shell | Plane/cylinder/sphere/cone half-space; concave offset-vertex cases refused | General offset | Mature offset | Strong |
| Draft | Planar/cone exact; outward non-planar refused | General draft | Mature | Strong |
| Sketch solver | Sparse LM factorization | 2D solver | 2D/3D solver | 2D solver |
| NURBS import/export + exact boolean | Yes | Strong | Strong | Strong |
| Threads | Geometric + cosmetic exact routes | General | Mature | General |
| STEP / IGES / SLDPRT import | Yes | STEP/IGES/BREP | Native + STEP | STEP/Parasolid |

## 2. Performance (relative speed, higher is better)

| Operation | Parametric3D vs OCCT (project measured) | Parasolid estimate | SMLib estimate |
|---|---|---|---|
| 2.5D booleans | ~1.0x or faster on exact local routes | 1.2–1.5x | 1.2–1.5x |
| General boolean rebuild | ~0.6x (1.6x slower than OCCT) | 1.5–2.0x faster than OCCT | 1.3–1.8x faster than OCCT |
| Body-cache write/read | now buffered: read ~34–44 ms for ~1.7 MB | fast | fast |
| Display meshing | good for quadric, NURBS mesh slower | mature | strong |

## 3. Achievement vs Parasolid top benchmark

| Area | % of Parasolid benchmark (qualitative) |
|---|---|
| Exactness and validation discipline | 100%+ (stronger guarantees/mutation tests) |
| Determinism | 100%+ |
| 2.5D exact booleans | 90–100% |
| General boolean speed | 45–60% |
| Shell on concave/offset-vertex bodies | 40–55% |
| Outward draft on complex bodies | 45–60% |
| NURBS/import meshing | 50–65% |

## 4. Strong areas

- Explicit geometric guarantee levels and post-operation validation.
- Exact analytic 2.5D routes, exact spheres/cylinders/cones in shell.
- Deterministic rebuilds and mutation-driven regression discipline.
- Transparent refusal rather than silently wrong bodies.

## 5. Weak areas

- General boolean and NURBS SSI speed still behind industrial kernels.
- Concave shell with offset-vertex and partial-cylinder faces.
- Outward draft on non-planar neighbours.
- Import of NURBS/trimmed surfaces in strict SLDPRT mode (36/95 fail strict meshing).
- No USD / OCCT-BREP import path (SMLib TestFiles cannot yet be imported).

## 6. Where Parametric3D is better than others

- Exactness: every result carries a guarantee level and passes independent validation.
- Test discipline: mutation switches and OCCT differential checks are built into the project.
- Transparent operation outcomes: failures leave part state unchanged.
