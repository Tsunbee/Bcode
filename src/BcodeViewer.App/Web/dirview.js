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
//     của field.

const DIRVIEW_HOVER_LANGUAGES = ['xml', 'fcode-xml'];

class BcodeDirView {
  constructor(bcode) {
    this.bcode = bcode;
    this.decorations = new WeakMap(); // model -> decoration ids
    this.timers = new WeakMap();

    monaco.languages.registerHoverProvider(DIRVIEW_HOVER_LANGUAGES, {
      provideHover: (model, position) => this.provideHover(model, position),
    });

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
    clearTimeout(this.timers.get(model));
    this.timers.set(model, setTimeout(() => this.decorate(model), delay));
  }

  // ---- Parsing ------------------------------------------------------------------------

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

      if (/^\s*\d+(\s*,\s*\d+)+\s*$/.test(value)) {
        const nums = [];
        const numRe = /\d+/g;
        let n;
        while ((n = numRe.exec(value))) nums.push({ value: parseInt(n[0], 10), start: valueStart + n.index, end: valueStart + n.index + n[0].length });
        widths = nums;
        widthRowsAndLines.push({ kind: 'widths', nums });
        continue;
      }

      if (!widths) continue;
      const maskMatch = /^(\s*)([01\-]+)(\s*:)/.exec(value);
      if (!maskMatch) continue;
      const mask = maskMatch[2];
      const maskStart = valueStart + maskMatch[1].length;
      const restStart = valueStart + maskMatch[0].length;
      const rest = value.slice(maskMatch[0].length);
      widthRowsAndLines.push({ kind: 'line', widths, mask, maskStart, elements: this.parseElements(rest, restStart, mask) });
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

      const fm = /^\[([^\]]+)\](?:\.(\w+))?$/.exec(text);
      elements.push({
        text, start, end: start + text.length, span,
        field: fm ? fm[1] : null,
        suffix: fm && fm[2] ? fm[2] : null,
        bracketEnd: fm ? start + fm[1].length + 2 : start,
      });
      idx++;
    }
    return elements;
  }

  // ---- Colouring ----------------------------------------------------------------------

  decorate(model) {
    if (model.isDisposed()) return;
    const text = model.getValue();
    let decos = [];
    if (/<item\b[^>]*\bvalue="/.test(text)) {
      for (const row of this.parse(text)) {
        if (row.kind === 'widths') {
          for (const n of row.nums) decos.push(this.range(model, n.start, n.end, n.value === 0 ? 'fcd-w0' : 'fcd-w'));
        } else {
          for (let i = 0; i < row.mask.length; i++) {
            const c = row.mask[i];
            const cls = c === '1' ? 'fcd-mask-on' : c === '0' ? 'fcd-mask-off' : 'fcd-mask-gap';
            decos.push(this.range(model, row.maskStart + i, row.maskStart + i + 1, cls));
          }
          for (const el of row.elements) {
            if (!el.field) continue;
            const withSuffix = !!el.suffix;
            decos.push(this.range(model, el.start, el.bracketEnd, withSuffix ? 'fcd-field-sub' : 'fcd-field'));
            if (withSuffix) decos.push(this.range(model, el.bracketEnd, el.end, 'fcd-suffix'));
          }
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

  // ---- Hover --------------------------------------------------------------------------

  provideHover(model, position) {
    const text = model.getValue();
    if (!/<item\b[^>]*\bvalue="/.test(text)) return null;
    const offset = model.getOffsetAt(position);

    const rows = this.parse(text).filter((r) => r.kind === 'line');
    for (const row of rows) {
      const lineStart = row.maskStart;
      const lineEnd = row.elements.length ? row.elements[row.elements.length - 1].end : row.maskStart + row.mask.length;
      if (offset < lineStart || offset > lineEnd) continue;

      // Đang trỏ vào ký tự mặt nạ → tìm phần tử mà cột đó thuộc về.
      let el = row.elements.find((e) => offset >= e.start && offset <= e.end);
      let hoverRange;
      if (el) {
        hoverRange = [el.start, el.end];
      } else if (offset >= row.maskStart && offset < row.maskStart + row.mask.length) {
        const col = offset - row.maskStart;
        el = row.elements.find((e) => e.span && col >= e.span[0] && col <= e.span[1]);
        hoverRange = [row.maskStart + col, row.maskStart + col + 1];
        if (!el) return this.hoverGap(model, row, col, hoverRange);
      } else {
        continue;
      }
      if (!el.span) return null; // phần tử thừa so với số '1' của mặt nạ — không có cột để tính
      return this.hoverElement(model, text, row, el, hoverRange);
    }
    return null;
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
    lines.push(`**Rộng ${sum} / ${total}** (${pct}%) — ${b - a + 1} cột (${a + 1}${a === b ? '' : '–' + (b + 1)})`);
    lines.push('`' + shown.join(', ') + '`');
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
