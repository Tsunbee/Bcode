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
    this.models = [];
    this.onKeyDown = null;
  }

  get host() {
    return window.chrome.webview.hostObjects.host;
  }

  // ---- The external-change comparison ------------------------------------------------

  /// Called from the banner's "Xem khác biệt" button. Left is the disk version (theirs),
  /// right is what's in the editor (yours) — the same order VSCode uses for a merge, and
  /// the one that makes "their change" read as an insertion rather than a deletion.
  async showExternalDiff(bcode, diskWriteTimeUtc) {
    if (!bcode.activePath) return;

    let disk;
    try {
      disk = await this.host.ReadFile(bcode.activePath);
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
          run: () => {
            this.close();
            bcode.dismissedWriteTimeUtc = diskWriteTimeUtc;
            bcode.hideExternalChangeBanner();
          },
        },
      ],
    });
  }

  // ---- Local history -----------------------------------------------------------------

  async showHistory(bcode) {
    if (!bcode.activePath) {
      alert('Chưa mở file nào.');
      return;
    }

    let entries;
    try {
      entries = JSON.parse(await this.host.GetFileHistory(bcode.activePath));
    } catch {
      entries = [];
    }

    if (!entries.length) {
      alert(
        'File này chưa có lịch sử.\n\n' +
        'Mỗi lần bạn Save, BcodeViewer chép lại nội dung ĐANG có trên đĩa trước khi ghi đè — ' +
        'nên lịch sử bắt đầu có từ lần Save đầu tiên sau khi bật tính năng này.'
      );
      return;
    }

    const current = bcode.currentModel.getValue();
    this.openDiff({
      title: 'Lịch sử file',
      subtitle: bcode.activePath,
      originalLabel: 'Bản đã chọn',
      original: '',
      modifiedLabel: 'Hiện tại' + (bcode.dirty ? ' (chưa lưu)' : ''),
      modified: current,
      path: bcode.activePath,
      entries,
      onSelectEntry: async (entry) => {
        let text = '';
        try {
          text = await this.host.GetHistorySnapshot(bcode.activePath, entry.id);
        } catch { /* pruned between listing and clicking — show it as empty */ }
        this.setOriginal(text, 'Bản lúc ' + entry.savedAt);
        this.selectedEntry = entry;
        this.selectedText = text;
      },
      actions: [
        {
          label: 'Mở thư mục lịch sử',
          run: () => { try { this.host.OpenHistoryFolder(bcode.activePath); } catch {} },
        },
        {
          label: 'Khôi phục bản này',
          primary: true,
          title: 'Thay nội dung đang mở bằng bản bên trái (chưa ghi xuống đĩa — vẫn phải Save)',
          run: () => {
            if (this.selectedText === undefined || this.selectedText === null) return;
            const restored = this.selectedText;
            const label = this.selectedEntry ? this.selectedEntry.savedAt : '';
            if (!confirm('Thay nội dung đang mở bằng bản lúc ' + label + '?\n\n' +
                         'Chưa ghi xuống đĩa — xem lại rồi bấm Save nếu đúng.')) return;
            this.close();
            // Through the model, not WriteFile: this becomes a normal undoable edit, and
            // nothing touches the file until the user decides to Save. Restoring straight
            // to disk would be the same blind overwrite this feature exists to prevent.
            bcode.currentModel.setValue(restored);
            bcode.editor.focus();
          },
        },
      ],
    });

    // Preselect the newest version so the panel opens showing a real comparison rather
    // than an empty left pane.
    const list = this.overlay.querySelector('.histList');
    if (list && list.firstChild) list.firstChild.click();
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
      const list = document.createElement('div');
      list.className = 'histList';
      for (const entry of opts.entries) {
        const row = document.createElement('div');
        row.className = 'histRow';
        const when = document.createElement('div');
        when.className = 'histWhen';
        when.textContent = entry.savedAt;
        const size = document.createElement('div');
        size.className = 'histSize';
        size.textContent = formatSize(entry.length);
        row.appendChild(when);
        row.appendChild(size);
        row.onclick = () => {
          for (const r of list.querySelectorAll('.histRow')) r.classList.remove('selected');
          row.classList.add('selected');
          opts.onSelectEntry(entry);
        };
        list.appendChild(row);
      }
      body.appendChild(list);
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

    const editorHost = document.createElement('div');
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

    this.diffEditor = monaco.editor.createDiffEditor(editorHost, {
      // Theme comes from whatever bcodeTheme last set — createDiffEditor picks up the
      // global theme, so this follows the app's 15 themes with no extra wiring.
      automaticLayout: true,
      readOnly: true,
      originalEditable: false,
      renderSideBySide: true,
      fontFamily: 'Consolas',
      fontSize: 13,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
    });

    const originalModel = monaco.editor.createModel(opts.original || '', language);
    const modifiedModel = monaco.editor.createModel(opts.modified || '', language);
    this.models = [originalModel, modifiedModel];
    this.diffEditor.setModel({ original: originalModel, modified: modifiedModel });

    this.onKeyDown = (e) => { if (e.key === 'Escape') this.close(); };
    document.addEventListener('keydown', this.onKeyDown);
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
      // Clear the model reference before disposing the editor, then dispose the models
      // themselves — disposing the editor alone leaves both models registered with Monaco.
      this.diffEditor.setModel(null);
      this.diffEditor.dispose();
      this.diffEditor = null;
    }
    for (const m of this.models) {
      try { m.dispose(); } catch { /* already gone */ }
    }
    this.models = [];
    if (this.overlay) {
      this.overlay.remove();
      this.overlay = null;
    }
    this.leftLabelEl = null;
  }
}

function formatSize(bytes) {
  if (bytes < 1024) return bytes + ' B';
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
  return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
}

window.bcodeHistory = new BcodeHistory();
