// Hint Code (khai báo snippet mẫu) và File > New from Template — vẽ ngay trong trang thay cho
// HintCodeForm / TemplatePickerForm (WinForms) để cùng theme, cùng kiểu với mọi hộp thoại khác ở đây.
// Dữ liệu vẫn do C# giữ (EditorBridge: BeginLoadHintSnippets / BeginSaveHintSnippet / ...), ô code là
// một Monaco nữa ngay trong trang — cùng theme, cùng bộ tô màu FCode với editor chính.

const HINT_CATEGORIES = ['JS', 'SQL', 'XML', 'CSS'];
const HINT_LANG = { JS: 'javascript', SQL: 'sql', XML: 'fcode-xml', CSS: 'css' };

function tplEl(tag, className, text) {
  const e = document.createElement(tag);
  if (className) e.className = className;
  if (text != null) e.textContent = text;
  return e;
}

function tplButton(text, kind, onClick) {
  const b = tplEl('button', 'dlgButton' + (kind === 'primary' ? ' primary' : ''), text);
  if (onClick) b.onclick = onClick;
  return b;
}

/// Khung chung: overlay + hộp lớn có tiêu đề và nút ✕; Esc đóng. Trả { overlay, box, body, close }.
function tplDialog(title, className, onClose) {
  const overlay = tplEl('div', 'dlgOverlay');
  const box = tplEl('div', 'dlgBox tplBox ' + (className || ''));
  const header = tplEl('div', 'dlgHeader tplHeader');
  header.appendChild(tplEl('span', null, title));
  const x = tplEl('span', 'tplClose', '✕');
  header.appendChild(x);
  const body = tplEl('div', 'tplBody');
  box.append(header, body);
  overlay.appendChild(box);
  const onKey = (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(); } };
  const close = () => {
    document.removeEventListener('keydown', onKey, true);
    overlay.remove();
    if (onClose) onClose();
  };
  x.onclick = close;
  document.addEventListener('keydown', onKey, true);
  document.body.appendChild(overlay);
  return { overlay, box, body, close };
}

function monacoTheme() {
  return window.bcodeTheme ? window.bcodeTheme.monacoThemeName : 'vs-dark';
}

// ---- Hint Code ------------------------------------------------------------------------------

class BcodeHintCodeDialog {
  // Panel gắn bên phải cửa sổ (như FCodeViewer) thay cho hộp thoại nổi: mở lại (Hint / phím tắt) khi đang mở = đóng.
  // Có truyền code nháp (từ menu chuột phải "Lưu thành hint") thì chỉ đổ code vào ô code.
  async show(initialCode) {
    if (this.dlg) {
      if (initialCode) { this.showDetail(null); this.editor.setValue(initialCode); this.prefix.focus(); }
      else this.close();
      return;
    }
    if (this.opening) return;
    this.opening = true;
    let list;
    try { list = JSON.parse(await window.bcodeHost.call('BeginLoadHintSnippets')); }
    catch (e) { this.opening = false; alert('Không đọc được thư viện Hint Code:\n' + e); return; }
    this.opening = false;
    this.list = list;
    this.editing = null; // snippet đang sửa; null = đang tạo mới

    const panel = tplEl('div');
    panel.id = 'hintPanel';
    const resizer = tplEl('div', 'hintResizer');
    const header = tplEl('div', 'hintHeader');
    header.appendChild(tplEl('span', 'hintTitle', 'Hint Code'));
    const x = tplEl('span', 'tplClose', '✕');
    x.title = 'Đóng';
    x.onclick = () => this.close();
    header.appendChild(x);
    this.pathBar = tplEl('div', 'hintPathBar');
    const body = tplEl('div', 'tplBody hintBody');
    panel.append(resizer, header, this.pathBar, body);
    this.dlg = { panel, body, close: () => this.close() };
    this.setupResize(resizer, panel);
    let w = 0;
    try { w = parseInt(localStorage.getItem('hint.width') || '0', 10); } catch { /* storage blocked */ }
    panel.style.width = (w >= 420 ? Math.min(w, window.innerWidth - 300) : Math.min(780, Math.round(window.innerWidth * 0.5))) + 'px';
    const host = document.getElementById('main') || document.body;
    host.appendChild(panel);
    this.loadStorageInfo();

    // Trái: tìm + lọc category + danh sách thẻ
    const left = tplEl('div', 'hintLeft');
    const searchRow = tplEl('div', 'hintSearchRow');
    this.search = tplEl('input', 'setInput');
    this.search.placeholder = 'Tìm theo prefix, tag, mô tả, code...';
    this.search.oninput = () => this.renderList();
    searchRow.append(this.search, tplButton('Refresh', null, () => this.reload()));
    const filterRow = tplEl('div', 'hintFilters');
    this.catChecks = {};
    for (const cat of HINT_CATEGORIES) {
      const lab = tplEl('label', 'setCheck');
      const c = document.createElement('input');
      c.type = 'checkbox';
      c.checked = true;
      c.onchange = () => this.renderList();
      lab.append(c, document.createTextNode(' ' + cat));
      this.catChecks[cat] = c;
      filterRow.appendChild(lab);
    }
    this.listEl = tplEl('div', 'hintList');
    left.append(searchRow, filterRow, this.listEl);

    // Phải: nút + thông tin + form + ô code
    const right = tplEl('div', 'hintRight');
    const actions = tplEl('div', 'hintActions');
    this.newBtn = tplButton('New', 'primary', () => this.showDetail(null));
    this.saveBtn = tplButton('Save', null, () => this.save());
    this.deleteBtn = tplButton('Delete', null, () => this.remove());
    this.insertBtn = tplButton('Insert', 'primary', () => this.insert());
    this.exportBtn = tplButton('Export...', null, () => this.exportAll());
    this.importBtn = tplButton('Import...', null, () => this.importAll());
    this.exportBtn.title = 'Xuất hint riêng ra file (.code-snippets của VSCode hoặc .json đầy đủ trường)';
    this.importBtn.title = 'Nhập hint từ file .code-snippets / .json vào thư viện riêng';
    actions.append(this.newBtn, this.saveBtn, this.deleteBtn, this.insertBtn, this.exportBtn, this.importBtn);
    this.meta = tplEl('div', 'hintMeta');

    const form = tplEl('div', 'hintForm');
    const field = (label, control) => {
      form.appendChild(tplEl('label', 'setLabel', label));
      control.classList.add('setInput');
      form.appendChild(control);
      return control;
    };
    this.category = document.createElement('select');
    for (const c of HINT_CATEGORIES) this.category.add(new Option(c, c));
    this.category.onchange = () => this.setLanguage(this.category.value);
    field('Category', this.category);
    this.prefix = field('Prefix', tplEl('input'));
    this.prefix.placeholder = 'vd: fld — gõ từ này trong editor để hiện gợi ý';
    this.type = field('Type', tplEl('input'));
    this.type.setAttribute('list', 'hintTypeList');
    const types = tplEl('datalist');
    types.id = 'hintTypeList';
    // Gợi ý = các Type đang có trong thư viện (vẫn gõ tự do được, như ô Type cũ).
    for (const t of [...new Set(['Declare', ...list.map((s) => s.type).filter(Boolean)])]) types.appendChild(new Option(t));
    form.appendChild(types);
    this.tags = field('Tag, Keywords', tplEl('input'));
    this.description = field('Description', tplEl('input'));
    this.pathScope = field('Chỉ ở file', tplEl('input'));
    this.pathScope.placeholder = 'vd: *\\Controllers\\*;*\\Grid\\*  (bỏ trống = mọi file)';
    form.appendChild(tplEl('span'));
    const sis = tplEl('label', 'setCheck');
    this.showInIntelliSense = document.createElement('input');
    this.showInIntelliSense.type = 'checkbox';
    sis.append(this.showInIntelliSense, document.createTextNode(' Hiện trong danh sách gợi ý khi gõ'));
    form.appendChild(sis);

    const editorHost = tplEl('div', 'hintEditor');
    right.append(actions, this.meta, form, editorHost);
    body.append(left, right);

    this.editor = monaco.editor.create(editorHost, {
      value: '', language: 'javascript', theme: monacoTheme(), automaticLayout: true,
      fontFamily: window.bcodeTheme ? window.bcodeTheme.fontFamily : "'Roboto', Consolas, monospace", fontSize: 14, minimap: { enabled: false },
      scrollBeyondLastLine: false,
    });

    this.renderList();
    this.showDetail(null);
    if (initialCode) {
      this.editor.setValue(initialCode);
      this.prefix.focus();
    } else {
      this.search.focus();
    }
  }

  async reload() {
    try { this.list = JSON.parse(await window.bcodeHost.call('BeginLoadHintSnippets')); }
    catch (e) { alert('Không đọc được thư viện Hint Code:\n' + e); return; }
    this.renderList();
  }

  filtered() {
    const q = this.search.value.trim().toLowerCase();
    const has = (s) => (s || '').toLowerCase().includes(q);
    return this.list
      .filter((s) => this.catChecks[s.category] ? this.catChecks[s.category].checked : true)
      .filter((s) => !q || has(s.description) || has(s.tags) || has(s.prefix) || has(s.code))
      .sort((a, b) => (a.isShared - b.isShared) || (a.modifiedSort < b.modifiedSort ? 1 : -1)); // riêng trước, mới trước
  }

  renderList() {
    this.listEl.textContent = '';
    for (const s of this.filtered()) {
      const card = tplEl('div', 'hintCard' + (this.editing && this.editing.id === s.id ? ' selected' : ''));
      const top = tplEl('div', 'hintCardTop');
      top.appendChild(tplEl('span', 'hintBadge hint' + s.category, s.category));
      let line = s.prefix ? `${s.prefix}  ·  ${s.modifiedDate.slice(0, 10)}` : s.modifiedDate.slice(0, 10);
      if (s.isShared) line += `  ·  chung (${s.sourceLabel})`;
      top.appendChild(tplEl('span', 'hintCardMeta', line));
      card.append(top, tplEl('div', 'hintCardTitle', s.tags || s.description || '(không mô tả)'));
      card.onclick = () => this.showDetail(s);
      card.ondblclick = () => { this.showDetail(s); this.insert(); };
      this.listEl.appendChild(card);
    }
  }

  setLanguage(category) {
    const model = this.editor && this.editor.getModel();
    if (model) monaco.editor.setModelLanguage(model, HINT_LANG[category] || 'plaintext');
  }

  showDetail(s) {
    this.editing = s;
    this.category.value = s ? s.category : 'JS';
    this.prefix.value = s ? s.prefix : '';
    this.type.value = s ? s.type : 'Declare';
    this.tags.value = s ? s.tags : '';
    this.description.value = s ? s.description : '';
    this.pathScope.value = s ? s.pathScope : '';
    this.showInIntelliSense.checked = s ? s.showInIntelliSense : true;
    this.setLanguage(this.category.value);
    this.editor.setValue(s ? s.code : '');

    const shared = !!(s && s.isShared);
    this.meta.textContent = !s ? 'Snippet mới'
      : shared ? `Thư viện dùng chung — ${s.sourceLabel} (chỉ đọc, Save sẽ tạo bản riêng)`
      : `Created: ${s.createdDate} by ${s.createdBy}   ·   Modified: ${s.modifiedDate} by ${s.modifiedBy}`;
    this.saveBtn.textContent = shared ? 'Lưu bản riêng' : 'Save';
    this.deleteBtn.disabled = !s || shared;
    this.insertBtn.disabled = !s;
    this.renderList();
  }

  async save() {
    const code = this.editor.getValue();
    if (!code.trim()) { alert('Chưa nhập code.'); return; }
    const data = {
      id: this.editing && !this.editing.isShared ? this.editing.id : '',
      category: this.category.value, type: this.type.value, tags: this.tags.value,
      description: this.description.value, prefix: this.prefix.value, pathScope: this.pathScope.value,
      showInIntelliSense: this.showInIntelliSense.checked, code,
    };
    let res;
    try { res = JSON.parse(await window.bcodeHost.call('BeginSaveHintSnippet', JSON.stringify(data))); }
    catch (e) { alert('Không lưu được:\n' + e); return; }
    this.list = res.list;
    this.showDetail(this.list.find((s) => s.id === res.id) || null);
    if (window.bcodeCompletion) window.bcodeCompletion.reloadSnippets();
    if (window.bcodeViewer && window.bcodeViewer.showToast) window.bcodeViewer.showToast('Đã lưu Hint Code', 2500);
  }

  async remove() {
    const s = this.editing;
    if (!s) return;
    if (s.isShared) { alert('Đây là snippet của thư viện dùng chung — xoá nó phải xoá file trong thư mục chung.'); return; }
    if (!(await window.bcodeUi.confirm('Xóa hint này?', { okText: 'Xoá', danger: true }))) return;
    try { this.list = JSON.parse(await window.bcodeHost.call('BeginDeleteHintSnippet', s.id)); }
    catch (e) { alert('Không xoá được:\n' + e); return; }
    this.showDetail(null);
    if (window.bcodeCompletion) window.bcodeCompletion.reloadSnippets();
  }

  insert() {
    if (!this.editing || !window.bcodeViewer) return;
    window.bcodeViewer.insertTextAtCursor(this.editor.getValue());
  }

  async exportAll() {
    try {
      const msg = await window.bcodeHost.call('BeginExportHintSnippets');
      if (msg) alert(msg);
    } catch (e) {
      alert('Không ghi được file:\n' + e);
    }
  }

  async importAll() {
    let res;
    try { res = JSON.parse(await window.bcodeHost.call('BeginImportHintSnippets')); }
    catch (e) { alert('Không nhập được file:\n' + e); return; }
    if (!res.message) return; // bấm Cancel ở hộp chọn file
    if (res.list) {
      this.list = res.list;
      this.renderList();
      if (window.bcodeCompletion) window.bcodeCompletion.reloadSnippets();
    }
    alert(res.message);
  }

  /// Hiện nơi thư viện riêng đang được lưu (mỗi máy một file) + thư mục dùng chung nếu có cấu hình.
  async loadStorageInfo() {
    let info;
    try { info = JSON.parse(await window.bcodeHost.call('BeginHintStorageInfo')); }
    catch { return; }
    if (!this.pathBar) return;
    this.pathBar.textContent = '';
    const line = (label, path, openPath) => {
      const row = tplEl('div', 'hintPathRow');
      row.appendChild(tplEl('span', 'hintPathLabel', label));
      const p = tplEl('span', 'hintPathText', path);
      p.title = path;
      row.appendChild(p);
      const open = tplEl('span', 'hintPathOpen', 'Mở thư mục');
      open.onclick = () => window.chrome.webview.hostObjects.host.OpenFolder(openPath);
      row.appendChild(open);
      this.pathBar.appendChild(row);
    };
    line('Riêng:', info.personalPath, info.personalPath);
    if (info.sharedFolder) line('Chung:', info.sharedFolder, info.sharedFolder);
  }

  close() {
    if (!this.dlg) return;
    if (this.editor) { const m = this.editor.getModel(); this.editor.dispose(); if (m) m.dispose(); }
    this.editor = null;
    this.dlg.panel.remove();
    this.dlg = null;
    if (window.bcodeViewer && window.bcodeViewer.editor) window.bcodeViewer.editor.focus();
  }

  setupResize(handle, panel) {
    handle.addEventListener('mousedown', (e) => {
      e.preventDefault();
      const startX = e.clientX, startW = panel.getBoundingClientRect().width;
      const onMove = (ev) => { panel.style.width = Math.max(420, Math.min(window.innerWidth - 300, startW + (startX - ev.clientX))) + 'px'; };
      const onUp = () => {
        document.removeEventListener('mousemove', onMove); document.removeEventListener('mouseup', onUp);
        try { localStorage.setItem('hint.width', String(Math.round(panel.getBoundingClientRect().width))); } catch { /* storage blocked */ }
      };
      document.addEventListener('mousemove', onMove); document.addEventListener('mouseup', onUp);
    });
  }
}

// ---- New from Template ----------------------------------------------------------------------

class BcodeTemplatePickerDialog {
  async show() {
    if (this.dlg) return;
    let info;
    try { info = JSON.parse(await window.bcodeHost.call('BeginListFileTemplates')); }
    catch (e) { alert('Không đọc được template:\n' + e); return; }

    this.dlg = tplDialog('New from Template', 'tplPickBox', () => {
      if (this.preview) { const m = this.preview.getModel(); this.preview.dispose(); if (m) m.dispose(); }
      this.preview = null;
      this.dlg = null;
    });
    const body = this.dlg.body;
    body.classList.add('tplPickBody');
    const templates = info.templates || [];

    const openFolderBtn = tplButton('Mở thư mục template', null,
      () => window.chrome.webview.hostObjects.host.OpenFolder(info.personalFolder));

    if (templates.length === 0) {
      const empty = tplEl('div', 'tplEmpty');
      empty.textContent = 'Chưa có template file nào.\n\nĐặt file mẫu vào:\n  ' + info.personalFolder + '\n' +
        (info.sharedFolder ? 'hoặc thư mục dùng chung:\n  ' + info.sharedFolder : 'hoặc cấu hình thư mục dùng chung ở File > Settings.');
      body.appendChild(empty);
      this.dlg.box.appendChild(this.buttonRow([openFolderBtn, tplButton('Đóng', null, () => this.dlg.close())]));
      return;
    }

    const listEl = tplEl('div', 'tplList');
    const previewHost = tplEl('div', 'tplPreview');
    body.append(listEl, previewHost);
    this.preview = monaco.editor.create(previewHost, {
      value: '', language: 'plaintext', theme: monacoTheme(), readOnly: true, automaticLayout: true,
      fontFamily: window.bcodeTheme ? window.bcodeTheme.fontFamily : "'Roboto', Consolas, monospace", fontSize: 13, minimap: { enabled: false },
      scrollBeyondLastLine: false,
    });

    let selected = null;
    let readToken = 0;
    const select = async (t, row) => {
      selected = t;
      listEl.querySelectorAll('.tplItem').forEach((r) => r.classList.toggle('selected', r === row));
      const token = ++readToken;
      let text = '';
      try { text = await window.bcodeHost.call('BeginReadFileTemplate', t.path); } catch (e) { text = String(e); }
      if (token !== readToken || !this.preview) return;
      // Placeholder (${Name}...) để nguyên — thấy chúng ở đây là cách người dùng biết có placeholder.
      const lang = typeof detectLanguage === 'function' ? detectLanguage(t.path, text) : 'plaintext';
      monaco.editor.setModelLanguage(this.preview.getModel(), lang);
      this.preview.setValue(text);
    };

    const create = async () => {
      if (!selected) return;
      let path = '';
      try {
        path = await window.bcodeHost.call('BeginCreateFromTemplate', selected.path,
          (window.bcodeViewer && window.bcodeViewer.activePath) || '');
      } catch (e) { alert('Không tạo được file:\n' + e); return; }
      if (!path) return; // huỷ hộp chọn chỗ lưu
      this.dlg.close();
      if (window.bcodeViewer) window.bcodeViewer.openFile(path);
    };

    templates.forEach((t, i) => {
      const row = tplEl('div', 'tplItem');
      row.appendChild(tplEl('span', null, t.name));
      if (t.isShared) row.appendChild(tplEl('span', 'tplShared', 'dùng chung'));
      row.onclick = () => select(t, row);
      row.ondblclick = () => { select(t, row); create(); };
      listEl.appendChild(row);
      if (i === 0) select(t, row);
    });

    this.dlg.box.appendChild(this.buttonRow([
      openFolderBtn,
      tplEl('span', 'tplSpacer'),
      tplButton('Tạo file', 'primary', create),
      tplButton('Cancel', null, () => this.dlg.close()),
    ]));
  }

  buttonRow(buttons) {
    const row = tplEl('div', 'dlgButtonRow setButtons');
    buttons.forEach((b) => row.appendChild(b));
    return row;
  }
}

window.bcodeHintCode = new BcodeHintCodeDialog();
window.bcodeTemplatePicker = new BcodeTemplatePickerDialog();
