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
    this._showToken = 0;
    document.addEventListener('click', () => this.hide());
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') this.hide(); });
    // Vị trí menu là toạ độ màn hình cố định, nên đổi kích thước cửa sổ là nó nằm sai chỗ —
    // có khi nằm hẳn ngoài vùng nhìn thấy mà vẫn đang mở, chặn mất cú click kế tiếp.
    window.addEventListener('resize', () => this.hide());
  }

  hide() {
    this._showToken++; // huỷ mọi lượt show đang chờ dữ liệu
    if (this.root) { this.root.remove(); this.root = null; }
  }

  async show(x, y, editorInstance) {
    this.hide();
    // Bấm chuột phải lần nữa trong lúc phần bất đồng bộ bên dưới đang chạy thì lượt cũ phải
    // im lặng rút lui — không thì nó chèn thêm mục vào menu đã bị thay thế, hoặc tệ hơn là
    // ghi đè this.root và bỏ lại một menu mồ côi trong DOM.
    const token = (this._showToken = (this._showToken || 0) + 1);

    const openFileConfigItems = [
      { label: 'Aggregation', submenu: [
        { label: 'Aggregation List', run: () => editorInstance.openConfigFile('Controllers\\Grid\\Config\\Aggregation.xml') },
        { label: 'Aggregation Voucher', run: () => editorInstance.openConfigFile('Controllers\\Grid\\Config\\Aggregation.v') }
      ] }
    ];
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

    // Đọc tài liệu MỘT lần cho mọi điều kiện bên dưới: getValue() trả về cả file dưới dạng
    // chuỗi, gọi lại năm lần cho năm mục là năm lần serialise lại toàn bộ.
    const model = editorInstance.editor.getModel();
    const docText = model ? model.getValue() : '';
    const hasFile = !!editorInstance.activePath;
    const has = (needle) => hasFile && docText.indexOf(needle) >= 0;

    // Chuột phải trong <items style="Lookup" controller="vctck2" .../> → mở file của controller đó.
    // Mục hiện ngay (bấm sớm vẫn mở được); danh sách file có thật được điền vào sau, vì phải hỏi ổ mạng.
    const ctrl = hasFile ? this.controllerAtCaret(editorInstance, docText) : null;
    const openController = async () => {
      const files = await editorInstance.resolveControllerFiles(ctrl.controller, ctrl.style);
      if (files.length) editorInstance.openFile(files[0].path);
      else alert(`Không tìm thấy file của controller "${ctrl.controller}" trong App_Data\\Controllers.`);
    };

    // Clone file của controller đó thành tên mới, rồi hỏi đổi luôn controller="..." ở đây sang tên mới (Ctrl+Z hoàn tác được).
    const cloneController = async () => {
      const files = await editorInstance.resolveControllerFiles(ctrl.controller, ctrl.style);
      if (!files.length) {
        alert(`Không tìm thấy file của controller "${ctrl.controller}" trong App_Data\\Controllers.`);
        return;
      }
      const cloned = await editorInstance.cloneFile(files[0].path, { open: false });
      if (!cloned) return;
      const ed = editorInstance.editor;
      const mdl = ed.getModel();
      // Chỉ sửa khi tài liệu vẫn là file cũ và chữ ở vị trí đó vẫn đúng tên cũ (người dùng có thể đã gõ thêm trong lúc chờ).
      if (mdl === model && confirm(`Đổi controller="${ctrl.controller}" thành "${cloned.newBase}" trong file đang mở?`)) {
        const from = mdl.getPositionAt(ctrl.valueOffset);
        const to = mdl.getPositionAt(ctrl.valueOffset + ctrl.controller.length);
        const range = new monaco.Range(from.lineNumber, from.column, to.lineNumber, to.column);
        if (mdl.getValueInRange(range) === ctrl.controller)
          ed.executeEdits('bcode-clone-controller', [{ range, text: cloned.newBase }]);
      }
      editorInstance.openFile(cloned.created[0]);
    };

    const items = [
      ...(ctrl ? [
        { label: `Goto File: ${ctrl.controller}`, id: 'gotoController', run: openController },
        { label: `Clone file: ${ctrl.controller}...`, run: cloneController },
        { sep: true }
      ] : []),
      { label: 'Goto Response Tag', run: () => editorInstance.gotoResponseTag(),
        enabled: () => has('<response'),
        disabledHint: 'File này không có thẻ <response>' },
      { label: 'Goto Command', run: () => editorInstance.gotoCommand(),
        enabled: () => has('<command'),
        disabledHint: 'File này không có thẻ <command>' },
      // Không có tên hàm trên dòng hiện tại thì F11 vẫn nhảy về <script>, nên chỉ mờ khi
      // file không có cả hai.
      { label: 'Goto Function', shortcut: 'F11', run: () => editorInstance.gotoFunctionAtCaret(),
        enabled: () => hasFile && (!!editorInstance.handlerNameAtCaret() || docText.indexOf('<script') >= 0),
        disabledHint: 'Dòng hiện tại không gọi hàm nào, và file không có <script>' },
      // Same key F12 already uses, listed here because a value entity has no visible
      // declaration to click through to — until you look, there is nothing on screen to
      // suggest &Name; leads anywhere at all.
      { label: 'Xem trước ENTITY / file', shortcut: 'F12', run: () => editorInstance.jumpToEntityAtCaret('peek'),
        enabled: () => hasFile && this.entityAtCaret(editorInstance, docText),
        disabledHint: 'Con trỏ không đứng trên một &Entity; hay đường dẫn file nào' },
      { label: 'Đi tới ENTITY / file', shortcut: 'Ctrl+F12', run: () => editorInstance.jumpToEntityAtCaret('go'),
        enabled: () => hasFile && this.entityAtCaret(editorInstance, docText),
        disabledHint: 'Con trỏ không đứng trên một &Entity; hay đường dẫn file nào' },
      { sep: true },
      { label: 'Open File Config', id: 'openFileConfig', submenu: openFileConfigItems },
      { label: 'Open Folder', submenu: [
        { label: 'Folder Images', run: () => editorInstance.openAppDataFolder('Images') },
        { label: 'Folder Options', run: () => editorInstance.openAppDataFolder('Options') },
        { label: 'Folder Lookup', run: () => editorInstance.openAppDataFolder('Lookup') },
        { label: 'Folder Templates', run: () => editorInstance.openAppDataFolder('Templates') }
      ] },
      { sep: true },
      { label: 'Chạy SQL tại con trỏ', shortcut: 'Ctrl+Enter', run: () => window.bcodeSqlRun.run(),
        // Hỏi thẳng chính bộ máy sẽ chạy nó, thay vì đoán lại lần nữa ở đây — một phép đoán
        // riêng sẽ lệch khỏi sqlAtCaret() ngay lần đầu ai đó sửa một trong hai.
        enabled: () => {
          if (!hasFile || !window.bcodeSqlRun) return false;
          try {
            const picked = window.bcodeSqlRun.sqlAtCaret();
            return !!(picked && picked.text && picked.text.trim());
          } catch { return true; }
        },
        disabledHint: 'Đặt con trỏ trong <command>/<action> hoặc bôi đen câu lệnh' },
      { sep: true },
      { label: 'Create Function', run: () => editorInstance.createFunctionAtCaret(),
        enabled: () => hasFile && !!editorInstance.handlerNameAtCaret(),
        disabledHint: 'Cần một dòng dạng onchange="TenHam(this)" ở vị trí con trỏ' },
      { label: 'Lookup Regex', run: () => window.bcodeDialogs.showLookupRegex(editorInstance) },
      { label: 'Convert to XML', run: () => window.bcodeDialogs.showConvertToXml() },
      { label: 'Dịch caption (v → e)', shortcut: 'Ctrl+Alt+T', run: () => window.bcodeDialogs.showTranslateHeaders(editorInstance),
        enabled: () => hasFile, disabledHint: 'Cần mở 1 file' },
      { sep: true },
      // An explicit way to ask for suggestions. Quick-suggestions-while-typing depends on
      // Monaco's own auto-trigger rules (token type at the caret, whether the character is
      // a word character); this always works, and makes the feature reachable when they
      // don't fire.
      { label: 'Gợi ý (IntelliSense)', shortcut: 'Ctrl+Space', run: () => editorInstance.triggerSuggest() },
      { label: 'Lịch sử file', shortcut: 'Ctrl+Shift+H', run: () => window.bcodeHistory.showHistory(editorInstance) },
      { label: 'Clone file...', run: () => editorInstance.cloneFile(editorInstance.activePath),
        enabled: () => hasFile, disabledHint: 'Cần mở 1 file' },
      { label: 'Get Hash Source', run: () => window.bcodeDialogs.showHashSource(editorInstance),
        enabled: () => hasFile, disabledHint: 'Cần mở 1 file' },
      { label: 'Refresh', run: () => editorInstance.refreshActive() }
    ];

    this.root = this.buildMenu(items);
    document.body.appendChild(this.root);
    this.position(this.root, x, y);
    if (ctrl) this.fillControllerFiles(token, editorInstance, ctrl);

    // Hai mục này phải đọc thư mục trên ổ mạng. Menu hiện trước, chúng chèn vào sau khi có
    // kết quả — chờ cả hai lần đọc xong mới vẽ menu là một khoảng chết không rõ lý do.
    let listExtend = [];
    let voucherExtend = [];
    try {
      [listExtend, voucherExtend] = await Promise.all([
        editorInstance.listIncludeConfigFiles('List'),
        editorInstance.listIncludeConfigFiles('Voucher')
      ]);
    } catch {
      return; // không liệt kê được thì menu vẫn dùng bình thường, chỉ thiếu hai mục này
    }
    if (token !== this._showToken || !this.root) return; // menu đã đóng hoặc bị thay

    const submenu = this.root.querySelector('[data-ctx-submenu-of="openFileConfig"]');
    if (!submenu) return;

    const toFileItems = (entries) => entries.map((f) => ({ label: f.name, run: () => editorInstance.openFile(f.path) }));
    const extra = [];
    if (listExtend.length > 0) extra.push({ label: 'List Extend', submenu: toFileItems(listExtend) });
    if (voucherExtend.length > 0) extra.push({ label: 'Voucher Extend', submenu: toFileItems(voucherExtend) });
    if (extra.length === 0) return;

    // Chèn ngay sau "Aggregation", đúng chỗ chúng vẫn đứng trước đây.
    const built = this.buildMenu(extra);
    const anchor = submenu.children[1] || null;
    while (built.firstChild) submenu.insertBefore(built.firstChild, anchor);

    // Menu vừa dài thêm nên có thể tràn xuống dưới màn hình; đặt lại nếu nó đang mở.
    if (submenu.style.display === 'block') {
      const row = submenu.parentElement;
      this.openSubmenu(row, submenu);
    }
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
      if (item.id) row.dataset.ctxItem = item.id;

      // Mục không dùng được ở đây thì làm mờ, thay vì để sáng rồi bấm vào không có gì xảy
      // ra — người dùng không phân biệt được là bấm sai chỗ hay chương trình hỏng. Lý do ở
      // tooltip, vì đó mới trả lời được "vậy phải làm sao". Điều kiện viết theo hướng DỄ
      // DÃI: làm mờ nhầm một mục vẫn chạy được thì tệ hơn hẳn để sáng một mục vô hiệu.
      let disabled = false;
      if (item.enabled) {
        try { disabled = !item.enabled(); } catch { disabled = false; }
      }
      if (disabled) {
        row.classList.add('ctxDisabled');
        if (item.disabledHint) row.title = item.disabledHint;
      }

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
        if (item.id) submenuEl.dataset.ctxSubmenuOf = item.id;
        row.appendChild(submenuEl);

        row.addEventListener('mouseenter', () => {
          clearTimeout(row._ctxCloseTimer);
          this.openSubmenu(row, submenuEl);
        });
        // Đóng có TRỄ. Submenu nằm bên phải hàng, nên đường chuột đi tới nó thường lướt
        // chéo qua mấy pixel của menu cha; đóng ngay lập tức thì nó biến mất đúng lúc người
        // dùng đang với tới, và phải rê thật vuông góc mới bấm được.
        row.addEventListener('mouseleave', () => {
          clearTimeout(row._ctxCloseTimer);
          row._ctxCloseTimer = setTimeout(() => this.closeSubmenu(row, submenuEl), 260);
        });
      } else if (disabled) {
        // Không gắn click: menu đã chặn click nổi lên document, nên bấm vào mục mờ là không
        // có gì xảy ra và menu vẫn mở — đúng như mọi menu khác.
      } else {
        row.addEventListener('click', () => { item.run(); this.hide(); });
      }

      menu.appendChild(row);
    }

    // Submenu là position: fixed nên nó không cuộn theo menu cha. Menu cha chỉ cuộn được
    // khi dài hơn màn hình, và lúc đó submenu đang mở sẽ đứng lại một chỗ sai — đóng nó đi
    // thay vì để nó trôi lệch khỏi hàng sinh ra nó.
    menu.addEventListener('scroll', () => this.closeSubmenusIn(menu));
    return menu;
  }

  /// Con trỏ nằm trong 1 thẻ <items ... controller="X" ...> (có thể trải nhiều dòng) thì trả về
  /// { controller, style }, không thì null. Giá trị ghép lúc chạy (&Entity;, {$...}) bỏ qua vì
  /// không ứng với tên file nào.
  controllerAtCaret(editorInstance, docText) {
    const pos = editorInstance.editor.getPosition();
    const model = editorInstance.editor.getModel();
    if (!pos || !model) return null;
    const offset = model.getOffsetAt(pos);
    const start = docText.lastIndexOf('<items', offset);
    if (start < 0) return null;
    const end = docText.indexOf('>', start);
    if (end < 0 || offset > end + 1) return null;
    const tag = docText.slice(start, end + 1);
    if (!/^<items[\s/>]/.test(tag)) return null;
    const m = /\scontroller\s*=\s*["']([^"']*)["']/.exec(tag);
    const controller = m ? m[1] : '';
    if (!/^[\w.$-]+$/.test(controller)) return null;
    const style = (/\sstyle\s*=\s*["']([^"']*)["']/.exec(tag) || [])[1] || '';
    // Vị trí giá trị controller trong tài liệu (ngay trước dấu nháy đóng) — để Clone đổi luôn tên ở đây.
    const valueOffset = start + m.index + m[0].length - 1 - controller.length;
    return { controller, style, valueOffset };
  }

  /// Thay mục "Goto File" tạm bằng kết quả thật: 1 file → mở thẳng (nhãn ghi rõ thư mục),
  /// nhiều file (vd Grid\Item.f và Grid\Item.xml) → submenu để chọn, không có → làm mờ.
  async fillControllerFiles(token, editorInstance, ctrl) {
    const files = await editorInstance.resolveControllerFiles(ctrl.controller, ctrl.style);
    if (token !== this._showToken || !this.root) return; // menu đã đóng hoặc bị thay
    const row = this.root.querySelector('[data-ctx-item="gotoController"]');
    if (!row) return;
    let replacement;
    if (files.length === 0) {
      replacement = { label: `Goto File: ${ctrl.controller}`, run: () => {}, enabled: () => false,
        disabledHint: `Không tìm thấy ${ctrl.controller}.f / .xml trong App_Data\\Controllers` };
    } else if (files.length === 1) {
      replacement = { label: `Goto File: ${files[0].rel}`, run: () => editorInstance.openFile(files[0].path) };
    } else {
      replacement = { label: `Goto File: ${ctrl.controller}`,
        submenu: files.map((f) => ({ label: f.rel, run: () => editorInstance.openFile(f.path) })) };
    }
    row.replaceWith(this.buildMenu([replacement]).firstChild);
  }

  /// Con trỏ có đứng trên thứ gì mà F12 mở được không.
  ///
  /// Việc giải tên entity là BẤT ĐỒNG BỘ (phải lần theo cả chuỗi file include) mà dựng menu
  /// thì phải xong ngay, nên đây là phép xấp xỉ đồng bộ, cố ý nghiêng về phía "cho phép".
  entityAtCaret(editorInstance, docText) {
    const entity = window.bcodeEntity;
    if (!entity) return true; // không kiểm được thì đừng chặn

    try {
      if (entity.quotedPathAtCaret()) return true;

      const name = entity.nameAtCaret();
      if (!name) return false;

      // Khai ngay trong DOCTYPE của file này.
      if (new RegExp('<!ENTITY\\s+%?\\s*' + name.replace(/[.$]/g, '\\$&') + '\\b').test(docText)) return true;

      // Hoặc trong một file mà nó include.
      const index = entity.includeIndexFor(editorInstance.activePath);
      return !!(index && index.has(name));
    } catch {
      return true;
    }
  }

  closeSubmenu(row, submenuEl) {
    submenuEl.style.display = 'none';
    row.classList.remove('ctxOpen');
  }

  closeSubmenusIn(menu) {
    for (const sub of menu.querySelectorAll(':scope > .ctxItem > .ctxSubmenu')) {
      this.closeSubmenu(sub.parentElement, sub);
    }
  }

  openSubmenu(row, submenuEl) {
    // Chỉ một submenu mở cùng lúc trong một cấp — nếu không, cái đang chờ đóng (260ms ở
    // trên) còn nằm đó và chồng lên cái vừa mở.
    const parent = row.parentElement;
    for (const other of parent.querySelectorAll(':scope > .ctxItem > .ctxSubmenu')) {
      if (other !== submenuEl) this.closeSubmenu(other.parentElement, other);
    }

    // Giữ hàng cha sáng suốt thời gian submenu mở. Chuột rê sang submenu là hàng cha mất
    // :hover, nên trước đây vệt sáng tắt ngay và không còn gì chỉ ra menu con đang mở ra từ
    // đâu — đi hai ba cấp là mất dấu hoàn toàn.
    row.classList.add('ctxOpen');

    // ĐO TRƯỚC KHI HIỆN. getBoundingClientRect ép tính lại layout ngay tại chỗ, nên đo sau
    // display = 'block' là đọc đúng cái hàng vừa bị submenu kéo dãn ra. Nay .ctxSubmenu đã
    // là position: fixed sẵn trong CSS nên không kéo dãn được nữa, nhưng thứ tự vẫn đúng.
    const anchor = row.getBoundingClientRect();
    submenuEl.style.display = 'block';
    this.position(submenuEl, anchor.right - 2, anchor.top - 4, anchor);
  }

  /// Đặt và kẹp trong màn hình, ngay tại chỗ chứ không đợi requestAnimationFrame — đợi một
  /// khung hình là menu được vẽ sai chỗ một nhịp rồi mới nhảy về.
  ///
  /// `anchorRect` là hàng đã sinh ra submenu: hết chỗ bên phải thì lật sang bên TRÁI của
  /// hàng đó, chứ không dồn vào mép màn hình rồi đè lên chính menu cha.
  position(el, x, y, anchorRect) {
    el.style.position = 'fixed';
    el.style.left = x + 'px';
    el.style.top = y + 'px';

    const rect = el.getBoundingClientRect();
    const margin = 4;
    let left = x;
    let top = y;

    if (left + rect.width > window.innerWidth - margin) {
      left = anchorRect
        ? anchorRect.left - rect.width + 2
        : window.innerWidth - rect.width - margin;
    }
    if (top + rect.height > window.innerHeight - margin) {
      top = window.innerHeight - rect.height - margin;
    }

    el.style.left = Math.max(margin, left) + 'px';
    el.style.top = Math.max(margin, top) + 'px';
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
        const reply = await window.bcodeHost.call('BeginAskAI', systemPrompt, context, editorInstance.activePath);
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

  /// Dịch caption: điền e="..." cho mọi <header v="Tiếng Việt" e=""> còn trống (trong phần bôi đen, hoặc cả file
  /// nếu không bôi đen). Gửi Claude 1 lần cho cả lô (kèm tên field làm ngữ cảnh: dvt → UOM), hiện bảng cho
  /// sửa tay, rồi mới "Áp dụng" — không tự ghi vào file.
  showTranslateHeaders(editorInstance) {
    if (!editorInstance || !editorInstance.activePath) return;
    const ed = editorInstance.editor;
    const model = ed.getModel();
    const sel = ed.getSelection();
    const scoped = sel && !sel.isEmpty();
    const base = scoped ? model.getOffsetAt(sel.getStartPosition()) : 0;
    const text = scoped ? model.getValueInRange(sel) : model.getValue();
    const full = model.getValue();

    const { overlay, body } = this.makeDialog('Dịch caption (v → e)');
    const info = this.makeLabel('');
    const langBox = document.createElement('input');
    langBox.className = 'dlgTextarea';
    langBox.value = 'English';
    const overwriteLabel = document.createElement('label');
    overwriteLabel.className = 'dlgLabel';
    const overwrite = document.createElement('input');
    overwrite.type = 'checkbox';
    overwriteLabel.appendChild(overwrite);
    overwriteLabel.appendChild(document.createTextNode(' Dịch lại cả những header đã có e'));
    const list = document.createElement('div');
    list.style.cssText = 'max-height:340px;overflow:auto;display:flex;flex-direction:column;gap:3px';

    const attr = (tag, name) => {
      const m = new RegExp(`\\s${name}\\s*=\\s*"([^"]*)"`).exec(tag);
      return m ? m[1] : null;
    };
    const unesc = (s) => s.replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');
    const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

    let rows = []; // { start, end (offset trong file), tag, vi, field, input }
    const scan = () => {
      rows = [];
      list.textContent = '';
      const re = /<header\b[^>]*?>/g;
      let m;
      while ((m = re.exec(text))) {
        const tag = m[0];
        const v = attr(tag, 'v');
        const e = attr(tag, 'e');
        if (!v || !v.trim()) continue;
        if (!overwrite.checked && e && e.trim()) continue;
        const start = base + m.index;
        const fieldAt = full.lastIndexOf('<field', start);
        const fm = fieldAt >= 0 ? /\bname\s*=\s*"([^"]*)"/.exec(full.slice(fieldAt, start)) : null;
        rows.push({ start, end: start + tag.length, tag, vi: unesc(v), field: fm ? fm[1] : '', input: null });
      }
      info.textContent = rows.length
        ? `${rows.length} header cần dịch${scoped ? ' (trong phần bôi đen)' : ' (cả file)'}.`
        : 'Không có header nào cần dịch (v có chữ, e còn trống).';
      rows.forEach((r) => {
        const line = document.createElement('div');
        line.style.cssText = 'display:grid;grid-template-columns:1fr 1fr;gap:6px';
        const vi = document.createElement('div');
        vi.className = 'dlgLabel';
        vi.textContent = r.vi + (r.field ? `  [${r.field}]` : '');
        r.input = document.createElement('input');
        r.input.className = 'dlgTextarea';
        line.appendChild(vi);
        line.appendChild(r.input);
        list.appendChild(line);
      });
      translateBtn.disabled = rows.length === 0;
      applyBtn.disabled = true;
    };

    const translateBtn = this.makeButton('Dịch', 'primary');
    const applyBtn = this.makeButton('Áp dụng');
    const closeBtn = this.makeButton('Close');
    closeBtn.onclick = () => overlay.remove();
    overwrite.onchange = scan;

    translateBtn.onclick = async () => {
      const lang = langBox.value.trim() || 'English';
      translateBtn.disabled = true;
      const old = info.textContent;
      try {
        const BATCH = 40;
        for (let i = 0; i < rows.length; i += BATCH) {
          info.textContent = `Đang dịch ${Math.min(i + BATCH, rows.length)}/${rows.length}...`;
          const chunk = rows.slice(i, i + BATCH);
          const payload = JSON.stringify(chunk.map((r) => ({ vi: r.vi, field: r.field })));
          const prompt =
            `Dịch các caption/nhãn của phần mềm ERP (tên cột, nút, tiêu đề màn hình) từ tiếng Việt sang ${lang}. ` +
            `"field" là tên field trong code, chỉ để hiểu ngữ cảnh (vd dvt = đơn vị tính → UOM). ` +
            `Dùng thuật ngữ kế toán/ERP chuẩn, ngắn gọn, viết hoa chữ cái đầu mỗi từ chính như tiêu đề cột. ` +
            `Chỉ trả về MỘT mảng JSON gồm đúng ${chunk.length} chuỗi, cùng thứ tự, không giải thích, không markdown.\n\n${payload}`;
          const reply = await window.bcodeHost.call('BeginAskAI', prompt, null, editorInstance.activePath);
          const arr = /\[[\s\S]*\]/.exec(stripCodeFence(reply || ''));
          let out;
          try { out = arr ? JSON.parse(arr[0]) : null; } catch { out = null; }
          if (!Array.isArray(out) || out.length !== chunk.length) {
            info.textContent = reply && reply.trim() ? 'Lỗi: ' + reply.trim().slice(0, 300) : 'Chưa có Anthropic API key (Settings).';
            translateBtn.disabled = false;
            return;
          }
          chunk.forEach((r, k) => { r.input.value = String(out[k] ?? ''); });
        }
        info.textContent = old + ' Đã dịch — sửa tay nếu cần rồi bấm Áp dụng.';
        applyBtn.disabled = false;
      } catch (err) {
        info.textContent = 'Lỗi: ' + err;
      }
      translateBtn.disabled = false;
    };

    applyBtn.onclick = () => {
      // Từ cuối lên đầu để offset phía trước không bị lệch khi chèn.
      const edits = rows.filter((r) => r.input.value.trim()).reverse().map((r) => {
        const val = esc(r.input.value.trim());
        const tag = /\se\s*=\s*"[^"]*"/.test(r.tag)
          ? r.tag.replace(/(\se\s*=\s*)"[^"]*"/, (_, p) => `${p}"${val}"`)
          : r.tag.replace(/\s*(\/?>)$/, ` e="${val}"$1`);
        const a = model.getPositionAt(r.start);
        const b = model.getPositionAt(r.end);
        return { range: new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column), text: tag };
      });
      if (edits.length) { ed.pushUndoStop(); ed.executeEdits('translate-headers', edits); ed.pushUndoStop(); }
      overlay.remove();
      ed.focus();
    };

    body.appendChild(info);
    body.appendChild(this.makeLabel('Dịch sang ngôn ngữ'));
    body.appendChild(langBox);
    body.appendChild(overwriteLabel);
    body.appendChild(list);
    body.appendChild(this.makeButtonRow([translateBtn, applyBtn, closeBtn]));
    document.body.appendChild(overlay);
    scan();
    langBox.focus();
  }

  /// Get Hash Source: băm SHA-256 + ngày sửa của file đang mở và các file cùng tên gốc trong site, dạng JSON để dán/so sánh.
  showHashSource(editorInstance) {
    if (!editorInstance || !editorInstance.activePath) return;
    const { overlay, body } = this.makeDialog('Get Hash Source');
    const info = this.makeLabel('Đang tính hash...');
    const box = document.createElement('textarea');
    box.className = 'dlgTextarea';
    box.rows = 16;
    box.readOnly = true;
    const copyBtn = this.makeButton('Copy', 'primary');
    copyBtn.disabled = true;
    const closeBtn = this.makeButton('Close');
    closeBtn.onclick = () => overlay.remove();
    copyBtn.onclick = async () => {
      try { await navigator.clipboard.writeText(box.value); }
      catch { box.select(); document.execCommand('copy'); }
      info.textContent = 'Đã copy vào clipboard.';
    };
    body.appendChild(info);
    body.appendChild(box);
    body.appendChild(this.makeButtonRow([copyBtn, closeBtn]));
    document.body.appendChild(overlay);

    window.bcodeHost.call('BeginGetHashSource', editorInstance.activePath).then((json) => {
      let rows = [];
      try { rows = JSON.parse(json); } catch { /* hiện nguyên văn bên dưới */ }
      box.value = rows.length ? JSON.stringify(rows) : json;
      info.textContent = rows.length
        ? `${rows.length} file — ${rows.map((r) => r.name + ' ' + r.date.slice(0, 19).replace('T', ' ')).slice(0, 3).join(' | ')}${rows.length > 3 ? ' ...' : ''}`
        : 'Không có dữ liệu.';
      copyBtn.disabled = !rows.length;
    }).catch((e) => { info.textContent = 'Lỗi: ' + e; });
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
