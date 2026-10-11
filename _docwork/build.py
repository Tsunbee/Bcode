import os, sys, datetime
sys.stdout.reconfigure(encoding='utf-8')
sys.path.insert(0, os.path.dirname(__file__))
from content import *
from docx import Document
from docx.shared import Pt, Cm, RGBColor, Inches
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.enum.section import WD_ORIENT
from docx.oxml.ns import qn
from docx.oxml import OxmlElement

ROOT = r'D:\Bee\Tool\Bcode'
SRC = ROOT + r'\src'
WORK = ROOT + r'\_docwork'
EX = {'bin', 'obj', 'node_modules', 'vs', 'Libs', '.vs'}


# ------------------------------------------------------------ cây thư mục tự sinh
def count_lines(p):
    try:
        with open(p, 'rb') as f:
            return sum(1 for _ in f)
    except Exception:
        return 0


def tree_lines(proj, summarize=('Templates',)):
    base = os.path.join(SRC, proj)
    out = [proj + '/']

    def walk(d, prefix, depth):
        try:
            names = sorted(os.listdir(d), key=lambda n: (os.path.isfile(os.path.join(d, n)), n.lower()))
        except Exception:
            return
        names = [n for n in names if n not in EX and not n.endswith(('.resx', '.csproj.user'))]
        for i, n in enumerate(names):
            p = os.path.join(d, n)
            last = i == len(names) - 1
            br = '└── ' if last else '├── '
            nxt = prefix + ('    ' if last else '│   ')
            if os.path.isdir(p):
                nfiles = 0
                for _r, _ds, _fs in os.walk(p):
                    _ds[:] = [x for x in _ds if x not in EX]
                    nfiles += len(_fs)
                if n in summarize:
                    out.append(f'{prefix}{br}{n}/  ({nfiles} file — không liệt kê)')
                else:
                    out.append(f'{prefix}{br}{n}/')
                    walk(p, nxt, depth + 1)
            else:
                ext = os.path.splitext(n)[1].lower()
                if ext in ('.cs', '.html', '.js', '.css', '.csproj', '.md', '.txt', '.json', '.xml'):
                    out.append(f'{prefix}{br}{n}  {count_lines(p)}')
    walk(base, '', 0)
    return out


TREES = {p: tree_lines(p) for p in ['Bcode.App', 'BcodeViewer.App', 'Bcode.ScreenDesigner', 'Bcode.ReportBuilder', 'Bcode.KeyGen', 'Shared']}


# ------------------------------------------------------------ docx helpers
def shade(cell, hex_):
    tcPr = cell._tc.get_or_add_tcPr()
    shd = OxmlElement('w:shd')
    shd.set(qn('w:val'), 'clear'); shd.set(qn('w:color'), 'auto'); shd.set(qn('w:fill'), hex_)
    tcPr.append(shd)


def set_cell(cell, text, bold=False, size=8.5, mono=False, color=None):
    cell.text = ''
    p = cell.paragraphs[0]
    p.paragraph_format.space_after = Pt(1)
    r = p.add_run(text)
    r.font.size = Pt(size); r.bold = bold
    if mono:
        r.font.name = 'Consolas'; r._element.rPr.rFonts.set(qn('w:eastAsia'), 'Consolas')
    if color:
        r.font.color.rgb = RGBColor.from_string(color)


def repeat_header(row):
    trPr = row._tr.get_or_add_trPr()
    h = OxmlElement('w:tblHeader'); h.set(qn('w:val'), 'true'); trPr.append(h)


def table(doc, header, rows, widths, size=8.5, mono_cols=()):
    t = doc.add_table(rows=1, cols=len(header))
    t.style = 'Table Grid'
    t.autofit = False
    for i, h in enumerate(header):
        c = t.rows[0].cells[i]
        set_cell(c, h, bold=True, size=size, color='FFFFFF'); shade(c, '2F4F8F')
    repeat_header(t.rows[0])
    for r in rows:
        cells = t.add_row().cells
        for i, v in enumerate(r):
            set_cell(cells[i], v, size=size, bold=(i == 0), mono=(i in mono_cols and size <= 8.5))
    for row in t.rows:
        for i, w in enumerate(widths):
            row.cells[i].width = Cm(w)
    doc.add_paragraph().paragraph_format.space_after = Pt(2)
    return t


def para(doc, text, size=10, bold=False, style=None, after=4):
    p = doc.add_paragraph(style=style)
    r = p.add_run(text); r.font.size = Pt(size); r.bold = bold
    p.paragraph_format.space_after = Pt(after)
    return p


def bullet(doc, text, size=10):
    p = doc.add_paragraph(style='List Bullet')
    r = p.add_run(text); r.font.size = Pt(size)
    p.paragraph_format.space_after = Pt(2)


def code_block(doc, lines, size=7):
    for ln in lines:
        p = doc.add_paragraph()
        p.paragraph_format.space_after = Pt(0); p.paragraph_format.space_before = Pt(0)
        p.paragraph_format.line_spacing = 1.0
        r = p.add_run(ln)
        r.font.name = 'Consolas'; r.font.size = Pt(size)
        r._element.rPr.rFonts.set(qn('w:eastAsia'), 'Consolas')


def image(doc, name, width_cm=None, height_cm=None):
    path = os.path.join(WORK, name)
    if height_cm:
        doc.add_picture(path, height=Cm(height_cm))
    else:
        doc.add_picture(path, width=Cm(width_cm or 17))
    doc.paragraphs[-1].alignment = WD_ALIGN_PARAGRAPH.CENTER


def toc(doc):
    p = doc.add_paragraph()
    run = p.add_run()
    for t, txt in (('begin', None), (None, 'TOC \\o "1-2" \\h \\z \\u'), ('separate', None)):
        if t:
            e = OxmlElement('w:fldChar'); e.set(qn('w:fldCharType'), t); run._r.append(e)
        else:
            e = OxmlElement('w:instrText'); e.set(qn('xml:space'), 'preserve'); e.text = txt; run._r.append(e)
    r2 = p.add_run('(Mở file trong Word, bấm chuột phải vào đây → Update Field để hiện mục lục)')
    r2.italic = True
    run3 = p.add_run()
    e = OxmlElement('w:fldChar'); e.set(qn('w:fldCharType'), 'end'); run3._r.append(e)


# ------------------------------------------------------------ docx
doc = Document()
sec = doc.sections[0]
sec.page_width, sec.page_height = Cm(21), Cm(29.7)
for m in ('left_margin', 'right_margin'):
    setattr(sec, m, Cm(1.6))
sec.top_margin = Cm(1.6); sec.bottom_margin = Cm(1.6)
st = doc.styles['Normal']; st.font.name = 'Segoe UI'; st.font.size = Pt(10)
st.element.rPr.rFonts.set(qn('w:eastAsia'), 'Segoe UI')
for n, sz in (('Heading 1', 17), ('Heading 2', 13), ('Heading 3', 11)):
    s = doc.styles[n]; s.font.name = 'Segoe UI'; s.font.size = Pt(sz); s.font.color.rgb = RGBColor(0x1F, 0x3A, 0x6E)
    s.element.rPr.rFonts.set(qn('w:eastAsia'), 'Segoe UI')

t = doc.add_paragraph(); r = t.add_run('Cấu trúc Bcode & BcodeViewer'); r.bold = True; r.font.size = Pt(24); r.font.color.rgb = RGBColor(0x1F, 0x3A, 0x6E)
para(doc, f'Bản v3 · cập nhật {TODAY} · @Bee · thay thế bản v2 (02/10/2026)', 10)
para(doc, 'Mục đích: tài liệu tra cứu nhanh để sửa code mà không phải dò lại cả dự án — "muốn sửa X thì mở file Y". Bản tóm tắt dạng text cho AI/grep nằm ở STRUCTURE.md (cùng nội dung bảng).', 10)
doc.add_paragraph('Mục lục', style='Heading 2')
toc(doc)
doc.add_page_break()

# ---- A
doc.add_heading('A. Tổng quan', 1)
doc.add_heading('I. Ngữ cảnh & quan hệ giữa các chương trình', 2)
for c in CONTEXT:
    bullet(doc, c)
doc.add_heading('II. So sánh nhanh', 2)
table(doc, ['Tiêu chí', 'Bcode.App', 'BcodeViewer.App'], [(a, b, c or b) for a, b, c in COMPARE], [3, 7.2, 7.2], 9)
doc.add_heading('III. Các project trong solution', 2)
table(doc, ['Thư mục', 'Đầu ra', 'Loại', 'Ghi chú'], PROJECTS, [3.6, 3.6, 3, 7.2], 8.5)
doc.add_heading('IV. Sơ đồ tổng quan hệ thống', 2)
image(doc, 'dg_overview.png', 17.6)
doc.add_page_break()

# ---- B
doc.add_heading('B. Kiến trúc Bcode.App', 1)
para(doc, 'Program.cs chỉ gọi Application.Run(new MainForm()). Toàn bộ service được new ngay trong constructor của Forms/MainForm.cs (không DI container). Mỗi công cụ là 1 UserControl/Form bọc 1 trang HTML (Web/Shell) qua WebBarHost/WebFrameHost/WebDialogForm; C# và trang nói chuyện bằng JSON {action, data}.')
image(doc, 'dg_bcode.png', 17.6)
doc.add_heading('Quy ước quan trọng', 2)
for s in [
    'MainForm chia partial: MainForm.cs (dựng UI, _toolSpecs, mọi Open*Tab), .Session.cs (khôi phục phiên), .Hibernate.cs (ngủ đông tab SQL), .ToolGroups.cs (nhóm công cụ).',
    'Trang HTML luôn nạp shell.css + dialog.css + autoscale.js để ăn Template giao diện và tự co giãn (UiScale, DPI). Không viết CSS cứng cỡ chữ trong trang.',
    'Việc nặng chạy ở luồng nền; kết quả đẩy về trang bằng Call(...) (marshal về UI thread bằng BeginInvoke).',
    'Dữ liệu người dùng luôn dưới %AppData%\\Bcode (BcodePaths.cs tự chọn nơi ghi được: Roaming → Local → UserProfile → thư mục exe).',
]:
    bullet(doc, s)
doc.add_page_break()

# ---- C
doc.add_heading('C. Kiến trúc BcodeViewer.App', 1)
para(doc, 'Vỏ WinForms mỏng; giao diện (menu, toolbar, cây Project, hộp thoại, settings) nằm hết trong trang Web (shell.js, ui.js, settings.js...). Mọi thao tác đụng file/mạng/CSDL đi qua host object EditorBridge theo quy ước Begin<Ten>(requestId,...) → hostCallResult, để không treo UI khi gõ phím.')
image(doc, 'dg_viewer.png', 17.6)
doc.add_heading('Quy ước quan trọng', 2)
for s in [
    'Notify* / Get* chạy đồng bộ trên UI thread, chỉ đổi state trong RAM hoặc đẩy dữ liệu 1 lần. Begin* xếp hàng qua Host/AsyncHostCall.cs (thread-pool; CompletionGroup tự huỷ request ghost text cũ).',
    'Web Worker (problems-worker.js) nạp lại problems.js/entity.js; vì không có cầu nối nên đọc file qua RPC về trang.',
    'Mỗi tiến trình Viewer có profile WebView2 riêng; BcodeViewer đọc workspace từ settings.json của Bcode (không tham chiếu project Bcode.App).',
]:
    bullet(doc, s)
doc.add_heading('Vòng đời một lần gọi AI ghost text', 2)
image(doc, 'old_seq.png', 15)
para(doc, '(Sơ đồ giữ từ v2; luồng Begin* → ClaudeChatService → hostCallResult không đổi.)', 8.5)
doc.add_page_break()

# ---- D
doc.add_heading('D. Cây cấu trúc tính năng', 1)
para(doc, 'Mỗi nhánh: nhóm → tính năng → file chính. Chi tiết từng dòng ở phần E, F.')
doc.add_heading('I. Bcode.App', 2)
image(doc, 'dg_tree_bcode.png', height_cm=23.5)
doc.add_page_break()
doc.add_heading('II. BcodeViewer.App', 2)
image(doc, 'dg_tree_viewer.png', 17.6)
doc.add_page_break()

# ---- E (cây thư mục)
doc.add_heading('E. Cây cấu trúc thư mục', 1)
para(doc, 'Tự sinh từ ổ đĩa ngày ' + TODAY + '. Số cuối dòng = số dòng file. Đã bỏ bin/, obj/, Libs/ (DLL) và Web/vs/ (Monaco vendor); thư mục có >60 file chỉ ghi số lượng.', 9)
for proj, label in (('Bcode.App', 'Bcode.App/'), ('BcodeViewer.App', 'BcodeViewer.App/'), ('Bcode.ScreenDesigner', 'Bcode.ScreenDesigner/'),
                    ('Bcode.ReportBuilder', 'Bcode.ReportBuilder/'), ('Bcode.KeyGen', 'Bcode.KeyGen/'), ('Shared', 'Shared/')):
    doc.add_heading(label, 2)
    code_block(doc, TREES[proj], 6.5)
doc.add_page_break()

# ---- F/G
def feature_section(title, groups, intro):
    doc.add_heading(title, 1)
    para(doc, intro)
    for gname, rows in groups:
        doc.add_heading(gname, 2)
        table(doc, ['Tính năng', 'File thực thi', 'Ghi chú / phím'], rows, [3.6, 7.0, 6.8], 8)


feature_section('F. Bcode.App — tính năng & file', BCODE,
                'Đường dẫn tương đối trong src/Bcode.App/. Web/Shell/* là trang HTML tương ứng của tính năng.')
doc.add_page_break()
feature_section('G. BcodeViewer.App — tính năng & file', VIEWER,
                'Đường dẫn tương đối trong src/BcodeViewer.App/. Web/* = trang chạy trong WebView2; Host/* = C# phía host.')
doc.add_page_break()

doc.add_heading('H. Phím tắt chính', 1)
doc.add_heading('Bcode.App (định nghĩa ở UI/ShortcutRegistry.cs, người dùng đổi được)', 2)
table(doc, ['Phím', 'Chức năng'], SHORTCUTS_BCODE, [6.4, 11], 8.5)
doc.add_heading('BcodeViewer.App', 2)
table(doc, ['Phím', 'Chức năng'], SHORTCUTS_VIEWER, [6.4, 11], 8.5)

doc.add_heading('I. Dữ liệu cục bộ & cấu hình', 1)
table(doc, ['Vị trí', 'Của', 'Nội dung'], DATA, [6.5, 1.7, 9.2], 8)

doc.add_heading('J. Bản đồ tra nhanh — muốn sửa X thì mở file nào', 1)
table(doc, ['Việc cần làm', 'Cách làm / file'], RECIPES, [4.4, 13], 8.5)
doc.add_heading('Bẫy đã biết & ghi chú', 2)
for s in TRAPS:
    bullet(doc, s, 9.5)

doc.add_heading('K. Thay đổi so với v2 (02/10/2026)', 1)
for s in [
    'Mới ở Bcode.App: Command Palette, tab switcher, nhóm công cụ, khôi phục phiên + ngủ đông, Chế độ hiệu năng, Template giao diện/UiScale/UiOverrides/ShortcutRegistry, WebFrameHost (giảm RAM), SQL history + Lịch sử SQL + So sánh object + Ai đang dùng, gợi ý code SQL, debug từng bước, Check Include, Check LCTT/CĐKT, Biên bản Word, Tạo nhanh danh mục, Tạo báo cáo (Bcode.ReportBuilder), Create RPT & XML, Excel → FRX, Check Mail, Setup eInvoice dạng tab, Báo cáo yêu cầu FSG, Cấp source/Copy to nhiều file, key bản quyền (+Bcode.KeyGen), Claude/Gemini nhúng, API chuẩn LT3.',
    'Project mới: Bcode.ScreenDesigner, Bcode.ReportBuilder, Bcode.KeyGen, Shared/, tools/lt3-api-docs.',
    'BcodeViewer: giao diện chuyển sang web shell (shell.js, ui.js, keys.js, quickopen.js, tabswitcher.js, mailpreview.js); thêm CaptionTranslator, IncludeTree, VsCodeThemeImporter, FcodeFieldDictionary; QuickOpenForm.cs (WinForms) đã bỏ — thay bằng quickopen.js.',
    'Sửa nhận định cũ: pipe Bcode.Control.<session> NAY ĐANG DÙNG ("Debug trong Bcode", F5 qua Bcode); trước đây ghi là chưa ai gọi.',
]:
    bullet(doc, s, 9.5)

out = os.path.join(ROOT, 'Cấu trúc Bcode  BcodeViewer (v3).docx')
doc.save(out)
print('saved', out)

# ------------------------------------------------------------ STRUCTURE.md
md = []
md.append(f'# Bản đồ cấu trúc Bcode & BcodeViewer ({TODAY})\n')
md.append('Đọc file này TRƯỚC khi tìm code. Tài liệu đầy đủ kèm sơ đồ: `Cấu trúc Bcode  BcodeViewer (v3).docx`. Đường dẫn bên dưới tính từ `src/<project>/`.\n')
md.append('## Quan hệ\n')
for c in CONTEXT:
    md.append('- ' + c)
md.append('\n## Projects\n')
for p in PROJECTS:
    md.append('- `%s` → %s (%s): %s' % p)
for title, groups in (('Bcode.App', BCODE), ('BcodeViewer.App', VIEWER)):
    md.append(f'\n## {title} — tính năng → file\n')
    for g, rows in groups:
        md.append(f'### {g}')
        for f, files, note in rows:
            md.append(f'- **{f}** — {files}' + (f' — _{note}_' if note else ''))
md.append('\n## Phím tắt\n')
for k, v in SHORTCUTS_BCODE:
    md.append(f'- Bcode `{k}`: {v}')
for k, v in SHORTCUTS_VIEWER:
    md.append(f'- Viewer `{k}`: {v}')
md.append('\n## Dữ liệu cục bộ\n')
for a, b, c in DATA:
    md.append(f'- `{a}` ({b}): {c}')
md.append('\n## Muốn sửa X thì mở file nào\n')
for a, b in RECIPES:
    md.append(f'- **{a}**: {b}')
md.append('\n## Bẫy & ghi chú\n')
for s in TRAPS:
    md.append('- ' + s)
md.append('\n(Cây thư mục đầy đủ: STRUCTURE-TREE.md)\n')
open(os.path.join(ROOT, 'STRUCTURE.md'), 'w', encoding='utf-8').write('\n'.join(md) + '\n')
md = ['# Cây thư mục Bcode (số cuối = số dòng file; tự sinh ' + TODAY + ')\n']
for proj in ('Bcode.App', 'BcodeViewer.App', 'Bcode.ScreenDesigner', 'Bcode.ReportBuilder', 'Bcode.KeyGen', 'Shared'):
    md.append('```')
    md.extend(TREES[proj])
    md.append('```')
open(os.path.join(ROOT, 'STRUCTURE-TREE.md'), 'w', encoding='utf-8').write('\n'.join(md) + '\n')
print('md ok')
