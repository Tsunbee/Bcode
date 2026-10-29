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
    const tbModel = window.bcodeShell ? window.bcodeShell.tb.model() : { order: [], groups: [] };   // bản sao mới, sửa thoải mái
    const draft = {
      shortcuts: { ...prefs.data.shortcuts },
      toolbarOrder: tbModel.order,
      groups: tbModel.groups,
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
      // Mặc định tắt (tabs.js items): cây file đang hiện thì thanh tab chỉ giữ file không có trong cây.
      line('', check('Vẫn hiện thanh tab khi đang hiện cây file bên trái (tắt = chỉ hiện tab của file không có trong cây)', draft.layout.tabstripWithTree === '1',
        (on) => { if (on) draft.layout.tabstripWithTree = '1'; else delete draft.layout.tabstripWithTree; }));
      h.appendChild(Object.assign(document.createElement('div'), {
        className: 'setNote setNoteWide',
        textContent: 'Ẩn cả menu + toolbar + đường dẫn thì vào lại bằng phím tắt "Hiện/ẩn thanh menu + toolbar" (gán ở nhóm Phím tắt) hoặc xoá mục này trong Settings. Cây file và các bảng (Problems, Outline...) bật/tắt bằng menu View hoặc phím tắt.',
      }));
      sec('Chuyển tab (Ctrl+Tab)');
      line('Ctrl+Tab bấm nhanh:', (() => {
        const sel = select([['sequential', 'Tab tới: A → B → C (tab kế tiếp)'], ['toggle', 'Tab song song: A ⇄ B (qua lại 2 tab gần nhất)']],
          draft.layout.tabSwitchMode === 'toggle' ? 'toggle' : 'sequential');
        sel.onchange = () => { if (sel.value === 'toggle') draft.layout.tabSwitchMode = 'toggle'; else delete draft.layout.tabSwitchMode; };
        return sel;
      })());
      h.appendChild(Object.assign(document.createElement('div'), {
        className: 'setNote setNoteWide',
        textContent: 'Giống Bcode (Template → Cửa sổ → Ctrl+Tab). Áp cho Ctrl+Tab / Ctrl+Shift+Tab bấm nhanh; giữ Ctrl rồi bấm Tab vẫn hiện hộp chọn tab như cũ. Phím đổi được ở nhóm Phím tắt.',
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

      sec('Thanh công cụ — ghim, nhóm, sắp xếp, vị trí');
      line('Vị trí thanh công cụ:', (() => {
        const sel = select([['top', 'Trên — dưới thanh menu (mặc định)'], ['menu', 'Cùng hàng với thanh menu'], ['bottom', 'Dưới cùng cửa sổ']],
          ['menu', 'bottom'].includes(draft.layout.toolbarPos) ? draft.layout.toolbarPos : 'top');
        sel.onchange = () => { if (sel.value === 'top') delete draft.layout.toolbarPos; else draft.layout.toolbarPos = sel.value; };
        return sel;
      })());
      line('Kiểu nút:', (() => {
        const sel = select([['text', 'Chữ (mặc định)'], ['both', 'Icon + chữ'], ['icon', 'Chỉ icon (chữ hiện ở tooltip)']],
          ['both', 'icon'].includes(draft.layout.toolbarStyle) ? draft.layout.toolbarStyle : 'text');
        sel.onchange = () => { if (sel.value === 'text') delete draft.layout.toolbarStyle; else draft.layout.toolbarStyle = sel.value; };
        return sel;
      })());
      h.appendChild(Object.assign(document.createElement('div'), {
        className: 'setNote setNoteWide',
        textContent: 'Kéo ☰ để đổi thứ tự; thả lên NỬA DƯỚI dòng nhóm (▦) = cho vào nhóm. Bỏ tích = bỏ ghim: nút không hiện trên thanh nhưng vẫn chạy được ở menu ⚙ Toolbar → Nút chưa ghim, menu chính và phím tắt. '
          + 'Nhóm hiện thành 1 nút thả menu; Alt+1…Alt+5 mở nhóm thứ 1…5 tính từ trái (đổi phím ở nhóm Phím tắt → Thanh công cụ). Trên thanh cũng kéo thả nút và chuột phải nút được.',
      }));
      const list = document.createElement('div');
      list.className = 'setTbList';
      h.appendChild(list);
      const groupOf = (id) => draft.groups.find((g) => g.id === id);
      // Kéo: {kind:'top', idx} (trên thanh) | {kind:'item', gid, j} (trong nhóm). Thả: thêm {kind:'into', gid} (cuối nhóm).
      let drag = null;
      const tokOf = (d) => (d.kind === 'top' ? draft.toolbarOrder[d.idx] : groupOf(d.gid).items[d.j]);
      const doDrop = (target) => {
        const tok = tokOf(drag);
        if (target.kind !== 'top') {
          if (tok === '|') return;   // vạch ngăn chỉ đứng trên thanh
          if (tok.startsWith('grp:')) target = { kind: 'top', idx: draft.toolbarOrder.indexOf('grp:' + target.gid) };   // nhóm không lồng nhóm
        }
        // Cùng 1 danh sách: phần tử kéo tới chiếm đúng chỗ dòng được thả (kéo xuống = đứng sau, kéo lên = đứng trước) — như trước khi có nhóm.
        if (target.kind === 'top') {
          if (drag.kind === 'top') { if (drag.idx === target.idx) return; draft.toolbarOrder.splice(drag.idx, 1); }
          else groupOf(drag.gid).items.splice(drag.j, 1);
          draft.toolbarOrder.splice(target.idx, 0, tok);
        } else {
          const g = groupOf(target.gid);
          const to = target.kind === 'into' ? g.items.length : target.j;
          if (drag.kind === 'item' && drag.gid === target.gid) { if (drag.j === to) return; g.items.splice(drag.j, 1); }
          else if (drag.kind === 'top') draft.toolbarOrder.splice(drag.idx, 1);
          else groupOf(drag.gid).items.splice(drag.j, 1);
          g.items.splice(to, 0, tok);
        }
      };
      const hiddenBox = (tok) => {
        const cb = document.createElement('input');
        cb.type = 'checkbox';
        cb.checked = !draft.hidden.has(tok);
        cb.title = 'Tích = ghim (hiện)';
        cb.onchange = () => { if (cb.checked) draft.hidden.delete(tok); else draft.hidden.add(tok); };
        return cb;
      };
      const mkRow = (cls, dragInfo, dropFor) => {
        const r = document.createElement('div');
        r.className = 'setTbRow' + (cls ? ' ' + cls : '');
        if (dragInfo) {
          r.draggable = true;
          r.append(Object.assign(document.createElement('span'), { className: 'setTbGrip', textContent: '☰', title: 'Kéo để đổi vị trí' }));
          r.addEventListener('dragstart', (e) => { drag = dragInfo; e.dataTransfer.effectAllowed = 'move'; r.classList.add('dragging'); });
          r.addEventListener('dragend', () => { r.classList.remove('dragging'); });
        }
        // dropFor(e) → đích thả (hoặc null); dòng nhóm trả 'into' ở nửa dưới.
        r.addEventListener('dragover', (e) => {
          const t = drag && dropFor(e);
          if (!t) return;
          e.preventDefault();
          r.classList.toggle('over', t.kind !== 'into');
          r.classList.toggle('overInto', t.kind === 'into');
        });
        r.addEventListener('dragleave', () => r.classList.remove('over', 'overInto'));
        r.addEventListener('drop', (e) => {
          e.preventDefault();
          const t = drag && dropFor(e);
          if (t) doDrop(t);
          drag = null;
          renderList();
        });
        list.appendChild(r);
        return r;
      };
      const itemLabel = (tok) => (tok.startsWith('cmd:') ? ((keys.find(tok.slice(4)) || {}).label || tok) + '  (nút thêm)' : tok);
      const renderList = () => {
        list.textContent = '';
        draft.toolbarOrder.forEach((tok, idx) => {
          const top = { kind: 'top', idx };
          if (tok.startsWith('grp:')) {
            const g = groupOf(tok.slice(4));
            if (!g) return;
            const r = mkRow('grp', top, (e) => {
              const rc = e.currentTarget.getBoundingClientRect();
              return e.clientY > rc.top + rc.height / 2 ? { kind: 'into', gid: g.id } : top;
            });
            const name = input(g.name);
            name.classList.add('setTbName');
            name.title = 'Tên nhóm — bắt đầu bằng 1 ký hiệu (vd "🛠 Công cụ") thì kiểu "Chỉ icon" chỉ hiện ký hiệu đó';
            name.oninput = () => { g.name = name.value.trim() || 'Nhóm'; };
            r.append(hiddenBox(tok), Object.assign(document.createElement('span'), { textContent: '▦' }), name,
              Object.assign(document.createElement('span'), { className: 'setTbLabel muted', textContent: `nhóm · ${g.items.length} nút` }),
              button('✕', () => { BcodeTbModel.ungroup({ order: draft.toolbarOrder, groups: draft.groups }, g.id); draft.hidden.delete(tok); renderList(); }, 'setTbDel'));
            r.lastChild.title = 'Bỏ nhóm — đưa các nút ra thanh';
            g.items.forEach((it, j) => {
              const at = { kind: 'item', gid: g.id, j };
              const ir = mkRow('inGrp', at, () => at);
              ir.append(hiddenBox(it), Object.assign(document.createElement('span'), { className: 'setTbLabel', textContent: itemLabel(it) }));
              if (it.startsWith('cmd:')) ir.append(button('✕', () => { g.items.splice(j, 1); draft.hidden.delete(it); renderList(); }, 'setTbDel'));
            });
            if (!g.items.length) {
              mkRow('inGrp sep', null, () => ({ kind: 'into', gid: g.id }))
                .append(Object.assign(document.createElement('span'), { className: 'setTbLabel muted', textContent: '— kéo nút vào đây —' }));
            }
            return;
          }
          const r = mkRow(tok === '|' ? 'sep' : '', top, () => top);
          if (tok === '|') r.append(Object.assign(document.createElement('span'), { className: 'setTbLabel muted', textContent: '— vạch ngăn —' }));
          else r.append(hiddenBox(tok), Object.assign(document.createElement('span'), { className: 'setTbLabel', textContent: itemLabel(tok) }));
          if (tok === '|' || tok.startsWith('cmd:')) {
            r.append(button('✕', () => { draft.toolbarOrder.splice(idx, 1); draft.hidden.delete(tok); renderList(); }, 'setTbDel'));
          }
        });
        mkRow('sep end', null, () => ({ kind: 'top', idx: draft.toolbarOrder.length }))
          .append(Object.assign(document.createElement('span'), { className: 'setTbLabel muted', textContent: '— thả vào đây để đưa xuống cuối thanh —' }));
      };
      renderList();
      const adder = document.createElement('div');
      adder.className = 'setTbAdd';
      const cmdSel = document.createElement('select');
      cmdSel.className = 'setInput';
      for (const c of COMMANDS) cmdSel.add(new Option(`${c.group} › ${c.label}`, c.id));
      adder.append(
        button('+ Vạch ngăn', () => { draft.toolbarOrder.push('|'); renderList(); }),
        button('+ Nhóm mới', () => {
          const m = { order: draft.toolbarOrder, groups: draft.groups };
          const id = BcodeTbModel.newGroupId(m);
          draft.groups.push({ id, name: 'Nhóm ' + (draft.groups.length + 1), items: [] });
          draft.toolbarOrder.push('grp:' + id);
          renderList();
          const inputs = list.querySelectorAll('.setTbName');
          if (inputs.length) { inputs[inputs.length - 1].focus(); inputs[inputs.length - 1].select(); }
        }),
        button('Gợi ý nhóm', () => {
          const sm = BcodeTbModel.suggest({ order: draft.toolbarOrder, groups: draft.groups });
          draft.toolbarOrder = sm.order;
          draft.groups = sm.groups;
          renderList();
          window.bcodeViewer.showToast('Đã chia nhóm gợi ý vào bản nháp — bấm OK để áp dụng', 3000);
        }),
        cmdSel,
        button('+ Thêm nút', () => {
          const tok = 'cmd:' + cmdSel.value;
          if (draft.toolbarOrder.includes(tok) || draft.groups.some((g) => g.items.includes(tok))) { window.bcodeUi.alert('Nút này đã có trên thanh công cụ.'); return; }
          draft.toolbarOrder.push(tok);
          draft.hidden.delete(tok);
          renderList();
        }),
      );
      h.appendChild(adder);
      const resetToolbar = () => {
        draft.toolbarOrder = window.bcodeShell.defaultTokens();
        draft.groups = [];
        draft.hidden = new Set();
        delete draft.layout.toolbarPos;
        delete draft.layout.toolbarStyle;
      };
      const reset = document.createElement('div');
      reset.className = 'setTbAdd';
      reset.append(button('Đưa toolbar về mặc định', () => {
        resetToolbar();
        pages.find((p) => p.id === 'layout').render();
      }), button('Đưa cả Bố cục về mặc định', () => {
        resetToolbar();
        draft.layout = {};
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

    // ===== Gợi ý & Hỏi Fast (không AI) =====
    addPage('fast', 'Gợi ý & Hỏi Fast');
    section('Gợi ý theo cấu trúc Fast (không AI)');
    const fastGhost = row('', check('Gợi ý theo cấu trúc Fast khi gõ (XML controller / JavaScript / ASPX)', s.enableFastGhost !== false));
    const fastLong = row('', check('Gợi ý Fast dài (gợi ý dài / nhiều chỗ hơn, đúng ≥ 60%; tắt = ngắn, đúng ≥ 90%) — Alt+Shift+\\ đổi nhanh', !!s.fastGhostLong));
    note('Chạy trên máy, không gọi mạng: học từ source Fast mẫu đi kèm, source của dự án đang mở (đọc ở nền lần đầu) và source Fast các phiên bản bên dưới. Tab để nhận.');
    section('Kiến thức chung (cả nhóm)');
    const knowledgeDir = input(s.knowledgeDir || '');
    row('Thư mục kiến thức chung:', knowledgeDir, button('Browse...', async () => {
      const p = await window.bcodeHost.call('BeginChooseFolder', knowledgeDir.value);
      if (p) knowledgeDir.value = p;
    }));
    const knowledgeStatus = document.createElement('div');
    knowledgeStatus.className = 'setNote';
    knowledgeStatus.textContent = s.knowledgeSummary || '';
    row('', knowledgeStatus);
    note('Đặt 1 thư mục mạng cả nhóm cùng đọc / ghi (vd \\\\172.168.5.14\\Bee\\Knowledge): ma trận học từ source các dự án được gộp thành KIẾN THỨC CHUNG (dự án chưa mở lần nào vẫn được gợi ý theo cách viết của các dự án khác), '
      + 'và mỗi lần ai đó nhận (Tab) hay bỏ qua 1 gợi ý, ô ma trận sinh ra nó được chỉnh lại — gợi ý hay bị bỏ qua tự ít hiện đi, hay được nhận thì chắc hơn, cho cả nhóm. '
      + 'Trống = dùng "Thư mục kiến thức" của Bcode (Template → Đường dẫn), Bcode cũng trống thì chỉ máy này.');
    section('Source Fast theo phiên bản');
    const versionsPath = input(s.fastSourceVersionsPath || '');
    row('Thư mục các phiên bản:', versionsPath, button('Browse...', async () => {
      const p = await window.bcodeHost.call('BeginChooseFolder', versionsPath.value);
      if (p) versionsPath.value = p;
    }));
    const fastStatus = document.createElement('div');
    fastStatus.className = 'setNote';
    fastStatus.textContent = s.fastSourceStatus || '';
    row('', button('Học lại ngay', async () => {
      fastStatus.textContent = 'Đang học lại…';
      try { fastStatus.textContent = await window.bcodeHost.call('BeginRelearnFastSource', window.bcodeViewer && window.bcodeViewer.activePath || ''); }
      catch (e) { fastStatus.textContent = 'Lỗi: ' + e; }
    }));
    lastRow.appendChild(fastStatus);
    note('Thư mục chứa các gói source Fast theo bản (FBOR1, FBOR2SP01 … FBOR2SP24.x, mỗi thư mục chỉ chứa file đổi của bản đó). Bcode quét theo thứ tự bản, so từng file controller với lần trước (field / thuộc tính / entity / hàm JS / command / toolbar thêm-bỏ-sửa) → Hỏi Fast trả lời được "AIDetail sửa gì ở SP22.6", "field ma_kh của AIDetail đổi từ bản nào"; bản mới nhất của mọi file dùng để học ghost text. Lần đầu mất vài phút (đọc ở nền), lưu ở Thư mục kiến thức (Bcode → Template → Đường dẫn).');

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
        textContent: 'Xuất phím tắt + thứ tự / nhóm toolbar + bố cục thành 1 đoạn JSON để chép sang máy khác (hoặc lưu làm bản dự phòng), rồi dán vào và bấm "Nhập". Không gồm API key, mật khẩu và đường dẫn. Nhập chỉ nạp vào bản nháp — bấm OK mới có hiệu lực.',
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
            shortcuts: draft.shortcuts, toolbarOrder: draft.toolbarOrder, toolbarGroups: draft.groups, layout: draft.layout, hiddenToolbar: [...draft.hidden],
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
          if (Array.isArray(d.toolbarGroups)) draft.groups = d.toolbarGroups;
          if (d.layout && typeof d.layout === 'object') draft.layout = Object.fromEntries(Object.entries(d.layout).filter(([, v]) => typeof v === 'string'));
          if (Array.isArray(d.hiddenToolbar)) draft.hidden = new Set(d.hiddenToolbar.filter((t) => typeof t === 'string'));
          // Chuẩn hoá thứ tự + nhóm cho đủ nút (nút thiếu chèn cạnh nút đứng trước nó) rồi vẽ lại các nhóm.
          const nm = BcodeTbModel.normalize(draft.toolbarOrder, draft.groups);
          draft.toolbarOrder = nm.order;
          draft.groups = nm.groups;
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
        enableFastGhost: fastGhost.box.checked, fastGhostLong: fastLong.box.checked, fastSourceVersionsPath: versionsPath.value, knowledgeDir: knowledgeDir.value,
        fcodeConfigXmlPath: fcodeConfig.value, fcodeSqlPassword: fcodePassword.value,
        sqlRegionTags: regionTags.value, editorFontFamily: font.value, fcodeThemeStyle: fcodeStyle.value,
        showMinimap: showMinimap.box.checked, aiSidebarPosition: aiPos.value, autoFitFont: autoFit.box.checked, autoFitMinFont: parseInt(minFont.value, 10) || 9, lightMode: lite.value, wordWrap: wrap.box.checked,
      };
      try {
        await window.bcodeHost.call('BeginSaveSettings', JSON.stringify(data));
        // Chỉ lưu phần khác mặc định: thứ tự toolbar trùng mặc định → để rỗng (nút mới của bản cập nhật tự xuất hiện đúng chỗ).
        const isDefaultOrder = BcodeTbModel.isDefault({ order: draft.toolbarOrder, groups: draft.groups });
        await prefs.save({
          shortcuts: draft.shortcuts, toolbarOrder: isDefaultOrder ? [] : draft.toolbarOrder, toolbarGroups: draft.groups, layout: draft.layout, hiddenToolbar: [...draft.hidden],
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
