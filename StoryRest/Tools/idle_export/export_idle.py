"""대기화면 PSD → 바닥(테이블) 대기 안내 글자 스프라이트.

- 문구를 음절 하나당 흰 글씨 PNG 한 장으로 그린다(Assets/4.Sprite/IdleGuide/Glyph_NN.png).
  글자마다 따로 움직여야(차례로 나타나기 · 물결) 하므로 한 줄을 한 장으로 굽지 않는다.
- 색은 굽지 않는다. 층별 그라데이션은 프로그램이 입힌다(ArUcoIdleGuide, 색 값은 PSD 에서 읽은 것).
- 모든 장의 세로 범위가 같아 나란히 놓으면 기준선이 맞는다. 가로 자리는 glyphs.json 의 center(1배 px)다.
글자는 PSD 래스터를 잘라 쓰지 않고 레이어의 텍스트를 같은 글꼴로 다시 그린다(→ docs/WALL_CONTENT.md §6).

사용법: python export_idle.py [출력폴더] [PSD]
"""
import os, sys, json
from psd_tools import PSDImage
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, '..', '..')
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, 'Assets', '4.Sprite', 'IdleGuide')
PSD = sys.argv[2] if len(sys.argv) > 2 else os.path.expanduser('~/Desktop/전북과_스토리쉼터-대기화면_260928.psd')
FONT = os.path.join(ROOT, 'Assets', '10.Font', 'Paperlogy-8ExtraBold.ttf')
SCALE = 2.0   # 프로젝터에서 키워도 흐리지 않게 2배로 그린다. Unity 에서는 절반 크기로 놓는다
PAD = 6       # 2배 px. 획이 글자 칸 밖으로 살짝 나가는 몫

def nm(l):
    try: return l.name.encode('latin-1').decode('cp949')
    except Exception: return l.name

# ---- PSD: 1층 스마트 오브젝트 안의 아래쪽 문구(위쪽은 같은 문구를 180° 돌린 것) ----
psd = PSDImage.open(PSD)
outer = psd[0]                                   # '1층' 스마트 오브젝트 (세 층 모두 문구 · 배치가 같고 색만 다르다)
inner = PSDImage.open(__import__('io').BytesIO(outer.smart_object.data))
texts = [l for l in inner.descendants() if l.kind == 'type']
bottom = max(texts, key=lambda l: l.bbox[1])
text = bottom.text.strip()
sd = bottom.engine_dict['StyleRun']['RunArray'][0]['StyleSheet']['StyleSheetData']
place = (outer.bbox[2] - outer.bbox[0]) / inner.width          # 스마트 오브젝트가 화면에 놓인 배율
size = float(sd['FontSize']) * float(bottom.transform[0]) * place   # 1920x1080 화면 기준 글자 px

def center_y(l):  # 1920x1080 화면 기준 줄 가운데 y
    return outer.bbox[1] + (l.bbox[1] + l.bbox[3]) / 2 * place
top = min(texts, key=lambda l: l.bbox[1])

f = ImageFont.truetype(FONT, round(size * SCALE))
ink = f.getbbox(text)                                    # 줄 전체의 세로 잉크 범위 — 모든 장이 같은 범위를 쓴다
y0, y1 = ink[1] - PAD, ink[3] + PAD
os.makedirs(OUT, exist_ok=True)

glyphs = []
width = f.getlength(text) / SCALE
for i, ch in enumerate(text):
    left = f.getlength(text[:i])
    adv = f.getlength(text[:i + 1]) - left               # 앞 글자와의 커닝까지 포함한 칸
    if ch.isspace():
        continue
    w = int(round(adv)) + PAD * 2
    a = Image.new('L', (w, y1 - y0), 0)
    ImageDraw.Draw(a).text((PAD, -y0), ch, font=f, fill=255)
    im = Image.new('RGBA', a.size, (255, 255, 255, 0))
    im.putalpha(a)
    name = f'Glyph_{len(glyphs) + 1:02d}.png'
    im.save(os.path.join(OUT, name))
    glyphs.append({'file': name, 'char': ch,
                   'center': round((left + adv / 2) / SCALE - width / 2, 2)})  # 줄 가운데 = 0

info = {
    'text': text, 'fontPx': round(size, 2), 'scale': SCALE, 'lineWidth': round(width, 1),
    'bottomCenterY': round(center_y(bottom), 1), 'topCenterY': round(center_y(top), 1),
    'glyphs': glyphs,
}
json.dump(info, open(os.path.join(OUT, 'glyphs.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print(f'{len(glyphs)} glyphs, font {size:.1f}px, line {width:.0f}px, y bottom {info["bottomCenterY"]} top {info["topCenterY"]} → {OUT}')
