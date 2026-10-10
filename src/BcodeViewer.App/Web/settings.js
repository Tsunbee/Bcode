// File > Settings... — vẽ ngay trong trang (cùng theme với mọi hộp thoại khác: .dlgOverlay/.dlgBox, màu lấy từ biến --bc-*).
//
// Bố cục hai cột: bên trái là danh sách nhóm (Giao diện, Bố cục, Phím tắt, Editor, AI, Template, SQL, Sao lưu) kèm ô tìm kiếm
// lọc mọi mục theo chữ; bên phải là nội dung của nhóm đang chọn.
//   • Đọc/ghi các mục cũ qua EditorBridge.GetSettings / BeginSaveSettings.
//   • Phím tắt, thứ tự toolbar, bố cục lưu qua bcodePrefs (keys.js → EditorBridge.GetUiPrefs / SetUiPrefs).
//   • Mọi thay đổi chỉ có hiệu lực khi bấm OK (Cancel / Esc bỏ hết); hộp chọn thư mục/file vẫn là của Windows.

class BcodeSettingsDialog {
  async show(startPage) {
    if (document.getElementById('settingsOverlay')) return; // đang mở rồi
    let s;
    try { s = JSON.parse(await window.chrome.webview.hostObjects.host.GetSettings()); }
    catch (e) { alert('Không đọc được Settings:\n' + e); return; }
    const keys = window.bcodeKeys;
    const prefs = window.bcodePrefs;
    const COMMANDS = BCODE_COMMANDS;

    // ---- bản nháp của các tuỳ chỉnh (chỉ ghi khi OK) --------------------------------------------
    const draft = {
      shortcuts: { ...prefs.data.shortcuts },
      toolbarOrder: window.bcodeShell ? window.bcodeShell.toolbarTokens().slice() : [],
      hidden: new Set(prefs.data.hiddenToolbar || []),
      layout: { ...prefs.data.layout },
    };
    const curKey = (id) => {
      const def = keys.find(id);
      if (Object.prototype.hasOwnProperty.call(draft.shortcuts, id)) return draft.shortcuts[id] === '' ? '' : (keys.normalize(draft.shortcuts[id]) || def.def);
      return def.def;
    };
    const layoutOn = (k) => draft.layout[k] !== '0';

    // ---- khung hộp thoại -----------------------------------------------------------------------
    const overlay = document.createElement('div');
    overlay.id = 'settingsOverlay';
    overlay.className = 'dlgOverlay';
    const box = document.createElement('div');
    box.className = 'dlgBox setBox';
    const header = document.createElement('div');
    header.className = 'dlgHeader';
    header.textContent = 'BcodeViewer — Settings';
    const main = document.createElement('div');
    main.className = 'setMain';
    const nav = document.createElement('div');
    nav.className = 'setNav';
    const search = document.createElement('input');
    search.className = 'setInput setSearch';
    search.placeholder = 'Tìm mục cài đặt...';
    search.spellcheck = false;
    const navList = document.createElement('div');
    navList.className = 'setNavList';
    nav.append(search, navList);
    const content = document.createElement('div');
    content.className = 'setBody';
    main.append(nav, content);
    box.append(header, main);
    overlay.appendChild(box);

    // ---- dựng từng nhóm ------------------------------------------------------------------------
    const pages = []; // {id, title, el, rows:[{el, text}]}
    let page = null, lastRow = null;
    const addPage = (id, title) => {
      const el = document.createElement('div');
      el.className = 'setPage';
      content.appendChild(el);
      page = { id, title, el, rows: [], custom: false };
      pages.push(page);
      lastRow = null;
    };
    const addRowEl = (el) => { page.el.appendChild(el); page.rows.push(el); lastRow = el; return el; };
    const section = (text) => {
      const h = document.createElement('div');
      h.className = 'setSection';
      h.textContent = text;
      addRowEl(h);
      h.dataset.section = '1';
    };
    const note = (text) => {
      const n = document.createElement('div');
      n.className = 'setNote';
      n.textContent = text;
      if (lastRow && !lastRow.dataset.section) lastRow.appendChild(n);
      else { const r = document.createElement('div'); r.className = 'setRow'; r.appendChild(n); addRowEl(r); }
    };
    // Một dòng: nhãn | ô nhập | nút phụ (tuỳ chọn)
    const row = (label, control, trailing) => {
      const r = document.createElement('div');
      r.className = 'setRow';
      const l = document.createElement('label');
      l.className = 'setLabel';
      l.textContent = label;
      r.appendChild(l);
      control.classList.add('setControl');
      if (!trailing) control.classList.add('setSpan');
      r.appendChild(control);
      if (trailing) r.appendChild(trailing);
      addRowEl(r);
      return control;
    };
    const input = (value, password) => {
      const i = document.createElement('input');
      i.type = password ? 'password' : 'text';
      i.className = 'setInput';
      i.value = value || '';
      i.spellcheck = false;
      return i;
    };
    const check = (text, checked, onChange) => {
      const wrap = document.createElement('label');
      wrap.className = 'setCheck';
      const c = document.createElement('input');
      c.type = 'checkbox';
      c.checked = !!checked;
      if (onChange) c.onchange = () => onChange(c.checked);
      wrap.append(c, document.createTextNode(' ' + text));
      wrap.box = c;
      return wrap;
    };
    const button = (text, onClick, cls) => {
      const b = document.createElement('button');
      b.className = 'dlgButton' + (cls ? ' ' + cls : '');
      b.textContent = text;
      b.onclick = onClick;
      return b;
    };
    const select = (options, value) => {
      const el = document.createElement('select');
      el.className = 'setInput';
      for (const [v, t] of options) el.add(new Option(t, v));
      el.value = value;
      return el;
    };
    // Nhóm tuỳ biến (Phím tắt, Bố cục, Sao lưu): tự vẽ lại khi dữ liệu nháp đổi.
    const customPage = (id, title, render) => {
      addPage(id, title);
      page.custom = true;
      const holder = document.createElement('div');
      holder.className = 'setCustom';
      page.el.appendChild(holder);
      const p = page;
      p.render = () => { holder.textContent = ''; render(holder); };
      p.render();
      return p;
    };

    // ===== Giao diện =====
    addPage('ui', 'Giao diện');
    section('Giao diện editor');
    const font = input(s.editorFontFamily);
    font.placeholder = "Để trống = mặc định ('Roboto', Consolas, monospace)";
    font.setAttribute('list', 'settingsFontList');
    const fonts = document.createElement('datalist');
    fonts.id = 'settingsFontList';
    for (const f of ['Consolas', "'Cascadia Code', Consolas, monospace", "'Cascadia Mono', Consolas, monospace",
      "'JetBrains Mono', Consolas, monospace", "'Fira Code', Consolas, monospace", "'Courier New', monospace",
      "'Roboto', Consolas, monospace"]) fonts.appendChild(new Option(f));
    page.el.appendChild(fonts);
    row('Phông chữ editor:', font);
    note('VS Code mặc định dùng Consolas trên Windows — chọn Consolas để chữ trông giống VS Code nhất. Phông phải có sẵn trên máy (Cascadia có sẵn trên Windows 11; JetBrains Mono / Fira Code phải tự cài).');
    const fcodeStyle = select([['bcode', 'Bcode — chỉ đổi màu editor'], ['fcode', 'Fcode — giống FcodeViewer']], s.fcodeThemeStyle === 'fcode' ? 'fcode' : 'bcode');
    row('Kiểu theme Fcode (.xml):', fcodeStyle);
    note('Áp dụng cho theme nhập từ file .xml của FcodeViewer; khung app (menu, toolbar, cây thư mục) luôn giữ màu Dark+/Light+. Bcode: chỉ editor đổi màu. Fcode: editor còn dùng font ghi trong theme (<Font name>, vượt mục Phông chữ ở trên) và bỏ các đường kẻ dọc thụt lề như Fcode.');
    section('Hiển thị editor');
    const showMinimap = row('', check('Hiện thanh minimap (bản thu nhỏ của code) bên phải editor', s.showMinimap !== false));
    const autoFit = row('', check('Tự thu nhỏ cỡ chữ khi khung editor bị hẹp (vd kéo rộng khung Claude / Gemini) để thấy đủ code', s.autoFitFont !== false));
    const minFont = row('Cỡ chữ nhỏ nhất:', input(String(s.autoFitMinFont || 9)));
    const wrap = row('', check('Tự ngắt dòng dài theo bề ngang khung (Wrap) — không cần cuộn ngang; nhẹ hơn "Tự thu nhỏ cỡ chữ" (khi bật Wrap, tự thu nhỏ cỡ chữ không còn tác dụng)', s.wordWrap === true));
    const lite = row('Chế độ nhẹ (máy yếu):', select([['auto', 'Tự động — bật khi máy ≤ 4 luồng CPU hoặc ≤ 4 GB RAM'], ['on', 'Bật'], ['off', 'Tắt']], ['on', 'off'].includes(s.lightMode) ? s.lightMode : 'auto'));
    note('Chế độ nhẹ tắt: tô màu cặp ngoặc theo cấp, tô các từ trùng với từ đang chọn, tô ngoặc tương ứng và đường gióng thụt lề — giúp gõ và di con trỏ mượt hơn trên máy yếu (đo được nhanh hơn khoảng 20–25%). Minimap chỉ bị tắt khi chọn "Bật" chế độ nhẹ thủ công — ở "Tự động" nó theo ô tick phía trên. "Tự thu nhỏ cỡ chữ" ở trên là tuỳ chọn riêng. Đổi nhanh bằng phím tắt (Settings → Phím tắt: "Bật/tắt Chế độ nhẹ", "Bật/tắt tự co chữ").');
    note('Chữ lớn nhất là 15. Khi khung hẹp lại, cỡ chữ giảm dần cho tới mức này để các dòng đang hiện vẫn vừa khung; khung rộng ra thì chữ tự lớn lại.');
    const aiPos = select([['right', 'Bên phải editor (mặc định)'], ['bottom', 'Bên dưới editor'], ['top', 'Phía trên editor']], s.aiSidebarPosition || 'right');
    row('Vị trí khung Claude / Gemini:', aiPos);
    note('Theme màu chọn ở menu Theme (nhập thêm theme VS Code / Fcode ở đó).');

    // ===== Bố cục =====
    customPage('layout', 'Bố cục', (h) => {
      const sec = (t) => h.appendChild(Object.assign(document.createElement('div'), { className: 'setSection', textContent: t }));
      const line = (label, control) => {
        const r = document.createElement('div');
        r.className = 'setRow';
        r.appendChild(Object.assign(document.createElement('label'), { className: 'setLabel', textContent: label }));
        control.classList.add('setControl', 'setSpan');
        r.appendChild(control);
        h.appendChild(r);
      };
      sec('Hiện / ẩn các khu vực');
      const regions = [['menubar', 'Thanh menu (File, View, Theme...)'], ['toolbar', 'Thanh công cụ (Save, Undo, Hint...)'],
        ['breadcrumb', 'Đường dẫn file đang mở'], ['tabstrip', 'Thanh tab các file đang mở']];
      for (const [k, t] of regions) line('', check(t, layoutOn(k), (on) => { draft.layout[k] = on ? '1' : '0'; }));
      h.appendChild(Object.assign(document.createElement('div'), {
        className: 'setNote setNoteWide',
        textContent: 'Ẩn cả menu + toolbar + đường dẫn thì vào lại bằng phím tắt "Hiện/ẩn thanh menu + toolbar" (gán ở nhóm Phím tắt) hoặc xoá mục này trong Settings. Cây file và các bảng (Problems, Outline...) bật/tắt bằng menu View hoặc phím tắt.',
      }));
      sec('Cây file bên trái');
      line('Vị trí cây file:', (() => {
        const sel = select([['left', 'Bên trái (mặc định)'], ['right', 'Bên phải']], draft.layout.sidebarSide === 'right' ? 'right' : 'left');
        sel.onchange = () => { draft.layout.sidebarSide = sel.value; };
        return sel;
      })());
      line('Độ rộng cây file (px):', (() => {
        const i = input(draft.layout.sidebarWidth || '');
        i.placeholder = 'Để trống = giữ độ rộng đang kéo';
        i.oninput = () => { const n = parseInt(i.value, 10); if (n) draft.layout.sidebarWidth = String(Math.max(160, Math.min(600, n))); else delete draft.layout.sidebarWidth; };
        return i;
      })());

      sec('Thanh công cụ — sắp xếp, ẩn / hiện, thêm nút');
      h.appendChild(Object.assign(document.createElement('div'), {
        className: 'setNote setNoteWide',
        textContent: 'Kéo ☰ để đổi thứ tự. Bỏ tích để ẩn nút. Có thể thêm vạch ngăn và nút cho BẤT KỲ chức năng nào trong bảng phím tắt.',
      }));
      const list = document.createElement('div');
      list.className = 'setTbList';
      h.appendChild(list);
      let dragIdx = -1;
      const renderList = () => {
        list.textContent = '';
        draft.toolbarOrder.forEach((tok, idx) => {
          const r = document.createElement('div');
          r.className = 'setTbRow' + (tok === '|' ? ' sep' : '');
          r.draggable = true;
          r.append(Object.assign(document.createElement('span'), { className: 'setTbGrip', textContent: '☰', title: 'Kéo để đổi vị trí' }));
          if (tok === '|') {
            r.append(Object.assign(document.createElement('span'), { className: 'setTbLabel muted', textContent: '— vạch ngăn —' }));
          } else {
            const cb = document.createElement('input');
            cb.type = 'checkbox';
            cb.checked = !draft.hidden.has(tok);
            cb.onchange = () => { if (cb.checked) draft.hidden.delete(tok); else draft.hidden.add(tok); };
            const label = tok.startsWith('cmd:') ? ((keys.find(tok.slice(4)) || {}).label || tok) + '  (nút thêm)' : tok;
            r.append(cb, Object.assign(document.createElement('span'), { className: 'setTbLabel', textContent: label }));
          }
          if (tok === '|' || tok.startsWith('cmd:')) {
            r.append(button('✕', () => { draft.toolbarOrder.splice(idx, 1); draft.hidden.delete(tok); renderList(); }, 'setTbDel'));
          }
          r.addEventListener('dragstart', (e) => { dragIdx = idx; e.dataTransfer.effectAllowed = 'move'; r.classList.add('dragging'); });
          r.addEventListener('dragend', () => { r.classList.remove('dragging'); });
          r.addEventListener('dragover', (e) => { e.preventDefault(); r.classList.add('over'); });
          r.addEventListener('dragleave', () => r.classList.remove('over'));
          r.addEventListener('drop', (e) => {
            e.preventDefault();
            if (dragIdx < 0 || dragIdx === idx) return;
            const [moved] = draft.toolbarOrder.splice(dragIdx, 1);
            draft.toolbarOrder.splice(idx, 0, moved);
            dragIdx = -1;
            renderList();
          });
          list.appendChild(r);
        });
      };
      renderList();
      const adder = document.createElement('div');
      adder.className = 'setTbAdd';
      const cmdSel = document.createElement('select');
      cmdSel.className = 'setInput';
      for (const c of COMMANDS) cmdSel.add(new Option(`${c.group} › ${c.label}`, c.id));
      adder.append(
        button('+ Vạch ngăn', () => { draft.toolbarOrder.push('|'); renderList(); }),
        cmdSel,
        button('+ Thêm nút', () => {
          const tok = 'cmd:' + cmdSel.value;
          if (draft.toolbarOrder.includes(tok)) { window.bcodeUi.alert('Nút này đã có trên thanh công cụ.'); return; }
          draft.toolbarOrder.push(tok);
          draft.hidden.delete(tok);
          renderList();
        }),
      );
      h.appendChild(adder);
      const reset = document.createElement('div');
      reset.className = 'setTbAdd';
      reset.append(button('Đưa toolbar về mặc định', () => {
        draft.toolbarOrder = window.bcodeShell.defaultTokens();
        draft.hidden = new Set();
        renderList();
      }), button('Đưa cả Bố cục về mặc định', () => {
        draft.layout = {};
        draft.toolbarOrder = window.bcodeShell.defaultTokens();
        draft.hidden = new Set();
        pages.find((p) => p.id === 'layout').render();
      }));
      h.appendChild(reset);
    });

    // ===== Phím tắt =====
    let recording = null, recMsg = '', pendingConflict = null, recHandler = null;
    const stopRecording = () => {
      if (recHandler) document.removeEventListener('keydown', recHandler, true);
      recHandler = null; recording = null; keys.recording = false;
      renderKeysPage();
    };
    const startRecording = (id) => {
      if (recHandler) document.removeEventListener('keydown', recHandler, true);
      recording = id; recMsg = ''; pendingConflict = null; keys.recording = true;
      recHandler = (e) => {
        e.preventDefault(); e.stopPropagation();
        if (e.key === 'Escape' && !e.ctrlKey && !e.altKey && !e.shiftKey) { stopRecording(); return; }
        if (['Control', 'Shift', 'Alt', 'Meta'].includes(e.key)) return; // mới nhấn phím bổ trợ — chờ phím chính
        const combo = keys.comboOf(e);
        if (!combo) { recMsg = 'Phím này không dùng được làm phím tắt.'; renderKeysPage(); return; }
        const bad = keys.problem(combo);
        if (bad) { recMsg = bad; renderKeysPage(); return; }
        const other = COMMANDS.find((c) => c.id !== recording && curKey(c.id) === combo);
        if (other) { pendingConflict = { id: recording, combo, other: other.id }; if (recHandler) document.removeEventListener('keydown', recHandler, true); recHandler = null; recording = null; keys.recording = false; renderKeysPage(); return; }
        setKey(recording, combo);
        stopRecording();
      };
      document.addEventListener('keydown', recHandler, true);
      renderKeysPage();
    };
    const setKey = (id, combo) => {
      const def = keys.find(id).def;
      if (combo === def) delete draft.shortcuts[id]; else draft.shortcuts[id] = combo;
    };
    let keyFilterBox = null;
    const renderKeysPage = () => { const p = pages.find((x) => x.id === 'keys'); if (p && p.render) p.render(); };
    customPage('keys', 'Phím tắt', (h) => {
      const top = document.createElement('div');
      top.className = 'setNote setNoteWide';
      top.textContent = 'Bấm "Đổi" rồi nhấn tổ hợp phím mới (Esc để huỷ). Phím mới chạy ở mọi nơi trong cửa sổ, kể cả khi con trỏ đang trong editor. Phím cũ của chức năng được đổi sẽ hết tác dụng. Ctrl+C/V/X/Z/Y/A và Alt+F4 giữ nguyên cho hệ thống. Các phím chuẩn khác của editor (Ctrl+F tìm, Ctrl+H thay, Alt+↑/↓ di chuyển dòng...) không nằm trong bảng này.';
      h.appendChild(top);
      const filter = document.createElement('input');
      filter.className = 'setInput setKeyFilter';
      filter.placeholder = 'Lọc theo tên chức năng hoặc phím...';
      filter.value = keyFilterBox || '';
      filter.oninput = () => { keyFilterBox = filter.value; renderKeysPage(); const f = pages.find((x) => x.id === 'keys').el.querySelector('.setKeyFilter'); if (f) { f.focus(); f.setSelectionRange(f.value.length, f.value.length); } };
      h.appendChild(filter);
      if (pendingConflict) {
        const w = document.createElement('div');
        w.className = 'setKeyWarn';
        const name = (id) => keys.find(id).label;
        w.appendChild(Object.assign(document.createElement('div'), { textContent: `Phím ${pendingConflict.combo} đang dùng cho "${name(pendingConflict.other)}". Gán cho "${name(pendingConflict.id)}" và bỏ phím của mục kia?` }));
        const bs = document.createElement('div');
        bs.append(button('Gán đè', () => {
          const c = pendingConflict; pendingConflict = null;
          draft.shortcuts[c.other] = '';
          setKey(c.id, c.combo);
          renderKeysPage();
        }, 'primary'), button('Huỷ', () => { pendingConflict = null; renderKeysPage(); }));
        w.appendChild(bs);
        h.appendChild(w);
      }
      const q = (keyFilterBox || '').trim().toLowerCase();
      const grid = document.createElement('div');
      grid.className = 'setKeyGrid';
      let lastGroup = null;
      for (const c of COMMANDS) {
        const cur = curKey(c.id);
        if (q && !(c.label.toLowerCase().includes(q) || cur.toLowerCase().includes(q) || c.group.toLowerCase().includes(q))) continue;
        if (c.group !== lastGroup) { lastGroup = c.group; grid.appendChild(Object.assign(document.createElement('div'), { className: 'setKeyGroup', textContent: c.group })); }
        const changed = cur !== c.def;
        const r = document.createElement('div');
        r.className = 'setKeyRow';
        r.appendChild(Object.assign(document.createElement('div'), { className: 'setKeyName' + (changed ? ' chg' : ''), textContent: c.label, title: c.label }));
        const kb = document.createElement('span');
        if (recording === c.id) { kb.className = 'setKb rec'; kb.textContent = 'Nhấn phím mới...'; }
        else if (cur) { kb.className = 'setKb'; kb.textContent = cur; } else { kb.className = 'setKb none'; kb.textContent = 'Chưa gán'; }
        const keyCell = document.createElement('div');
        keyCell.className = 'setKeyCell';
        keyCell.appendChild(kb);
        if (recording === c.id && recMsg) keyCell.appendChild(Object.assign(document.createElement('span'), { className: 'setKeyMsg', textContent: recMsg }));
        r.appendChild(keyCell);
        const acts = document.createElement('div');
        acts.className = 'setKeyActs';
        acts.append(
          button(recording === c.id ? 'Huỷ' : 'Đổi', () => (recording === c.id ? stopRecording() : startRecording(c.id))),
          Object.assign(button('Xoá phím', () => { draft.shortcuts[c.id] = ''; renderKeysPage(); }), { disabled: !cur }),
          Object.assign(button('Mặc định', () => { delete draft.shortcuts[c.id]; renderKeysPage(); }), { disabled: !changed, title: 'Về phím mặc định (' + (c.def || 'không có') + ')' }),
        );
        r.appendChild(acts);
        grid.appendChild(r);
      }
      h.appendChild(grid);
      const foot = document.createElement('div');
      foot.className = 'setTbAdd';
      foot.append(button('Đưa TẤT CẢ phím tắt về mặc định', async () => {
        if (await window.bcodeUi.confirm('Đưa mọi phím tắt về mặc định? (Chỉ có hiệu lực khi bấm OK.)', { okText: 'Đưa về mặc định' })) { draft.shortcuts = {}; renderKeysPage(); }
      }));
      h.appendChild(foot);
    });

    // ===== Editor / AI =====
    addPage('ai', 'AI (chat + gợi ý)');
    section('AI (chat + gợi ý)');
    const apiKey = row('Anthropic API key:', input(s.anthropicApiKey, true));
    const geminiKey = row('Gemini API key:', input(s.geminiApiKey, true));
    const model = row('Model (chat):', input(s.model));
    const aiCompletion = row('', check('Bật gợi ý AI khi gõ (ghost text)', s.enableAiCompletion));
    note('Mỗi lần ngừng gõ ~0,4 giây sẽ gọi Claude 1 lần và bị tính phí. Ctrl+I (gọi tay) vẫn chạy kể cả khi tắt mục này.');
    const completionModel = row('Model (ghost text):', input(s.completionModel));
    const engine = select([['claude', 'Claude'], ['gemini', 'Gemini']], s.completionEngine === 'gemini' ? 'gemini' : 'claude');
    row('Engine gợi ý (ghost text):', engine);
    note('Chọn Gemini vẫn dùng nguyên prompt như Claude, chỉ đổi nơi gửi request — cần điền Gemini API key ở trên. Chat panel và Ctrl+I không đổi theo mục này, luôn dùng Claude.');
    const translateEngine = select([['google', 'Google (không cần API key)'], ['gemini', 'Gemini (cần Gemini API key)'], ['claude', 'Claude (cần Anthropic API key)']],
      ['gemini', 'claude'].includes(s.translateEngine) ? s.translateEngine : 'google');
    row('Engine dịch caption:', translateEngine);
    note('Dùng cho "Dịch caption (v → e)". Google gọi endpoint web của Google Translate — không cần key nhưng không chính thức (có thể bị giới hạn tốc độ) và dịch từng caption không có ngữ cảnh; Gemini/Claude dịch tốt hơn nhờ biết tên field và thuật ngữ ERP.');

    // ===== Template =====
    addPage('template', 'Template dùng chung');
    section('Template dùng chung');
    const sharedPath = input(s.sharedTemplatePath);
    row('Thư mục chung:', sharedPath, button('Browse...', async () => {
      const p = await window.bcodeHost.call('BeginChooseFolder', sharedPath.value);
      if (p) sharedPath.value = p;
    }));
    note('Thư mục UNC hoặc 1 repo git cả nhóm đọc được. Trong đó: *.code-snippets (định dạng VSCode) và *.json là snippet gõ theo prefix; thư mục con files\\ là template cả file cho Ctrl+N. BcodeViewer chỉ ĐỌC thư mục này.');

    // ===== SQL =====
    addPage('sql', 'Gợi ý & chạy SQL');
    section('Gợi ý SQL');
    const sqlStatus = document.createElement('div');
    sqlStatus.className = 'setNote';
    const sqlCompletion = row('', check('Gợi ý tên bảng / cột trong vùng SQL', s.enableSqlCompletion), button('Test', async () => {
      sqlStatus.textContent = 'Đang kiểm tra...';
      try { sqlStatus.textContent = await window.chrome.webview.hostObjects.host.DescribeSqlStatus(); }
      catch (e) { sqlStatus.textContent = 'Lỗi: ' + e; }
    }));
    lastRow.appendChild(sqlStatus);
    const sqlWrites = row('', check('Cho phép "Chạy SQL" thực thi câu ghi (INSERT/UPDATE/DELETE/EXEC...)', s.enableSqlWrites));
    note('Mặc định TẮT. Khi tắt, câu có lệnh ghi vẫn chạy được ở chế độ "Chạy thử (rollback)" — chạy trong transaction rồi luôn rollback, nên vẫn biết được số dòng bị ảnh hưởng mà không đổi dữ liệu. Chỉ bật mục này khi bạn thực sự muốn ghi thật: BcodeViewer chạy trên đúng WS mà Bcode đang chọn, thường là CSDL thật của khách.');
    const fcodeConfig = input(s.fcodeConfigXmlPath);
    row('Config.xml của FCode:', fcodeConfig, button('Browse...', async () => {
      const p = await window.bcodeHost.call('BeginChooseFile', fcodeConfig.value);
      if (p) fcodeConfig.value = p;
    }));
    const fcodePassword = row('Mật khẩu SQL:', input(s.fcodeSqlPassword, true));
    note('Dự phòng cho "Chạy SQL" / gợi ý SQL khi Bcode CHƯA có workspace nào (chưa có settings.json): tự kết nối theo project trong Config.xml của FCode (khớp theo thư mục của file đang mở, không khớp thì lấy project đầu). Password trong Config.xml bị FCode mã hoá nên phải nhập mật khẩu SQL ở đây.');
    const regionTags = row('Thẻ chứa SQL:', input(s.sqlRegionTags));
    note('Thường để trống. BcodeViewer đã tự nhận cấu trúc controller: command và action là SQL, script và clientScript là JavaScript, css là CSS, CDATA có /* <flatten type="Javascript"> */ là JavaScript, và giá trị <!ENTITY> được phân loại theo nội dung. Chỉ khai thêm ở đây nếu dự án bạn còn thẻ riêng nào khác chứa SQL (cách nhau bằng dấu phẩy).');
    note('Dùng lại đúng workspace (WS) mà Bcode đang chọn — không cần khai báo kết nối riêng. Chỉ kết nối khi mở file .sql, và schema lấy về được nhớ sẵn cho tới khi đóng app.');

    // ===== Sao lưu =====
    customPage('backup', 'Sao lưu / chuyển máy', (h) => {
      h.appendChild(Object.assign(document.createElement('div'), {
        className: 'setNote setNoteWide',
        textContent: 'Xuất phím tắt + thứ tự toolbar + bố cục thành 1 đoạn JSON để chép sang máy khác (hoặc lưu làm bản dự phòng), rồi dán vào và bấm "Nhập". Không gồm API key, mật khẩu và đường dẫn. Nhập chỉ nạp vào bản nháp — bấm OK mới có hiệu lực.',
      }));
      const ta = document.createElement('textarea');
      ta.className = 'setInput setJson';
      ta.spellcheck = false;
      ta.placeholder = '{ "shortcuts": {...}, "toolbarOrder": [...], "layout": {...}, "hiddenToolbar": [...] }';
      h.appendChild(ta);
      const bar = document.createElement('div');
      bar.className = 'setTbAdd';
      bar.append(
        button('Xuất (từ bản nháp hiện tại)', () => {
          ta.value = JSON.stringify({
            shortcuts: draft.shortcuts, toolbarOrder: draft.toolbarOrder, layout: draft.layout, hiddenToolbar: [...draft.hidden],
          }, null, 2);
          ta.select();
        }),
        button('Sao chép', async () => { try { await navigator.clipboard.writeText(ta.value); window.bcodeViewer.showToast('Đã chép', 1500); } catch { ta.select(); } }),
        button('Nhập (vào bản nháp)', async () => {
          let d;
          try { d = JSON.parse(ta.value); } catch (e) { window.bcodeUi.alert('JSON không hợp lệ:\n' + e.message, { kind: 'error' }); return; }
          if (d.shortcuts && typeof d.shortcuts === 'object') {
            draft.shortcuts = {};
            for (const [id, v] of Object.entries(d.shortcuts)) {
              if (!keys.find(id) || typeof v !== 'string') continue;
              const n = v === '' ? '' : keys.normalize(v);
              if (n === null || (n && keys.problem(n))) continue;
              draft.shortcuts[id] = n;
            }
          }
          if (Array.isArray(d.toolbarOrder)) draft.toolbarOrder = d.toolbarOrder.filter((t) => typeof t === 'string');
          if (d.layout && typeof d.layout === 'object') draft.layout = Object.fromEntries(Object.entries(d.layout).filter(([, v]) => typeof v === 'string'));
          if (Array.isArray(d.hiddenToolbar)) draft.hidden = new Set(d.hiddenToolbar.filter((t) => typeof t === 'string'));
          // Nạp lại thứ tự cho đủ nút (nút thiếu được nối vào cuối) rồi vẽ lại các nhóm.
          const missing = window.bcodeShell.defaultTokens().filter((t) => t !== '|' && !draft.toolbarOrder.includes(t));
          draft.toolbarOrder.push(...missing);
          for (const p of pages) if (p.custom && p.id !== 'backup') p.render();
          window.bcodeViewer.showToast('Đã nhập vào bản nháp — bấm OK để áp dụng', 3000);
        }),
      );
      h.appendChild(bar);
    });

    // ---- điều hướng + tìm kiếm -----------------------------------------------------------------
    let current = null;
    const showPage = (id) => {
      current = id;
      search.value = '';
      for (const p of pages) { p.el.style.display = p.id === id ? '' : 'none'; for (const r of p.rows) r.style.display = ''; }
      for (const b of navList.children) b.classList.toggle('active', b.dataset.id === id);
      content.scrollTop = 0;
    };
    for (const p of pages) {
      const b = document.createElement('div');
      b.className = 'setNavItem';
      b.dataset.id = p.id;
      b.textContent = p.title;
      b.onclick = () => showPage(p.id);
      navList.appendChild(b);
    }
    search.addEventListener('input', () => {
      const q = search.value.trim().toLowerCase();
      if (!q) { showPage(current || pages[0].id); return; }
      for (const b of navList.children) b.classList.remove('active');
      for (const p of pages) {
        // Nhóm tuỳ biến (phím tắt, bố cục...) lọc theo tên nhóm; các nhóm còn lại lọc từng mục.
        if (p.custom) { p.el.style.display = p.title.toLowerCase().includes(q) ? '' : 'none'; continue; }
        let any = false, secEl = null, secShown = false;
        for (const r of p.rows) {
          if (r.dataset.section) { secEl = r; r.style.display = 'none'; secShown = false; continue; }
          const hit = r.textContent.toLowerCase().includes(q) || [...r.querySelectorAll('input,select')].some((i) => (i.value || '').toLowerCase().includes(q) && i.type !== 'password');
          r.style.display = hit ? '' : 'none';
          if (hit) { any = true; if (secEl && !secShown) { secEl.style.display = ''; secShown = true; } }
        }
        p.el.style.display = any ? '' : 'none';
      }
    });

    // ---- OK / Cancel ---------------------------------------------------------------------------
    const close = () => {
      if (recHandler) document.removeEventListener('keydown', recHandler, true);
      keys.recording = false;
      overlay.remove();
      document.removeEventListener('keydown', onKey, true);
    };
    const okBtn = document.createElement('button');
    okBtn.className = 'dlgButton primary';
    okBtn.textContent = 'OK';
    okBtn.onclick = async () => {
      okBtn.disabled = true;
      const data = {
        anthropicApiKey: apiKey.value, geminiApiKey: geminiKey.value, model: model.value,
        enableAiCompletion: aiCompletion.box.checked, completionModel: completionModel.value,
        completionEngine: engine.value, translateEngine: translateEngine.value, sharedTemplatePath: sharedPath.value,
        enableSqlCompletion: sqlCompletion.box.checked, enableSqlWrites: sqlWrites.box.checked,
        fcodeConfigXmlPath: fcodeConfig.value, fcodeSqlPassword: fcodePassword.value,
        sqlRegionTags: regionTags.value, editorFontFamily: font.value, fcodeThemeStyle: fcodeStyle.value,
        showMinimap: showMinimap.box.checked, aiSidebarPosition: aiPos.value, autoFitFont: autoFit.box.checked, autoFitMinFont: parseInt(minFont.value, 10) || 9, lightMode: lite.value, wordWrap: wrap.box.checked,
      };
      try {
        await window.bcodeHost.call('BeginSaveSettings', JSON.stringify(data));
        // Chỉ lưu phần khác mặc định: thứ tự toolbar trùng mặc định → để rỗng (nút mới của bản cập nhật tự xuất hiện đúng chỗ).
        const isDefaultOrder = JSON.stringify(draft.toolbarOrder) === JSON.stringify(window.bcodeShell.defaultTokens());
        await prefs.save({
          shortcuts: draft.shortcuts, toolbarOrder: isDefaultOrder ? [] : draft.toolbarOrder, layout: draft.layout, hiddenToolbar: [...draft.hidden],
        });
      } catch (e) {
        alert('Không lưu được Settings:\n' + e);
        okBtn.disabled = false;
        return;
      }
      close();
      keys.rebuild();
      if (window.bcodeShell) { await window.bcodeShell.refreshState(); window.bcodeShell.applyLayout(); }
      if (window.bcodeCompletion) window.bcodeCompletion.reloadSnippets();
      if (window.bcodeTheme) window.bcodeTheme.init(); // áp phông chữ mới cho mọi editor đang mở
      if (window.bcodeViewer && window.bcodeViewer.applyViewConfig) window.bcodeViewer.applyViewConfig();
      if (window.bcodeViewer && window.bcodeViewer.showToast) window.bcodeViewer.showToast('Đã lưu Settings', 2500);
    };
    const cancelBtn = document.createElement('button');
    cancelBtn.className = 'dlgButton';
    cancelBtn.textContent = 'Cancel';
    cancelBtn.onclick = close;
    const buttons = document.createElement('div');
    buttons.className = 'dlgButtonRow setButtons';
    buttons.append(okBtn, cancelBtn);
    box.appendChild(buttons);

    // Esc = Cancel, Enter (ngoài ô nhập nhiều dòng) = OK — như hộp thoại cũ. Đang ghi phím mới thì để bộ ghi phím tự xử lý.
    const onKey = (e) => {
      if (recording) return;
      if (e.key === 'Enter' && e.target === search) return;
      if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(); }
      else if (e.key === 'Enter' && e.target.tagName !== 'BUTTON' && e.target.tagName !== 'TEXTAREA') { e.preventDefault(); e.stopPropagation(); okBtn.click(); }
    };
    document.addEventListener('keydown', onKey, true);

    document.body.appendChild(overlay);
    showPage(pages.some((p) => p.id === startPage) ? startPage : 'ui');
    search.focus();
  }
}

window.bcodeSettings = new BcodeSettingsDialog();
