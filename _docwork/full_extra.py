import os, re, html, sys

SRC = r'D:\Bee\Tool\Bcode\src'
HERE = os.path.dirname(os.path.abspath(__file__))
EX = {'bin', 'obj', 'node_modules', 'vs', 'Libs', 'Templates', 'fonts'}
STALE = ('QuickOpenForm', 'ViewerControlServer', 'không còn gửi lệnh', 'Ngược lại Bcode.App vẫn lắng nghe', 'DecryptSqlObjectConfigService')


def clean_doc(lines):
    t = ' '.join(lines)
    t = re.sub(r'<see cref="([^"]+)"\s*/>', lambda m: m.group(1).split('.')[-1], t)
    t = re.sub(r'<paramref name="([^"]+)"\s*/>', r'\1', t)
    t = re.sub(r'<[^>]+>', ' ', t)
    t = html.unescape(t)
    t = re.sub(r'\s+', ' ', t).strip(' =-')
    return t


def cs_summary(path, limit=1100):
    try:
        lines = open(path, encoding='utf-8-sig', errors='replace').read().splitlines()
    except Exception:
        return ''
    doc = []
    for ln in lines[:120]:
        s = ln.strip()
        if s.startswith('///') or (s.startswith('//') and doc is not None and len(doc) > 0 and s.startswith('//')):
            doc.append(re.sub(r'^/+\s*', '', s))
        elif doc and re.match(r'(public|internal|sealed|static|abstract|partial|\[|class|record)', s):
            break
        elif re.match(r'.*\b(class|interface|enum|struct|record)\b\s+\w+', s) and not s.startswith('//'):
            if doc:
                break
    t = clean_doc(doc)
    return (t[:limit] + '…') if len(t) > limit else t


def js_summary(path, limit=800):
    try:
        lines = open(path, encoding='utf-8-sig', errors='replace').read().splitlines()
    except Exception:
        return ''
    doc = []
    for ln in lines[:60]:
        s = ln.strip()
        if s.startswith('//'):
            doc.append(re.sub(r'^/+\s*', '', s))
        elif doc:
            break
    t = re.sub(r'\s+', ' ', ' '.join(doc)).strip()
    return (t[:limit] + '…') if len(t) > limit else t


def files_in(proj, sub, exts):
    base = os.path.join(SRC, proj, sub)
    res = []
    for r, ds, fs in os.walk(base):
        ds[:] = sorted(d for d in ds if d not in EX)
        for f in sorted(fs):
            if f.endswith(exts) and not f.endswith('.Designer.cs'):
                res.append(os.path.join(r, f))
    return res


def add(doc, g):
    table, para, bullet, code_block = g['table'], g['para'], g['bullet'], g['code_block']
    set_cell, shade = g['set_cell'], g['shade']
    doc.add_page_break()

    # ---------------------------------------------------------- L. chi tiết hành vi từ v2
    doc.add_heading('L. Chi tiết hành vi từng tính năng (kế thừa v1/v2, đã lọc mục lỗi thời)', 1)
    para(doc, 'Phần này giữ nguyên mô tả chi tiết của bản v1 và v2 (v1 là tập con của v2). Đã bỏ các câu nói về QuickOpenForm, ViewerControlServer "chưa ai gọi" và DecryptSqlObjectConfigService vì không còn đúng; những thay đổi sau v2 xem ở phần M và phần A–K.', 9.5)
    lines = open(os.path.join(HERE, 'v2_detail.txt'), encoding='utf-8').read().splitlines()
    want = {'Thanh trên cùng (WebView2 — Web/Shell/topbar.html)': 'Bcode.App — thanh trên cùng',
            'Cột icon trái (chuyển panel nội dung)': 'Bcode.App — cột icon trái',
            'Toolbar chính (native, _toolSpecs trong MainForm.cs, mỗi nút có phím tắt Ctrl+Shift+<chữ>)': 'Bcode.App — toolbar chính (từng công cụ)',
            'Phím tắt ẩn (không có trên menu/toolbar)': 'Bcode.App — phím tắt ẩn',
            'Soạn thảo lõi': 'BcodeViewer — soạn thảo lõi',
            'IntelliSense — 4 lớp gợi ý xếp chồng trong Web/completion.js': 'BcodeViewer — IntelliSense 4 lớp',
            'AI (Claude / Gemini) — các điểm gọi ra API': 'BcodeViewer — AI (Claude / Gemini): các điểm gọi API',
            'Cầu nối host ↔ JS': 'BcodeViewer — cầu nối host ↔ JS'}
    i = 0
    cur = None
    while i < len(lines):
        ln = lines[i]
        m = re.match(r'\[Heading\d\] (.*)', ln)
        if m:
            cur = want.get(m.group(1).strip())
            if cur:
                doc.add_heading(cur, 2)
            i += 1
            continue
        if cur and ln == '<TABLE>':
            j = i + 1
            rows = []
            while lines[j] != '</TABLE>':
                rows.append([c.strip() for c in lines[j].split(' | ')])
                j += 1
            rows = [r for r in rows if not any(x in ' '.join(r) for x in STALE)]
            if rows:
                n = max(len(r) for r in rows)
                rows = [r + [''] * (n - len(r)) for r in rows]
                widths = {2: [5, 12.4], 3: [3.6, 5.6, 8.2], 4: [2.6, 4.2, 4.6, 6]}.get(n, [17.4 / n] * n)
                table(doc, rows[0], rows[1:], widths, 7.8)
            i = j + 1
            continue
        if cur and ln.startswith('[ListParagraph]'):
            t = ln[len('[ListParagraph]'):].strip()
            if t and not any(x in t for x in STALE):
                bullet(doc, t, 8.8)
        i += 1

    # ---------------------------------------------------------- M. sau v2
    doc.add_heading('M. Mô tả chi tiết từng file (trích chú thích trong mã nguồn hiện tại)', 1)
    para(doc, 'Tự động trích từ chú thích đầu mỗi file (giữ nguyên lời của tác giả code, có thể lẫn tiếng Anh). Đây là nguồn chi tiết cho các tính năng mới sau v2: Command Palette, phiên/ngủ đông, Template giao diện, SQL history, Check LCTT/CĐKT, biên bản Word, Tạo báo cáo, Check Include, API LT3...', 9.5)
    sections = [
        ('Bcode.App — Controls/', 'Bcode.App', 'Controls'), ('Bcode.App — Forms/', 'Bcode.App', 'Forms'),
        ('Bcode.App — Services/', 'Bcode.App', 'Services'), ('Bcode.App — UI/', 'Bcode.App', 'UI'), ('Bcode.App — Models/', 'Bcode.App', 'Models'),
        ('BcodeViewer.App — Host/', 'BcodeViewer.App', 'Host'), ('BcodeViewer.App — Settings/', 'BcodeViewer.App', 'Settings'),
        ('BcodeViewer.App — UI/', 'BcodeViewer.App', 'UI'),
        ('Bcode.ScreenDesigner', 'Bcode.ScreenDesigner', '.'), ('Bcode.ReportBuilder', 'Bcode.ReportBuilder', '.'),
        ('Bcode.KeyGen & Shared', 'Bcode.KeyGen', '.'), ('Shared', 'Shared', '.'),
    ]
    for title, proj, sub in sections:
        rows = []
        for fp in files_in(proj, sub, ('.cs',)):
            s = cs_summary(fp)
            if not s:
                continue
            rel = os.path.relpath(fp, os.path.join(SRC, proj)).replace(os.sep, '/')
            rows.append((rel, s))
        if not rows:
            continue
        doc.add_heading(title, 2)
        table(doc, ['File', 'Mô tả (từ chú thích code)'], rows, [5.2, 12.2], 7.5)

    # ---------------------------------------------------------- N. trang web
    doc.add_heading('N. Trang web (HTML/JS) và nơi sử dụng', 1)
    para(doc, 'Bcode.App/Web/Shell/*.html: cột "Dùng bởi" là các file C# nhắc tới tên trang. BcodeViewer/Web/*.js: tóm tắt từ chú thích đầu file.', 9.5)
    cs_all = {}
    for fp in files_in('Bcode.App', '.', ('.cs',)) + files_in('Bcode.ReportBuilder', '.', ('.cs',)):
        try:
            cs_all[fp] = open(fp, encoding='utf-8-sig', errors='replace').read()
        except Exception:
            pass
    rows = []
    for fp in sorted(files_in('Bcode.App', 'Web/Shell', ('.html',))):
        name = os.path.basename(fp)
        users = sorted({os.path.basename(p) for p, t in cs_all.items() if name in t})
        n = sum(1 for _ in open(fp, 'rb'))
        rows.append((name, f'{n} dòng', ', '.join(users[:6]) or '(trang nạp bằng JS/trang khác)'))
    doc.add_heading('Bcode.App/Web/Shell', 2)
    table(doc, ['Trang', 'Cỡ', 'Dùng bởi'], rows, [4.4, 2, 11], 7.5)
    rows = []
    for fp in sorted(files_in('BcodeViewer.App', 'Web', ('.js',))):
        if os.sep + 'vs' + os.sep in fp:
            continue
        rows.append((os.path.relpath(fp, os.path.join(SRC, 'BcodeViewer.App', 'Web')).replace(os.sep, '/'), js_summary(fp)))
    doc.add_heading('BcodeViewer.App/Web', 2)
    table(doc, ['File', 'Mô tả (từ chú thích đầu file)'], rows, [3.4, 14], 7.5)
    rows = []
    for fp in sorted(files_in('Bcode.ScreenDesigner', 'Web', ('.js', '.html'))) + sorted(files_in('Bcode.ReportBuilder', 'Web', ('.html',))):
        rows.append((os.path.relpath(fp, SRC).replace(os.sep, '/'), js_summary(fp) if fp.endswith('.js') else ''))
    doc.add_heading('Bcode.ScreenDesigner / ReportBuilder — Web', 2)
    table(doc, ['File', 'Ghi chú'], rows, [7, 10.4], 7.5)

    # ---------------------------------------------------------- O. README
    doc.add_heading('O. Ghi chú từ README.md (còn hiệu lực)', 1)
    for t in [
        'Bcode viết mới hoàn toàn dựa trên mô tả chức năng và ảnh chụp màn hình — không decompile/patch FCode.exe hay các .dll của nó. Ngoại lệ: Libs/FastBusiness.Crypto.dll là DLL của chính người dùng, chỉ gọi API public (Crypto.RSADecrypt, Crypto.Encode...); xem Libs/README.md.',
        'Mở project: cần .NET 8 SDK + workload ".NET Desktop Development"; mở Bcode.sln hoặc dotnet run --project src/Bcode.App.',
        'Bảng phân kỳ $000000: bảng giao dịch FastBusiness (m21$000000...) thực tế chia theo kỳ (m21$202601...). PeriodTableQueryService.DiscoverPeriodTablesAsync tìm các bảng <base>$<6 số> qua sys.tables (loại $000000 là bảng mẫu); BuildUnionSubquery ghép UNION ALL; SqlQueryService.ResolveFromClauseAsync tự thay khi ô FROM có dạng <base>$000000 (có/không alias). Regex bắt tên bảng phải dùng [\\w$]+ (\\w không gồm $ — từng gây lỗi trả 0 dòng). Nếu quy ước kỳ khác (quý, năm 4 số...) sửa pattern trong PeriodTableQueryService.',
        'Decrypt SQL Object: không cố khôi phục thuật toán riêng của FCode; dùng engine SqlDecryptor.Core (known-plaintext trên object của chính người dùng qua DAC). IDecryptionProvider là điểm cắm cũ.',
        'Việc nên làm tiếp (README): xác nhận cột thật của bảng wcommand; test PeriodTableQueryService với dữ liệu thật; mã hoá mật khẩu SQL trong AppSettings (hiện lưu plain text trong JSON local) nếu máy dùng chung.',
        'README.md còn mục "Cập nhật gần đây" (~400 dòng, nhật ký sửa lỗi/tính năng theo thời gian) — dùng để tra lịch sử, không lặp lại ở đây.',
    ]:
        bullet(doc, t, 9)
