"""Remove the 'DeeVid AI' wordmark from the top-right corner of the portrait,
plus the faint leftover glyphs along the very top edge.

Why not plain inpainting: the backdrop there is a smooth dark studio gradient
with fine sensor grain.  cv2.inpaint would reconstruct a gradient but leave a
too-smooth patch and a faint halo around the glyphs.  Instead we fit a low-order
2D polynomial surface to the clean backdrop around the glyphs and refill only
the glyph pixels with that surface plus grain sampled from the backdrop's own
residuals: the gradient stays continuous and the noise texture matches.

Requires opencv + numpy.  If they are not installed globally, run once:
    python -m pip install --target .pylibs opencv-python-headless numpy

Verification is quantitative (no eyeballing):
  * hold-out RMSE of the surface against unseen clean pixels (should be ~grain),
  * no bright pixel may survive where the wordmark was,
  * mean of the filled area vs. the ring around it (seam check),
  * every changed pixel must be inside the detected mask.
"""

import os
import sys

sys.path.insert(0, os.path.abspath(".pylibs"))
import cv2
import numpy as np

SRC = "photo_src.png"
OUT = "clean_photo.png"
DEG = 3

img = cv2.imread(SRC, cv2.IMREAD_COLOR)
assert img is not None, "cannot read " + SRC
h, w = img.shape[:2]
gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY).astype(np.float64)


def poly_terms(roi, deg=DEG):
    """Polynomial design matrix over roi=(y0,y1,x0,x1), centred and scaled."""
    y0, y1, x0, x1 = roi
    yy, xx = np.mgrid[y0:y1, x0:x1]
    s = 1.0 / max(x1 - x0, y1 - y0)
    x = (xx - (x0 + x1) / 2.0) * s
    y = (yy - (y0 + y1) / 2.0) * s
    cols = [(x ** i) * (y ** j) for i in range(deg + 1) for j in range(deg + 1 - i)]
    return np.stack([c.ravel() for c in cols], axis=1)


def fit_surface(roi, mask, deg=DEG):
    A = poly_terms(roi, deg)
    v = gray[roi[0]:roi[1], roi[2]:roi[3]].ravel()
    clean = np.where(mask[roi[0]:roi[1], roi[2]:roi[3]].ravel() == 0)[0]
    coef, *_ = np.linalg.lstsq(A[clean], v[clean], rcond=None)
    for _ in range(5):  # robust re-fits: ignore anything not backdrop (hair, falloff)
        r = np.abs(v[clean] - A[clean] @ coef)
        keep = r <= max(3.0 * np.median(r), 2.5)
        coef, *_ = np.linalg.lstsq(A[clean][keep], v[clean][keep], rcond=None)
    resid = v[clean] - A[clean] @ coef
    keep = np.abs(resid) <= max(3.0 * np.median(np.abs(resid)), 2.5)
    return A, coef, resid[keep]


def holdout_rmse(roi, mask, deg=DEG):
    A = poly_terms(roi, deg)
    v = gray[roi[0]:roi[1], roi[2]:roi[3]].ravel()
    clean = np.where(mask[roi[0]:roi[1], roi[2]:roi[3]].ravel() == 0)[0]
    rng = np.random.default_rng(0)
    rng.shuffle(clean)
    half = clean.size // 2
    coef, *_ = np.linalg.lstsq(A[clean[:half]], v[clean[:half]], rcond=None)
    err = v[clean[half:]] - A[clean[half:]] @ coef
    return float(np.sqrt((err ** 2).mean())), float(np.abs(err).max())


def mask_bright(roi, offset, pad):
    """Overlay glyphs are much brighter than the backdrop -> simple threshold."""
    y0, y1, x0, x1 = roi
    patch = gray[y0:y1, x0:x1]
    mask = np.zeros((h, w), np.uint8)
    mask[y0:y1, x0:x1] = ((patch > float(np.median(patch)) + offset) * 255).astype(np.uint8)
    if pad:
        mask = cv2.dilate(mask, np.ones((2 * pad + 1, 2 * pad + 1), np.uint8))
    return mask


def mask_strokes(roi, delta, offset, max_area, max_h, pad):
    """Faint text: brighter than the local backdrop AND high-passed (thin strokes)."""
    y0, y1, x0, x1 = roi
    hp = gray - cv2.GaussianBlur(gray, (0, 0), 4.0)
    patch = gray[y0:y1, x0:x1]
    brighter = patch > float(np.median(patch)) + offset
    binary = ((brighter & (hp[y0:y1, x0:x1] > delta)) * 255).astype(np.uint8)
    n, lab, stats, _ = cv2.connectedComponentsWithStats(binary, 8)
    keep = np.zeros(binary.shape, np.uint8)
    for i in range(1, n):
        x, y, ww, hh, area = stats[i]
        if area <= max_area and hh <= max_h:
            keep[lab == i] = 255
    mask = np.zeros((h, w), np.uint8)
    mask[y0:y1, x0:x1] = keep
    if pad:
        mask = cv2.dilate(mask, np.ones((2 * pad + 1, 2 * pad + 1), np.uint8))
    return mask


def refill(img, roi, mask, seed):
    """Replace masked pixels by backdrop surface + matched grain, feathered."""
    y0, y1, x0, x1 = roi
    A, coef, resid = fit_surface(roi, mask)
    surf = (A @ coef).reshape(y1 - y0, x1 - x0)
    grain = np.random.default_rng(seed).choice(resid, size=surf.size, replace=True)
    fill = np.clip(np.rint(surf + grain.reshape(surf.shape)), 0, 255)

    out = img.copy()
    sel = mask[y0:y1, x0:x1] > 0
    for c in range(3):
        ch = out[y0:y1, x0:x1, c].astype(np.float64)
        ch[sel] = fill[sel]
        out[y0:y1, x0:x1, c] = np.clip(np.rint(ch), 0, 255).astype(np.uint8)

    alpha = cv2.GaussianBlur(mask.astype(np.float32) / 255.0, (0, 0), 1.0)[y0:y1, x0:x1]
    a = np.clip(alpha, 0.0, 1.0)[..., None]
    orig = img[y0:y1, x0:x1].astype(np.float64)
    patched = out[y0:y1, x0:x1].astype(np.float64)
    out[y0:y1, x0:x1] = np.clip(np.rint(orig * (1 - a) + patched * a), 0, 255).astype(np.uint8)
    return out, float(resid.std())


JOBS = [
    # name, roi (y0,y1,x0,x1), mask fn, seed
    ("top-right wordmark", (0, 80, 760, w),
     lambda roi: mask_bright(roi, offset=10.0, pad=3), 7),
    ("top-edge sliver", (0, 12, 0, 520),
     lambda roi: mask_strokes(roi, delta=6.0, offset=10.0, max_area=80, max_h=10, pad=2), 11),
]

print(f"image {w}x{h}")
result = img
union = np.zeros((h, w), np.uint8)
for name, roi, make_mask, seed in JOBS:
    mask = make_mask(roi)
    rmse, mx = holdout_rmse(roi, mask)
    before = cv2.cvtColor(result, cv2.COLOR_BGR2GRAY).astype(np.float64)
    result, gstd = refill(result, roi, mask, seed)
    union = cv2.bitwise_or(union, mask)
    print(f"{name}: {int((mask>0).sum())} px masked | grain std {gstd:.2f} | "
          f"surface hold-out RMSE {rmse:.2f} (max {mx:.1f}) gray levels")

cv2.imwrite(OUT, result)
print(f"wrote {OUT} ({w}x{h}, {os.path.getsize(OUT)/1e6:.2f} MB, "
      f"{result.shape[2]} channels)")

# ------------------------------ verification ------------------------------
after = cv2.cvtColor(result, cv2.COLOR_BGR2GRAY).astype(np.float64)
inner = union > 0
ring = (cv2.dilate(inner.astype(np.uint8), np.ones((17, 17), np.uint8)) > 0) & ~inner
print(f"filled mean {after[inner].mean():.2f} vs ring mean {after[ring].mean():.2f} "
      f"(diff {abs(after[inner].mean() - after[ring].mean()):.2f})")
print("bright(>60) px left in wordmark box:", int((after[25:72, 795:1005] > 60).sum()))
print("pixels >52 left in the top 10 rows, x<520:", int((after[0:10, 0:520] > 52).sum()))

diff = np.abs(after - gray)
outside = (diff > 0) & ~cv2.dilate(inner.astype(np.uint8), np.ones((5, 5), np.uint8)).astype(bool)
print(f"pixels changed outside the masked areas: {int(outside.sum())}")
print(f"max change inside masked areas: {diff[inner].max():.0f} gray levels")
q = cv2.imread(OUT, cv2.IMREAD_UNCHANGED)
print("saved file:", q.shape, q.dtype, "| identical to buffer:", bool((q == result).all()))
