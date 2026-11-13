# Generates AgentWatch.ico (16-256 px) next to this script: the mascot eyes (two vertical pill slits with a soft,
# lighter shaded centre - never flat black) on a rounded tile. Usage: python make_icon.py [preview.png]  (needs Pillow)
from PIL import Image, ImageDraw, ImageFilter
def hexc(h, a=255): h = h.lstrip('#'); return tuple(int(h[i:i+2], 16) for i in (0, 2, 4)) + (a,)
def lerp(c1, c2, t): return tuple(int(round(c1[i] + (c2[i] - c1[i]) * t)) for i in range(4))
def tile(size, top='#A5B4FC', bot='#7C3AED'):
    S = size * 4  # supersample
    im = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    # tile gradient (diagonal)
    grad = Image.new('RGBA', (S, S))
    a, b = hexc(top), hexc(bot)
    px = grad.load()
    for y in range(S):
        for x in range(S):
            px[x, y] = lerp(a, b, (x * 0.45 + y) / (S * 1.45))
    mask = Image.new('L', (S, S), 0)
    inset = max(1, int(S * 0.03)); r = int(S * (0.26 if size <= 24 else 0.24))
    ImageDraw.Draw(mask).rounded_rectangle([inset, inset, S - inset - 1, S - inset - 1], r, fill=255)
    im.paste(grad, (0, 0), mask)
    small = size <= 24
    pw = S * (0.22 if small else 0.19); ph = S * (0.56 if small else 0.52)
    for cx in (0.34, 0.66):
        x0 = S * cx - pw / 2; y0 = S * 0.5 - ph / 2
        pill = Image.new('RGBA', (S, S), (0, 0, 0, 0)); pp = pill.load()
        dark, light = hexc('#07060C'), hexc('#34303F' if not small else '#3C3848')
        for x in range(int(x0), int(x0 + pw) + 1):
            u = (x - x0) / pw; t = max(0.0, 1 - abs(u - 0.5) * 2)  # 1 in the centre
            col = lerp(dark, light, t ** 1.6)
            for y in range(int(y0), int(y0 + ph) + 1): pp[x, y] = col
        pm = Image.new('L', (S, S), 0)
        ImageDraw.Draw(pm).rounded_rectangle([x0, y0, x0 + pw, y0 + ph], pw / 2, fill=255)
        im.paste(pill, (0, 0), pm)
        if not small:  # soft centre highlight
            hl = Image.new('RGBA', (S, S), (255, 255, 255, 0)); hm = Image.new('L', (S, S), 0)
            ImageDraw.Draw(hm).ellipse([S * cx - pw * 0.2, S * 0.5 - ph * 0.26, S * cx + pw * 0.2, S * 0.5 + ph * 0.2], fill=34)
            hm = hm.filter(ImageFilter.GaussianBlur(S * 0.02))
            hm = Image.composite(hm, Image.new('L', (S, S), 0), pm)
            hl.putalpha(hm); im = Image.alpha_composite(im, hl)
    # subtle rim
    rim = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(rim).rounded_rectangle([inset, inset, S - inset - 1, S - inset - 1], r, outline=(255, 255, 255, 70), width=max(2, S // 48))
    im = Image.alpha_composite(im, rim)
    return im.resize((size, size), Image.LANCZOS)
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
imgs = [tile(s) for s in sizes]
import os, sys
here = os.path.dirname(os.path.abspath(__file__))
imgs[-1].save(os.path.join(here, 'AgentWatch.ico'), format='ICO', sizes=[(s, s) for s in sizes], append_images=imgs[:-1])
# preview sheet
sheet = Image.new('RGBA', (sum(sizes) + 10 * len(sizes) + 10, 276), (30, 27, 75, 255)); x = 10
for s, im in zip(sizes, imgs): sheet.paste(im, (x, 10), im); x += s + 10
sheet.save(sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, 'icon-preview.png'))
print('ok')
