// "Xem trước Dir": dựng lại màn hình nhập liệu của 1 controller Dir ngay trong BcodeViewer, từ chính
// source đang mở — sửa file là thấy form đổi theo, khỏi phải deploy rồi lên FastBusiness xem.
//
// CÁCH FASTBUSINESS DỰNG FORM (đối chiếu với HTML thật của 1 hóa đơn trên FBO, không đoán):
//   <table class="FormTable" style="table-layout:fixed">
//     <tr class="FormRow"><th style="width:100px"> <th style="width:30px"> ...      ← dòng bề rộng ("100, 30, 70, ...")
//     <tr class="FormRow"><td id="formCell_{hàng}.{cột cuối}" colspan="{số cột}" class="FormCell"> ... </td> ...
//   Mỗi dòng thiết kế "110100000011011: [a].Label, [a], [b], ..." = 1 <tr>: ký tự '1' mở 1 ô, các '0' liền sau
//   kéo dài ô đó (colspan), '-' là cột trống. Nhãn: <div class="FormContainer">; ô nhập: <div class="FormContainer
//   FormContainerInput"><input class="FormInput FormTextInput"> + biểu tượng lookup/ngày; read-only:
//   FormContainerInputDisabled; bắt buộc: td có class "Required" (tam giác cam ở góc).
//   - view split="N": N cột đầu là bảng trái, phần còn lại là bảng phải (khối "Ngày lập, Số ct, ..."): ở đây vẽ
//     chung 1 bảng để 2 khối luôn thẳng hàng.
//   - view anchor="K": cột co giãn — nhận hết phần rộng còn lại của cửa sổ (thường là cột đệm nhỏ ở giữa nên
//     khối bên phải dính mép phải). Cột đó 0 bề rộng thì lấy cột kế tiếp có bề rộng.
//   - Dòng thiết kế của field có categoryIndex=N nằm trong TAB N và dùng dòng bề rộng riêng của category
//     (<category index="N" columns="30, 90, ...">), không dùng dòng bề rộng của view.
// Entity (&ExportViews;, &BI.Dir.Field.X; ...) được khai triển TRƯỚC khi dựng — cả entity có giá trị lẫn entity
// SYSTEM trỏ tới file include.
//
// GIỚI HẠN (đây là mô phỏng, không phải runtime FastBusiness):
//  - Chỉ vẽ cấu trúc: nhãn, ô nhập, checkbox, ô lookup/ngày/chọn. Không chạy script, không có dữ liệu thật.
//  - Lưới chi tiết (items style="Grid") chỉ là khung giữ chỗ — cột/toolbar do controller khác dựng lúc chạy.
//  - Cỡ chữ, màu, khoảng cách là ước lượng theo ảnh FBO (file CSS của FBO nằm ngoài source này).
//  - Entity không tìm thấy bị bỏ qua (liệt kê ở chân panel) — bố cục có thể thiếu phần của chúng.

const DP_BUILTIN_ENTITIES = new Set(['amp', 'lt', 'gt', 'quot', 'apos']);

class BcodeDirPreview {
  constructor(bcode) {
    this.bcode = bcode;
    this.open = false;
    this.timer = null;
    this.version = 0;
    this.activeTab = null;
    // Cache giữa các lần vẽ: sửa cột chỉ đổi vài ký tự, không cần đọc lại file entity mỗi lần.
    this.entCache = new Map();
    this.entHead = null;
    this.entTime = 0;
    this.expMemo = new Map();

    this.panel = document.createElement('div');
    this.panel.id = 'dirPreviewPanel';
    this.panel.style.display = 'none';
    this.panel.innerHTML = `
      <div id="dpResizer" title="Kéo để đổi độ rộng"></div>
      <div id="dpHeader">
        <span id="dpTitle">Xem trước Dir</span>
        <span id="dpStatus"></span>
        <span class="dpBtn" id="dpRefresh" title="Vẽ lại ngay">⟳</span>
        <span class="dpBtn" id="dpClose" title="Đóng">✕</span>
      </div>
      <div id="dpScroll"><div id="dpRoot"></div></div>
      <div id="dpFooter"></div>`;
    document.getElementById('main').appendChild(this.panel);

    this.root = this.panel.querySelector('#dpRoot');
    this.status = this.panel.querySelector('#dpStatus');
    this.footer = this.panel.querySelector('#dpFooter');
    this.panel.querySelector('#dpClose').onclick = () => this.close();
    this.panel.querySelector('#dpRefresh').onclick = () => this.render(true);
    this.setupResize();

    const editor = bcode.editor;
    editor.onDidChangeModelContent(() => this.schedule());
    editor.onDidChangeModel(() => { this.activeTab = null; this.schedule(0); });
  }

  toggle() { this.open ? this.close() : this.openPanel(); }

  openPanel() {
    this.open = true;
    this.panel.style.display = 'flex';
    this.render();
  }

  close() {
    this.open = false;
    this.panel.style.display = 'none';
  }

  schedule(delay = 200) {
    if (!this.open) return;
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.render(), delay);
  }

  setupResize() {
    const handle = this.panel.querySelector('#dpResizer');
    handle.addEventListener('mousedown', (e) => {
      e.preventDefault();
      const startX = e.clientX;
      const startW = this.panel.getBoundingClientRect().width;
      const onMove = (ev) => {
        const w = Math.max(360, Math.min(window.innerWidth - 300, startW + (startX - ev.clientX)));
        this.panel.style.width = w + 'px';
      };
      const onUp = () => {
        document.removeEventListener('mousemove', onMove);
        document.removeEventListener('mouseup', onUp);
      };
      document.addEventListener('mousemove', onMove);
      document.addEventListener('mouseup', onUp);
    });
  }

  // ---- Rendering pipeline ------------------------------------------------------------------

  async render(force = false) {
    if (!this.open) return;
    const run = ++this.version;
    const model = this.bcode.currentModel;
    if (!model) { this.message('Chưa mở file nào.'); return; }

    const text = model.getValue();
    if (!/<view\b[^>]*\bid\s*=\s*"Dir"/i.test(text)) {
      this.message('File này không có <view id="Dir"> — xem trước chỉ hỗ trợ controller Dir.');
      return;
    }

    this.status.textContent = 'Đang dựng…';
    // Khai báo DOCTYPE đổi (hoặc bấm ⟳, hoặc cache quá 20s — file include có thể vừa sửa) thì bỏ cache.
    const head = (/<!DOCTYPE[sS]*?]>/i.exec(text) || [''])[0];
    if (force || head !== this.entHead || Date.now() - this.entTime > 20000) {
      this.entCache = new Map();
      this.expMemo = new Map();
      this.entHead = head;
      this.entTime = Date.now();
    }
    const ctx = { cache: this.entCache, unresolved: new Set() };
    let fieldsXml, viewsXml;
    try {
      const fieldsRaw = (/<fields\b[\s\S]*?<\/fields>/i.exec(text) || [''])[0];
      const viewsRaw = (/<views\b[\s\S]*?<\/views>/i.exec(text) || [''])[0];
      [fieldsXml, viewsXml] = await Promise.all([this.expandMemo(fieldsRaw, ctx), this.expandMemo(viewsRaw, ctx)]);
    } catch (e) {
      if (run === this.version) this.message('Không khai triển được entity: ' + (e && e.message ? e.message : e));
      return;
    }
    if (run !== this.version) return; // đã có lần vẽ mới hơn

    const fieldsDoc = this.parseXml(fieldsXml);
    const viewsDoc = this.parseXml(viewsXml);
    if (fieldsDoc.error || viewsDoc.error) {
      this.message('XML chưa hợp lệ nên chưa vẽ được: ' + (fieldsDoc.error || viewsDoc.error), true);
      return;
    }

    const fields = this.collectFields(fieldsDoc.doc);
    const view = Array.from(viewsDoc.doc.getElementsByTagName('view')).find((v) => v.getAttribute('id') === 'Dir');
    if (!view) { this.message('Không đọc được <view id="Dir">.'); return; }

    const title = (/<title\b[^>]*\bv="([^"]*)"/i.exec(text) || [])[1] || '';
    this.draw({ title: this.cleanText(title), fields, view });
    this.status.textContent = new Date().toLocaleTimeString();
    this.footer.textContent = ctx.unresolved.size
      ? `Entity chưa giải được (${ctx.unresolved.size}): ` + [...ctx.unresolved].slice(0, 12).map((n) => '&' + n + ';').join(' ') + (ctx.unresolved.size > 12 ? ' …' : '')
      : '';
  }

  message(text, isError = false) {
    this.root.innerHTML = `<div class="dpMessage${isError ? ' err' : ''}">${this.esc(text)}</div>`;
    this.status.textContent = '';
    this.footer.textContent = '';
  }

  esc(s) { return String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }

  cleanText(s) { return String(s || '').replace(/<[^>]*>/g, '').replace(/&nbsp;/g, ' ').trim(); }

  parseXml(xml) {
    const doc = new DOMParser().parseFromString('<r>' + xml + '</r>', 'text/xml');
    const err = doc.getElementsByTagName('parsererror')[0];
    return err ? { error: this.cleanText(err.textContent).slice(0, 220) } : { doc };
  }

  // ---- Entity expansion -------------------------------------------------------------------

  // Khối không đổi so với lần vẽ trước (thường là <fields> khi chỉ sửa <view>, và ngược lại) thì dùng lại kết quả.
  async expandMemo(raw, ctx) {
    const hit = this.expMemo.get(raw);
    if (hit) { hit.unresolved.forEach((n) => ctx.unresolved.add(n)); return hit.out; }
    const local = { cache: ctx.cache, unresolved: new Set() };
    const out = await this.expand(raw, local);
    local.unresolved.forEach((n) => ctx.unresolved.add(n));
    if (this.expMemo.size > 6) this.expMemo.clear();
    this.expMemo.set(raw, { out, unresolved: [...local.unresolved] });
    return out;
  }

  async expand(block, ctx) {
    let out = block;
    for (let pass = 0; pass < 5; pass++) {
      const names = [...new Set([...out.matchAll(/&([A-Za-z_][\w.:$-]*);/g)].map((m) => m[1]))]
        .filter((n) => !DP_BUILTIN_ENTITIES.has(n) && !ctx.unresolved.has(n));
      if (!names.length) break;
      let changed = false;
      const values = await Promise.all(names.map((n) => this.entityText(n, ctx)));
      names.forEach((name, i) => {
        const value = values[i];
        if (value == null) { ctx.unresolved.add(name); return; }
        out = out.split('&' + name + ';').join(value);
        changed = true;
      });
      if (!changed) break;
    }
    // Entity còn sót (không giải được) bỏ đi để phần còn lại vẫn là XML hợp lệ.
    return out.replace(/&([A-Za-z_][\w.:$-]*);/g, (m, n) => (DP_BUILTIN_ENTITIES.has(n) ? m : ''));
  }

  async entityText(name, ctx) {
    if (ctx.cache.has(name)) return ctx.cache.get(name);
    let value = null;
    try {
      const found = window.bcodeEntity ? await window.bcodeEntity.resolveActive(name) : null;
      if (found && found.decl.kind === 'value') {
        value = found.decl.value;
      } else if (found && found.decl.kind === 'system') {
        const target = resolvePath(dirNameOf(found.path), found.decl.systemPath.replace(/\//g, '\\'));
        const raw = await window.bcodeHost.call('BeginReadFile', target);
        value = String(raw || '').replace(/^﻿/, '').replace(/^\s*<\?xml[^>]*\?>/, '');
      }
    } catch { value = null; }
    ctx.cache.set(name, value);
    return value;
  }

  // ---- Model -------------------------------------------------------------------------------

  collectFields(doc) {
    const map = new Map();
    const fieldsEl = doc.getElementsByTagName('fields')[0];
    if (!fieldsEl) return map;
    for (const f of Array.from(fieldsEl.children)) {
      if (f.tagName !== 'field') continue;
      const name = f.getAttribute('name');
      if (!name) continue;
      const header = Array.from(f.children).find((c) => c.tagName === 'header');
      const footer = Array.from(f.children).find((c) => c.tagName === 'footer');
      const itemsEl = Array.from(f.children).find((c) => c.tagName === 'items');
      map.set(name, {
        name,
        type: f.getAttribute('type') || '',
        allowNulls: f.getAttribute('allowNulls') !== 'false',
        readOnly: f.getAttribute('readOnly') === 'true' || f.getAttribute('inactivate') === 'true',
        external: f.getAttribute('external') === 'true',
        rows: parseInt(f.getAttribute('rows') || '1', 10) || 1,
        category: f.getAttribute('categoryIndex'),
        label: header ? this.cleanText(header.getAttribute('v')) : '',
        footer: footer ? this.cleanText(footer.getAttribute('v')) : '',
        itemsStyle: itemsEl ? itemsEl.getAttribute('style') || '' : '',
        controller: itemsEl ? itemsEl.getAttribute('controller') || '' : '',
        options: itemsEl ? Array.from(itemsEl.children).filter((c) => c.tagName === 'item').map((c) => this.cleanText(c.getAttribute('value') || c.textContent)) : [],
      });
    }
    return map;
  }

  /// <category index columns anchor><header v e/></category> — mỗi tab có dòng bề rộng riêng.
  collectCategories(view) {
    const map = new Map();
    for (const c of Array.from(view.getElementsByTagName('category'))) {
      const index = c.getAttribute('index');
      if (index == null) continue;
      const header = Array.from(c.children).find((x) => x.tagName === 'header');
      const cols = (c.getAttribute('columns') || '').trim();
      map.set(index, {
        index,
        label: header ? this.cleanText(header.getAttribute('v')) : 'Tab ' + index,
        widths: /^\d+(?:\s*,\s*\d+)*$/.test(cols) ? cols.split(',').map((n) => parseInt(n, 10)) : null,
        anchor: parseInt(c.getAttribute('anchor') || '-1', 10),
        rows: [],
      });
    }
    return map;
  }

  /// Các dòng thiết kế, gắn với đúng dòng bề rộng của nó (view hoặc category).
  parseRows(view, fields, categories) {
    const rows = [];
    let viewWidths = null;
    for (const item of Array.from(view.children)) {
      if (item.tagName !== 'item') continue;
      const v = item.getAttribute('value') || '';
      if (/^\s*\d+(?:\s*,\s*\d+)+\s*$/.test(v)) { viewWidths = v.match(/\d+/g).map(Number); continue; }
      const m = /^\s*([01\-]+)\s*:([\s\S]*)$/.exec(v);
      if (!m) continue;

      const mask = m[1];
      const ones = [];
      for (let i = 0; i < mask.length; i++) if (mask[i] === '1') ones.push(i);
      const cells = [];
      m[2].split(',').map((t) => t.trim()).filter(Boolean).forEach((token, idx) => {
        const start = ones[idx];
        if (start === undefined) return;
        let end = start;
        while (end + 1 < mask.length && mask[end + 1] === '0') end++;
        cells.push({ token, start, end });
      });

      // Dòng thuộc tab nào: theo field đầu tiên có categoryIndex (và category đó có khai báo).
      let cat = null;
      for (const c of cells) {
        const tm = /^\[([^\]]+)\]/.exec(c.token);
        const f = tm ? fields.get(tm[1]) : null;
        if (f && f.category != null && categories.has(f.category)) { cat = f.category; break; }
      }
      const catInfo = cat != null ? categories.get(cat) : null;
      let widths = (catInfo && catInfo.widths) || (cat == null ? viewWidths : null);
      if (!widths) widths = Array(mask.length).fill(80); // category chưa có bề rộng đọc được: chia đều tạm
      const row = { mask, cells, widths, cat, anchor: catInfo ? catInfo.anchor : parseInt(view.getAttribute('anchor') || '-1', 10) };
      if (cat != null) catInfo.rows.push(row); else rows.push(row);
    }
    return rows;
  }

  // ---- Drawing -----------------------------------------------------------------------------

  draw({ title, fields, view }) {
    const categories = this.collectCategories(view);
    const mainRows = this.parseRows(view, fields, categories);

    const tabs = [...categories.values()];
    let tabsHtml = '';
    if (tabs.length) {
      if (!this.activeTab || !categories.has(this.activeTab)) this.activeTab = tabs[0].index;
      tabsHtml = `<div class="dpTabs">` + tabs.map((c) =>
        `<span class="dpTab${c.index === this.activeTab ? ' active' : ''}" data-tab="${this.esc(c.index)}" title="${this.esc(c.label)}">${this.esc(c.label)}</span>`
      ).join('') + `</div><div class="dpTabBody">${this.tabBodyHtml(categories.get(this.activeTab), fields)}</div>`;
    }

    this.root.innerHTML = `
      <div class="dpWindow">
        <div class="dpTitleBar">${this.esc(title ? 'Thêm ' + title : 'Dir')}</div>
        <div class="dpFormArea">${mainRows.length ? this.tableHtml(mainRows, fields) : '<div class="dpTabEmpty">Không có dòng thiết kế nào ngoài các tab.</div>'}</div>
        ${tabsHtml}
        <div class="dpButtons"><span class="dpBtnFake primary">Lưu</span><span class="dpBtnFake">Hủy</span></div>
      </div>`;

    this.root.querySelectorAll('.dpTab').forEach((t) => {
      t.onclick = () => { this.activeTab = t.dataset.tab; this.draw({ title, fields, view }); };
    });
    this.root.querySelectorAll('[data-field]').forEach((el) => {
      el.onclick = (e) => { e.stopPropagation(); this.revealField(el.dataset.field); };
    });
  }

  /// Nội dung 1 tab: các dòng thiết kế của category (bảng riêng, bề rộng riêng); chưa có dòng nào thì liệt kê
  /// field có categoryIndex tương ứng.
  tabBodyHtml(cat, fields) {
    if (cat.rows.length) return this.tableHtml(cat.rows, fields);
    const list = [...fields.values()].filter((f) => f.category === cat.index && !f.external);
    if (!list.length) return `<div class="dpTabEmpty">Tab này không có dòng thiết kế hay field nào khai <code>categoryIndex="${this.esc(cat.index)}"</code> (nội dung do lưới/khung khác dựng lúc chạy — không mô phỏng).</div>`;
    return `<div class="dpTabGrid">` + list.map((f) =>
      `<div class="dpTabRow"><div class="dpLbl" data-field="${this.esc(f.name)}">${this.esc(f.label || f.name)}</div><div class="dpFC">${this.controlInner(f, true)}</div></div>`
    ).join('') + `</div>`;
  }

  /// Bảng FormTable: hàng th = bề rộng cột; mỗi dòng thiết kế = 1 <tr>, ô theo colspan từ mặt nạ. Các dòng cùng bảng
  /// phải CÙNG dòng bề rộng — dòng khác bề rộng (hiếm) mở bảng mới.
  tableHtml(rows, fields) {
    // Dòng chỉ chứa 1 lưới (<item value="1: [d81]"/>): FBO đặt lưới trong bảng RIÊNG 1 cột chiếm cả bề ngang
    // (th width = toàn bộ), không dùng dòng bề rộng của view/category → vẽ full-width, ngoài bảng thiết kế.
    const isGridRow = (r) => {
      if (r.cells.length !== 1) return false;
      const m = /^\[([^\]]+)\]$/.exec(r.cells[0].token);
      const f = m ? fields.get(m[1]) : null;
      return !!f && f.itemsStyle === 'Grid';
    };

    const out = [];
    let pending = [];
    const flush = () => {
      if (!pending.length) return;
      const groups = [];
      for (const r of pending) {
        const key = r.widths.join(',') + '|' + r.anchor;
        const g = groups[groups.length - 1];
        if (g && g.key === key) g.rows.push(r); else groups.push({ key, rows: [r], widths: r.widths, anchor: r.anchor });
      }
      out.push(...groups.map((g) => this.oneTableHtml(g, fields)));
      pending = [];
    };
    for (const r of rows) {
      if (isGridRow(r)) { flush(); out.push('<div class="dpGridRow">' + this.cellContent(r.cells[0].token, fields).html + '</div>'); }
      else pending.push(r);
    }
    flush();
    return out.join('');
  }

  oneTableHtml({ rows, widths, anchor }, fields) {
    const n = widths.length;
    // Cột co giãn: cột `anchor`; nếu 0 bề rộng thì cột kế tiếp có bề rộng (đúng theo HTML thật của FBO).
    let stretch = -1;
    if (anchor >= 0 && anchor < n) {
      stretch = anchor;
      while (stretch < n && widths[stretch] === 0) stretch++;
      if (stretch >= n) stretch = anchor;
    }
    const fixed = widths.reduce((a, w, i) => a + (i === stretch ? 0 : w), 0);
    const th = widths.map((w, i) => `<th${i === stretch ? ' class="dpStretch"' : ` style="width:${w}px"`}></th>`).join('');

    let body = '';
    for (const row of rows) {
      let tds = '';
      let col = 0;
      for (const cell of [...row.cells].sort((a, b) => a.start - b.start)) {
        if (cell.start < col) continue; // chồng lên ô trước — bỏ
        if (cell.start > col) tds += `<td colspan="${cell.start - col}"></td>`;
        const span = Math.min(cell.end, n - 1) - cell.start + 1;
        const { html, required } = this.cellContent(cell.token, fields);
        tds += `<td colspan="${Math.max(1, span)}" class="dpTd${required ? ' Required' : ''}">${html}</td>`;
        col = cell.end + 1;
      }
      if (col < n) tds += `<td colspan="${n - col}"></td>`;
      body += `<tr>${tds}</tr>`;
    }
    return `<table class="dpTable" style="min-width:${fixed + 40}px"><tbody><tr class="dpWidthRow">${th}</tr>${body}</tbody></table>`;
  }

  cellContent(token, fields) {
    const m = /^\[([^\]]+)\](?:\.(\w+))?$/.exec(token);
    if (!m) return { html: `<div class="dpUnknown" title="${this.esc(token)}">${this.esc(token)}</div>`, required: false };
    const name = m[1];
    const suffix = m[2];
    const f = fields.get(name);
    const attr = `data-field="${this.esc(name)}"`;

    if (suffix === 'Label') return { html: `<div class="dpLbl" ${attr} title="${this.esc(name)}">${this.esc((f && f.label) || name)}</div>`, required: false };
    if (suffix === 'Footer' || suffix === 'Description') return { html: `<div class="dpNote" ${attr}>${this.esc((f && (f.footer || f.label)) || name)}</div>`, required: false };

    const field = f || { name, type: '', allowNulls: true, readOnly: false, rows: 1, itemsStyle: '', options: [] };
    if (field.itemsStyle === 'Grid') {
      return { html: `<div class="dpGrid" ${attr} title="${this.esc(name)}"><div class="dpGridBar">${this.esc(field.label || name)}</div><div class="dpGridBody">Lưới chi tiết${field.controller ? ' — controller ' + this.esc(field.controller) : ''}<br><small>(chỉ giữ chỗ, không mô phỏng cột)</small></div></div>`, required: false };
    }
    const cls = ['dpFC'];
    if (field.readOnly || field.external) cls.push('ro');
    if (!f) cls.push('unknown');
    const title = this.esc(name + (f ? '' : ' — chưa khai báo <field> trong file này'));
    return { html: `<div class="${cls.join(' ')}" ${attr} title="${title}">${this.controlInner(field, !!f)}</div>`, required: !field.allowNulls };
  }

  controlInner(f) {
    if (f.type === 'Boolean') return `<span class="dpChk"></span>`;
    if (f.rows > 1) return `<span class="dpBox multi" style="height:${f.rows * 17}px"></span>`;
    if (f.itemsStyle === 'DropDownList') return `<span class="dpBox">${this.esc(f.options[0] || '')}<b class="dpIco">▾</b></span>`;
    if (f.type === 'DateTime') return `<span class="dpBox"><em>dd/mm/yyyy</em><b class="dpIco cal">▦</b></span>`;
    if (f.itemsStyle === 'AutoComplete') return `<span class="dpBox"><b class="dpIco look">▤</b></span>`;
    if (f.type === 'Decimal' || f.itemsStyle === 'Numeric') return `<span class="dpBox num">0</span>`;
    return `<span class="dpBox"></span>`;
  }

  /// Bấm vào ô trên preview → chọn ký hiệu [field] tương ứng trong editor (dòng thiết kế đầu tiên có nó).
  revealField(name) {
    const model = this.bcode.currentModel;
    if (!model) return;
    const text = model.getValue();
    const viewsAt = text.search(/<views\b/i);
    const needle = '[' + name + ']';
    const at = text.indexOf(needle, Math.max(0, viewsAt));
    if (at < 0) return;
    const start = model.getPositionAt(at);
    const end = model.getPositionAt(at + needle.length);
    this.bcode.editor.setSelection(new monaco.Range(start.lineNumber, start.column, end.lineNumber, end.column));
    this.bcode.editor.revealLineInCenter(start.lineNumber);
    this.bcode.editor.focus();
  }
}
