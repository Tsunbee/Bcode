// Hỗ trợ đọc "design" của <view> (Dir/Grid form) trong controller FCode:
//
//   <view id="Dir">
//     <item value="25, 75, 30, 70, 50, 187, 100, 8, 58, 42, 8, 100, 0, 0, 0"/>      ← bề rộng các cột
//     <item value="1010100-100111: [ma_kh].Label, [ma_kh], [ten_kh%l], ..."/>         ← 1 dòng thiết kế
//
// Dòng thiết kế = "mặt nạ: danh sách phần tử". Mỗi ký tự của mặt nạ ứng với 1 cột ở dòng bề rộng:
//   '1' = bắt đầu 1 phần tử, các '0' liền sau nó = phần tử đó chiếm thêm các cột đó,
//   '-' = cột trống (không có control nào) — không tính vào bề rộng của phần tử.
// Phần tử thứ n trong danh sách ứng với ký tự '1' thứ n của mặt nạ.
//
// Module này làm 2 việc, đều chỉ ĐỌC văn bản (không đổi file):
//  1. Tô màu riêng từng thành phần (mặt nạ 1/0/-, tên biến, nhãn, bề rộng) — trước đây cả giá trị
//     value="..." là 1 màu thuộc tính nên mặt nạ, biến và số lẫn vào nhau, khó đọc.
//  2. Hover vào 1 biến (hoặc 1 ký tự mặt nạ) → hiện bề rộng thật của biến đó: tổng các cột nó
//     chiếm / tổng bề rộng cả dòng, kèm dòng bề rộng với cột của nó trong ngoặc [..] và Header
//     của field; dòng "Cờ" cho biết biến ứng với ký tự nào của mặt nạ.
//  3. Chuột hoặc con trỏ đứng trên 1 biến → tô các ký tự mặt nạ của nó, và ngược lại (đứng trên
//     1 ký tự mặt nạ → tô biến). Cách ghép biến ↔ ký tự y hệt BcodeDirPreview.parseRows (Alt+P).

const DIRVIEW_HOVER_LANGUAGES = ['xml', 'fcode-xml'];

class BcodeDirView {
  constructor(bcode) {
    this.bcode = bcode;
    this.decorations = new WeakMap(); // model -> decoration ids
    this.timers = new WeakMap();
    this.parseCache = new WeakMap(); // model -> { ver, rows } — hover/chuột/con trỏ dùng chung, chỉ quét lại khi file đổi
    this.linked = new WeakMap(); // editor -> trạng thái tô cặp biến ↔ mặt nạ

    monaco.languages.registerHoverProvider(DIRVIEW_HOVER_LANGUAGES, {
      provideHover: (model, position) => this.provideHover(model, position),
    });

    // Mọi editor (chính, chia đôi, peek) — tô cặp biến ↔ ký tự mặt nạ theo chuột / con trỏ.
    monaco.editor.getEditors().forEach((ed) => this.hookEditor(ed));
    monaco.editor.onDidCreateEditor((ed) => this.hookEditor(ed));

    const attach = (model) => {
      if (!this.isCandidate(model)) return;
      model.onDidChangeContent(() => this.schedule(model));
      this.schedule(model, 0);
    };
    monaco.editor.getModels().forEach(attach);
    monaco.editor.onDidCreateModel(attach);
    // Ngôn ngữ của model có thể được gán sau khi tạo (setModelLanguage) — quét lại khi đổi.
    monaco.editor.onDidChangeModelLanguage((e) => attach(e.model));
  }

  isCandidate(model) {
    const lang = model.getLanguageId();
    return DIRVIEW_HOVER_LANGUAGES.includes(lang);
  }

  schedule(model, delay = 250) {
    // Mở/đổi ngôn ngữ (delay 0): tô ngay như cũ. Sửa nội dung: vào bộ lập lịch chung (editor.js bcodeTyping), mỗi model một khoá.
    if (delay === 0 || !window.bcodeTyping) {
      clearTimeout(this.timers.get(model));
      this.timers.set(model, setTimeout(() => this.decorate(model), delay));
      return;
    }
    window.bcodeTyping.request('dirview:' + model.uri.toString(), () => { if (!model.isDisposed()) this.decorate(model); }, delay, 2000);
  }

  // ---- Parsing ------------------------------------------------------------------------

  /// parse() của bản hiện tại của model, nhớ theo versionId.
  parsed(model) {
    const ver = model.getVersionId();
    const c = this.parseCache.get(model);
    if (c && c.ver === ver) return c.rows;
    const text = model.getValue();
    const rows = /<item\b[^>]*\bvalue="/.test(text) ? this.parse(text) : [];
    this.parseCache.set(model, { ver, rows });
    return rows;
  }

  /// Quét văn bản, trả về { rows: [{ widths, widthsOffsets, line }] }. `line` có thể null (chỉ là
  /// dòng bề rộng). Mỗi dòng thiết kế được gắn với dòng bề rộng gần nhất phía trên trong cùng <view>.
  parse(text) {
    const widthRowsAndLines = [];
    const re = /<view\b[^>]*>|<\/view\s*>|<item\b[^>]*?\bvalue="([^"]*)"/g;
    let widths = null;
    let m;
    while ((m = re.exec(text))) {
      if (m[0].startsWith('<view')) { widths = null; continue; }
      if (m[0].startsWith('</view')) { widths = null; continue; }

      const value = m[1];
      const valueStart = m.index + m[0].length - 1 - value.length; // m[0] kết thúc bằng dấu " đóng của value

      // Dòng bề rộng = "số, số, số..." và có thể nối thêm entity ở cuối (vd "..., 100, 0&ddDir0Views;" —
      // entity đó khai thêm cột lúc chạy). Chỉ đọc số ở phần tiền tố số: tên entity cũng chứa chữ số
      // ("ddDir0Views") nên không được quét cả chuỗi.
      const widthPrefix = /^\s*\d+(?:\s*,\s*\d+)+/.exec(value);
      if (widthPrefix && /^\s*(?:&[\w.:$-]+;\s*)*$/.test(value.slice(widthPrefix[0].length))) {
        const nums = [];
        const numRe = /\d+/g;
        let n;
        while ((n = numRe.exec(widthPrefix[0]))) nums.push({ value: parseInt(n[0], 10), start: valueStart + n.index, end: valueStart + n.index + n[0].length });
        nums.hasEntity = value.slice(widthPrefix[0].length).trim().length > 0;
        widths = nums;
        widthRowsAndLines.push({ kind: 'widths', nums });
        continue;
      }

      if (!widths) continue;
      // Entity có thể nằm giữa mặt nạ ("110&X.Split;-----101-"): `mask` = phần trước entity đầu tiên (vị trí chắc
      // chắn), phần từ entity trở đi là `maskTail` — lúc chạy entity chèn thêm ký tự nên vị trí phía sau không biết.
      const maskMatch = /^(\s*)([01\-]+)((?:&[\w.:$-]+;[01\-]*)*)(\s*:)/.exec(value);
      if (!maskMatch) continue;
      const mask = maskMatch[2];
      const maskStart = valueStart + maskMatch[1].length;
      const restStart = valueStart + maskMatch[0].length;
      const rest = value.slice(maskMatch[0].length);
      widthRowsAndLines.push({
        kind: 'line', widths, mask, maskStart,
        maskTail: maskMatch[3] ? { text: maskMatch[3], start: maskStart + mask.length, entity: /&[\w.:$-]+;/.exec(maskMatch[3])[0] } : null,
        // entity trong dòng bề rộng/mặt nạ khai thêm cột lúc chạy → bề rộng hiển thị có thể thiếu cột.
        hasEntity: !!maskMatch[3] || !!widths.hasEntity,
        elements: this.parseElements(rest, restStart, mask),
      });
    }
    return widthRowsAndLines;
  }

  parseElements(rest, restStart, mask) {
    const elements = [];
    const ones = [];
    for (let i = 0; i < mask.length; i++) if (mask[i] === '1') ones.push(i);

    const tokenRe = /[^,]+/g;
    let t;
    let idx = 0;
    while ((t = tokenRe.exec(rest))) {
      const raw = t[0];
      const lead = raw.length - raw.trimStart().length;
      const text = raw.trim();
      if (!text) continue;
      const start = restStart + t.index + lead;

      let span = null;
      const col = ones[idx];
      if (col !== undefined) {
        let last = col;
        while (last + 1 < mask.length && mask[last + 1] === '0') last++;
        span = [col, last];
      }

      const fm = /^\[([^\]]+)\](?:\.(\w+))?((?:&[\w.:$-]+;)*)$/.exec(text);
      elements.push({
        text, start, end: start + text.length, span, index: idx,
        // &entity; ngoài [..] (cả phần tử là entity, hoặc đuôi "[x]&ddDir2Views;") khai triển ra thêm phần tử lúc
        // chạy → các phần tử SAU nó không còn ứng với '1' thứ n như viết.
        expands: /&[\w.:$-]+;/.test(text.replace(/\[[^\]]*\]/g, '')),
        field: fm ? fm[1] : null,
        suffix: fm && fm[2] ? fm[2] : null,
        bracketEnd: fm ? start + fm[1].length + 2 : start,
        // hết phần "[x].Label" (không tính entity nối sau) — để tô màu đúng chỗ.
        coreEnd: fm ? start + text.length - fm[3].length : start + text.length,
      });
      idx++;
    }
    return elements;
  }

  // ---- Colouring ----------------------------------------------------------------------

  decorate(model) {
    if (model.isDisposed()) return;
    const decos = [];
    for (const row of this.parsed(model)) {
      if (row.kind === 'widths') {
        for (const n of row.nums) decos.push(this.range(model, n.start, n.end, n.value === 0 ? 'fcd-w0' : 'fcd-w'));
      } else {
        for (let i = 0; i < row.mask.length; i++) {
          const c = row.mask[i];
          const cls = c === '1' ? 'fcd-mask-on' : c === '0' ? 'fcd-mask-off' : 'fcd-mask-gap';
          decos.push(this.range(model, row.maskStart + i, row.maskStart + i + 1, cls));
        }
        if (row.maskTail) {
          // Ký tự mặt nạ sau entity giữa mặt nạ — tô cùng màu, bỏ qua chữ số trong tên entity.
          const re = /&[\w.:$-]+;|[01\-]/g;
          let t;
          while ((t = re.exec(row.maskTail.text))) {
            const c = t[0];
            if (c.length > 1) continue;
            const cls = c === '1' ? 'fcd-mask-on' : c === '0' ? 'fcd-mask-off' : 'fcd-mask-gap';
            decos.push(this.range(model, row.maskTail.start + t.index, row.maskTail.start + t.index + 1, cls));
          }
        }
        for (const el of row.elements) {
          if (!el.field) continue;
          const withSuffix = !!el.suffix;
          decos.push(this.range(model, el.start, el.bracketEnd, withSuffix ? 'fcd-field-sub' : 'fcd-field'));
          if (withSuffix) decos.push(this.range(model, el.bracketEnd, el.coreEnd, 'fcd-suffix'));
        }
      }
    }
    const old = this.decorations.get(model) || [];
    this.decorations.set(model, model.deltaDecorations(old, decos));
  }

  range(model, start, end, inlineClassName) {
    const a = model.getPositionAt(start);
    const b = model.getPositionAt(end);
    return {
      range: new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column),
      options: { inlineClassName },
    };
  }

  // ---- Tô cặp biến ↔ ký tự mặt nạ -------------------------------------------------------
  // Chuột đang trỏ (ưu tiên) hoặc con trỏ đứng trên 1 biến → tô các ký tự mặt nạ của nó và chính nó; đứng trên
  // 1 ký tự mặt nạ thì cũng tô cả cặp. Không chắc (entity chèn phần tử) → viền đứt. Chỉ đổi decoration của editor.

  hookEditor(ed) {
    if (this.linked.has(ed)) return;
    const st = { deco: ed.createDecorationsCollection(), mouse: null, key: '', timer: 0, edited: 0 };
    this.linked.set(ed, st);
    ed.onMouseMove((e) => {
      const t = e.target;
      const T = monaco.editor.MouseTargetType;
      if (t.type === T.CONTENT_WIDGET || t.type === T.OVERLAY_WIDGET) return; // đang ở trong khung hover: giữ nguyên
      const model = ed.getModel();
      const pos = t.type === T.CONTENT_TEXT ? (t.range ? t.range.getStartPosition() : t.position) : null;
      const offset = model && pos ? model.getOffsetAt(pos) : null;
      if (offset === st.mouse) return;
      st.mouse = offset;
      this.updateLink(ed, st);
    });
    ed.onMouseLeave(() => { st.mouse = null; this.updateLink(ed, st); });
    ed.onDidChangeCursorPosition(() => this.scheduleLink(ed, st));
    ed.onDidChangeModel(() => { st.mouse = null; this.scheduleLink(ed, st); });
    // Sửa file: vị trí cũ lệch → xoá ngay; tô lại khi ngừng gõ (khỏi quét lại cả file mỗi phím).
    ed.onDidChangeModelContent(() => { st.edited = Date.now(); st.key = ''; st.deco.clear(); });
  }

  scheduleLink(ed, st) {
    clearTimeout(st.timer);
    st.timer = setTimeout(() => this.updateLink(ed, st), Date.now() - st.edited < 400 ? 400 : 30);
  }

  updateLink(ed, st) {
    const model = ed.getModel();
    let ranges = [];
    if (model && this.isCandidate(model)) {
      const linkOf = (offset) => {
        const hit = offset == null ? null : this.locate(model, offset);
        if (!hit || !hit.el || !hit.el.span) return null;
        const f = this.flagOf(hit.row, hit.el);
        const cls = f.sure ? 'fcd-link' : 'fcd-link-guess';
        return [
          [hit.row.maskStart + f.a, hit.row.maskStart + f.b + 1, cls],
          [hit.el.start, hit.el.end, cls],
        ];
      };
      const pos = ed.getPosition();
      ranges = linkOf(st.mouse) || linkOf(pos ? model.getOffsetAt(pos) : null) || [];
    }
    const key = ranges.map((r) => r.join(':')).join('|');
    if (key === st.key) return;
    st.key = key;
    // className (lớp nền riêng của Monaco) thay vì inlineClassName: 1 khung liền cho cả "[x].Label" dù chữ bên trong
    // bị các decoration màu chia thành nhiều đoạn.
    st.deco.set(ranges.map(([s, e, cls]) => {
      const d = this.range(model, s, e, cls);
      return { range: d.range, options: { className: cls } };
    }));
  }

  // ---- Hover --------------------------------------------------------------------------

  provideHover(model, position) {
    const hit = this.locate(model, model.getOffsetAt(position));
    if (!hit) return null;
    const { row, el, col } = hit;
    if (hit.tail != null) return this.hoverTail(model, row, hit.tail);
    const hoverRange = col == null ? [el.start, el.end] : [row.maskStart + col, row.maskStart + col + 1];
    if (!el) return this.hoverGap(model, row, col, hoverRange);
    if (!el.span) return this.hoverNoFlag(model, row, el, hoverRange); // phần tử thừa so với số '1' của mặt nạ
    return this.hoverElement(model, model.getValue(), row, el, hoverRange);
  }

  /// `offset` nằm trên phần tử nào của dòng thiết kế nào: { row, el, col }. `col` (cột mặt nạ) chỉ có khi đang
  /// trỏ vào ký tự mặt nạ — khi đó `el` là phần tử mà cột đó thuộc về, null nếu cột không thuộc phần tử nào.
  locate(model, offset) {
    for (const row of this.parsed(model)) {
      if (row.kind !== 'line') continue;
      const lineEnd = row.elements.length ? row.elements[row.elements.length - 1].end : row.maskStart + row.mask.length;
      if (offset < row.maskStart || offset > lineEnd) continue;

      const el = row.elements.find((e) => offset >= e.start && offset <= e.end);
      if (el) return { row, el, col: null };
      // Đang trỏ vào ký tự mặt nạ → tìm phần tử mà cột đó thuộc về.
      if (offset >= row.maskStart && offset < row.maskStart + row.mask.length) {
        const col = offset - row.maskStart;
        return { row, col, el: row.elements.find((e) => e.span && col >= e.span[0] && col <= e.span[1]) || null };
      }
      const tail = row.maskTail;
      if (tail && offset >= tail.start && offset < tail.start + tail.text.length) return { row, col: null, el: null, tail: offset };
    }
    return null;
  }

  hoverTail(model, row, offset) {
    const p1 = model.getPositionAt(offset);
    const p2 = model.getPositionAt(offset + 1);
    return {
      range: new monaco.Range(p1.lineNumber, p1.column, p2.lineNumber, p2.column),
      contents: [{ value: `**Cờ: không xác định** — phần mặt nạ từ \`${row.maskTail.entity}\` trở đi: entity chèn thêm ký tự lúc chạy nên không biết ký tự này ở vị trí nào / ứng với biến nào. Chỉ ${row.mask.length} ký tự đầu (\`${row.mask}\`) là chắc chắn.` }],
    };
  }

  /// Ký tự mặt nạ của phần tử: '1' thứ n + các '0' liền sau (= el.span, đọc như BcodeDirPreview.parseRows).
  /// `sure` = false khi 1 phần tử phía trước có &entity; ngoài [..] (`by`) — lúc chạy nó khai triển ra thêm phần tử
  /// nên '1' thứ n không còn là phần tử này; `self` = cả phần tử là &entity;: biết chỗ bắt đầu, không biết chiếm mấy ô.
  flagOf(row, el) {
    if (!el.span) return null;
    const by = row.elements.slice(0, el.index).find((e) => e.expands) || null;
    const self = !by && !el.field && el.expands;
    // Ô chạm tới entity giữa mặt nạ: entity có thể chèn thêm '0' kéo ô dài hơn.
    const open = !by && !self && !!row.maskTail && el.span[1] === row.mask.length - 1;
    return { a: el.span[0], b: el.span[1], by, self, open, sure: !by && !self && !open };
  }

  flagLines(row, el) {
    const f = this.flagOf(row, el);
    const [a, b] = [f.a, f.b];
    const val = row.mask.slice(a, b + 1);
    const pos = `vị trí ${a + 1}${a === b ? '' : '–' + (b + 1)} = \`${val}\``;
    const meaning = `'1' thứ ${el.index + 1} mở ô ở cột ${a + 1}`
      + (b > a ? `, ${b - a} số '0' kéo ô sang tới cột ${b + 1} (colspan ${b - a + 1})` : '');
    const lines = [];
    if (f.sure) lines.push(`**Cờ: ${pos}** — ${meaning}`);
    else if (f.open) lines.push(`**Cờ: ${pos}** — ${meaning}; \`${row.maskTail.entity}\` ngay sau có thể chèn thêm '0' kéo ô dài hơn lúc chạy`);
    else if (f.self) lines.push(`**Cờ: bắt đầu ở vị trí ${a + 1}** — \`${el.text}\` khai triển lúc chạy, chiếm bao nhiêu '1' kể từ đây thì không xác định được; phần viết trực tiếp tính ${pos}`);
    else lines.push(`**Cờ: không xác định** — \`${f.by.text}\` (phần tử thứ ${f.by.index + 1}) chèn thêm phần tử lúc chạy nên '1' thứ ${el.index + 1} chưa chắc là biến này. Theo phần viết trực tiếp: ${pos}`);
    lines.push('`' + row.mask.slice(0, a) + '[' + val + ']' + row.mask.slice(b + 1) + '`');
    return lines;
  }

  hoverNoFlag(model, row, el, hoverRange) {
    const ones = row.mask.split('').filter((c) => c === '1').length;
    const p1 = model.getPositionAt(hoverRange[0]);
    const p2 = model.getPositionAt(hoverRange[1]);
    const value = row.maskTail
      ? `**Cờ: không xác định** — đây là phần tử thứ ${el.index + 1}, phần mặt nạ viết trực tiếp \`${row.mask}\` có ${ones} ký tự '1', phần sau do \`${row.maskTail.entity}\` chèn thêm lúc chạy.`
      : `**Cờ: không có** — đây là phần tử thứ ${el.index + 1} nhưng mặt nạ \`${row.mask}\` chỉ có ${ones} ký tự '1' (Xem trước Dir bỏ qua phần tử này).`;
    return {
      range: new monaco.Range(p1.lineNumber, p1.column, p2.lineNumber, p2.column),
      contents: [{ value }],
    };
  }

  hoverElement(model, text, row, el, hoverRange) {
    const widths = row.widths.map((w) => w.value);
    const [a, b] = el.span;
    let sum = 0;
    for (let i = a; i <= b && i < widths.length; i++) sum += widths[i];
    const total = widths.reduce((x, y) => x + y, 0);
    const pct = total > 0 ? ((sum / total) * 100).toFixed(1) : '0';

    const cells = widths.map((w, i) => {
      if (row.mask[i] === '-') return `-${w}-`;
      return String(w);
    });
    // Ngoặc chỉ bao các cột của phần tử: [30, 70]
    const shown = [];
    cells.forEach((c, i) => {
      if (i === a) shown.push('[' + c);
      else if (i === b) shown.push(c + ']');
      else shown.push(c);
    });
    if (a === b) shown[a] = '[' + cells[a] + ']';

    const info = this.fieldInfo(text, el.field);
    const lines = [];
    if (row.hasEntity) lines.push('*Dòng này có &entity; chèn thêm cột lúc chạy — số cột/bề rộng bên dưới chỉ tính phần viết trực tiếp.*');
    lines.push(`**Rộng ${sum} / ${total}** (${pct}%) — ${b - a + 1} cột (${a + 1}${a === b ? '' : '–' + (b + 1)})`);
    lines.push('`' + shown.join(', ') + '`');
    lines.push(...this.flagLines(row, el));
    if (el.field) {
      const parts = [];
      if (info && info.v) parts.push(`**Header:** ${info.v}`);
      if (info && info.e) parts.push(`**EN:** ${info.e}`);
      if (el.suffix) parts.push(`*(${el.suffix})*`);
      if (!info) parts.push('*(chưa thấy khai báo `<field name="' + el.field + '">` trong file này)*');
      if (parts.length) lines.push(parts.join(' · '));
    }
    const p1 = model.getPositionAt(hoverRange[0]);
    const p2 = model.getPositionAt(hoverRange[1]);
    return {
      range: new monaco.Range(p1.lineNumber, p1.column, p2.lineNumber, p2.column),
      contents: [{ value: lines.join('\n\n') }],
    };
  }

  hoverGap(model, row, col, hoverRange) {
    const p1 = model.getPositionAt(hoverRange[0]);
    const p2 = model.getPositionAt(hoverRange[1]);
    const w = row.widths[col] ? row.widths[col].value : 0;
    const what = row.mask[col] === '-' ? 'cột trống (không có control)' : 'cột không thuộc phần tử nào';
    return {
      range: new monaco.Range(p1.lineNumber, p1.column, p2.lineNumber, p2.column),
      contents: [{ value: `Cột ${col + 1}: ${what}, rộng **${w}**` }],
    };
  }

  /// Header của <field name="X"> trong file hiện tại. Tên field có thể là &entity; (vd name="&k;")
  /// khai báo ở DOCTYPE bằng <!ENTITY k "ma_kh"> — thử khớp cả dạng đã/chưa khai triển.
  fieldInfo(text, fieldName) {
    if (!fieldName) return null;
    const wanted = new Set([fieldName, this.expandEntity(text, fieldName)]);
    const re = /<field\b[^>]*?\bname="([^"]+)"[^>]*>\s*<header\b([^>]*)>/g;
    let m;
    while ((m = re.exec(text))) {
      const name = m[1];
      if (!wanted.has(name) && !wanted.has(this.expandEntity(text, name))) continue;
      const v = /\bv="([^"]*)"/.exec(m[2]);
      const e = /\be="([^"]*)"/.exec(m[2]);
      return { v: v ? this.unescape(v[1]) : '', e: e ? this.unescape(e[1]) : '' };
    }
    return null;
  }

  expandEntity(text, name) {
    const em = /^&([A-Za-z_][\w.:$-]*);$/.exec(name);
    if (!em) return name;
    const decl = new RegExp('<!ENTITY\\s+' + em[1].replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\s+"([^"]*)"').exec(text);
    return decl ? decl[1] : name;
  }

  unescape(s) {
    return s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&amp;/g, '&');
  }
}
