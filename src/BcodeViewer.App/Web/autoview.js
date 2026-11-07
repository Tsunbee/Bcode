// Khai field xong tự sinh dòng view (Settings → Gợi ý & Hỏi Fast → "Khai field xong tự sinh dòng <field name/> trong <views>",
// ViewerSettings.AutoViewOnFieldDeclare, mặc định TẮT — tắt thì file này không làm gì).
//
// Grid / Lookup: khai thêm <field name="X" …> trong <fields> mà <views> chưa nhắc tới X → thêm <field name="X"/> vào <view>:
//   - chỉ field MỚI so với lúc mở file (field thiếu sẵn trong file cũ không tự thêm — đó là việc của Check Field / gợi ý chéo);
//   - "khai xong" = thẻ mở <field …> đã đóng và con trỏ đã ra khỏi thẻ đó, ngừng gõ ~0,7 giây (đang gõ dở tên thì chờ);
//   - đặt sau dòng view của field khai ngay trước nó (đúng thứ tự khai báo), không có thì trước dòng của field khai sau nó,
//     không có nữa thì cuối <view>; thụt lề + kiểu viết (<field name="x"/> / <field name="x" />) theo dòng view sẵn có;
//   - 1 lần sửa riêng (Ctrl+Z bỏ đúng dòng vừa thêm); Ctrl+Z / Ctrl+Y / nạp lại file không kích hoạt;
//   - đổi tên field vừa được thêm tự (sửa lỗi gõ) thì dòng view đổi tên theo thay vì thêm dòng mới.
// Dir / Filter đặt field bằng <item value="mặt nạ: [x].Label, [x]"> (bề rộng theo mặt nạ, hay đặt qua entity) nên không tự thêm.
// Dùng lại hàm quét của completion.js: rootKindOf, declaredFieldNames, collectViewInfo.

class BcodeAutoView {
  constructor(bcode) {
    this.bcode = bcode;
    this.editor = bcode.editor;
    // model -> { names: Set (field đã khai, đã xét), added: Set (field do mình thêm vào view), pending: còn field khai dở chờ con trỏ rời thẻ }
    this.snap = new WeakMap();
    this._timer = null;
    this._self = false;
    this.editor.onDidChangeModel(() => this.prime(this.editor.getModel(), false));
    this.editor.onDidChangeModelContent((e) => this.onChange(e));
    // Gõ xong <field name="x" …/> rồi bấm chuột / phím mũi tên sang chỗ khác (không gõ thêm) thì vẫn phải xét.
    this.editor.onDidChangeCursorPosition(() => {
      const st = this.snap.get(this.editor.getModel());
      if (st && st.pending) this.schedule(this.editor.getModel(), 300);
    });
  }

  get enabled() {
    const c = window.bcodeCompletion && window.bcodeCompletion.config;
    return !!(c && c.autoViewField);
  }

  /// Ghi nhớ danh sách field đang khai của model (force = ghi đè: nạp lại file, Ctrl+Z / Ctrl+Y).
  prime(model, force) {
    if (!model || (!force && this.snap.has(model))) return;
    if (!this.enabled) { this.snap.delete(model); return; }
    const old = this.snap.get(model);
    this.snap.set(model, { names: new Set(declaredFieldNames(autoViewMask(model.getValue()))), added: old ? old.added : new Set(), lost: new Set(), pending: false });
  }

  schedule(model, ms) {
    clearTimeout(this._timer);
    this._timer = setTimeout(() => this.check(model), ms);
  }

  onChange(e) {
    const model = this.editor.getModel();
    if (!model || this._self) return;
    if (!this.enabled) { this.snap.delete(model); return; }
    if (e.isFlush || e.isUndoing || e.isRedoing || !this.snap.has(model)) { clearTimeout(this._timer); this.prime(model, true); return; }
    this.schedule(model, 700);
  }

  check(model) {
    if (!this.enabled || !model || model !== this.editor.getModel() || model.isDisposed()) return;
    if (this.editor.getOption(monaco.editor.EditorOption.readOnly)) return;
    const st = this.snap.get(model);
    if (!st) return;
    const masked0 = autoViewMask(model.getValue());
    const names = declaredFieldNames(masked0);
    st.pending = false;
    if (!['grid', 'lookup'].includes(rootKindOf(masked0))) { st.names = new Set(names); return; }

    const caret = model.getOffsetAt(this.editor.getPosition());
    const fresh = [];
    for (const n of names) {
      if (st.names.has(n)) continue;
      const tag = this.declTag(masked0, n);
      // Chưa đóng thẻ / con trỏ còn trong thẻ: đang gõ dở — chưa tính là đã xét, chờ lượt sau.
      if (!tag || (caret > tag.start && caret <= tag.end)) { st.pending = true; continue; }
      fresh.push(n);
    }
    const declared = new Set(names);
    // Field do mình thêm mà tên khai báo vừa biến mất (đang sửa tên): nhớ tới khi tên mới gõ xong (con trỏ rời thẻ) để đổi tên dòng view.
    st.lost = new Set([...(st.lost || []), ...[...st.names].filter((n) => !declared.has(n) && st.added.has(n))].filter((n) => !declared.has(n)));
    const gone = [...st.lost];
    st.names = new Set(names.filter((n) => st.names.has(n) || fresh.includes(n)));
    // Không còn gì đang gõ dở mà cũng không có tên mới = xoá hẳn khai báo, không phải đổi tên: quên đi.
    if (!fresh.length && !st.pending) st.lost = new Set();
    if (!fresh.length || fresh.length > 50) return;
    st.lost = new Set();

    const eol = model.getEOL();
    const done = [];
    const apply = (at, len, text) => {
      const a = model.getPositionAt(at), b = model.getPositionAt(at + len);
      this.editor.executeEdits('autoview', [{ range: new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column), text }]);
    };
    this._self = true;
    try {
      this.editor.pushUndoStop();
      for (const n of fresh) {
        // Chèn từng dòng lên chính model (gom chung 1 lần Ctrl+Z) rồi đọc lại — field sau xếp theo dòng vừa chèn.
        const masked = autoViewMask(model.getValue());
        const view = collectViewInfo(masked);
        if (view.start < 0 || view.referenced.has(n)) continue;
        // Sửa tên field vừa tự thêm (ma_k → ma_kh): đổi tên dòng view thay vì thêm dòng mới.
        const old = gone.length === 1 && fresh.length === 1 && st.added.has(gone[0]) ? this.viewLine(masked, view, gone[0]) : null;
        if (old) {
          apply(old.start + old.line.indexOf('"' + gone[0] + '"') + 1, gone[0].length, n);
          st.added.delete(gone[0]);
        } else {
          const ins = this.insertion(masked, view, names, n, eol);
          if (!ins) continue;
          apply(ins.at, 0, ins.text);
        }
        st.added.add(n);
        done.push(n);
      }
      this.editor.pushUndoStop();
    } finally { this._self = false; }
    if (!done.length) return;
    const msg = done.length === 1 ? `Đã thêm <field name="${done[0]}"/> vào <views>` : `Đã thêm ${done.length} field vào <views>`;
    if (window.bcodeViewer && window.bcodeViewer.showToast) window.bcodeViewer.showToast(msg + ' — Ctrl+Z để bỏ', 2500);
  }

  /// Thẻ mở <field … name="n" …> trong khối <fields> → {start, end} (end = sau dấu '>'), null nếu chưa đóng thẻ.
  declTag(masked, n) {
    const q = n.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const re = new RegExp('<field\\b[^<>]*?\\sname\\s*=\\s*"' + q + '"', 'g');
    const blocks = /<fields\b[^>]*>([\s\S]*?)<\/fields>/g;
    let b;
    while ((b = blocks.exec(masked))) {
      // Nội dung khối bắt đầu ngay sau thẻ mở <fields …> (b[1] KHÔNG nằm cuối b[0] — sau nó còn "</fields>", lệch 9 ký tự thì
      // thẻ <field> của field khai trước bị coi là chưa đóng mãi: f.ma_kho chỉ sinh view cho ten_kho%l).
      const base = b.index + b[0].indexOf('>') + 1;
      re.lastIndex = 0;
      const m = re.exec(b[1]);
      if (!m) continue;
      const start = base + m.index;
      const close = masked.indexOf('>', start + m[0].length);
      const nextOpen = masked.indexOf('<', start + 1);
      if (close < 0 || (nextOpen >= 0 && nextOpen < close)) return null;
      return { start, end: close + 1 };
    }
    return null;
  }

  /// Dòng <field name="n" …/> đầu tiên trong <views> → {start (đầu dòng), end (sau xuống dòng), line}.
  viewLine(masked, view, n) {
    const q = n.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const re = new RegExp('<field\\b[^<>]*?\\sname\\s*=\\s*"' + q + '"[^<>]*>', 'g');
    re.lastIndex = view.start;
    const m = re.exec(masked);
    if (!m || m.index > view.end) return null;
    const start = masked.lastIndexOf('\n', m.index) + 1;
    let end = masked.indexOf('\n', m.index);
    end = end < 0 ? masked.length : end + 1;
    return { start, end, line: masked.slice(start, end).replace(/\r?\n$/, '') };
  }

  /// Chỗ chèn + nội dung cho field n: sau dòng view của field khai trước nó, trước dòng của field khai sau nó, hoặc cuối <view>.
  insertion(masked, view, names, n, eol) {
    const idx = names.indexOf(n);
    const style = (line) => {
      const lead = /^[ \t]*/.exec(line)[0];
      const body = line.trim();
      const m = /^<field(\s+)name(\s*=\s*)"[^"]*"(\s*)\/>$/.exec(body);
      return lead + (m ? `<field${m[1]}name${m[2]}"${n}"${m[3]}/>` : `<field name="${n}"/>`);
    };
    for (let i = idx - 1; i >= 0; i--) {
      const prev = this.viewLine(masked, view, names[i]);
      if (prev && /^\s*<field\b[^<>]*\/>\s*$/.test(prev.line)) return { at: prev.end, text: style(prev.line) + (masked.charAt(prev.end - 1) === '\n' ? eol : '') };
    }
    for (let i = idx + 1; i < names.length; i++) {
      const next = this.viewLine(masked, view, names[i]);
      if (next && /^\s*<field\b[^<>]*\/>\s*$/.test(next.line)) return { at: next.start, text: style(next.line) + eol };
    }
    // Không có dòng <field/> nào để dựa vào: cuối <view> đầu tiên (như nút View của Check Field).
    const close = masked.indexOf('</view>', view.start);
    if (close < 0 || close > view.end) return null;
    const ls = masked.lastIndexOf('\n', close) + 1;
    const lead = /^[ \t]*/.exec(masked.slice(ls))[0];
    const own = masked.slice(ls, close).trim().length === 0;
    const unit = lead.includes('\t') ? '\t' : '  ';
    return own ? { at: ls, text: lead + unit + `<field name="${n}"/>` + eol } : { at: close, text: `<field name="${n}"/>` };
  }
}

/// Che comment (giữ nguyên độ dài + xuống dòng) để offset vẫn khớp văn bản gốc.
function autoViewMask(text) {
  return text.replace(/<!--[\s\S]*?-->/g, (m) => m.replace(/[^\n]/g, ' '));
}
