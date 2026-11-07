// Note thuộc tính field — thư viện gợi ý khi khai <field …> / <items …>: mỗi thuộc tính có mô tả tiếng Việt + giá trị hợp lệ.
//   - Định nghĩa của Fast (chỉ đọc): Assets\fieldnotes\fast-field-notes.json đi kèm exe (soạn từ thống kê source Fast + form khai field của FCode).
//   - Note của bạn: <Thư mục kiến thức>\viewer\field-notes.json (Settings/FieldNoteStore.cs) — cùng thẻ + thuộc tính + loại file thì thắng định nghĩa Fast.
// Dùng ở: gợi ý tên / giá trị thuộc tính (completion.js provideXml gọi docFor / valuesFor / attrsFor), rê chuột lên thuộc tính (hover ở đây),
// hộp xem / sửa (Tool → Note thuộc tính field…, phím tool.fieldNotes). Nạp 1 lần từ host, tra trong bộ nhớ (provider chạy mỗi phím gõ).

const FIELD_NOTE_SCOPES = [
  ['', 'Mọi loại file'], ['grid', 'Grid'], ['dir', 'Dir'], ['filter', 'Filter'], ['lookup', 'Lookup'], ['report', 'Report'], ['import', 'Import (Upload)'], ['clipboard', 'Clipboard'],
];

class BcodeFieldNotes {
  constructor(bcode) {
    this.bcode = bcode;
    this.builtin = [];
    this.user = [];
    this.path = '';
    this.index = new Map();   // "tag|attr(thường)|scope" -> note (của bạn thắng Fast)
    this.ready = this.load();
    monaco.languages.registerHoverProvider(MARKUP_LANGUAGES, { provideHover: (model, position) => this.provideHover(model, position) });
  }

  async load() {
    try {
      const d = JSON.parse(String(await window.bcodeHost.call('BeginGetFieldNotes') || '{}'));
      this.builtin = (d.builtin || []).map((n) => ({ ...n, src: 'fast' }));
      this.user = (d.user || []).map((n) => ({ ...n, src: 'user' }));
      this.path = d.path || '';
    } catch { /* host cũ / file hỏng: không có note, mọi thứ khác chạy như cũ */ }
    this.rebuild();
  }

  rebuild() {
    this.index = new Map();
    for (const n of [...this.builtin, ...this.user]) this.index.set(BcodeFieldNotes.key(n.tag, n.attr, n.scope), n);
  }

  static key(tag, attr, scope) { return `${(tag || 'field').toLowerCase()}|${String(attr || '').toLowerCase()}|${(scope || '').toLowerCase()}`; }

  /// Loại file cho việc chọn note: rootKind của completion.js (đã nhớ theo phiên bản model), Filter tách khỏi Dir theo đường dẫn.
  kindOf(model) {
    let kind = 'dir';
    try { kind = docFacts(model).rootKind; } catch { /* trang chưa có completion.js */ }
    const path = (this.bcode && this.bcode.activePath) || '';
    if (kind === 'dir' && /[\\/]filter[\\/]/i.test(path)) return 'filter';
    return kind;
  }

  /// Note của thuộc tính: đúng loại file trước, rồi note chung. File import chỉ dùng note của import (bộ thuộc tính khác hẳn).
  find(tag, attr, kind) {
    const k = (s) => this.index.get(BcodeFieldNotes.key(tag, attr, s));
    return k(kind) || (kind === 'import' || kind === 'clipboard' ? null : k('')) || null;
  }

  /// Tên thuộc tính có note cho thẻ ở loại file này, hay dùng trước (count của Fast; note riêng của bạn đứng đầu).
  attrsFor(tag, kind) {
    const out = new Map();
    const t = (tag || '').toLowerCase();
    const list = [...this.index.values()].filter((n) => (n.tag || 'field').toLowerCase() === t
      && ((n.scope || '') === kind || (!n.scope && kind !== 'import' && kind !== 'clipboard')));
    list.sort((a, b) => (a.src === 'user' ? -1e9 : -(a.count || 0)) - (b.src === 'user' ? -1e9 : -(b.count || 0)));
    for (const n of list) if (!out.has(n.attr.toLowerCase())) out.set(n.attr.toLowerCase(), n.attr);
    return [...out.values()];
  }

  /// Giá trị gợi ý [{v, d}] của thuộc tính.
  valuesFor(tag, attr, kind) {
    const n = this.find(tag, attr, kind);
    return n ? (n.values || []) : [];
  }

  /// Markdown cho dropdown / hover. value = giá trị đang đứng (tô đậm dòng của nó).
  markdown(n, value) {
    if (!n) return null;
    const esc = (s) => String(s || '').replace(/[\\`*_{}[\]()#+\-.!|<>]/g, '\\$&');
    const lines = [`**${esc(n.attr)}** — \`<${n.tag || 'field'}>\`${n.scope ? ' · ' + esc(n.scope) : ''} · ${n.src === 'user' ? 'note của bạn' : 'định nghĩa Fast'}`, '', esc(n.desc || '(chưa có mô tả)')];
    if ((n.values || []).length) {
      lines.push('');
      for (const v of n.values) {
        const on = value != null && v.v === value;
        lines.push(`- ${on ? '**' : ''}\`${v.v.replace(/`/g, "'")}\`${v.d ? ' — ' + esc(v.d) : ''}${on ? '**' : ''}`);
      }
    }
    if (n.example) lines.push('', 'Ví dụ: `' + n.example.replace(/`/g, "'") + '`');
    if (n.count) lines.push('', `_Gặp ${n.count.toLocaleString('vi-VN')} lần trong source Fast._`);
    return { value: lines.join('\n') };
  }

  docFor(model, tag, attr) {
    return this.markdown(this.find(tag, attr, this.kindOf(model)));
  }

  // ---- hover: rê chuột lên tên / giá trị thuộc tính trong thẻ mở ------------------------------------------------------

  provideHover(model, position) {
    if (!this.index.size) return null;
    const c = window.bcodeCompletion;
    if (c && c.regionAt(model, position) !== 'xml') return null;
    const first = Math.max(1, position.lineNumber - 15);
    const last = Math.min(model.getLineCount(), position.lineNumber + 3);
    const winStart = model.getOffsetAt({ lineNumber: first, column: 1 });
    const text = model.getValueInRange({ startLineNumber: first, startColumn: 1, endLineNumber: last, endColumn: model.getLineMaxColumn(last) });
    const at = model.getOffsetAt(position) - winStart;
    const lt = text.lastIndexOf('<', at);
    if (lt < 0 || text.lastIndexOf('>', at - 1) > lt) return null;
    const tm = /^<([A-Za-z][\w-]*)/.exec(text.slice(lt));
    if (!tm) return null;
    const gt = text.indexOf('>', at);
    const tagText = text.slice(lt, gt < 0 ? text.length : gt);
    const re = /([A-Za-z_:][\w:.-]*)(\s*=\s*)"([^"]*)"?/g;
    let m;
    while ((m = re.exec(tagText))) {
      const s = lt + m.index, e = s + m[0].length;
      if (at < s || at > e) continue;
      const n = this.find(tm[1], m[1], this.kindOf(model));
      if (!n) return null;
      const inValue = at > s + m[1].length + m[2].length;
      const a = model.getPositionAt(winStart + s), b = model.getPositionAt(winStart + e);
      return { range: new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column), contents: [this.markdown(n, inValue ? m[3] : null)] };
    }
    return null;
  }

  /// Thuộc tính đang đứng (cho hộp Note mở sẵn đúng dòng): {tag, attr} hoặc null.
  attrAtCaret() {
    const ed = this.bcode.editor, model = ed && ed.getModel();
    if (!model) return null;
    const h = this.provideHover(model, ed.getPosition());
    if (!h) return null;
    const t = model.getValueInRange(h.range);
    const m = /^([A-Za-z_:][\w:.-]*)/.exec(t);
    return m ? m[1] : null;
  }

  // ---- hộp xem / sửa ---------------------------------------------------------------------------------------------------

  async show() {
    await this.ready;
    const dlg = window.bcodeDialogs;
    const { overlay, body } = dlg.makeDialog('Note thuộc tính field');
    overlay.firstChild.style.width = '1040px';
    overlay.firstChild.style.maxWidth = '96vw';
    const el = (tag, css, text) => { const e = document.createElement(tag); if (css) e.style.cssText = css; if (text != null) e.textContent = text; return e; };
    const input = (ph, w) => { const i = el('input', w ? `width:${w}` : ''); i.className = 'dlgTextarea'; i.placeholder = ph || ''; return i; };
    const select = (opts, val) => { const s = el('select'); s.className = 'dlgTextarea'; for (const [v, t] of opts) { const o = el('option', '', t); o.value = v; s.appendChild(o); } s.value = val || ''; return s; };
    const grid = (cols) => el('div', `display:grid;grid-template-columns:${cols};gap:6px;align-items:center`);
    const bare = (s) => String(s || '').toLowerCase().replace(/đ/g, 'd').normalize('NFD').replace(/[̀-ͯ]/g, '');

    const info = dlg.makeLabel('');
    info.style.marginTop = '0';
    const filter = input('Lọc: tên thuộc tính / mô tả / giá trị (gõ không dấu được)…');
    const tagFilter = select([['', 'Mọi thẻ'], ['field', '<field>'], ['items', '<items>']], '');
    const scopeFilter = select([['*', 'Mọi loại file'], ...FIELD_NOTE_SCOPES.slice(1)], '*');
    const onlyMine = el('label', 'display:flex;gap:4px;align-items:center;white-space:nowrap');
    const onlyMineBox = el('input'); onlyMineBox.type = 'checkbox';
    onlyMine.append(onlyMineBox, document.createTextNode('Chỉ note của tôi'));
    const top = grid('1fr 110px 150px auto');
    top.append(filter, tagFilter, scopeFilter, onlyMine);

    // Khung sửa 1 note (ẩn tới khi bấm Thêm / Sửa).
    const form = el('div', 'display:none;border:1px solid var(--bc-accent,#e8912d);border-radius:6px;padding:8px;margin-top:6px;flex-direction:column;gap:6px');
    const fTag = select([['field', '<field>'], ['items', '<items>']], 'field');
    const fAttr = input('Tên thuộc tính (vd allowNulls)');
    const fScope = select(FIELD_NOTE_SCOPES, '');
    const fDesc = el('textarea', 'height:54px'); fDesc.className = 'dlgTextarea'; fDesc.placeholder = 'Mô tả tiếng Việt (vd: Bắt buộc nhập giá trị)';
    const fValues = el('textarea', 'height:84px'); fValues.className = 'dlgTextarea'; fValues.placeholder = 'Giá trị — mỗi dòng 1 giá trị: giá trị = ý nghĩa\nfalse = bắt buộc nhập\ntrue = được để trống';
    const fExample = input('Ví dụ (tuỳ chọn): allowNulls="false"');
    const fRow1 = grid('110px 1fr 170px'); fRow1.append(fTag, fAttr, fScope);
    const fOk = dlg.makeButton('Ghi vào danh sách', 'primary');
    const fCancel = dlg.makeButton('Huỷ');
    const fNote = dlg.makeLabel(''); fNote.style.marginTop = '0';
    form.append(fRow1, fDesc, fValues, fExample, dlg.makeButtonRow([fNote, fOk, fCancel]));

    const list = el('div', 'height:420px;overflow:auto;display:flex;flex-direction:column;gap:2px;border-top:1px solid var(--bc-border);padding-top:6px;margin-top:6px');
    const addBtn = dlg.makeButton('Thêm note');
    const saveBtn = dlg.makeButton('Lưu', 'primary');
    const closeBtn = dlg.makeButton('Đóng');
    saveBtn.disabled = true;

    let user = this.user.map((n) => ({ ...n, values: (n.values || []).map((v) => ({ ...v })) }));
    let dirty = false;
    let editing = null;   // note của bạn đang sửa (null = thêm mới)
    const setDirty = () => { dirty = true; saveBtn.disabled = false; updateInfo(); };
    const updateInfo = () => { info.textContent = `${this.builtin.length} định nghĩa Fast · ${user.length} note của bạn${dirty ? ' · CHƯA LƯU' : ''} — ${this.path}`; };
    const scopeName = (s) => (FIELD_NOTE_SCOPES.find((x) => x[0] === (s || '')) || [s, s])[1];

    const openForm = (n, asNew) => {
      editing = asNew ? null : n;
      fTag.value = n ? (n.tag || 'field') : 'field';
      fAttr.value = n ? n.attr : '';
      fScope.value = n ? (n.scope || '') : '';
      fDesc.value = n ? (n.desc || '') : '';
      fValues.value = n ? (n.values || []).map((v) => v.v + (v.d ? ' = ' + v.d : '')).join('\n') : '';
      fExample.value = n ? (n.example || '') : '';
      fNote.textContent = n && n.src === 'fast' ? 'Sửa định nghĩa của Fast = tạo note của bạn cùng tên (thắng bản của Fast, xoá note thì quay về bản Fast).' : '';
      form.style.display = 'flex';
      (n ? fDesc : fAttr).focus();
    };
    const closeForm = () => { form.style.display = 'none'; editing = null; };
    fCancel.onclick = closeForm;
    fOk.onclick = () => {
      const attr = fAttr.value.trim();
      if (!/^[A-Za-z_:][\w:.-]*$/.test(attr)) { fNote.textContent = 'Tên thuộc tính không hợp lệ.'; fAttr.focus(); return; }
      const values = fValues.value.split(/\r?\n/).map((l) => l.trim()).filter(Boolean).map((l) => {
        const i = l.indexOf('=');
        // "a = b": tách ở dấu "=" có khoảng trắng hai bên trước (giá trị SQL có thể chứa "="), không có thì dấu "=" đầu tiên.
        const sp = l.indexOf(' = ');
        const at = sp >= 0 ? sp : i;
        return at > 0 ? { v: l.slice(0, at).trim(), d: l.slice(at + (sp >= 0 ? 3 : 1)).trim() } : { v: l, d: '' };
      });
      const note = { tag: fTag.value, attr, scope: fScope.value, desc: fDesc.value.trim(), values, example: fExample.value.trim(), count: 0, src: 'user' };
      const k = BcodeFieldNotes.key(note.tag, note.attr, note.scope);
      user = user.filter((x) => x !== editing && BcodeFieldNotes.key(x.tag, x.attr, x.scope) !== k);
      user.unshift(note);
      closeForm();
      setDirty();
      render();
    };

    const render = () => {
      list.textContent = '';
      const q = bare(filter.value.trim());
      const mine = new Map(user.map((n) => [BcodeFieldNotes.key(n.tag, n.attr, n.scope), n]));
      const rows = [...user, ...(onlyMineBox.checked ? [] : this.builtin.filter((n) => !mine.has(BcodeFieldNotes.key(n.tag, n.attr, n.scope))))]
        .filter((n) => !tagFilter.value || (n.tag || 'field') === tagFilter.value)
        .filter((n) => scopeFilter.value === '*' || (n.scope || '') === scopeFilter.value)
        .filter((n) => !q || bare([n.attr, n.desc, n.example, ...(n.values || []).map((v) => v.v + ' ' + v.d)].join(' ')).includes(q));
      const head = grid('150px 90px 1fr 1fr 120px');
      head.style.fontWeight = '600';
      ['Thuộc tính', 'Loại file', 'Mô tả', 'Giá trị', ''].forEach((t) => head.appendChild(el('div', '', t)));
      list.appendChild(head);
      for (const n of rows) {
        const r = grid('150px 90px 1fr 1fr 120px');
        r.style.cssText += ';padding:3px 0;border-bottom:1px solid var(--bc-border)';
        const name = el('div', 'font-family:Consolas,monospace');
        name.textContent = (n.tag && n.tag !== 'field' ? `<${n.tag}> ` : '') + n.attr;
        name.title = n.src === 'user' ? 'Note của bạn' : 'Định nghĩa của Fast' + (n.count ? ` — gặp ${n.count} lần trong source Fast` : '');
        if (n.src === 'user') name.style.color = 'var(--bc-accent,#e8912d)';
        const vals = el('div', 'font-size:12px;opacity:.9;white-space:pre-wrap', (n.values || []).map((v) => v.v + (v.d ? ' = ' + v.d : '')).join('\n'));
        const acts = el('div', 'display:flex;gap:4px');
        const edit = dlg.makeButton('Sửa');
        edit.onclick = () => openForm(n, n.src !== 'user');
        acts.appendChild(edit);
        if (n.src === 'user') {
          const del = dlg.makeButton('Xoá');
          del.onclick = () => { user = user.filter((x) => x !== n); setDirty(); render(); };
          acts.appendChild(del);
        }
        r.append(name, el('div', 'font-size:12px', scopeName(n.scope)), el('div', 'white-space:pre-wrap', n.desc || ''), vals, acts);
        list.appendChild(r);
      }
      if (!rows.length) list.appendChild(dlg.makeLabel('Không có note nào khớp.'));
    };

    const close = async () => {
      if (dirty && !(await window.bcodeUi.confirm('Còn thay đổi chưa lưu. Đóng không lưu?', { okText: 'Đóng không lưu' }))) return;
      document.removeEventListener('keydown', onKey, true);
      overlay.remove();
    };
    const onKey = (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); if (form.style.display !== 'none') closeForm(); else close(); } };
    document.addEventListener('keydown', onKey, true);
    overlay.addEventListener('click', (e) => { if (e.target === overlay) { e.stopImmediatePropagation(); } }, true);

    addBtn.onclick = () => openForm(null, true);
    closeBtn.onclick = close;
    saveBtn.onclick = async () => {
      saveBtn.disabled = true;
      try {
        const payload = user.map(({ tag, attr, scope, desc, values, example }) => ({ tag, attr, scope, desc, values, example }));
        this.path = String(await window.bcodeHost.call('BeginSaveFieldNotes', JSON.stringify({ notes: payload })) || this.path);
        this.user = user.map((n) => ({ ...n, src: 'user' }));
        this.rebuild();
        dirty = false;
        updateInfo();
        window.bcodeViewer && window.bcodeViewer.showToast && window.bcodeViewer.showToast('Đã lưu note thuộc tính field', 2000);
      } catch (e) {
        saveBtn.disabled = false;
        window.bcodeUi.alert('Không lưu được note:\n' + e, { kind: 'error' });
      }
    };
    [filter, tagFilter, scopeFilter, onlyMineBox].forEach((x) => x.addEventListener(x === filter ? 'input' : 'change', render));

    body.append(info, top, form, list, dlg.makeButtonRow([addBtn, saveBtn, closeBtn]));
    document.body.appendChild(overlay);
    // Mở từ editor khi con trỏ đứng trên 1 thuộc tính: lọc sẵn theo thuộc tính đó.
    const here = this.attrAtCaret();
    if (here) filter.value = here;
    updateInfo();
    render();
    filter.focus();
  }
}
