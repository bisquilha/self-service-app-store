"""Confere o contraste (WCAG) das cores dos temas claro/escuro do app WPF (src/Loja.App/Themes). Uso: python -I tests/contraste-app.py"""
import os
import re
import sys

base = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "Loja.App", "Themes")


def load(name):
    xml = open(f'{base}/{name}.xaml', encoding='utf-8-sig').read()
    return dict(re.findall(r'x:Key="(\w+)" Color="(#[0-9A-Fa-f]{6})"', xml))


def lum(h):
    c = [int(h[i:i + 2], 16) / 255 for i in (1, 3, 5)]
    c = [v / 12.92 if v <= 0.03928 else ((v + 0.055) / 1.055) ** 2.4 for v in c]
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]


def ratio(a, b):
    la, lb = sorted((lum(a), lum(b)), reverse=True)
    return (la + 0.05) / (lb + 0.05)


# (primeiro plano, fundo, minimo): texto 4.5, elementos de interface 3
pares = [
    ('TextPrimary', 'BgWindow', 4.5), ('TextPrimary', 'BgSurface', 4.5), ('TextPrimary', 'BgHover', 4.5),
    ('TextPrimary', 'AccentSoft', 4.5), ('TextSecondary', 'BgWindow', 4.5), ('TextSecondary', 'BgSurface', 4.5),
    ('TextSecondary', 'BgSurfaceAlt', 4.5), ('TextSecondary', 'BgHover', 4.5), ('TextMuted', 'BgSurface', 4.5),
    ('OnAccent', 'Accent', 4.5), ('OnAccent', 'AccentHover', 4.5), ('AccentText', 'BgSurface', 4.5),
    ('AccentText', 'BgWindow', 4.5), ('OkText', 'BgSurface', 4.5), ('BadText', 'BgSurface', 4.5),
    ('WarnText', 'BgSurface', 4.5), ('WarnText', 'WarnBg', 4.5), ('HeaderText', 'HeaderBg', 4.5),
    ('HeaderSubtext', 'HeaderBg', 4.5), ('InfoBarText', 'InfoBarBg', 4.5), ('HeaderText', 'HeaderButtonHover', 4.5),
    ('FocusRing', 'BgSurface', 3), ('FocusRing', 'BgWindow', 3), ('BorderStrong', 'BgSurface', 3),
    ('BorderStrong', 'BgWindow', 3), ('AccentLine', 'BgWindow', 3), ('HeaderButtonBorder', 'HeaderBg', 3),
]
avatares = ['#1F5FBF', '#0E7C66', '#7D3C98', '#A84300', '#B3261E', '#2C3E50', '#1E7A44', '#7A5C00']

ruins = 0
for tema in ('Light', 'Dark'):
    c = load(tema)
    print(f'--- {tema}')
    for fg, bg, minimo in pares:
        r = ratio(c[fg], c[bg])
        ok = r >= minimo
        ruins += 0 if ok else 1
        print(f'{"ok " if ok else "RUIM"} {fg:<18} sobre {bg:<18} {r:5.2f}:1 (minimo {minimo})')
print('--- iniciais dos avatares (branco por cima)')
for a in avatares:
    r = ratio('#FFFFFF', a)
    ok = r >= 4.5
    ruins += 0 if ok else 1
    print(f'{"ok " if ok else "RUIM"} branco sobre {a} {r:5.2f}:1')
print(f'\n{ruins} par(es) abaixo do minimo')
