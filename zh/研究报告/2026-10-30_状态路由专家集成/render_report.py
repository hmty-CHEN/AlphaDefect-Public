"""把同目录的报告转换成可直接在 Windows 浏览器打开的离线 HTML。

只支持本报告使用的 Markdown 子集，使用标准库；不加载远程脚本、字体或资源。
"""
from html import escape
from pathlib import Path
import re


def inline(s):
    chunks = re.split(r'(`[^`]+`)', s)
    out = []
    for chunk in chunks:
        if chunk.startswith('`') and chunk.endswith('`'):
            out.append('<code>' + escape(chunk[1:-1]) + '</code>')
        else:
            x = escape(chunk)
            x = re.sub(r'\*\*(.+?)\*\*', r'<strong>\1</strong>', x)
            x = re.sub(r'(?<!\*)\*([^*]+)\*(?!\*)', r'<em>\1</em>', x)
            out.append(x)
    return ''.join(out)


def render(s):
    lines = s.splitlines()
    out, nav, i = [], [], 0
    while i < len(lines):
        line = lines[i]
        if not line.strip():
            i += 1
            continue
        if line.startswith('```'):
            code = []
            i += 1
            while i < len(lines) and not lines[i].startswith('```'):
                code.append(lines[i]); i += 1
            out.append('<pre><code>' + escape('\n'.join(code)) + '</code></pre>')
            i += 1
            continue
        if line.startswith('# '):
            out.append('<h1>' + inline(line[2:]) + '</h1>'); i += 1
            continue
        if re.match(r'^\*\*\d+．', line) and line.endswith('**'):
            label = line[2:-2]
            anchor = 'section-' + re.match(r'\d+', label)[0]
            out.append(f'<h2 id="{anchor}">{inline(label)}</h2>')
            nav.append(f'<a href="#{anchor}">{escape(label)}</a>')
            i += 1
            continue
        if line.startswith('|'):
            rows = []
            while i < len(lines) and lines[i].startswith('|'):
                cells = [x.strip() for x in lines[i].strip().strip('|').split('|')]
                if not all(re.fullmatch(r':?-+:?', c) for c in cells):
                    tag = 'th' if not rows else 'td'
                    rows.append('<tr>' + ''.join(f'<{tag}>{inline(c)}</{tag}>' for c in cells) + '</tr>')
                i += 1
            out.append('<div class="table-wrap"><table>' + ''.join(rows) + '</table></div>')
            continue
        unordered = line.startswith('- ')
        ordered = bool(re.match(r'^\d+\. ', line))
        if unordered or ordered:
            tag = 'ul' if unordered else 'ol'
            items = []
            while i < len(lines):
                line = lines[i]
                if unordered and line.startswith('- '):
                    items.append('<li>' + inline(line[2:]) + '</li>')
                elif ordered and re.match(r'^\d+\. ', line):
                    items.append('<li>' + inline(re.sub(r'^\d+\. ', '', line)) + '</li>')
                else:
                    break
                i += 1
            out.append(f'<{tag}>' + ''.join(items) + f'</{tag}>')
            continue
        para = [line]
        i += 1
        while i < len(lines) and lines[i].strip() and not re.match(r'^(?:\||```|# |\*\*\d+．|- |\d+\. )', lines[i]):
            para.append(lines[i]); i += 1
        out.append('<p>' + inline('\n'.join(para)) + '</p>')
    return '\n'.join(out), '\n'.join(nav)


def main():
    root = Path(__file__).resolve().parent
    body, nav = render((root / '调查报告.md').read_text(encoding='utf-8'))
    css = '''
    :root{color-scheme:light;--ink:#202c36;--muted:#5e6c78;--line:#d8e1e7;--accent:#185c76}
    *{box-sizing:border-box}body{margin:0;background:#f3f6f8;color:var(--ink);font:16px/1.85 system-ui,"Microsoft YaHei",sans-serif}
    main{max-width:1120px;margin:34px auto;background:white;padding:40px 56px 64px;border:1px solid var(--line);border-radius:10px}
    h1{font-size:29px;line-height:1.5;margin:0 0 24px}h2{font-size:21px;line-height:1.6;border-top:2px solid var(--line);padding-top:28px;margin-top:42px;scroll-margin-top:22px}
    p{margin:16px 0}strong{color:#153e50}li{margin:10px 0}ul,ol{padding-left:27px}
    .table-wrap{overflow:auto;margin:22px 0}table{width:100%;border-collapse:collapse;font-size:14px;line-height:1.7}th,td{border:1px solid var(--line);padding:10px 12px;text-align:left;vertical-align:top}th{background:#eaf1f5}tr:nth-child(even) td{background:#f8fafb}
    code{font:13px/1.7 ui-monospace,Consolas,monospace;background:#edf2f5;padding:2px 4px;overflow-wrap:anywhere}pre{white-space:pre-wrap;overflow:auto;border-left:3px solid var(--accent);background:#f1f5f7;padding:18px}pre code{background:none;padding:0}
    details{background:#f3f7f9;border:1px solid var(--line);padding:14px 18px;margin:0 0 26px}summary{cursor:pointer;color:var(--accent);font-weight:650}nav{display:grid;grid-template-columns:1fr 1fr;gap:7px 20px;padding:12px 0}a{color:var(--accent);text-decoration:none}a:hover{text-decoration:underline}.meta{color:var(--muted);font-size:13px}
    @media(max-width:700px){main{margin:0;border:0;border-radius:0;padding:24px 19px}h1{font-size:24px}nav{grid-template-columns:1fr}th,td{min-width:105px}}
    @media print{body{background:white}main{margin:0;border:0;padding:0;max-width:none}details{display:none}h2{break-after:avoid}table,pre{break-inside:auto}th{background:#eee}}
    '''
    doc = f'''<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>状态路由专家集成调查报告 · sls2 · 2026-10-30</title><style>{css}</style></head>
<body><main><p class="meta">sls2 · 研究报告 · 2026-10-30 · 离线可读</p><details><summary>目录（15 节）</summary><nav>{nav}</nav></details>{body}</main></body></html>'''
    out = root / '调查报告.html'
    out.write_text(doc, encoding='utf-8')
    print(out)


if __name__ == '__main__':
    main()
