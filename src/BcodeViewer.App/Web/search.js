// Find (and replace) in Files, across the whole project folder.
//
// Ctrl+F only ever searched the one model Monaco had open, which in an FCode site is a
// single controller out of hundreds — and the question people actually have ("where else
// is this field/handler/stored procedure used?") always spans files. The walk itself runs
// in the host (Host/WorkspaceSearchService.cs) for one round trip instead of one per file;
// this side is the form, the grouped result list, and the replace confirmation.
//
// The reference list from Shift+F12 lands in this same panel (see showResults) rather than
// in a peek widget: the results are the same shape, and one list to learn is better than two.

class BcodeSearch {
  constructor(bcode) {
    this.bcode = bcode;
    this.dock = window.bcodeDock || (window.bcodeDock = new BcodeDock());
    this.pane = this.dock.register('searchPanel', 'Tìm kiếm');
    this.root = null;
    this.matches = [];
    this.collapsed = new Set(); // file paths whose group is folded shut
    this.searching = false;

    this.buildForm();
  }

  buildForm() {
    const form = document.createElement('div');
    form.id = 'searchForm';

    const row1 = document.createElement('div');
    row1.className = 'searchRow';
    this.queryInput = this.textInput('Tìm...');
    this.findBtn = this.button('Tìm', 'primary');
    this.findBtn.onclick = () => this.run();
    row1.appendChild(this.queryInput);
    row1.appendChild(this.findBtn);

    const row2 = document.createElement('div');
    row2.className = 'searchRow';
    this.replaceInput = this.textInput('Thay bằng...');
    this.replaceBtn = this.button('Thay tất cả');
    this.replaceBtn.onclick = () => this.replaceAll();
    row2.appendChild(this.replaceInput);
    row2.appendChild(this.replaceBtn);

    const row3 = document.createElement('div');
    row3.className = 'searchRow';
    this.caseBox = this.checkbox(row3, 'Aa', 'Phân biệt hoa thường');
    this.wordBox = this.checkbox(row3, 'ab|', 'Khớp trọn từ');
    this.regexBox = this.checkbox(row3, '.*', 'Dùng biểu thức chính quy');
    this.includeInput = this.textInput('Lọc file: *.xml, *.sql');
    this.includeInput.style.maxWidth = '200px';
    row3.appendChild(this.includeInput);
    this.rootLabel = document.createElement('span');
    this.rootLabel.className = 'rootLabel';
    row3.appendChild(this.rootLabel);

    form.appendChild(row1);
    form.appendChild(row2);
    form.appendChild(row3);

    this.status = document.createElement('div');
    this.status.id = 'searchStatus';
    this.list = document.createElement('div');
    this.list.className = 'resultList';

    this.pane.appendChild(form);
    this.pane.appendChild(this.status);
    this.pane.appendChild(this.list);

    // Enter searches from either text box; Enter in "replace" still searches rather than
    // replacing, because a replace-all triggered by a stray Enter is not recoverable from
    // muscle memory.
    for (const input of [this.queryInput, this.replaceInput, this.includeInput]) {
      input.addEventListener('keydown', (e) => { if (e.key === 'Enter') this.run(); });
    }
  }

  textInput(placeholder) {
    const el = document.createElement('input');
    el.type = 'text';
    el.placeholder = placeholder;
    return el;
  }

  button(text, kind) {
    const el = document.createElement('button');
    el.className = 'dlgButton' + (kind ? ' ' + kind : '');
    el.textContent = text;
    return el;
  }

  /// A toggle drawn as a labelled checkbox — the same three options Monaco's own find
  /// widget offers, named the same way, so the project-wide search behaves like the
  /// in-file one people already use.
  checkbox(parent, text, title) {
    const label = document.createElement('label');
    label.title = title;
    const box = document.createElement('input');
    box.type = 'checkbox';
    label.appendChild(box);
    label.appendChild(document.createTextNode(text));
    parent.appendChild(label);
    return box;
  }

  /// Opens the panel, seeded with <paramref>seed</paramref> (the editor's selection when
  /// the command came from Ctrl+Shift+F) and focused ready to type.
  async open(seed) {
    this.dock.show('searchPanel');
    if (seed) {
      // A multi-line selection is almost never the intended query — it is usually just
      // what happened to be selected — so only a single line seeds the box.
      const oneLine = seed.split('\n')[0].trim();
      if (oneLine) this.queryInput.value = oneLine;
    }
    this.queryInput.focus();
    this.queryInput.select();
    await this.ensureRoot();
  }

  /// The folder every search covers: the App_Data above the open file, resolved by the
  /// host. Refreshed whenever the active file changes project, which is why this is looked
  /// up per search rather than once at startup.
  async ensureRoot() {
    const anchor = this.bcode.activePath;
    if (!anchor) {
      this.root = null;
      this.rootLabel.textContent = 'Chưa mở file nào — không xác định được phạm vi tìm.';
      return null;
    }
    try { this.root = await window.chrome.webview.hostObjects.host.GetWorkspaceRoot(anchor); }
    catch { this.root = null; }
    this.rootLabel.textContent = this.root ? 'Phạm vi: ' + this.root : '';
    this.rootLabel.title = this.root || '';
    return this.root;
  }

  currentOptions() {
    return {
      query: this.queryInput.value,
      regex: this.regexBox.checked,
      caseSensitive: this.caseBox.checked,
      wholeWord: this.wordBox.checked,
      include: this.includeInput.value,
    };
  }

  async run() {
    if (this.searching) return; // one walk at a time; a second Enter would just queue a duplicate
    const opts = this.currentOptions();
    if (!opts.query) { this.setStatus('Chưa nhập từ khóa.', true); return; }
    const root = await this.ensureRoot();
    if (!root) { this.setStatus('Chưa mở file nào — không xác định được phạm vi tìm.', true); return; }

    this.searching = true;
    this.findBtn.disabled = true;
    this.setStatus('Đang tìm trong ' + root + ' ...');
    this.list.innerHTML = '';

    let raw;
    try {
      raw = await window.bcodeHost.call('BeginSearchWorkspace',
        root, opts.query, opts.regex, opts.caseSensitive, opts.wholeWord, opts.include, 2000);
    } catch (e) {
      this.searching = false;
      this.findBtn.disabled = false;
      this.setStatus('Lỗi khi tìm: ' + e, true);
      return;
    }
    this.searching = false;
    this.findBtn.disabled = false;

    const result = JSON.parse(raw);
    if (result.error) { this.setStatus(result.error, true); return; }

    const files = new Set(result.matches.map((m) => m.path)).size;
    this.showResults(result.matches, opts.query, {
      status: `${result.matches.length} kết quả trong ${files} file` +
        (result.truncated ? ' (đã cắt bớt — hãy thu hẹp từ khóa)' : '') +
        ` · đã quét ${result.filesScanned} file`,
    });
  }

  /// Renders a match list grouped by file. Also the entry point used by "find references"
  /// (outline.js), which produces the same {path, line, column, length, preview} shape.
  showResults(matches, query, opts) {
    this.dock.show('searchPanel');
    this.matches = matches;
    this.collapsed.clear();
    this.setStatus((opts && opts.status) || `${matches.length} kết quả`);
    this.dock.setBadge('searchPanel', matches.length);
    if (opts && opts.query !== undefined) this.queryInput.value = opts.query;

    this.list.innerHTML = '';
    if (matches.length === 0) {
      const empty = document.createElement('div');
      empty.className = 'dockEmpty';
      empty.textContent = query ? `Không tìm thấy "${query}".` : 'Không có kết quả.';
      this.list.appendChild(empty);
      return;
    }

    // Grouped by file, in first-hit order: a flat list repeats the same long UNC path on
    // every row and buries how the hits are distributed.
    const byFile = new Map();
    for (const m of matches) {
      if (!byFile.has(m.path)) byFile.set(m.path, []);
      byFile.get(m.path).push(m);
    }
    for (const [path, rows] of byFile) {
      this.list.appendChild(this.buildGroup(path, rows));
    }
  }

  buildGroup(path, rows) {
    const wrapper = document.createElement('div');

    const header = document.createElement('div');
    header.className = 'resultGroup';
    const twisty = document.createElement('span');
    twisty.textContent = '▾';
    const name = document.createElement('span');
    name.textContent = fileNameOf(path);
    const dir = document.createElement('span');
    dir.className = 'dir';
    // Shown relative to the search root: the absolute prefix is identical on every row and
    // is the part least worth the width.
    dir.textContent = this.relativeDir(path);
    dir.title = path;
    const count = document.createElement('span');
    count.className = 'count';
    count.textContent = rows.length;
    header.append(twisty, name, dir, count);

    const body = document.createElement('div');
    for (const m of rows) {
      // The preview is the line with its indentation trimmed off, so the file column has
      // to be shifted by exactly what was removed (previewOffset) to land on the match.
      body.appendChild(buildResultRow({
        location: String(m.line),
        text: m.preview,
        highlight: { start: m.column - 1 - (m.previewOffset || 0), length: m.length },
        title: `${m.path}:${m.line}`,
        onClick: () => this.bcode.openFile(m.path, { line: m.line, column: m.column }),
      }));
    }

    header.onclick = () => {
      const hidden = body.style.display === 'none';
      body.style.display = hidden ? 'block' : 'none';
      twisty.textContent = hidden ? '▾' : '▸';
    };

    wrapper.appendChild(header);
    wrapper.appendChild(body);
    return wrapper;
  }

  relativeDir(path) {
    const dir = dirNameOf(path);
    if (this.root && dir.toLowerCase().startsWith(this.root.toLowerCase())) {
      return dir.slice(this.root.length).replace(/^\\/, '') || '.';
    }
    return dir;
  }

  /// Replace across every file currently listed in the results.
  ///
  /// Confirmed with the real counts first, and every file is snapshotted into local history
  /// before it is written (see WorkspaceSearchService.Replace). This is the one action in
  /// the app that edits files nobody has opened, so "undo" has to mean something other than
  /// Ctrl+Z in a document that was never on screen.
  async replaceAll() {
    const opts = this.currentOptions();
    if (!opts.query) { this.setStatus('Chưa nhập từ khóa.', true); return; }
    if (this.matches.length === 0) { this.setStatus('Hãy bấm "Tìm" trước khi thay.', true); return; }

    const paths = [...new Set(this.matches.map((m) => m.path))];
    const dirtyOpen = paths.filter((p) => this.bcode.docs.get(p)?.dirty);
    if (dirtyOpen.length) {
      // Replacing on disk under an open buffer with unsaved edits means one of the two
      // versions is about to be lost, whichever way the user then saves.
      this.setStatus(
        `Có ${dirtyOpen.length} file đang mở và chưa lưu (${fileNameOf(dirtyOpen[0])}...). Hãy lưu trước khi thay.`, true);
      return;
    }

    const ok = confirm(
      `Thay "${opts.query}" bằng "${this.replaceInput.value}" trong ${this.matches.length} vị trí, ` +
      `${paths.length} file?\n\nBản cũ của mỗi file được lưu vào Lịch sử file trước khi ghi đè.`);
    if (!ok) return;

    this.setStatus('Đang thay...');
    let raw;
    try {
      raw = await window.bcodeHost.call('BeginReplaceInWorkspace',
        JSON.stringify(paths), opts.query, this.replaceInput.value,
        opts.regex, opts.caseSensitive, opts.wholeWord);
    } catch (e) {
      this.setStatus('Lỗi khi thay: ' + e, true);
      return;
    }

    const result = JSON.parse(raw);
    if (result.error) { this.setStatus(result.error, true); return; }

    // Anything open now differs from disk; reloading keeps the editor honest instead of
    // letting a stale buffer overwrite the replacement on the next Ctrl+S.
    for (const path of paths) {
      const doc = this.bcode.docs.get(path);
      if (!doc) continue;
      try {
        const content = await window.bcodeHost.call('BeginReadFile', path);
        doc.model.setValue(content);
        doc.loadedWriteTimeUtc = await window.bcodeHost.call('BeginGetFileWriteTimeUtc', path);
        doc.dirty = false;
      } catch { /* leave that tab as it was; the stale-file banner will pick it up */ }
    }
    window.bcodeTabs.render();

    const errors = (result.errors || []).length
      ? ` — ${result.errors.length} file lỗi: ${result.errors[0]}`
      : '';
    this.setStatus(`Đã thay ${result.replacements} vị trí trong ${result.filesChanged} file.${errors}`,
      (result.errors || []).length > 0);
    await this.run(); // refresh the list so it reflects what is on disk now
  }

  setStatus(text, isError) {
    this.status.textContent = text;
    this.status.classList.toggle('error', !!isError);
  }
}

window.BcodeSearch = BcodeSearch;
