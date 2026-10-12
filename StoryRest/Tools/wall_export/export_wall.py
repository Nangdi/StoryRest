"""PSD(벽면 양산) → StreamingAssets 흰 글씨 PNG.

- 해시태그: floor_<F>/<마커>/sprite/NN_<태그>.png  (홀수=핵심, 짝수=자세히 — 원고 md 기준으로 나눔)
- 질문:     floor_<F>/<마커>/question/question.png (핵심·자세히 같은 문장이라 두 마커에 같은 그림)
- 분류 문구: wall_titles/NN_<문구>.png
글자는 PSD 레이어의 텍스트를 같은 글꼴로 다시 그린다(PSD 래스터는 태그 높이 35px 라 확대 시 흐림).
"""
import re, sys, os, json
from collections import defaultdict
from psd_tools import PSDImage
from PIL import Image, ImageDraw, ImageFont

# 사용법: python export_wall.py <출력폴더> [PSD] [원고 md]
#   출력폴더를 Assets/StreamingAssets 로 주면 바로 들어간다. 규칙은 docs/WALL_CONTENT.md.
HERE = os.path.dirname(os.path.abspath(__file__))
FONT_DIR = os.path.join(HERE, '..', '..', 'Assets', '10.Font')
OUT = sys.argv[1] if len(sys.argv) > 1 else 'out'
PSD = sys.argv[2] if len(sys.argv) > 2 else os.path.expanduser('~/Desktop/전북과_스토리쉼터-벽면(양산)_260929_RGB.psd')
MD = sys.argv[3] if len(sys.argv) > 3 else os.path.expanduser('~/Desktop/스토리쉼터_벽면스크린_전시물제목_해시태그_띄어쓰기_260929_v02.md')
FONTS = {n: os.path.join(FONT_DIR, n + '.ttf') for n in ('Paperlogy-5Medium', 'Paperlogy-7Bold', 'Paperlogy-8ExtraBold')}
TAG_SCALE = 3.0      # 태그는 벽에서 긴 변 360~760px 로 키워지므로 PSD 의 3배로 그린다
TEXT_SCALE = 1.0     # 질문·분류 문구는 PSD 크기 그대로 (가로 1920 은 16:10 에서도 같다)
PAD = 4

def nm(l):
    try: return l.name.encode('latin-1').decode('cp949')
    except Exception: return l.name

def norm(s): return re.sub(r'\s+', '', s)

def style(l):
    sd = l.engine_dict['StyleRun']['RunArray'][0]['StyleSheet']['StyleSheetData']
    font = str(l.resource_dict['FontSet'][sd.get('Font', 0)]['Name']).strip("'")
    k = float(l.transform[0])
    size = float(sd['FontSize']) * k
    leading = float(sd.get('Leading', 0)) * k if not sd.get('AutoLeading', True) else size * 1.2
    return font, size, leading

def render(text, font, size, leading, scale):
    f = ImageFont.truetype(FONTS[font], round(size * scale))
    lines = text.replace('\r\n', '\n').replace('\r', '\n').split('\n')
    lead = leading * scale
    boxes = [f.getbbox(t) for t in lines]
    w = max(b[2] for b in boxes) + 40
    h = int(lead * (len(lines) - 1) + size * scale * 2) + 40
    im = Image.new('L', (int(w), h), 0)
    d = ImageDraw.Draw(im)
    for i, t in enumerate(lines):
        lw = f.getlength(t)
        d.text(((w - lw) / 2, 20 + i * lead), t, font=f, fill=255)   # 가운데 정렬(PSD Justification 2)
    bb = im.getbbox()
    a = im.crop((bb[0] - PAD, bb[1] - PAD, bb[2] + PAD, bb[3] + PAD))
    out = Image.new('RGBA', a.size, (255, 255, 255, 0))
    out.putalpha(a)
    return out

def safe(s): return re.sub(r'[\\/:*?"<>|]', '', s).strip()

# ---- 원고 md: 전시물별 핵심/자세히 태그 (원고에는 쉬움/어려움으로 적혀 있다) ----
md = open(MD, encoding='utf-8').read()
items = {}
for m in re.finditer(r'^### (\d)F-(\d+) · (.+?)\n(.*?)(?=^###|^## |\Z)', md, re.M | re.S):
    fl, no, name, body = int(m[1]), int(m[2]), m[3].strip(), m[4]
    q = re.search(r'- 질문 제목: (.+)', body)[1].strip()
    def tags(line):
        return [t.strip() for t in line.split('#') if t.strip()] if line and '#' in line else []
    common = re.search(r'- 해시태그\(쉬움·어려움 공통\): (.+)', body)
    if common:
        e = h = tags(common[1])
    else:
        e = tags(re.search(r'- 쉬움 해시태그: (.+)', body)[1])
        h = tags(re.search(r'- 어려움 해시태그: (.+)', body)[1])
    items[(fl, no)] = dict(name=name, q=q, core=e, detail=h)

# ---- PSD ----
psd = PSDImage.open(PSD)
groups = {nm(l): l for l in psd.descendants() if l.is_group()}
report = defaultdict(list)
manifest = []
written = 0

for fl in (1, 2, 3):
    tag_root = groups[f'{fl}층 해시태그 키워드']
    q_root = groups[f'{fl}층 전시물 질문']
    psd_tags = {nm(g): list(g) for g in tag_root if g.is_group()}
    psd_qs = {nm(l): l for l in q_root}
    nos = sorted(no for (f2, no) in items if f2 == fl)
    for key in set(psd_tags) | set(psd_qs):
        if int(key.split('-')[1]) not in nos:
            report['PSD에만 있는 항목'].append(key)
    for no in nos:
        it = items[(fl, no)]
        key = f'{fl}-{no}'
        core_m, detail_m = 2 * no - 1, 2 * no
        # 질문
        ql = psd_qs.get(key)
        if ql is None:
            report['PSD에 질문 없음'].append(key)
        else:
            if norm(ql.text) != norm(it['q']):
                report['질문 문장 차이 (PSD 로 그림)'].append(f"{key}: PSD「{ql.text.replace(chr(13), ' / ')}」 원고「{it['q']}」")
            img = render(ql.text, *style(ql), TEXT_SCALE)
            for mk in (core_m, detail_m):
                p = os.path.join(OUT, f'floor_{fl}', str(mk), 'question')
                os.makedirs(p, exist_ok=True)
                img.save(os.path.join(p, 'question.png')); written += 1
        # 태그
        layers = psd_tags.get(key, [])
        if not layers:
            report['PSD에 태그 그룹 없음'].append(key)
        by_norm = {norm(l.text): l for l in layers}
        used = set()
        for mk, diff, lst in ((core_m, '핵심', it['core']), (detail_m, '자세히', it['detail'])):
            p = os.path.join(OUT, f'floor_{fl}', str(mk), 'sprite')
            os.makedirs(p, exist_ok=True)
            files = []
            for i, t in enumerate(lst, 1):
                l = by_norm.get(norm(t))
                if l is None:
                    report['원고 태그가 PSD에 없음 (건너뜀)'].append(f'{key} {diff}: {t}')
                    continue
                used.add(norm(t))
                if l.text.strip() != t:
                    report['태그 띄어쓰기 차이 (PSD 로 그림)'].append(f'{key}: PSD「{l.text}」 원고「{t}」')
                fn = f'{i:02d}_{safe(l.text.strip())}.png'
                render(l.text.strip(), *style(l), TAG_SCALE).save(os.path.join(p, fn)); written += 1
                files.append(fn)
            if not files:
                report['태그 비어 있는 마커'].append(f'{fl}층 {mk}번 ({key} {diff})')
            manifest.append(dict(floor=fl, marker=mk, item=key, level=diff, name=it['name'],
                                 question=it['q'], tags=[f[3:-4] for f in files]))
        for k2, l in by_norm.items():
            if k2 not in used:
                report['PSD 태그가 원고에 없음 (안 씀)'].append(f'{key}: {l.text}')

# 분류 문구
tr = groups['벽면 분류 문구']
p = os.path.join(OUT, 'wall_titles'); os.makedirs(p, exist_ok=True)
titles = []
for i, l in enumerate(tr, 1):
    fn = f'{i:02d}_{safe(l.text.strip())}.png'
    render(l.text.strip(), *style(l), TEXT_SCALE).save(os.path.join(p, fn)); written += 1
    titles.append(l.text.strip())

json.dump(dict(titles=titles, markers=manifest), open(os.path.join(OUT, 'manifest.json'), 'w', encoding='utf-8'),
          ensure_ascii=False, indent=1)
print('written', written, 'markers', len(manifest))
for k, v in report.items():
    print(f'\n## {k} ({len(v)})')
    for x in v: print(' -', x)
