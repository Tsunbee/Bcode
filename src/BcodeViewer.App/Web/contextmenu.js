// Custom right-click menu matching FCode's own editor context menu — built by hand
// instead of Monaco's native contextMenuGroupId API because that can't do the nested
// "Open Folder >"/"Open File Config >" submenus FCode's real menu has. Only includes
// items whose exact behavior is actually known — "Goto File" and "Goto Completed" are
// still left out, but "Open File Config" is now populated from paths confirmed straight
// out of FCode's own internal gen_update search config (App_Data\Controllers\Options\
// Message.xml, \Grid\Config\Aggregation.xml/.v, \Include\Extender.ent, etc.) — see
// editor.js's openConfigFile/listIncludeConfigFiles.

class BcodeContextMenu {
  constructor() {
    this.root = null;
    document.addEventListener('click', () => this.hide());
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') this.hide(); });
  }

  hide() {
    if (this.root) { this.root.remove(); this.root = null; }
  }

  async show(x, y, editorInstance) {
    this.hide();

    // "List Extend"/"Voucher Extend" list whatever actually exists rather than a
    // hardcoded count — fetched up front so the menu can render as one static tree.
    const [listExtend, voucherExtend] = await Promise.all([
      editorInstance.listIncludeConfigFiles('List'),
      editorInstance.listIncludeConfigFiles('Voucher')
    ]);
    const toFileItems = (entries) => entries.map((f) => ({ label: f.name, run: () => editorInstance.openFile(f.path) }));

    const openFileConfigItems = [
      { label: 'Aggregation', submenu: [
        { label: 'Aggregation List', run: () => editorInstance.openConfigFile('Controllers\\Grid\\Config\\Aggregation.xml') },
        { label: 'Aggregation Voucher', run: () => editorInstance.openConfigFile('Controllers\\Grid\\Config\\Aggregation.v') }
      ] }
    ];
    if (listExtend.length > 0) openFileConfigItems.push({ label: 'List Extend', submenu: toFileItems(listExtend) });
    if (voucherExtend.length > 0) openFileConfigItems.push({ label: 'Voucher Extend', submenu: toFileItems(voucherExtend) });
    openFileConfigItems.push(
      { sep: true },
      { label: 'Extender.ent', run: () => editorInstance.openConfigFile('Controllers\\Include\\Extender.ent') },
      { label: 'Notify', run: () => editorInstance.openConfigFile('Controllers\\Notify\\Notify.xml') },
      { sep: true },
      { label: 'Message.xml', run: () => editorInstance.openConfigFile('Controllers\\Options\\Message.xml') },
      { label: 'Comment.xml', run: () => editorInstance.openConfigFile('Controllers\\Options\\Comment.xml') },
      { sep: true },
      { label: 'Filter Config', run: () => editorInstance.openConfigFile('Controllers\\Filter\\Config\\Initialize.xml') }
    );

    const items = [
      { label: 'Goto Response Tag', run: () => editorInstance.gotoResponseTag() },
      { label: 'Goto Command', run: () => editorInstance.gotoCommand() },
      { label: 'Goto Function', shortcut: 'F11', run: () => editorInstance.gotoFunctionAtCaret() },
      // Same key F12 already uses, listed here because a value entity has no visible
      // declaration to click through to — until you look, there is nothing on screen to
      // suggest &Name; leads anywhere at all.
      { label: 'Xem code ENTITY', shortcut: 'F12', run: () => editorInstance.jumpToEntityAtCaret() },
      { sep: true },
      { label: 'Open File Config', submenu: openFileConfigItems },
      { label: 'Open Folder', submenu: [
        { label: 'Folder Images', run: () => editorInstance.openAppDataFolder('Images') },
        { label: 'Folder Options', run: () => editorInstance.openAppDataFolder('Options') },
        { label: 'Folder Lookup', run: () => editorInstance.openAppDataFolder('Lookup') },
        { label: 'Folder Templates', run: () => editorInstance.openAppDataFolder('Templates') }
      ] },
      { sep: true },
      { label: 'Chạy SQL tại con trỏ', shortcut: 'Ctrl+Enter', run: () => window.bcodeSqlRun.run() },
      { sep: true },
      { label: 'Create Function', run: () => editorInstance.createFunctionAtCaret() },
      { label: 'Lookup Regex', run: () => window.bcodeDialogs.showLookupRegex(editorInstance) },
      { label: 'Convert to XML', run: () => window.bcodeDialogs.showConvertToXml() },
      { sep: true },
      // An explicit way to ask for suggestions. Quick-suggestions-while-typing depends on
      // Monaco's own auto-trigger rules (token type at the caret, whether the character is
      // a word character); this always works, and makes the feature reachable when they
      // don't fire.
      { label: 'Gợi ý (IntelliSense)', shortcut: 'Ctrl+Space', run: () => editorInstance.triggerSuggest() },
      { label: 'Lịch sử file', shortcut: 'Ctrl+Shift+H', run: () => window.bcodeHistory.showHistory(editorInstance) },
      { label: 'Refresh', run: () => editorInstance.refreshActive() }
    ];

    this.root = this.buildMenu(items);
    document.body.appendChild(this.root);
    this.position(this.root, x, y);
  }

  buildMenu(items) {
    const menu = document.createElement('div');
    menu.className = 'ctxMenu';
    menu.addEventListener('click', (e) => e.stopPropagation());

    for (const item of items) {
      if (item.sep) {
        const sep = document.createElement('div');
        sep.className = 'ctxSep';
        menu.appendChild(sep);
        continue;
      }

      const row = document.createElement('div');
      row.className = 'ctxItem';

      const label = document.createElement('span');
      label.textContent = item.label;
      row.appendChild(label);

      if (item.shortcut) {
        const sc = document.createElement('span');
        sc.className = 'ctxShortcut';
        sc.textContent = item.shortcut;
        row.appendChild(sc);
      }

      if (item.submenu) {
        row.classList.add('ctxHasSubmenu');
        const arrow = document.createElement('span');
        arrow.className = 'ctxArrow';
        arrow.textContent = '▸';
        row.appendChild(arrow);

        const submenuEl = this.buildMenu(item.submenu);
        submenuEl.classList.add('ctxSubmenu');
        row.appendChild(submenuEl);

        row.addEventListener('mouseenter', () => {
          submenuEl.style.display = 'block';
          this.position(submenuEl, row.getBoundingClientRect().right, row.getBoundingClientRect().top, true);
        });
        row.addEventListener('mouseleave', () => { submenuEl.style.display = 'none'; });
      } else {
        row.addEventListener('click', () => { item.run(); this.hide(); });
      }

      menu.appendChild(row);
    }
    return menu;
  }

  position(el, x, y, isSubmenu) {
    el.style.position = 'fixed';
    el.style.left = x + 'px';
    el.style.top = y + 'px';
    if (!isSubmenu) {
      // clamp on next frame once we know the real rendered size
      requestAnimationFrame(() => {
        const rect = el.getBoundingClientRect();
        if (rect.right > window.innerWidth) el.style.left = Math.max(0, window.innerWidth - rect.width - 4) + 'px';
        if (rect.bottom > window.innerHeight) el.style.top = Math.max(0, window.innerHeight - rect.height - 4) + 'px';
      });
    }
  }
}

// ---- Utility dialogs (Lookup Regex / Convert to XML / Ctrl+I AI generate) ----

class BcodeDialogs {
  /// Ctrl+I: a lighter version of VSCode Copilot's inline generate — asks Claude for code
  /// based on a one-line instruction plus whatever's selected (or the whole file if
  /// nothing is), then shows the result for review with an explicit "Insert" step rather
  /// than editing the buffer blind. Reuses the same host.AskAI call the chat panel uses,
  /// just with a system-style instruction telling the model to answer with code only.
  showInlineGenerate(editorInstance) {
    if (!editorInstance || !editorInstance.activePath) return;
    const selection = editorInstance.editor.getModel().getValueInRange(editorInstance.editor.getSelection());

    const { overlay, body } = this.makeDialog(selection ? 'AI: Sửa/Sinh code (có selection)' : 'AI: Sinh code');

    const promptLabel = this.makeLabel('Yêu cầu (Ctrl+Enter để gửi)');
    const promptBox = document.createElement('textarea');
    promptBox.className = 'dlgTextarea';
    promptBox.rows = 2;
    promptBox.placeholder = selection
      ? 'Ví dụ: sửa đoạn được chọn để thêm kiểm tra null...'
      : 'Ví dụ: viết hàm onChange$Voucher$Item kiểm tra tồn kho...';

    const resultLabel = this.makeLabel('Đề xuất từ AI');
    const resultBox = document.createElement('textarea');
    resultBox.className = 'dlgTextarea';
    resultBox.rows = 10;
    resultBox.readOnly = true;

    const generateBtn = this.makeButton('Generate', 'primary');
    const insertBtn = this.makeButton('Insert');
    insertBtn.disabled = true;
    const closeBtn = this.makeButton('Close');
    closeBtn.onclick = () => overlay.remove();

    const runGenerate = async () => {
      const instruction = promptBox.value.trim();
      if (!instruction) return;
      generateBtn.disabled = true;
      resultBox.value = 'Đang hỏi Claude...';
      try {
        const systemPrompt = selection
          ? `Chỉ trả về đoạn code đã sửa để thay thế phần được chọn dưới đây, không giải thích, không dùng markdown code fence:\n\n${selection}\n\nYêu cầu: ${instruction}`
          : `Chỉ trả về đoạn code cần thiết theo yêu cầu, không giải thích, không dùng markdown code fence.\n\nYêu cầu: ${instruction}`;
        const context = editorInstance.getActiveContent();
        const reply = await window.chrome.webview.hostObjects.host.AskAI(systemPrompt, context, editorInstance.activePath);
        resultBox.value = stripCodeFence(reply);
        insertBtn.disabled = false;
      } catch (e) {
        resultBox.value = 'Lỗi: ' + e;
      } finally {
        generateBtn.disabled = false;
      }
    };
    generateBtn.onclick = runGenerate;
    promptBox.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && e.ctrlKey) { e.preventDefault(); runGenerate(); }
    });

    insertBtn.onclick = () => {
      const ed = editorInstance.editor;
      const range = selection ? ed.getSelection() : ed.getSelection(); // replaces selection, or inserts at caret (an empty/collapsed selection is just the caret position)
      ed.executeEdits('ai-insert', [{ range, text: resultBox.value }]);
      overlay.remove();
      ed.focus();
    };

    body.appendChild(promptLabel);
    body.appendChild(promptBox);
    body.appendChild(resultLabel);
    body.appendChild(resultBox);
    body.appendChild(this.makeButtonRow([generateBtn, insertBtn, closeBtn]));
    document.body.appendChild(overlay);
    promptBox.focus();
  }

  showLookupRegex(editorInstance) {
    const selection = editorInstance && editorInstance.editor.getModel()
      ? editorInstance.editor.getModel().getValueInRange(editorInstance.editor.getSelection())
      : '';

    const { overlay, body } = this.makeDialog('Lookup Regex');

    const condLabel = this.makeLabel('Condition (regex)');
    const condBox = document.createElement('textarea');
    condBox.className = 'dlgTextarea';
    condBox.rows = 3;
    condBox.value = selection || '';

    const resultLabel = this.makeLabel('Kết quả');
    const resultBox = document.createElement('textarea');
    resultBox.className = 'dlgTextarea';
    resultBox.rows = 6;
    resultBox.readOnly = true;

    const runBtn = this.makeButton('Regular', 'primary');
    runBtn.onclick = () => {
      const text = editorInstance ? editorInstance.getActiveContent() || '' : '';
      try {
        const re = new RegExp(condBox.value, 'g');
        const matches = [...text.matchAll(re)].map((m, i) => `#${i + 1} @${m.index}: ${m[0]}`);
        resultBox.value = matches.length ? matches.join('\n') : '(không khớp)';
      } catch (e) {
        resultBox.value = 'Regex lỗi: ' + e.message;
      }
    };
    const copyBtn = this.makeButton('Copy to Clipboard');
    copyBtn.onclick = () => navigator.clipboard.writeText(resultBox.value);
    const closeBtn = this.makeButton('Close');
    closeBtn.onclick = () => overlay.remove();

    body.appendChild(condLabel);
    body.appendChild(condBox);
    body.appendChild(resultLabel);
    body.appendChild(resultBox);
    body.appendChild(this.makeButtonRow([runBtn, copyBtn, closeBtn]));
    document.body.appendChild(overlay);
  }

  showConvertToXml() {
    const { overlay, body } = this.makeDialog('Convert to XML');

    const fromLabel = this.makeLabel('From HTML');
    const fromBox = document.createElement('textarea');
    fromBox.className = 'dlgTextarea';
    fromBox.rows = 4;

    const toLabel = this.makeLabel('To XML');
    const toBox = document.createElement('textarea');
    toBox.className = 'dlgTextarea';
    toBox.rows = 4;
    toBox.readOnly = true;

    const exampleA = this.makeButton('Example <a>');
    exampleA.onclick = () => { fromBox.value = '<a href="#">link</a>'; };
    const exampleButton = this.makeButton('Example <button>');
    exampleButton.onclick = () => { fromBox.value = '<button class="btn">OK</button>'; };

    const convertBtn = this.makeButton('Convert', 'primary');
    let reverse = false;
    const reverseBtn = this.makeButton('Reverse');
    reverseBtn.onclick = () => {
      reverse = !reverse;
      reverseBtn.classList.toggle('active', reverse);
      fromLabel.textContent = reverse ? 'From XML' : 'From HTML';
      toLabel.textContent = reverse ? 'To HTML' : 'To XML';
    };
    convertBtn.onclick = () => {
      toBox.value = reverse ? xmlUnescape(fromBox.value) : xmlEscape(fromBox.value);
    };
    const copyBtn = this.makeButton('Copy to Clipboard');
    copyBtn.onclick = () => navigator.clipboard.writeText(toBox.value);
    const closeBtn = this.makeButton('Close');
    closeBtn.onclick = () => overlay.remove();

    body.appendChild(this.makeButtonRow([reverseBtn, exampleA, exampleButton]));
    body.appendChild(fromLabel);
    body.appendChild(fromBox);
    body.appendChild(toLabel);
    body.appendChild(toBox);
    body.appendChild(this.makeButtonRow([convertBtn, copyBtn, closeBtn]));
    document.body.appendChild(overlay);
  }

  makeDialog(title) {
    const overlay = document.createElement('div');
    overlay.className = 'dlgOverlay';
    const box = document.createElement('div');
    box.className = 'dlgBox';
    const header = document.createElement('div');
    header.className = 'dlgHeader';
    header.textContent = title;
    const body = document.createElement('div');
    body.className = 'dlgBody';
    box.appendChild(header);
    box.appendChild(body);
    overlay.appendChild(box);
    overlay.addEventListener('click', (e) => { if (e.target === overlay) overlay.remove(); });
    return { overlay, body };
  }

  makeLabel(text) {
    const l = document.createElement('div');
    l.className = 'dlgLabel';
    l.textContent = text;
    return l;
  }

  makeButton(text, kind) {
    const b = document.createElement('button');
    b.className = 'dlgButton' + (kind === 'primary' ? ' primary' : '');
    b.textContent = text;
    return b;
  }

  makeButtonRow(buttons) {
    const row = document.createElement('div');
    row.className = 'dlgButtonRow';
    buttons.forEach((b) => row.appendChild(b));
    return row;
  }
}

function xmlEscape(s) {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&apos;');
}
function xmlUnescape(s) {
  return s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'").replace(/&amp;/g, '&');
}

/// Claude often wraps code in a ```lang ... ``` fence even when told not to — strip one
/// off if present so "Insert" doesn't paste the fence markers into the file.
function stripCodeFence(text) {
  const m = /^```[\w-]*\r?\n([\s\S]*?)\r?\n?```\s*$/.exec(text.trim());
  return m ? m[1] : text;
}

window.bcodeContextMenu = new BcodeContextMenu();
window.bcodeDialogs = new BcodeDialogs();
