"""
편집모드 코너 보정용 인쇄 시트 생성기.

편집모드(F1 → F2)가 화면에 그리는 캘리브레이션 마커 배치를 그대로 종이에 옮긴다.
화면과 같은 규칙을 쓰므로, 시트를 투사 영역 크기로 인쇄하면 프로젝터가 쏘는 마커와
인쇄 마커가 정확히 겹친다.

  * 위치 — CalibrationConfig.DefaultProjectorPoints : (0.15,0.15) (0.85,0.15) (0.85,0.85) (0.15,0.85)
           ArUcoProjectionView.DrawOverlay 는 x 를 화면 폭, y 를 화면 높이 비율로 읽는다.
           그래서 시트의 활성 영역 비율이 프로젝터 화면비와 같아야 한다(--aspect).
  * 크기 — 오버레이 한 변 = manualTargetSize x 화면 짧은 변.
           ArUcoMarkerTexture 가 마커 둘레에 sizePx/8 의 여백(quiet zone)을 붙이므로
           그 정사각형 안에서 검은 마커가 차지하는 비율은 256/320 = 0.8 이다.
  * ID   — ArUcoConfig.calibrationMarkerIds, 딕셔너리는 dictionaryId(1 = DICT_4X4_100).

사용법:
    python make_calibration_sheet.py                     # a4 / a3 / a3 확대판을 만든다
    python make_calibration_sheet.py --page a3 --marker-scale 2
"""

import argparse
import json
import os

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

DPI = 300
PX_PER_MM = DPI / 25.4

# 마커 텍스처 안에서 검은 마커가 차지하는 비율 (→ ArUcoMarkerTexture.Create, quiet = sizePx/8)
MARKER_IN_TEXTURE = 256.0 / (256.0 + 2 * 32.0)

PAGES = {
    # 이름: (페이지 mm, 활성 영역 폭 mm, 위쪽 여백 mm)
    "a4": ((297.0, 210.0), 280.0, 8.0),
    "a3": ((420.0, 297.0), 400.0, 14.0),
}

FONT_PATH = r"C:\Windows\Fonts\malgun.ttf"
FONT_BOLD = r"C:\Windows\Fonts\malgunbd.ttf"

GRAY = (170, 170, 170)
DARK = (60, 60, 60)
BLACK = (0, 0, 0)

# OpenCV PredefinedDictionaryType 순서. 시트에 어떤 딕셔너리로 찍었는지 남기는 용도다.
DICTIONARY_NAMES = {
    0: "DICT_4X4_50", 1: "DICT_4X4_100", 2: "DICT_4X4_250", 3: "DICT_4X4_1000",
    4: "DICT_5X5_50", 5: "DICT_5X5_100", 6: "DICT_5X5_250", 7: "DICT_5X5_1000",
    8: "DICT_6X6_50", 9: "DICT_6X6_100", 10: "DICT_6X6_250", 11: "DICT_6X6_1000",
    12: "DICT_7X7_50", 13: "DICT_7X7_100", 14: "DICT_7X7_250", 15: "DICT_7X7_1000",
    16: "DICT_ARUCO_ORIGINAL",
}

CORNER_NAMES = ["좌상", "우상", "우하", "좌하"]
CORNER_ARROWS = ["\u2196", "\u2197", "\u2198", "\u2199"]


def mm(value):
    """mm → 픽셀"""
    return value * PX_PER_MM


def font(size_mm, bold=False):
    path = FONT_BOLD if bold else FONT_PATH
    return ImageFont.truetype(path, int(round(mm(size_mm))))


def marker_patch(dictionary_id, marker_id, side_px):
    """검은 마커(테두리 포함) 이미지. 목표 픽셀 크기에 정확히 맞춘다."""
    dictionary = cv2.aruco.getPredefinedDictionary(dictionary_id)

    # 6칸(4비트 + 테두리)이 균등하게 나오는 크기로 만든 뒤 목표 크기로 줄인다.
    base = max(6, int(round(side_px / 6.0)) * 6)
    image = cv2.aruco.generateImageMarker(dictionary, marker_id, base, borderBits=1)

    patch = Image.fromarray(image).convert("L")
    if base != side_px:
        # 보간하면 경계가 흐려져 검출률이 떨어진다. 각을 유지한다.
        patch = patch.resize((side_px, side_px), Image.NEAREST)
    return patch


def draw_sheet(page, marker_scale, config, out_path):
    (page_w, page_h), active_w, top_margin = PAGES[page]

    aspect = config["aspect"]
    active_h = active_w / aspect
    if top_margin + active_h > page_h - 25.0:
        raise SystemExit(f"{page} 에 {aspect:.3f} 비율 영역이 들어가지 않습니다.")

    left = (page_w - active_w) / 2.0
    top = top_margin

    # 화면에서의 오버레이 한 변(= manualTargetSize x 짧은 변)과 그 안의 검은 마커
    target_size = config["manualTargetSize"] * marker_scale
    overlay_mm = target_size * min(active_w, active_h)
    marker_mm = overlay_mm * MARKER_IN_TEXTURE

    canvas = Image.new("RGB", (int(round(mm(page_w))), int(round(mm(page_h)))), "white")
    draw = ImageDraw.Draw(canvas)

    # ── 활성 영역(= 프로젝터 화면 전체) ────────────────────────────────────────
    draw.rectangle(
        [mm(left), mm(top), mm(left + active_w), mm(top + active_h)],
        outline=GRAY, width=max(1, int(round(mm(0.25)))),
    )

    # 네 모서리를 진하게 — 투사 영역에 시트를 맞출 때의 기준선
    arm = min(active_w, active_h) * 0.06
    thick = max(1, int(round(mm(0.6))))
    for cx, cy, sx, sy in [
        (left, top, 1, 1),
        (left + active_w, top, -1, 1),
        (left + active_w, top + active_h, -1, -1),
        (left, top + active_h, 1, -1),
    ]:
        draw.line([mm(cx), mm(cy), mm(cx + sx * arm), mm(cy)], fill=DARK, width=thick)
        draw.line([mm(cx), mm(cy), mm(cx), mm(cy + sy * arm)], fill=DARK, width=thick)

    # 화면 중심
    center = (left + active_w / 2.0, top + active_h / 2.0)
    draw.line([mm(center[0] - 4), mm(center[1]), mm(center[0] + 4), mm(center[1])], fill=GRAY)
    draw.line([mm(center[0]), mm(center[1] - 4), mm(center[0]), mm(center[1] + 4)], fill=GRAY)

    # ── 마커 4개 ──────────────────────────────────────────────────────────────
    label_font = font(3.2)
    side_px = int(round(mm(marker_mm)))
    expected = []

    for index, (px, py) in enumerate(config["projectorPoints"]):
        marker_id = config["calibrationMarkerIds"][index]

        cx = left + px * active_w
        cy = top + py * active_h
        expected.append((marker_id, cx, cy))

        patch = marker_patch(config["dictionaryId"], marker_id, side_px)
        canvas.paste(patch.convert("RGB"),
                     (int(round(mm(cx))) - side_px // 2, int(round(mm(cy))) - side_px // 2))

        # 중심 표시. 여백(quiet zone)을 침범하면 검출이 깨지므로 오버레이 사각형 밖에서 시작한다.
        gap = overlay_mm / 2.0 + 2.0
        for dx, dy in [(1, 0), (-1, 0), (0, 1), (0, -1)]:
            draw.line(
                [mm(cx + dx * gap), mm(cy + dy * gap),
                 mm(cx + dx * (gap + 4)), mm(cy + dy * (gap + 4))],
                fill=DARK, width=max(1, int(round(mm(0.3)))),
            )

        text = f"{CORNER_ARROWS[index]} {marker_id}번 · {CORNER_NAMES[index]} ({px:g}, {py:g})"
        draw.text((mm(cx), mm(cy + overlay_mm / 2.0 + 8.0)), text,
                  font=label_font, fill=DARK, anchor="ma")

    # ── 정보 블록 ─────────────────────────────────────────────────────────────
    info_y = top + active_h + 7.5
    draw.text((mm(left), mm(info_y)), "StoryRest 코너 보정 시트",
              font=font(5.0, bold=True), fill=BLACK)

    dictionary_name = DICTIONARY_NAMES.get(config["dictionaryId"], f"dict {config['dictionaryId']}")
    gap_x = (config["projectorPoints"][1][0] - config["projectorPoints"][0][0]) * active_w
    gap_y = (config["projectorPoints"][2][1] - config["projectorPoints"][1][1]) * active_h

    lines = [
        f"{dictionary_name} · ID {', '.join(str(i) for i in config['calibrationMarkerIds'])}"
        f" · 화면비 {aspect:.4g}:1 · {page.upper()} 가로 {DPI}dpi",
        f"바깥 테두리 = 프로젝터 화면 전체 {active_w:g} x {active_h:g} mm"
        f" · 마커 중심 = 화면의 15% / 85% 지점 (중심 간격 {gap_x:.1f} x {gap_y:.1f} mm)",
        f"검은 마커 한 변 {marker_mm:.1f} mm (여백 포함 {overlay_mm:.1f} mm)"
        f" → 편집모드 자리 크기(manualTargetSize) 를 {target_size:.3f} 로 맞추면 화면 사각형과 같아진다",
        "반드시 '실제 크기 100%' 로 인쇄 — 페이지 맞춤을 켜면 비율이 어긋난다. 아래 눈금으로 확인.",
    ]
    for i, line in enumerate(lines):
        draw.text((mm(left), mm(info_y + 7.0 + i * 4.8)), line, font=font(3.3), fill=DARK)

    # 인쇄 배율 확인용 눈금 100mm
    ruler_y = info_y + 7.0 + len(lines) * 4.8 + 5.5
    draw.line([mm(left), mm(ruler_y), mm(left + 100), mm(ruler_y)], fill=BLACK,
              width=max(1, int(round(mm(0.3)))))
    for i in range(11):
        x = left + i * 10
        height = 3.0 if i % 5 == 0 else 1.8
        draw.line([mm(x), mm(ruler_y), mm(x), mm(ruler_y - height)], fill=BLACK,
                  width=max(1, int(round(mm(0.3)))))
    draw.text((mm(left + 103), mm(ruler_y - 3.4)), "100 mm", font=font(3.4), fill=BLACK)

    canvas.save(out_path, dpi=(DPI, DPI))
    return canvas, expected, dict(active=(active_w, active_h), marker_mm=marker_mm,
                                  overlay_mm=overlay_mm, target_size=target_size)


def verify(canvas, expected, dictionary_id):
    """만든 시트를 그대로 검출해 본다. 카메라 없이 ID 와 중심 위치를 확인할 수 있다."""
    gray = cv2.cvtColor(np.array(canvas), cv2.COLOR_RGB2GRAY)
    detector = cv2.aruco.ArucoDetector(
        cv2.aruco.getPredefinedDictionary(dictionary_id), cv2.aruco.DetectorParameters())
    corners, ids, _ = detector.detectMarkers(gray)

    found = {}
    if ids is not None:
        for corner, marker_id in zip(corners, ids.flatten()):
            found[int(marker_id)] = corner.reshape(4, 2).mean(axis=0)

    report = []
    for marker_id, cx, cy in expected:
        if marker_id not in found:
            report.append((marker_id, None))
            continue
        dx = found[marker_id][0] - mm(cx)
        dy = found[marker_id][1] - mm(cy)
        report.append((marker_id, (dx / PX_PER_MM, dy / PX_PER_MM)))
    return report


def load_config(json_path):
    with open(json_path, encoding="utf-8-sig") as f:
        data = json.load(f)

    points = data["sets"][0]["calibration"]["projectorPoints"]
    return {
        "dictionaryId": data.get("dictionaryId", 1),
        "calibrationMarkerIds": data.get("calibrationMarkerIds", [96, 97, 98, 99]),
        "manualTargetSize": data.get("manualTargetSize", 0.12),
        "projectorPoints": [(p["x"], p["y"]) for p in points],
    }


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    default_json = os.path.normpath(os.path.join(here, "..", "..", "Assets", "StreamingAssets", "aruco.json"))

    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default=default_json, help="aruco.json 경로")
    parser.add_argument("--page", choices=list(PAGES), help="한 장만 만들 때")
    parser.add_argument("--aspect", type=float, default=16.0 / 9.0, help="프로젝터 화면비 (기본 16:9)")
    parser.add_argument("--marker-scale", type=float, default=1.0,
                        help="마커만 키운다. 위치 비율은 그대로. 편집모드 자리 크기도 같은 배로 올려야 한다")
    parser.add_argument("--out", default=here)
    args = parser.parse_args()

    config = load_config(args.config)
    config["aspect"] = args.aspect

    jobs = ([(args.page, args.marker_scale)] if args.page
            else [("a4", 1.0), ("a3", 1.0), ("a3", 2.0)])

    for page, scale in jobs:
        suffix = "" if scale == 1.0 else f"_x{scale:g}"
        name = f"calibration_sheet_{page}{suffix}.png"
        path = os.path.join(args.out, name)

        canvas, expected, info = draw_sheet(page, scale, config, path)
        report = verify(canvas, expected, config["dictionaryId"])

        print(f"\n{name}  활성영역 {info['active'][0]:g} x {info['active'][1]:g} mm"
              f" · 마커 {info['marker_mm']:.2f} mm · manualTargetSize {info['target_size']:.3f}")
        for marker_id, error in report:
            if error is None:
                print(f"  {marker_id}번  검출 실패")
            else:
                print(f"  {marker_id}번  검출 OK  중심오차 {error[0]:+.3f}, {error[1]:+.3f} mm")


if __name__ == "__main__":
    main()
