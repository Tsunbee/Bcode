// Two features that turn out to be one screen: comparing the open file against another
// version of itself.
//
//   * "Xem khác biệt" on the external-change banner — the file on disk vs. what's open
//     here. Before this the banner offered Reload or dismiss, both blind: one throws away
//     your edits, the other eventually throws away theirs, and nothing showed what was
//     actually different.
//   * "Lịch sử file" — the versions local history kept (see LocalHistoryStore.cs), listed
//     down the left with the same diff on the right.
//
// Both use one overlay and one Monaco diff editor, disposed on close. That matters: a diff
// editor holds two models, and models are not garbage collected just because the DOM node
// containing the editor was removed — leaking a pair of them per comparison would grow
// unbounded across a working day.

class BcodeHistory {
  constructor() {
    this.overlay = null;
    this.diffEditor = null;
    // Diff editor GIỮ LẠI giữa các lần mở (chỉ huỷ model): huỷ nó làm Monaco còn tham chiếu tới InstantiationService đã đóng,
    // sau đó bảng gợi ý (suggest widget) của editor chính văng "InstantiationService has been disposed" khi gõ.
    this.keptEditor = null;
    this.keptHost = null;
    this.models = [];
    this.onKeyDown = null;
  }

  // ---- The external-change comparison ------------------------------------------------

  /// Called from the banner's "Xem khác biệt" button. Left is the disk version (theirs),
  /// right is what's in the editor (yours) — the same order VSCode uses for a merge, and
  /// the one that makes "their change" read as an insertion rather than a deletion.
  async showExternalDiff(bcode, diskWriteTimeUtc) {
    if (!bcode.activePath) return;

    let disk;
    try {
      disk = await window.bcodeHost.call('BeginReadFile', bcode.activePath);
    } catch (e) {
      alert('Không đọc được bản trên đĩa để so sánh:\n' + e);
      return;
    }

    this.openDiff({
      title: 'So sánh với bản trên đĩa',
      subtitle: bcode.activePath,
      originalLabel: 'Trên đĩa — máy khác vừa ghi',
      original: disk,
      modifiedLabel: 'Đang mở ở đây' + (bcode.dirty ? ' (có thay đổi chưa lưu)' : ''),
      modified: bcode.currentModel.getValue(),
      path: bcode.activePath,
      actions: [
        {
          label: 'Tải lại bản trên đĩa',
          title: 'Bỏ thay đổi chưa lưu ở đây và lấy nội dung bên trái',
          run: () => { this.close(); bcode.reloadFromDisk(diskWriteTimeUtc); },
        },
        {
          label: 'Giữ bản của tôi',
          primary: true,
          title: 'Đóng cảnh báo — bản trên đĩa vẫn được lưu vào lịch sử khi bạn Save đè',
          run: async () => {
            this.close();
            bcode.dismissedWriteTimeUtc = diskWriteTimeUtc;
            bcode.hideExternalChangeBanner();
          },
        },
      ],
    });
  }

  // ---- Lịch sử: chung (trong thư mục History của dự án) và cục bộ ----------------------------

  /// Một hộp thoại, ba góc nhìn: "File này" (lịch sử chung của file đang mở — ai/máy/IP/giờ), "Cả dự án" (mọi file đã sửa
  /// qua BcodeViewer, lọc theo ngày / chữ) và "Máy này" (bản sao lưu cục bộ cũ, không ghi người sửa).
  /// startMode: 'file' | 'project' | 'local'.
  async showHistory(bcode, startMode) {
    if (!bcode.activePath) {
      alert('Chưa mở file nào.');
      return;
    }
    const path = bcode.activePath;
    const st = { mode: startMode || 'file', purgeDays: 90, days: 7, filter: '', token: 0, sel: null, pair: null, localText: null };
    const who = (e) => (e.user ? e.user + (e.machine ? ' @ ' + e.machine : '') : '(không rõ người sửa)');
    const base = (rel) => (rel || '').split('\\').pop();
    const actionText = (e) => (e.action === 'baseline' ? 'bản gốc trước khi có lịch sử' : e.action === 'external' ? 'SỬA NGOÀI VIEWER' : 'lưu');
    const isExt = (e) => e.action === 'external';
    const fmtId = (id) => (id.length >= 15
      ? id.slice(6, 8) + '/' + id.slice(4, 6) + '/' + id.slice(0, 4) + ' ' + id.slice(9, 11) + ':' + id.slice(11, 13) + ':' + id.slice(13, 15)
      : id);

    // Đầu danh sách: chọn góc nhìn + bộ lọc (chỉ hiện ở "Cả dự án").
    const head = document.createElement('div');
    head.className = 'histHead';
    const tabs = document.createElement('div');
    tabs.className = 'histTabs';
    const tabEls = {};
    for (const [key, label] of [['file', 'File này'], ['project', 'Cả dự án'], ['local', 'Máy này']]) {
      const t = document.createElement('button');
      t.className = 'histTab';
      t.textContent = label;
      t.onclick = () => { st.mode = key; load(); };
      tabs.appendChild(t);
      tabEls[key] = t;
    }
    const filterRow = document.createElement('div');
    filterRow.className = 'histFilter';
    const days = document.createElement('select');
    for (const [v, l] of [[1, '1 ngày'], [7, '7 ngày'], [30, '30 ngày'], [0, 'Tất cả']]) {
      const o = document.createElement('option');
      o.value = v; o.textContent = l;
      if (v === st.days) o.selected = true;
      days.appendChild(o);
    }
    days.onchange = () => { st.days = +days.value; load(); };
    const filter = document.createElement('input');
    filter.type = 'text';
    filter.placeholder = 'Lọc theo file / người / máy...';
    filter.spellcheck = false;
    filter.oninput = () => { st.filter = filter.value.trim().toLowerCase(); applyFilter(); };
    filterRow.appendChild(days);
    filterRow.appendChild(filter);
    const note = document.createElement('div');
    note.className = 'histNote';
    head.appendChild(tabs);
    head.appendChild(filterRow);
    head.appendChild(note);

    this.openDiff({
      title: 'Lịch sử file',
      subtitle: path,
      originalLabel: 'Bản đã chọn',
      original: '',
      modifiedLabel: 'Hiện tại' + (bcode.dirty ? ' (chưa lưu)' : ''),
      modified: bcode.currentModel.getValue(),
      path,
      entries: [],
      listHeader: head,
      actions: [
        {
          label: 'Mở thư mục lịch sử',
          run: () => {
            try {
              if (st.mode === 'local') window.bcodeHost.raw.OpenHistoryFolder(path);
              else window.bcodeHost.raw.OpenSharedHistoryFolder(path, st.sel ? st.sel.rel : '');
            } catch {}
          },
        },
        {
          label: 'Dọn bản cũ…',
          title: 'Xoá các bản lịch sử chung cũ hơn N ngày (luôn giữ bản mới nhất của mỗi file)',
          run: async () => {
            if (st.mode !== 'file' && st.mode !== 'project') { alert('Chỉ dọn được ở tab "File này" hoặc "Cả dự án".'); return; }
            const scope = st.mode === 'project' ? 'project' : 'file';
            const answer = await window.bcodeUi.prompt('Xoá các bản lịch sử chung cũ hơn bao nhiêu ngày?', String(st.purgeDays));
            if (answer === null || answer === undefined) return;
            const days = parseInt(answer, 10);
            if (!(days >= 1)) { alert('Nhập số ngày từ 1 trở lên.'); return; }
            st.purgeDays = days;
            let dry;
            try { dry = JSON.parse(await window.bcodeHost.call('BeginPurgeSharedHistory', path, scope, days, true)); }
            catch (e) { alert('Không đếm được: ' + e); return; }
            if (dry.error) { alert('Không dọn được: ' + dry.error); return; }
            if (!dry.count) { alert('Không có bản nào cũ hơn ' + days + ' ngày để dọn.'); return; }
            const mb = (dry.bytes / 1048576).toFixed(2);
            const where = scope === 'project' ? 'CẢ DỰ ÁN' : 'file này';
            if (!(await window.bcodeUi.confirm('Xoá ' + dry.count + ' bản (khoảng ' + mb + ' MB) cũ hơn ' + days + ' ngày của ' + where + '?\n\nBản mới nhất của mỗi file luôn được giữ. Không hoàn tác được.'))) return;
            let done;
            try { done = JSON.parse(await window.bcodeHost.call('BeginPurgeSharedHistory', path, scope, days, false)); }
            catch (e) { alert('Không dọn được: ' + e); return; }
            if (done.error) { alert('Dọn dở dang: ' + done.error); }
            load();
          },
        },
        {
          label: 'Mở file này',
          title: 'Mở file của dòng đang chọn trong BcodeViewer',
          run: async () => { if (st.sel && st.sel.path) { const p = st.sel.path; this.close(); await bcode.openFile(p); } },
        },
        {
          label: 'Khôi phục bản này',
          primary: true,
          title: 'Thay nội dung đang mở bằng bản bên phải (chưa ghi xuống đĩa — vẫn phải Save)',
          run: async () => {
            const text = st.mode === 'local' ? st.localText : (st.pair ? st.pair.after : null);
            if (text === undefined || text === null) return;
            if (st.mode !== 'local' && st.sel && st.sel.path && st.sel.path.toLowerCase() !== path.toLowerCase()) {
              alert('Bản này thuộc file khác. Bấm "Mở file này" rồi khôi phục trong file đó.');
              return;
            }
            const label = st.sel ? st.sel.savedAt : '';
            if (!(await window.bcodeUi.confirm('Thay nội dung đang mở bằng bản lúc ' + label + '?\n\n' +
                         'Chưa ghi xuống đĩa — xem lại rồi bấm Save nếu đúng.'))) return;
            this.close();
            // Through the model, not WriteFile: this becomes a normal undoable edit, and
            // nothing touches the file until the user decides to Save.
            bcode.currentModel.setValue(text);
            bcode.editor.focus();
          },
        },
      ],
    });

    const rowText = (e) => {
      if (st.mode === 'local') return { a: e.savedAt, b: formatSize(e.length) };
      const first = (isExt(e) ? '⚠ ' : '') + (st.mode === 'project' ? base(e.rel) : e.savedAt);
      const second = (st.mode === 'project' ? e.savedAt + ' · ' : '') + (isExt(e) ? 'Sửa ngoài Viewer — không rõ người' : who(e));
      return { a: first, b: second, c: (e.ip ? e.ip + ' · ' : '') + (isExt(e) && e.owner ? 'chủ file lúc đó: ' + e.owner + ' · ' : '') + actionText(e) + ' · ' + formatSize(e.length) };
    };
    const applyFilter = () => {
      const rows = this.overlay ? this.overlay.querySelectorAll('.histRow') : [];
      for (const r of rows) r.style.display = !st.filter || (r.dataset.hay || '').includes(st.filter) ? '' : 'none';
    };
    const select = async (entry) => {
      st.sel = entry; st.pair = null; st.localText = null;
      if (st.mode === 'local') {
        let text = '';
        try { text = await window.bcodeHost.call('BeginGetHistorySnapshot', path, entry.id); } catch { /* pruned */ }
        if (st.sel !== entry) return;
        st.localText = text;
        this.setOriginal(text, 'Bản lúc ' + entry.savedAt);
        this.setModified(bcode.currentModel.getValue(), 'Hiện tại' + (bcode.dirty ? ' (chưa lưu)' : ''));
        return;
      }
      let pair = null;
      try {
        const raw = await window.bcodeHost.call('BeginGetSharedPair', path, entry.rel, entry.id);
        pair = raw ? JSON.parse(raw) : null;
      } catch { /* pruned */ }
      if (st.sel !== entry) return; // người dùng đã bấm dòng khác
      st.pair = pair;
      this.setOriginal(pair ? pair.before : '', pair && pair.beforeAt ? 'Bản trước đó (' + fmtId(pair.beforeAt) + ')' : 'Chưa có bản trước đó');
      this.setModified(pair ? pair.after : '',
        entry.action === 'baseline' ? 'Bản gốc trên đĩa trước khi có lịch sử'
          : isExt(entry) ? 'Bản do người lạ / công cụ khác sửa (không qua Viewer), file ghi lúc ' + entry.savedAt
          : 'Do ' + who(entry) + ' lưu lúc ' + entry.savedAt);
    };

    const load = async () => {
      const my = ++st.token;
      st.sel = null; st.pair = null; st.localText = null;
      for (const [k, el] of Object.entries(tabEls)) el.classList.toggle('on', k === st.mode);
      filterRow.style.display = st.mode === 'project' ? '' : 'none';
      note.textContent = 'Đang đọc...';
      this.renderEntries([], () => {}, rowText);
      let entries = [], err = '', sharedRoot = null;
      try {
        if (st.mode === 'local') {
          entries = JSON.parse(await window.bcodeHost.call('BeginGetFileHistory', path));
        } else {
          const r = JSON.parse(await window.bcodeHost.call('BeginGetSharedHistory', path, st.mode, st.days));
          entries = r.entries || [];
          err = r.error || '';
          sharedRoot = r.root;
        }
      } catch (e) { err = String(e); }
      if (my !== st.token || !this.overlay) return;
      if (err) note.textContent = 'Lỗi đọc lịch sử: ' + err;
      else if (st.mode !== 'local' && sharedRoot === null) note.textContent = 'File này không nằm trong site FastBusiness (không thấy thư mục App_Data ở các thư mục cha) nên không có lịch sử chung.';
      else if (!entries.length) {
        note.textContent = st.mode === 'local'
          ? 'File này chưa có bản sao lưu cục bộ.'
          : (st.mode === 'file'
            ? 'File này chưa có lịch sử chung. Mỗi lần Save bằng BcodeViewer sẽ ghi vào ' + sharedRoot + '\\History\\_files\\...'
            : 'Chưa có bản nào trong ' + (st.days ? st.days + ' ngày gần đây' : 'dự án này') + '.');
      } else {
        const ext = entries.filter(isExt).length;
        note.textContent = entries.length + ' bản' + (ext ? ' — ⚠ ' + ext + ' bản SỬA NGOÀI VIEWER (không rõ người)' : '') + ' — bấm một dòng để xem khác biệt.';
      }
      for (const e of entries) e.hay = ((e.rel || '') + ' ' + (e.user || '') + ' ' + (e.machine || '') + ' ' + (e.ip || '')).toLowerCase();
      this.renderEntries(entries, select, rowText);
      applyFilter();
      const list = this.overlay.querySelector('.histList');
      const firstRow = list && list.querySelector('.histRow');
      if (firstRow) firstRow.click();
      else { this.setOriginal('', 'Bản đã chọn'); this.setModified(bcode.currentModel.getValue(), 'Hiện tại' + (bcode.dirty ? ' (chưa lưu)' : '')); }
    };
    load();
  }

  // ---- The shared overlay ------------------------------------------------------------

  openDiff(opts) {
    this.close(); // never stack two overlays

    this.selectedEntry = null;
    this.selectedText = null;

    const overlay = document.createElement('div');
    overlay.className = 'diffOverlay';

    const box = document.createElement('div');
    box.className = 'diffBox';

    const header = document.createElement('div');
    header.className = 'diffHeader';
    const titleWrap = document.createElement('div');
    const title = document.createElement('div');
    title.className = 'diffTitle';
    title.textContent = opts.title;
    const subtitle = document.createElement('div');
    subtitle.className = 'diffSubtitle';
    subtitle.textContent = opts.subtitle || '';
    subtitle.title = opts.subtitle || '';
    titleWrap.appendChild(title);
    titleWrap.appendChild(subtitle);
    const closeBtn = document.createElement('span');
    closeBtn.className = 'diffClose';
    closeBtn.textContent = '✕';
    closeBtn.title = 'Đóng (Esc)';
    closeBtn.onclick = () => this.close();
    header.appendChild(titleWrap);
    header.appendChild(closeBtn);

    const body = document.createElement('div');
    body.className = 'diffBody';

    if (opts.entries) {
      const wrap = document.createElement('div');
      wrap.className = 'histWrap';
      if (opts.listHeader) wrap.appendChild(opts.listHeader);
      const list = document.createElement('div');
      list.className = 'histList';
      wrap.appendChild(list);
      body.appendChild(wrap);
      this.listEl = list;
      this.renderEntries(opts.entries, opts.onSelectEntry);
    }

    const labels = document.createElement('div');
    labels.className = 'diffLabels';
    const leftLabel = document.createElement('span');
    leftLabel.className = 'diffLabel';
    leftLabel.textContent = opts.originalLabel;
    const rightLabel = document.createElement('span');
    rightLabel.className = 'diffLabel';
    rightLabel.textContent = opts.modifiedLabel;
    labels.appendChild(leftLabel);
    labels.appendChild(rightLabel);
    this.leftLabelEl = leftLabel;
    this.rightLabelEl = rightLabel;

    const editorHost = this.keptHost || (this.keptHost = document.createElement('div'));
    editorHost.className = 'diffEditorHost';

    const right = document.createElement('div');
    right.className = 'diffRight';
    right.appendChild(labels);
    right.appendChild(editorHost);
    body.appendChild(right);

    const footer = document.createElement('div');
    footer.className = 'diffFooter';
    const hint = document.createElement('span');
    hint.className = 'diffHint';
    hint.textContent = 'Alt+F5 / Shift+Alt+F5: nhảy tới khác biệt tiếp theo / trước đó';
    footer.appendChild(hint);
    for (const action of opts.actions || []) {
      const btn = document.createElement('button');
      btn.className = 'dlgButton' + (action.primary ? ' primary' : '');
      btn.textContent = action.label;
      if (action.title) btn.title = action.title;
      btn.onclick = action.run;
      footer.appendChild(btn);
    }

    box.appendChild(header);
    box.appendChild(body);
    box.appendChild(footer);
    overlay.appendChild(box);
    document.body.appendChild(overlay);
    this.overlay = overlay;

    const language = typeof detectLanguage === 'function' && opts.path
      ? detectLanguage(opts.path)
      : 'plaintext';

    this.diffEditor = this.keptEditor || (this.keptEditor = monaco.editor.createDiffEditor(editorHost, {
      // Theme comes from whatever bcodeTheme last set — createDiffEditor picks up the
      // global theme, so this follows the app's 15 themes with no extra wiring.
      automaticLayout: true,
      readOnly: true,
      originalEditable: false,
      renderSideBySide: true,
      fontFamily: window.bcodeTheme ? window.bcodeTheme.fontFamily : "'Roboto', Consolas, monospace",
      fontSize: 15,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
    }));
    this.diffEditor.layout();

    const originalModel = monaco.editor.createModel(opts.original || '', language);
    const modifiedModel = monaco.editor.createModel(opts.modified || '', language);
    this.models = [originalModel, modifiedModel];
    this.diffEditor.setModel({ original: originalModel, modified: modifiedModel });

    this.onKeyDown = (e) => { if (e.key === 'Escape') this.close(); };
    document.addEventListener('keydown', this.onKeyDown);
  }

  /// Vẽ lại danh sách bản (dùng khi đổi góc nhìn). rowText(entry) → {a, b, c?}: hai/ba dòng chữ của một hàng.
  renderEntries(entries, onSelect, rowText) {
    const list = this.listEl;
    if (!list) return;
    list.textContent = '';
    for (const entry of entries) {
      const row = document.createElement('div');
      row.className = 'histRow';
      row.dataset.hay = entry.hay || '';
      const t = rowText ? rowText(entry) : { a: entry.savedAt, b: formatSize(entry.length) };
      const when = document.createElement('div');
      when.className = 'histWhen';
      when.textContent = t.a;
      const size = document.createElement('div');
      size.className = 'histSize';
      size.textContent = t.b;
      row.appendChild(when);
      row.appendChild(size);
      if (t.c) {
        const extra = document.createElement('div');
        extra.className = 'histSize';
        extra.textContent = t.c;
        row.appendChild(extra);
      }
      row.title = [t.a, t.b, t.c].filter(Boolean).join('\n');
      row.onclick = () => {
        for (const r of list.querySelectorAll('.histRow')) r.classList.remove('selected');
        row.classList.add('selected');
        if (onSelect) onSelect(entry);
      };
      list.appendChild(row);
    }
  }

  /// Đổi vế phải (bản "sau") mà không dựng lại hộp thoại.
  setModified(text, label) {
    if (!this.diffEditor) return;
    if (this.rightLabelEl && label) this.rightLabelEl.textContent = label;
    const model = this.diffEditor.getModel();
    if (model && model.modified) model.modified.setValue(text || '');
  }

  /// Swaps the left-hand side without rebuilding the overlay — used when clicking through
  /// the history list, where rebuilding would lose the scroll position and flicker.
  setOriginal(text, label) {
    if (!this.diffEditor) return;
    if (this.leftLabelEl && label) this.leftLabelEl.textContent = label;

    const model = this.diffEditor.getModel();
    if (model && model.original) model.original.setValue(text || '');
  }

  close() {
    if (this.onKeyDown) {
      document.removeEventListener('keydown', this.onKeyDown);
      this.onKeyDown = null;
    }
    if (this.diffEditor) {
      // Chỉ gỡ model khỏi editor rồi huỷ model; KHÔNG dispose editor (xem ghi chú ở constructor).
      this.diffEditor.setModel(null);
      this.diffEditor = null;
    }
    for (const m of this.models) {
      try { m.dispose(); } catch { /* already gone */ }
    }
    this.models = [];
    if (this.keptHost && this.keptHost.parentNode) this.keptHost.parentNode.removeChild(this.keptHost);
    if (this.overlay) {
      this.overlay.remove();
      this.overlay = null;
    }
    this.leftLabelEl = null;
    this.rightLabelEl = null;
    this.listEl = null;
  }
}

function formatSize(bytes) {
  if (bytes < 1024) return bytes + ' B';
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
  return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
}

window.bcodeHistory = new BcodeHistory();
