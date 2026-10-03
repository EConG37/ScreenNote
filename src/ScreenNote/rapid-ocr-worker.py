"""Bundled, offline PP-OCRv4 recognizer. One request; image data never leaves this process."""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")


def recognize(path):
    import cv2
    import numpy as np
    from rapidocr_onnxruntime import RapidOCR

    image = cv2.imdecode(np.fromfile(path, dtype=np.uint8), cv2.IMREAD_COLOR)
    if image is None:
        raise ValueError("图片无法读取")
    height, width = image.shape[:2]
    if width * height > 40_000_000:
        raise ValueError("图片过大，请缩小识别区域")
    engine = RapidOCR(intra_op_num_threads=4, inter_op_num_threads=1, use_cls=False,
                      det_limit_side_len=1024, det_limit_type="max", max_side_len=4096)
    lines = []
    tile_height, overlap = 1400, 140
    top = 0
    while top < height:
        bottom = min(height, top + tile_height)
        crop = image[top:bottom]
        scale = min(2.0, 2048.0 / max(width, bottom - top))
        if scale > 1.05:
            crop = cv2.resize(crop, None, fx=scale, fy=scale, interpolation=cv2.INTER_CUBIC)
        else:
            scale = 1.0
        results, _ = engine(crop)
        for box, text, score in results or []:
            if not text.strip():
                continue
            x = min(point[0] for point in box) / scale
            y = sum(point[1] for point in box) / (4 * scale) + top
            if (top > 0 and y < top + overlap / 2) or (bottom < height and y >= bottom - overlap / 2):
                continue
            size = (max(point[1] for point in box) - min(point[1] for point in box)) / scale
            if any(old[2] == text and abs(old[0] - y) <= max(8, size * .6) and abs(old[1] - x) < 20 for old in lines[-40:]):
                continue
            lines.append((y, x, text, float(score), size))
        if bottom == height:
            break
        top = bottom - overlap
    lines.sort(key=lambda line: (line[0], line[1]))
    rows = []
    for line in lines:
        if rows and abs(line[0] - rows[-1][0][0]) <= max(5, min(line[4], rows[-1][0][4]) * .4):
            rows[-1].append(line)
        else:
            rows.append([line])
    text = "\n".join("  ".join(line[2] for line in sorted(row, key=lambda line: line[1])) for row in rows)
    return {"Text": text, "Language": "中文增强 · 本地识别", "Error": None,
            "Confidence": sum(line[3] for line in lines) / len(lines) if lines else None}


try:
    if len(sys.argv) != 2:
        raise ValueError("识别参数不正确")
    result = recognize(sys.argv[1])
except Exception as error:
    result = {"Text": "", "Language": "", "Error": "中文增强识别未完成：" + str(error)}
print(json.dumps(result, ensure_ascii=False))
