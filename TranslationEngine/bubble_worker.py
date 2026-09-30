import json
import sys
import traceback
from datetime import datetime, timezone
from pathlib import Path

import cv2
import numpy as np
from PIL import Image


sys.stdin.reconfigure(encoding="utf-8")
sys.stdout.reconfigure(encoding="utf-8")

detector = None


def get_detector():
    global detector
    if detector is None:
        from rapidocr_onnxruntime import RapidOCR
        detector = RapidOCR()
    return detector


def nms(regions, threshold=0.55):
    kept = []
    for candidate in sorted(regions, key=lambda r: r[4], reverse=True):
        x, y, w, h, _ = candidate
        area = w * h
        overlaps = False
        for other in kept:
            ox, oy, ow, oh, _ = other
            intersection = max(0, min(x + w, ox + ow) - max(x, ox)) * max(0, min(y + h, oy + oh) - max(y, oy))
            union = area + ow * oh - intersection
            if union and intersection / union > threshold:
                overlaps = True
                break
        if not overlaps:
            kept.append(candidate)
    return kept


def true_runs(values):
    result = []
    start = None
    for index, value in enumerate(values):
        if value and start is None:
            start = index
        elif not value and start is not None:
            result.append((start, index))
            start = None
    if start is not None:
        result.append((start, len(values)))
    return result


def detect_panels(gray, rtl):
    height, width = gray.shape
    page_area = width * height
    dark = gray < 85

    # XY-cut the page at long horizontal and vertical panel rules. Thick art
    # regions are ignored as separators, which prevents black artwork from
    # being mistaken for a gutter.
    horizontal_runs = true_runs(np.mean(dark, axis=1) > 0.52)
    horizontal_runs = [(a, b) for a, b in horizontal_runs if b - a <= height * 0.08]
    y_bounds = [0] + [round((a + b) / 2) for a, b in horizontal_runs] + [height]
    y_bands = [(a, b) for a, b in zip(y_bounds, y_bounds[1:]) if b - a >= height * 0.085]
    panels = []

    for y0, y1 in y_bands:
        column_scores = np.mean(dark[y0:y1, :], axis=0)
        vertical_runs = true_runs(column_scores > 0.60)
        vertical_runs = [(a, b) for a, b in vertical_runs if b - a <= width * 0.04]
        x_bounds = [0] + [round((a + b) / 2) for a, b in vertical_runs] + [width]
        x_bands = [(a, b) for a, b in zip(x_bounds, x_bounds[1:]) if b - a >= width * 0.12]
        for x0, x1 in x_bands:
            w, h = x1 - x0, y1 - y0
            if w * h < page_area * 0.035:
                continue
            inset = max(0, round(min(w, h) * 0.005))
            panels.append((x0 + inset, y0 + inset, max(1, w - inset * 2), max(1, h - inset * 2), 0.91))

    # If the page has no usable gutter structure, look for large closed panel
    # borders. A whole-page region remains the final graceful fallback.
    if len(panels) <= 1:
        edges = cv2.Canny(cv2.GaussianBlur(gray, (5, 5), 0), 40, 120)
        edges = cv2.morphologyEx(edges, cv2.MORPH_CLOSE, np.ones((5, 5), np.uint8), iterations=2)
        contours, _ = cv2.findContours(edges, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
        contour_panels = []
        for contour in contours:
            x, y, w, h = cv2.boundingRect(contour)
            area_ratio = w * h / page_area
            rectangularity = cv2.contourArea(contour) / max(1, w * h)
            if 0.035 <= area_ratio <= 0.82 and 0.2 <= w / h <= 5.0 and rectangularity >= 0.58:
                contour_panels.append((x, y, w, h, min(0.88, 0.52 + rectangularity * 0.38)))
        contour_panels = nms(contour_panels, 0.42)
        # Prefer a useful multi-panel contour result; nested art contours are
        # rejected when their centers sit inside a larger retained panel.
        selected = []
        for candidate in sorted(contour_panels, key=lambda r: r[2] * r[3], reverse=True):
            cx, cy = candidate[0] + candidate[2] / 2, candidate[1] + candidate[3] / 2
            if any(p[0] <= cx <= p[0] + p[2] and p[1] <= cy <= p[1] + p[3] for p in selected):
                continue
            selected.append(candidate)
        if len(selected) >= 2:
            panels = selected[:16]

    if not panels:
        panels = [(0, 0, width, height, 0.45)]

    median_height = float(np.median([r[3] for r in panels])) if panels else 1.0
    panels.sort(key=lambda r: (round((r[1] + r[3] / 2) / max(1, median_height * 0.58)), -(r[0] + r[2] / 2) if rtl else r[0] + r[2] / 2))
    return [{
        "x": x / width,
        "y": y / height,
        "width": w / width,
        "height": h / height,
        "confidence": confidence,
        "order": order,
    } for order, (x, y, w, h, confidence) in enumerate(panels, 1)]


def analyze(image_path, page_index, rtl):
    data = np.fromfile(image_path, dtype=np.uint8)
    image = cv2.imdecode(data, cv2.IMREAD_COLOR)
    if image is None:
        raise ValueError("The page image could not be decoded.")

    original_h, original_w = image.shape[:2]
    scale = min(1.0, 1400.0 / max(original_w, original_h))
    if scale < 1.0:
        image = cv2.resize(image, (round(original_w * scale), round(original_h * scale)), interpolation=cv2.INTER_AREA)
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    height, width = gray.shape
    page_area = width * height

    # Closed light regions are a strong, inexpensive signal for speech balloons.
    smooth = cv2.GaussianBlur(gray, (5, 5), 0)
    light = cv2.threshold(smooth, 226, 255, cv2.THRESH_BINARY)[1]
    light = cv2.morphologyEx(light, cv2.MORPH_CLOSE, np.ones((7, 7), np.uint8), iterations=2)
    contours, _ = cv2.findContours(light, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
    candidates = []

    # RapidOCR's compact DB detector is language-independent. We only use its
    # local geometry output; recognition is deliberately disabled.
    detected_boxes, _ = get_detector()(image, use_cls=False, use_rec=False)
    text_rects = []
    for box in detected_boxes or []:
        points = np.asarray(box, dtype=np.float32)
        x, y, w, h = cv2.boundingRect(points.astype(np.int32))
        if w >= 8 and h >= 8 and w * h < page_area * 0.08:
            text_rects.append((x, y, w, h))

    parents = list(range(len(text_rects)))

    def find(value):
        while parents[value] != value:
            parents[value] = parents[parents[value]]
            value = parents[value]
        return value

    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb:
            parents[rb] = ra

    for i, a in enumerate(text_rects):
        ax, ay, aw, ah = a
        for j in range(i + 1, len(text_rects)):
            bx, by, bw, bh = text_rects[j]
            x_overlap = max(0, min(ax + aw, bx + bw) - max(ax, bx))
            y_overlap = max(0, min(ay + ah, by + bh) - max(ay, by))
            vertical_gap = max(0, max(ay, by) - min(ay + ah, by + bh))
            horizontal_gap = max(0, max(ax, bx) - min(ax + aw, bx + bw))
            line_height = max(ah, bh)
            same_column = x_overlap >= min(aw, bw) * 0.18 or abs((ax + aw / 2) - (bx + bw / 2)) < max(aw, bw) * 0.72
            same_row = y_overlap >= min(ah, bh) * 0.28
            if (vertical_gap <= line_height * 1.45 and same_column) or (horizontal_gap <= line_height * 1.1 and same_row):
                union(i, j)

    groups = {}
    for index, rect in enumerate(text_rects):
        groups.setdefault(find(index), []).append(rect)
    for group in groups.values():
        x = min(r[0] for r in group)
        y = min(r[1] for r in group)
        right = max(r[0] + r[2] for r in group)
        bottom = max(r[1] + r[3] for r in group)
        w, h = right - x, bottom - y
        line_height = float(np.median([r[3] for r in group]))
        pad_x = max(12, round(w * 0.18), round(line_height * 1.1))
        pad_y = max(12, round(h * 0.16), round(line_height * 0.9))
        px, py = max(0, x - pad_x), max(0, y - pad_y)
        pw, ph = min(width, right + pad_x) - px, min(height, bottom + pad_y) - py
        light_ratio = float(np.mean(gray[py:py + ph, px:px + pw] > 190))
        if light_ratio >= 0.42 and pw * ph <= page_area * 0.12:
            candidates.append((px, py, pw, ph, min(0.97, 0.72 + len(group) * 0.035 + light_ratio * 0.08)))

    # Use the geometric pass only when the text detector found nothing. It is a
    # fallback for unusual lettering, not a source of artwork false positives.
    for contour in contours if not text_rects else []:
        x, y, w, h = cv2.boundingRect(contour)
        area_ratio = (w * h) / page_area
        if area_ratio < 0.002 or area_ratio > 0.16 or w < 42 or h < 34:
            continue
        aspect = w / h
        if aspect < 0.28 or aspect > 4.5:
            continue
        contour_area = cv2.contourArea(contour)
        fill = contour_area / max(1, w * h)
        if fill < 0.48:
            continue

        roi = gray[y:y + h, x:x + w]
        ink_ratio = float(np.mean(roi < 170))
        light_ratio = float(np.mean(roi > 210))
        if ink_ratio < 0.006 or ink_ratio > 0.34 or light_ratio < 0.58:
            continue

        # Slight padding preserves balloon borders in the magnified crop.
        pad_x, pad_y = max(5, round(w * 0.07)), max(5, round(h * 0.09))
        px, py = max(0, x - pad_x), max(0, y - pad_y)
        pw, ph = min(width, x + w + pad_x) - px, min(height, y + h + pad_y) - py
        confidence = min(0.98, 0.42 + fill * 0.28 + light_ratio * 0.22 + min(ink_ratio, 0.12) * 0.65)
        candidates.append((px, py, pw, ph, confidence))

    regions = nms(candidates)

    # Reading order: top-to-bottom bands, then right-to-left for manga.
    median_height = float(np.median([r[3] for r in regions])) if regions else 1.0
    regions.sort(key=lambda r: (round((r[1] + r[3] / 2) / max(1, median_height * 0.65)), -(r[0] + r[2] / 2) if rtl else r[0] + r[2] / 2))
    bubbles = []
    for order, (x, y, w, h, confidence) in enumerate(regions, 1):
        bubbles.append({
            "x": x / width,
            "y": y / height,
            "width": w / width,
            "height": h / height,
            "confidence": confidence,
            "order": order,
        })

    return {
        "version": 2,
        "pageIndex": page_index,
        "imageWidth": original_w,
        "imageHeight": original_h,
        "analyzedAt": datetime.now(timezone.utc).isoformat(),
        "bubbles": bubbles,
        "panels": detect_panels(gray, rtl),
    }


def crop(request):
    source = Path(request["imagePath"])
    destination = Path(request["outputPath"])
    destination.parent.mkdir(parents=True, exist_ok=True)
    with Image.open(source) as image:
        width, height = image.size
        x = max(0, round(request["x"] * width))
        y = max(0, round(request["y"] * height))
        right = min(width, round((request["x"] + request["width"]) * width))
        bottom = min(height, round((request["y"] + request["height"]) * height))
        if right <= x or bottom <= y:
            raise ValueError("The detected speech region is empty.")
        result = image.crop((x, y, right, bottom)).convert("RGB")
        result.save(destination, "JPEG", quality=94, optimize=True)
    return {"outputPath": str(destination)}


for line in sys.stdin:
    request_id = None
    try:
        request = json.loads(line.lstrip("\ufeff"))
        request_id = request.get("id")
        if request.get("kind") == "crop":
            result = crop(request)
        else:
            result = analyze(request["imagePath"], request["pageIndex"], bool(request.get("rtl", False)))
        response = {"id": request_id, "result": result}
    except Exception as exc:
        response = {"id": request_id, "error": str(exc), "detail": traceback.format_exc(limit=3)}
    print(json.dumps(response, ensure_ascii=False), flush=True)
