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
    // Lưới chi tiết (controller Grid) đi cặp với Dir: đọc file Grid\<controller>.xml để vẽ cột thật.
    this.gridCache = new Map();  // đường dẫn -> { time, value }
    this.gridInfo = new Map();   // controller -> { columns, hidden } | null — chuẩn bị trước mỗi lần vẽ

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
    // Panel mở: dựng lại cả cây xem trước mỗi lần dừng gõ rất nặng — đi qua bộ lập lịch chung (chạy lúc trình duyệt rảnh, gõ tiếp thì dời), không chạy thẳng giữa lúc gõ.
    if (delay === 0 || !window.bcodeTyping) { this.timer = setTimeout(() => this.render(), delay); return; }
    window.bcodeTyping.request('dirpreview', () => this.render(), Math.max(delay, 500), 4000);
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
    if (!/<view\b[^>]*\bid\s*=\s*"(?:Dir|Grid)"/i.test(text)) {
      this.message('File này không có <view id="Dir"> hay <view id="Grid"> — xem trước chỉ hỗ trợ controller Dir và Grid.');
      return;
    }

    if (!(this._flashUntil > Date.now())) this.status.textContent = 'Đang dựng…';
    // Khai báo DOCTYPE đổi (hoặc bấm ⟳, hoặc cache quá 20s — file include có thể vừa sửa) thì bỏ cache.
    const head = (/<!DOCTYPE[sS]*?]>/i.exec(text) || [''])[0];
    if (force) this.gridCache = new Map();
    if (force || head !== this.entHead || Date.now() - this.entTime > 20000) {
      this.entCache = new Map();
      this.expMemo = new Map();
      this.gridCache = new Map();
      this.entHead = head;
      this.entTime = Date.now();
    }
    const ctx = { cache: this.entCache, unresolved: new Set() };
    let fieldsXml, viewsXml;
    try {
      const fieldsRaw = this.stripComments((/<fields\b[\s\S]*?<\/fields>/i.exec(text) || [''])[0]);
      const viewsRaw = this.stripComments((/<views\b[\s\S]*?<\/views>/i.exec(text) || [''])[0]);
      this._viewsRaw = viewsRaw;
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
    const views = Array.from(viewsDoc.doc.getElementsByTagName('view'));
    const view = views.find((v) => v.getAttribute('id') === 'Dir');
    const gridView = views.find((v) => v.getAttribute('id') === 'Grid');
    if (!view && !gridView) { this.message('Không đọc được <view id="Dir"> hay <view id="Grid">.'); return; }

    const title = (/<title\b[^>]*\bv="([^"]*)"/i.exec(text) || [])[1] || '';
    if (!view) {
      // File Grid (chi tiết): vẽ chính cái lưới của nó.
      this.gridEditable = true;
      this.drawGridFile({ title: this.cleanText(title), fields, view: gridView });
      this.wireGridDnD();
    } else {
      // Dir: nạp trước các lưới chi tiết nó nhúng (items style="Grid" controller="X") từ Grid\X.xml.
      this.rawMap = new Map();
      this.partialRaw = new Set();
      for (const m of String(this._viewsRaw || '').matchAll(/<item\b[^>]*?\bvalue="([^"]*)"/g)) {
        const rv = m[1];
        if (!rv.includes('&') || !/^\s*[01\-]+(?:&[A-Za-z_][\w.:$-]*;[01\-]*)*\s*:/.test(rv)) continue;
        // Entity ngoài [..] (trong mặt nạ, hoặc đuôi 1 biến) có thể thêm cột/biến lúc khai triển: dòng đó chỉ đổi chỗ
        // được các ô đứng trước phần entity (không đụng mặt nạ) — xem parseRows.
        const outside = /&[A-Za-z_][\w.:$-]*;/.test(rv.replace(/\[[^\]]*\]/g, ''));
        try {
          this.rawMap.set(await this.expand(rv, { cache: ctx.cache, unresolved: new Set() }), rv);
          if (outside) this.partialRaw.add(rv);
        } catch { /* bỏ qua dòng này */ }
      }
      this.gridInfo = new Map();
      this.gridEditable = false; // lưới nhúng trong Dir thuộc file khác — chỉ xem
      const controllers = [...new Set([...fields.values()].filter((f) => f.itemsStyle === 'Grid' && f.controller).map((f) => f.controller))];
      await Promise.all(controllers.map(async (c) => { this.gridInfo.set(c, await this.loadGrid(c)); }));
      if (run !== this.version) return;
      this.draw({ title: this.cleanText(title), fields, view });
    }
    // Giữ thông báo kéo-thả (flash) hiện đủ vài giây dù preview vừa vẽ lại.
    if (!(this._flashUntil > Date.now())) this.status.textContent = new Date().toLocaleTimeString();
    this.footer.textContent = ctx.unresolved.size
      ? `Entity chưa giải được (${ctx.unresolved.size}): ` + [...ctx.unresolved].slice(0, 12).map((n) => '&' + n + ';').join(' ') + (ctx.unresolved.size > 12 ? ' …' : '')
      : '';
  }

  // ---- Kéo-thả để đổi vị trí (sửa thẳng source, Ctrl+Z hoàn tác được) -----------------------------

  flash(text) {
    this.status.textContent = text;
    this._flashUntil = Date.now() + 4000;
    clearTimeout(this._flashTimer);
    this._flashTimer = setTimeout(() => { this.status.textContent = new Date().toLocaleTimeString(); }, 4000);
  }

  /// Dir — kéo-thả ô:
  ///   • kéo ô thả vào GIỮA ô đích      → đổi chỗ 2 ô (tráo)
  ///   • thả vào mép TRÁI/PHẢI ô đích   → CHÈN trước/sau ô đó, các ô khác bị đẩy nhường chỗ
  ///   • thả vào chỗ trống              → đặt ô tại cột đó (đẩy các ô phía sau nếu thiếu chỗ)
  ///   • Ctrl+bấm nhiều ô trong 1 dòng rồi kéo 1 ô đã chọn → dời CẢ NHÓM (giữ thứ tự và khoảng cách giữa các ô)
  wireDirDnD() {
    let drag = null; // { r, idxs: [chỉ số ô, tăng dần] }
    this.sel = new Set(); // "dòng:ô" của các ô đang chọn (xoá mỗi lần vẽ lại)

    const refreshSel = () => {
      this.root.querySelectorAll('td.dpTd').forEach((td) => td.classList.toggle('dpSel', this.sel.has(td.dataset.r + ':' + td.dataset.c)));
      if (this.sel.size) this.flash('Đã chọn ' + this.sel.size + ' ô — kéo 1 ô đã chọn để dời cả nhóm (Ctrl+bấm để chọn/bỏ chọn).');
    };

    this.root.querySelectorAll('td[data-r]').forEach((td) => {
      const isCell = td.classList.contains('dpTd');
      if (isCell) {
        // Pha capture: chặn onclick "chọn biến trong editor" của phần tử con khi đang Ctrl+bấm để chọn ô.
        td.addEventListener('click', (e) => {
          if (!(e.ctrlKey || e.metaKey)) return;
          e.preventDefault();
          e.stopPropagation();
          const key = td.dataset.r + ':' + td.dataset.c;
          // Chỉ chọn nhóm trong CÙNG 1 dòng: chọn ô ở dòng khác thì bắt đầu nhóm mới.
          if ([...this.sel].some((k) => k.split(':')[0] !== td.dataset.r)) this.sel.clear();
          if (this.sel.has(key)) this.sel.delete(key); else this.sel.add(key);
          refreshSel();
        }, true);

        td.addEventListener('dragstart', (e) => {
          const r = +td.dataset.r;
          const c = +td.dataset.c;
          const picked = [...this.sel].filter((k) => +k.split(':')[0] === r).map((k) => +k.split(':')[1]);
          const idxs = picked.includes(c) ? picked.sort((x, y) => x - y) : [c];
          drag = { r, idxs };
          e.dataTransfer.effectAllowed = 'move';
          e.dataTransfer.setData('text/plain', 'bcode-cell');
          this.root.querySelectorAll('td.dpTd').forEach((t) => { if (+t.dataset.r === r && idxs.includes(+t.dataset.c)) t.classList.add('dpDragging'); });
        });
        td.addEventListener('dragend', () => { drag = null; this.clearDropMarks(); });
      }
      td.addEventListener('dragover', (e) => {
        if (!drag && !this._extDrag) return;
        e.preventDefault();
        this.clearDropMarks(true);
        if (isCell) {
          const zone = this.zoneOf(td, e.clientX);
          const group = drag ? drag.idxs.length > 1 : true; // biến mới từ khay: chỉ chèn trước/sau, không tráo
          td.classList.add(zone === 'R' ? 'dpInsR' : (zone === 'L' || group) ? 'dpInsL' : 'dpDrop');
        } else td.classList.add('dpDrop');
      });
      td.addEventListener('dragleave', () => td.classList.remove('dpDrop', 'dpInsL', 'dpInsR'));
      td.addEventListener('drop', (e) => {
        e.preventDefault();
        const d = drag; drag = null;
        const ext = this._extDrag; this._extDrag = null;
        this.clearDropMarks();
        if (ext) this.applyInsertNew(ext, td, e.clientX);
        else if (d) this.applyDrop(d, td, e.clientX);
      });
    });
  }

  clearDropMarks(keepDragging) {
    this.root.querySelectorAll('.dpDrop, .dpDropL, .dpDropR, .dpInsL, .dpInsR' + (keepDragging ? '' : ', .dpDragging')).forEach((el) => el.classList.remove('dpDrop', 'dpDropL', 'dpDropR', 'dpInsL', 'dpInsR', 'dpDragging'));
  }

  /// Mép trái / mép phải / giữa ô → 'L' / 'R' / 'C'. Mép = 1/4 bề rộng nhưng tối đa 28px: ô dài (vd. thanh Người mua, Diễn
  /// giải ~800px) nếu để 1/4 thì mép rộng cả trăm px, thả "vào giữa để đổi chỗ" gần như không trúng.
  zoneOf(td, x) {
    const r = td.getBoundingClientRect();
    const edge = Math.min(r.width * 0.25, 28);
    const dx = x - r.left;
    return dx < edge ? 'L' : dx > r.width - edge ? 'R' : 'C';
  }

  /// Dựng lại value="…" của 1 dòng từ danh sách ô (mặt nạ 1/0/- + biến). Dòng có entity trong mặt nạ chỉ ghi lại danh sách biến.
  buildRowValue(X) {
    if (X.row.partial) {
      return X.row.rawPrefix + ' ' + X.row.rawTokens.map((t, i) => (X.cells[i] && X.cells[i].raw !== undefined ? X.cells[i].raw + X.row.rawTails[i] : t)).join(', ');
    }
    const cells = [...X.cells].sort((a, b) => a.start - b.start);
    const chars = Array(X.mask.length).fill('-');
    for (const c of cells) {
      chars[c.start] = '1';
      for (let k = c.start + 1; k <= c.end && k < chars.length; k++) chars[k] = '0';
    }
    // Hai ô chồng nhau → không ghi mặt nạ sai.
    for (let i = 1; i < cells.length; i++) if (cells[i].start <= cells[i - 1].end) return null;
    return chars.join('') + ': ' + cells.map((c) => c.raw).join(', ');
  }

  applyDrop(d, td, clientX) {
    const src = this.rowById.get(d.r);
    const dst = this.rowById.get(+td.dataset.r);
    if (!src || !dst) return;
    const isCellTarget = td.classList.contains('dpTd');
    const zone = isCellTarget ? this.zoneOf(td, clientX) : 'gap';
    const group = d.idxs.length > 1;

    // 1) Tráo 2 ô: kéo 1 ô thả vào GIỮA ô khác.
    if (!group && isCellTarget && zone === 'C') {
      const srcCell = src.cells[d.idxs[0]];
      const dstCell = dst.cells[+td.dataset.c];
      if (!srcCell || !dstCell || (dst === src && dstCell.idx === srcCell.idx)) return;
      if (!srcCell.swappable || !dstCell.swappable) { this.flash('Ô này mang entity (&...;) nên chưa kéo được — sửa tay trong source.'); return; }
      const A = { row: src, mask: src.mask, cells: src.cells.map((c) => ({ ...c })) };
      const B = dst === src ? A : { row: dst, mask: dst.mask, cells: dst.cells.map((c) => ({ ...c })) };
      const x = A.cells[srcCell.idx];
      const y = B.cells[dstCell.idx];
      [x.token, y.token] = [y.token, x.token];
      [x.raw, y.raw] = [y.raw, x.raw];
      const edits = [];
      const vA = this.buildRowValue(A);
      if (vA == null) { this.flash('Vị trí đích không đủ chỗ.'); return; }
      edits.push({ row: src, value: vA });
      if (B !== A) {
        const vB = this.buildRowValue(B);
        if (vB == null) { this.flash('Vị trí đích không đủ chỗ.'); return; }
        edits.push({ row: dst, value: vB });
      }
      if (this.applyRowEdits(edits)) this.flash('Đã đổi chỗ 2 ô — Ctrl+Z để hoàn tác.');
      return;
    }

    // 2) Chèn / dời (1 ô hoặc cả nhóm): cần sửa mặt nạ nên cả 2 dòng phải "thuần".
    if (!src.editable || !dst.editable) {
      this.flash('Dòng này có entity (&...;) trong mặt nạ nên chỉ đổi chỗ (thả vào giữa ô) được, chưa chèn/dời được.');
      return;
    }
    let c0;
    if (isCellTarget) {
      const dc = dst.cells[+td.dataset.c];
      if (!dc) return;
      if (dst === src && d.idxs.includes(dc.idx)) return; // thả lên chính ô đang kéo
      c0 = zone === 'R' ? dc.end + 1 : dc.start;
    } else {
      // Thả lên chỗ trống: cột đích tính theo vị trí chuột.
      const gs = +td.dataset.gs;
      const ge = +td.dataset.ge;
      c0 = gs;
      const ths = td.closest('table').querySelectorAll('tr.dpWidthRow th');
      for (let i = gs; i <= ge && i < ths.length; i++) {
        if (clientX >= ths[i].getBoundingClientRect().left) c0 = i;
      }
    }
    this.applyMove(src, d.idxs, dst, c0);
  }

  /// Đặt khối ô (đã có start/end tuyệt đối) vào dòng B tại cột c0; các ô của B từ c0 trở đi bị đẩy sang phải vừa đủ.
  /// Trả về chuỗi lỗi, hoặc null nếu xong.
  placeBlock(B, block, c0) {
    const n = B.mask.length;
    this._lastShrunk = 0;
    if (c0 < 0 || c0 >= n) return 'Không đủ chỗ ở dòng đích (hết cột).';
    const before = B.cells.filter((c) => c.end < c0);
    const after = B.cells.filter((c) => c.end >= c0).sort((x, y) => x.start - y.start);
    if (after.some((c) => c.start < c0)) return 'Vị trí đích nằm giữa 1 ô — thả vào mép ô hoặc chỗ trống.';

    // Độ dài (số cột - 1) hiện tại của từng ô, tính lại vị trí mỗi lần thử. Khoảng trống giữa các ô trong khối được giữ nguyên.
    const blk = block.map((c, i) => ({ c, len: c.end - c.start, gap: i === 0 ? 0 : c.start - block[i - 1].end - 1 }));
    const aft = after.map((c) => ({ c, start0: c.start, len: c.end - c.start }));
    const layout = () => {
      let prev = c0 - 1;
      for (const x of blk) {
        x.c.start = prev + 1 + x.gap;
        x.c.end = x.c.start + x.len;
        prev = x.c.end;
      }
      if (prev >= n) return false;
      for (const x of aft) {
        x.c.start = Math.max(prev + 1, x.start0);
        x.c.end = x.c.start + x.len;
        if (x.c.end >= n) return false;
        prev = x.c.end;
      }
      return true;
    };
    // Hết cột thì lần lượt bớt 1 cột của ô đang rộng nhất (ô dài như thanh Người mua/Diễn giải nhường chỗ trước) tới khi vừa.
    while (!layout()) {
      const cand = [...aft, ...blk].filter((x) => x.len > 0).sort((x, y) => y.len - x.len)[0];
      if (!cand) return 'Không đủ cột cho dòng đích: mỗi ô đã chỉ còn 1 cột mà vẫn không vừa.';
      cand.len--;
      this._lastShrunk++;
    }
    B.cells = [...before, ...block, ...after];
    return null;
  }

  shrinkNote() {
    return this._lastShrunk ? ' (đã thu hẹp ' + this._lastShrunk + ' cột của các ô dài để lấy chỗ)' : '';
  }

  // ---- Biến khai sẵn trong <fields> nhưng CHƯA có trong dòng thiết kế nào: kéo vào form để thêm ------------

  /// Khay "Biến chưa dùng" dưới form + vùng thả "tạo dòng mới". Kéo 1 biến thả vào mép ô / chỗ trống như kéo ô thường.
  addPalette(fields) {
    // Biến (ô nhập) và NHÃN ([x].Label) được theo dõi riêng: có biến mà chưa có nhãn thì khay đưa chip nhãn để kéo thêm.
    const usedCtl = new Set();
    const usedLbl = new Set();
    for (const r of this.rowById.values()) {
      for (const c of r.cells) {
        const m = /^\[([^\]]+)\](\.Label)?$/.exec(c.token);
        if (m) (m[2] ? usedLbl : usedCtl).add(m[1]);
      }
    }
    const free = [];
    for (const f of fields.values()) {
      if (f.hidden || f.itemsStyle === 'Grid') continue;
      if (!usedCtl.has(f.name)) free.push({ f, kind: 'ctl' });
      if (!/%l$/.test(f.name) && f.label && !usedLbl.has(f.name)) free.push({ f, kind: 'lbl' });
    }

    const win = this.root.querySelector('.dpWindow');
    if (!win) return;
    if (this.palOpen === undefined) this.palOpen = false;
    if (this.palLabel === undefined) this.palLabel = true;
    if (this.palSize === undefined) this.palSize = '0'; // '0' = tự động (~90px), số = px, 'rest' = đến ô kế tiếp
    const sizes = [['0', 'Tự động (~90px)'], ['40', 'Rất ngắn (~40px)'], ['70', 'Ngắn (~70px)'], ['100', 'Vừa (~100px)'], ['150', '~150px'], ['250', 'Dài (~250px)'], ['400', 'Rất dài (~400px)'], ['rest', 'Đến ô kế tiếp']];

    const box = document.createElement('div');
    box.className = 'dpPalette';
    box.innerHTML = '<div class="dpPalHead"><span class="dpPalToggle">' + (this.palOpen ? '▾' : '▸') + ' Biến / nhãn chưa dùng (' + free.length + ')</span>' +
      '<label class="dpPalOpt" title="Độ dài ô nhập của biến sẽ chèn">độ dài <select class="dpPalSize">' +
      sizes.map(([v, t]) => '<option value="' + v + '"' + (String(this.palSize) === v ? ' selected' : '') + '>' + t + '</option>').join('') + '</select></label>' +
      '<label class="dpPalOpt"><input type="checkbox" class="dpPalLabel"' + (this.palLabel ? ' checked' : '') + '> kèm nhãn</label>' +
      '<input class="dpPalFilter" placeholder="lọc…"></div>' +
      '<div class="dpPalBody" style="display:' + (this.palOpen ? 'block' : 'none') + '">' +
      (free.length ? free.map(({ f, kind }) => kind === 'lbl'
        ? '<span class="dpChip lbl" draggable="true" data-kind="lbl" data-name="' + this.esc(f.name) + '" data-text="' + this.esc(f.label) + '" title="Nhãn (tiếng Việt) của ' + this.esc(f.name) + '">Aa ' + this.esc(f.label) + '</span>'
        : '<span class="dpChip" draggable="true" data-kind="ctl" data-name="' + this.esc(f.name) + '" data-text="' + this.esc(f.label || '') + '" title="' + this.esc(f.label || f.name) + '">' + this.esc(f.name) + '</span>').join('')
        : '<span class="dpPalEmpty">Mọi biến và nhãn khai trong &lt;fields&gt; đã có trong form.</span>') + '</div>';
    this.root.appendChild(box);

    box.querySelector('.dpPalToggle').onclick = () => {
      this.palOpen = !this.palOpen;
      box.querySelector('.dpPalBody').style.display = this.palOpen ? 'block' : 'none';
      box.querySelector('.dpPalToggle').textContent = (this.palOpen ? '▾' : '▸') + ' Biến / nhãn chưa dùng (' + free.length + ')';
    };
    box.querySelector('.dpPalLabel').onchange = (e) => { this.palLabel = e.target.checked; };
    box.querySelector('.dpPalSize').onchange = (e) => { this.palSize = e.target.value; };
    box.querySelector('.dpPalFilter').oninput = (e) => {
      const q = e.target.value.trim().toLowerCase();
      box.querySelectorAll('.dpChip').forEach((c) => { c.style.display = !q || (c.dataset.name + ' ' + (c.dataset.text || '')).toLowerCase().includes(q) ? '' : 'none'; });
    };
    box.querySelectorAll('.dpChip').forEach((chip) => {
      chip.addEventListener('dragstart', (e) => {
        const kind = chip.dataset.kind;
        // Chip biến: kèm nhãn (nếu bật và nhãn chưa dùng ở đâu); chip nhãn: chỉ chèn nhãn.
        this._extDrag = { name: chip.dataset.name, kind, withLabel: kind === 'ctl' && this.palLabel && !usedLbl.has(chip.dataset.name), size: this.palSize };
        e.dataTransfer.effectAllowed = 'copy';
        e.dataTransfer.setData('text/plain', 'bcode-field');
      });
      chip.addEventListener('dragend', () => { this._extDrag = null; this.clearDropMarks(); this.root.querySelectorAll('.dpNewRow').forEach((n) => n.classList.remove('dpDrop')); });
    });

    // Vùng thả "tạo dòng mới" ngay dưới bảng của form chính.
    const area = win.querySelector('.dpFormArea');
    const hasMain = [...this.rowById.values()].some((r) => r.cat == null && r.editable);
    if (area && hasMain && this.rowById.size) {
      const nr = document.createElement('div');
      nr.className = 'dpNewRow';
      nr.textContent = '＋ thả biến vào đây để tạo dòng mới';
      area.appendChild(nr);
      nr.addEventListener('dragover', (e) => { if (!this._extDrag) return; e.preventDefault(); nr.classList.add('dpDrop'); });
      nr.addEventListener('dragleave', () => nr.classList.remove('dpDrop'));
      nr.addEventListener('drop', (e) => {
        e.preventDefault();
        nr.classList.remove('dpDrop');
        const ext = this._extDrag; this._extDrag = null;
        if (ext) this.applyNewRow(ext);
      });
    }
  }

  /// Mặt nạ của dòng, đệm '-' cho đủ số cột của dòng bề rộng (mặt nạ trong source có thể ngắn hơn số cột — các cột cuối bỏ trống).
  paddedMask(row) {
    return row.mask.padEnd(Math.max(row.mask.length, (row.widths || []).length), '-');
  }

  /// Khối ô cho 1 biến mới (nhãn + ô nhập hoặc chỉ ô nhập), độ rộng ô lấy theo dòng bề rộng của dòng đích.
  newBlock(ext, widths, c0, n, nextStart) {
    const spanEnd = (start, minPx) => {
      let w = 0;
      let e = start;
      while (e < n) { w += widths[e] || 0; if (w >= minPx) break; e++; }
      return Math.min(e, n - 1);
    };
    if (ext.kind === 'lbl') {
      const end = spanEnd(c0, 60);
      return [{ token: '[' + ext.name + '].Label', raw: '[' + ext.name + '].Label', swappable: true, start: c0, end }];
    }
    const out = [];
    let cur = c0;
    if (ext.withLabel && !/%l$/.test(ext.name)) {
      const end = spanEnd(cur, 60);
      out.push({ token: '[' + ext.name + '].Label', raw: '[' + ext.name + '].Label', swappable: true, start: cur, end });
      cur = end + 1;
    }
    // Hết cột sau nhãn (vd. vùng cuối toàn cột 0px): bỏ nhãn, chỉ chèn ô nhập.
    if (cur >= n && out.length) { cur = c0; out.length = 0; }
    if (cur >= n) return null;
    // Độ dài ô nhập: số px chọn trong khay (cộng dồn bề rộng các cột từ cột bắt đầu), hoặc 'rest' = tới trước ô kế tiếp.
    const size = ext.size === 'rest' ? 'rest' : (parseInt(ext.size, 10) || 90);
    const end = size === 'rest' ? Math.max(cur, (nextStart == null ? n : nextStart) - 1) : spanEnd(cur, size);
    out.push({ token: '[' + ext.name + ']', raw: '[' + ext.name + ']', swappable: true, start: cur, end });
    return out;
  }

  /// Ô/chỗ trống được thả lên → { dst: dòng, c0: cột bắt đầu đặt }.
  dropTarget(td, clientX) {
    const dst = this.rowById.get(+td.dataset.r);
    if (!dst) return null;
    if (td.classList.contains('dpTd')) {
      const dc = dst.cells[+td.dataset.c];
      if (!dc) return null;
      return { dst, c0: this.zoneOf(td, clientX) === 'R' ? dc.end + 1 : dc.start };
    }
    const gs = +td.dataset.gs;
    const ge = +td.dataset.ge;
    let c0 = gs;
    const ths = td.closest('table').querySelectorAll('tr.dpWidthRow th');
    for (let i = gs; i <= ge && i < ths.length; i++) if (clientX >= ths[i].getBoundingClientRect().left) c0 = i;
    return { dst, c0 };
  }

  applyInsertNew(ext, td, clientX) {
    const t = this.dropTarget(td, clientX);
    if (!t) return;
    const { dst, c0 } = t;
    if (!dst.editable) { this.flash('Dòng này có entity (&...;) trong mặt nạ nên chưa chèn biến mới vào được — sửa tay trong source.'); return; }
    const B = { row: dst, mask: this.paddedMask(dst), cells: dst.cells.map((c) => ({ ...c })) };
    const later = dst.cells.filter((c) => c.start >= c0).map((c) => c.start).sort((a, b) => a - b);
    const block = this.newBlock(ext, dst.widths, c0, B.mask.length, later.length ? later[0] : B.mask.length);
    if (!block) { this.flash('Không đủ chỗ ở dòng đích (hết cột).'); return; }
    const err = this.placeBlock(B, block, c0);
    if (err) { this.flash(err); return; }
    const v = this.buildRowValue(B);
    if (v == null) { this.flash('Vị trí đích không đủ chỗ.'); return; }
    if (this.applyRowEdits([{ row: dst, value: v }])) this.flash('Đã thêm ' + (ext.kind === 'lbl' ? 'nhãn của ' : '') + '[' + ext.name + '] vào form' + this.shrinkNote() + ' — Ctrl+Z để hoàn tác.');
  }

  /// Tạo 1 dòng thiết kế mới (cuối form chính) chỉ chứa biến vừa kéo.
  applyNewRow(ext) {
    const model = this.bcode.currentModel;
    const main = [...this.rowById.values()].filter((r) => r.cat == null && r.editable);
    const ref = main[main.length - 1];
    if (!model || !ref) { this.flash('Không có dòng nào làm mốc để thêm dòng mới.'); return; }
    const n = this.paddedMask(ref).length;
    const block = this.newBlock(ext, ref.widths, 0, n);
    if (!block) { this.flash('Không đủ cột để tạo dòng mới.'); return; }
    const value = this.buildRowValue({ row: { partial: false }, mask: '-'.repeat(n), cells: block });
    if (value == null) return;

    const text = model.getValue();
    const viewStart = text.search(/<view\b[^>]*\bid\s*=\s*"Dir"[^>]*>/i);
    const viewEnd = viewStart < 0 ? -1 : text.indexOf('</view>', viewStart);
    if (viewStart < 0) { this.flash('Không tìm thấy <view id="Dir"> trong source.'); return; }
    const line = this.locateRowLine(text, viewStart, viewEnd < 0 ? text.length : viewEnd, ref);
    if (!line) { this.flash('Không xác định được dòng mốc trong source — chưa tạo dòng mới.'); return; }
    const eol = text.includes('\r\n') ? '\r\n' : '\n';
    const indent = (/^[ \t]*/.exec(line.text) || [''])[0];
    const insert = (/\n$/.test(line.text) ? '' : eol) + indent + '<item value="' + value + '"/>' + eol;
    const pos = model.getPositionAt(line.to);
    const editor = this.bcode.editor;
    editor.pushUndoStop();
    editor.executeEdits('dirpreview-dnd', [{ range: new monaco.Range(pos.lineNumber, pos.column, pos.lineNumber, pos.column), text: insert }]);
    editor.pushUndoStop();
    this.flash('Đã tạo dòng mới cho [' + ext.name + '] — Ctrl+Z để hoàn tác.');
  }

  /// Bỏ các ô idxs khỏi dòng nguồn rồi đặt cả khối (giữ thứ tự và khoảng cách giữa các ô) vào dòng đích tại cột c0;
  /// các ô của dòng đích nằm từ c0 trở đi bị đẩy sang phải vừa đủ (tận dụng chỗ trống '-' sẵn có), không đủ chỗ thì huỷ.
  applyMove(src, idxs, dst, c0) {
    const clone = (row) => ({ row, mask: this.paddedMask(row), cells: row.cells.map((c) => ({ ...c })) });
    const A = clone(src);
    const B = dst === src ? A : clone(dst);
    const moving = A.cells.filter((c) => idxs.includes(c.idx)).sort((x, y) => x.start - y.start);
    if (!moving.length) return;
    if (dst !== src && moving.length === A.cells.length) { this.flash('Không thể chuyển hết ô ra khỏi dòng (dòng sẽ trống).'); return; }
    A.cells = A.cells.filter((c) => !idxs.includes(c.idx));
    if (B === A) B.cells = A.cells;

    const first = moving[0].start;
    const block = moving.map((c) => ({ token: c.token, raw: c.raw, swappable: true, start: c0 + (c.start - first), end: c0 + (c.end - first) }));
    const err = this.placeBlock(B, block, c0);
    if (err) { this.flash(err); return; }
    if (B === A) A.cells = B.cells;

    const edits = [];
    const vA = this.buildRowValue(A);
    if (vA == null) { this.flash('Vị trí đích không đủ chỗ.'); return; }
    edits.push({ row: src, value: vA });
    if (B !== A) {
      const vB = this.buildRowValue(B);
      if (vB == null) { this.flash('Vị trí đích không đủ chỗ.'); return; }
      edits.push({ row: dst, value: vB });
    }
    if (this.applyRowEdits(edits)) this.flash((idxs.length > 1 ? 'Đã dời cả nhóm ' + idxs.length + ' ô' : 'Đã chèn ô') + this.shrinkNote() + ' — Ctrl+Z để hoàn tác.');
  }

  /// Kéo tay nắm ⋮⋮ của 1 dòng thả lên dòng khác: nửa trên = chèn TRƯỚC dòng đó, nửa dưới = chèn SAU. Chuyển nguyên
  /// dòng <item value="…"/> (nhãn + mọi biến) trong source — dùng khi muốn đổi chỗ cả nhóm biến, vd đưa dòng Diễn giải
  /// lên trên dòng Mã thanh toán.
  wireRowDnD() {
    let dragRow = null;
    this.root.querySelectorAll('.dpGrip').forEach((g) => {
      g.addEventListener('dragstart', (e) => {
        dragRow = +g.dataset.grip;
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', 'bcode-row');
        e.stopPropagation();
        const tr = g.closest('tr');
        if (tr) tr.classList.add('dpDragging');
      });
      g.addEventListener('dragend', () => { dragRow = null; this.clearRowMarks(); });
    });
    this.root.querySelectorAll('tr[data-rid]').forEach((tr) => {
      tr.addEventListener('dragover', (e) => {
        if (dragRow == null) return;
        e.preventDefault();
        const r = tr.getBoundingClientRect();
        const after = e.clientY >= r.top + r.height / 2;
        this.clearRowMarks(true);
        tr.classList.add(after ? 'dpRowAfter' : 'dpRowBefore');
      });
      tr.addEventListener('drop', (e) => {
        if (dragRow == null) return;
        e.preventDefault();
        const r = tr.getBoundingClientRect();
        const after = e.clientY >= r.top + r.height / 2;
        const src = dragRow; dragRow = null; this.clearRowMarks();
        if (src !== +tr.dataset.rid) this.applyRowMove(src, +tr.dataset.rid, after);
      });
    });
  }

  clearRowMarks(keepDragging) {
    this.root.querySelectorAll('.dpRowBefore, .dpRowAfter' + (keepDragging ? '' : ', tr.dpDragging')).forEach((el) => el.classList.remove('dpRowBefore', 'dpRowAfter', 'dpDragging'));
  }

  /// Vị trí (offset) dòng <item …value="RAW"…/> của 1 dòng thiết kế trong source: { from, to } theo ĐẦU DÒNG → hết dòng.
  locateRowLine(text, viewStart, region, row) {
    let k = 0;
    for (const r of this.rowById.values()) if (r.id < row.id && r.value === row.value) k++;
    const needle = 'value="' + row.value + '"';
    let at = viewStart - 1;
    for (let i = 0; i <= k; i++) {
      at = text.indexOf(needle, at + 1);
      if (at < 0 || at > region) return null;
    }
    const from = text.lastIndexOf('\n', at) + 1;
    let to = text.indexOf('\n', at);
    to = to < 0 ? text.length : to + 1;
    if (!/^\s*<item\b[^>]*\/>\s*$/.test(text.slice(from, to))) return null; // không nằm riêng 1 dòng
    return { from, to, text: text.slice(from, to) };
  }

  applyRowMove(srcId, dstId, after) {
    const model = this.bcode.currentModel;
    const src = this.rowById.get(srcId);
    const dst = this.rowById.get(dstId);
    if (!model || !src || !dst) return;
    const text = model.getValue();
    const viewStart = text.search(/<view\b[^>]*\bid\s*=\s*"Dir"[^>]*>/i);
    if (viewStart < 0) { this.flash('Không tìm thấy <view id="Dir"> trong source.'); return; }
    const viewEnd = text.indexOf('</view>', viewStart);
    const region = viewEnd < 0 ? text.length : viewEnd;
    const a = this.locateRowLine(text, viewStart, region, src);
    const b = this.locateRowLine(text, viewStart, region, dst);
    if (!a || !b) { this.flash('Không xác định được dòng trong source (do entity hoặc không nằm riêng 1 dòng) — chưa chuyển.'); return; }

    const at = after ? b.to : b.from;
    if (at >= a.from && at <= a.to) return; // thả ngay chỗ cũ
    const eol = text.includes('\r\n') ? '\r\n' : '\n';
    let moved = a.text;
    if (!/\n$/.test(moved)) moved += eol;
    let insert = moved;
    if (after && !/\n$/.test(b.text)) insert = eol + moved.replace(/\r?\n$/, '');
    const pos = (o) => model.getPositionAt(o);
    const editor = this.bcode.editor;
    editor.pushUndoStop();
    editor.executeEdits('dirpreview-dnd', [
      { range: new monaco.Range(pos(a.from).lineNumber, pos(a.from).column, pos(a.to).lineNumber, pos(a.to).column), text: '' },
      { range: new monaco.Range(pos(at).lineNumber, pos(at).column, pos(at).lineNumber, pos(at).column), text: insert },
    ]);
    editor.pushUndoStop();
    this.flash('Đã chuyển cả dòng — Ctrl+Z để hoàn tác.');
  }

  /// Thay value="…" của các dòng thiết kế trong source (cùng 1 bước undo).
  applyRowEdits(edits) {
    const model = this.bcode.currentModel;
    if (!model) return false;
    const text = model.getValue();
    const viewStart = text.search(/<view\b[^>]*\bid\s*=\s*"Dir"[^>]*>/i);
    if (viewStart < 0) { this.flash('Không tìm thấy <view id="Dir"> trong source.'); return false; }
    const viewEnd = text.indexOf('</view>', viewStart);
    const region = viewEnd < 0 ? text.length : viewEnd;

    const monacoEdits = [];
    for (const { row, value } of edits) {
      // Dòng thứ k trong số các dòng thuần có cùng giá trị → lần xuất hiện thứ k của value="…" trong <view>.
      let k = 0;
      for (const r of this.rowById.values()) if (r.id < row.id && (r.editable || r.partial) && r.value === row.value) k++;
      const needle = 'value="' + row.value + '"';
      let at = viewStart - 1;
      for (let i = 0; i <= k; i++) {
        at = text.indexOf(needle, at + 1);
        if (at < 0 || at > region) { this.flash('Không tìm thấy dòng trong source (có thể do entity) — chưa đổi.'); return false; }
      }
      const from = model.getPositionAt(at + 7);
      const to = model.getPositionAt(at + 7 + row.value.length);
      monacoEdits.push({ range: new monaco.Range(from.lineNumber, from.column, to.lineNumber, to.column), text: value });
    }
    const editor = this.bcode.editor;
    editor.pushUndoStop();
    editor.executeEdits('dirpreview-dnd', monacoEdits);
    editor.pushUndoStop();
    return true;
  }

  /// Grid: kéo tiêu đề cột thả lên cột khác (nửa trái = chèn trước, nửa phải = chèn sau) → đổi thứ tự dòng <field name="…"/>.
  wireGridDnD() {
    let drag = null;
    this.root.querySelectorAll('th[data-gcol]').forEach((th) => {
      th.addEventListener('dragstart', (e) => {
        drag = th.dataset.gcol;
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', 'bcode-col');
        th.classList.add('dpDragging');
      });
      th.addEventListener('dragend', () => { drag = null; this.clearDropMarks(); });
      th.addEventListener('dragover', (e) => {
        if (!drag) return;
        e.preventDefault();
        const r = th.getBoundingClientRect();
        const left = e.clientX < r.left + r.width / 2;
        th.classList.toggle('dpDropL', left);
        th.classList.toggle('dpDropR', !left);
      });
      th.addEventListener('dragleave', () => th.classList.remove('dpDropL', 'dpDropR'));
      th.addEventListener('drop', (e) => {
        e.preventDefault();
        const r = th.getBoundingClientRect();
        const after = e.clientX >= r.left + r.width / 2;
        const src = drag; drag = null; this.clearDropMarks();
        if (src && src !== th.dataset.gcol) this.applyGridMove(src, th.dataset.gcol, after);
      });
    });
  }

  applyGridMove(srcName, dstName, after) {
    const model = this.bcode.currentModel;
    if (!model) return;
    const text = model.getValue();
    const vs = text.search(/<view\b[^>]*\bid\s*=\s*"Grid"[^>]*>/i);
    const ve = vs < 0 ? -1 : text.indexOf('</view>', vs);
    if (vs < 0 || ve < 0) { this.flash('Không tìm thấy <view id="Grid"> trong source.'); return; }
    const esc = (n) => n.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const lineOf = (name) => {
      const re = new RegExp('^[ \\t]*<field\\s+name="' + esc(name) + '"\\s*/>[ \\t]*(?:\\r?\\n|$)', 'm');
      const m = re.exec(text.slice(vs, ve));
      return m ? { from: vs + m.index, to: vs + m.index + m[0].length, text: m[0] } : null;
    };
    const a = lineOf(srcName);
    const b = lineOf(dstName);
    if (!a || !b) { this.flash('Cột này sinh từ entity (&...;) hoặc không nằm riêng 1 dòng — chưa kéo được, sửa tay trong source.'); return; }

    const eol = text.includes('\r\n') ? '\r\n' : '\n';
    let moved = a.text;
    if (!/\n$/.test(moved)) moved += eol;
    const at = after ? b.to : b.from;
    if (at >= a.from && at <= a.to) return; // thả ngay chỗ cũ
    const pos = (o) => model.getPositionAt(o);
    const edits = [
      { range: new monaco.Range(pos(a.from).lineNumber, pos(a.from).column, pos(a.to).lineNumber, pos(a.to).column), text: '' },
      { range: new monaco.Range(pos(at).lineNumber, pos(at).column, pos(at).lineNumber, pos(at).column), text: (after && !/\n$/.test(b.text)) ? eol + moved.replace(/(\r?\n)$/, '') : moved },
    ];
    const editor = this.bcode.editor;
    editor.pushUndoStop();
    editor.executeEdits('dirpreview-dnd', edits);
    editor.pushUndoStop();
    this.flash('Đã đổi thứ tự cột — Ctrl+Z để hoàn tác.');
  }

  // ---- Lưới chi tiết (controller Grid) ------------------------------------------------------

  /// Đọc Grid\<controller>.xml cạnh thư mục Dir đang mở và rút ra danh sách cột. null nếu không đọc được.
  async loadGrid(controller) {
    const activePath = this.bcode.activePath;
    if (!activePath) return null;
    const path = resolvePath(dirNameOf(activePath), '..\\Grid\\' + controller + '.xml');
    const key = path.toLowerCase();
    const hit = this.gridCache.get(key);
    if (hit && Date.now() - hit.time < 20000) return hit.value;

    let value = null;
    try {
      const raw = String(await window.bcodeHost.call('BeginReadFile', path) || '').replace(/^\uFEFF/, '');
      if (raw) value = await this.parseGridFile(path, raw);
    } catch { value = null; }
    this.gridCache.set(key, { time: Date.now(), value });
    return value;
  }

  async parseGridFile(path, text) {
    // Entity của file Grid tìm theo chuỗi include của chính nó, và có bộ nhớ riêng (tên entity có thể trùng với Dir).
    const ctx = { cache: new Map(), unresolved: new Set(), docPath: path, docText: text };
    const fieldsRaw = this.stripComments((/<fields\b[\s\S]*?<\/fields>/i.exec(text) || [''])[0]);
    const viewsRaw = this.stripComments((/<views\b[\s\S]*?<\/views>/i.exec(text) || [''])[0]);
    const [fieldsXml, viewsXml] = await Promise.all([this.expand(fieldsRaw, ctx), this.expand(viewsRaw, ctx)]);
    const fieldsDoc = this.parseXml(fieldsXml);
    const viewsDoc = this.parseXml(viewsXml);
    if (fieldsDoc.error || viewsDoc.error) return null;
    const fields = this.collectFields(fieldsDoc.doc);
    const view = Array.from(viewsDoc.doc.getElementsByTagName('view')).find((v) => v.getAttribute('id') === 'Grid');
    if (!view) return null;
    return Object.assign(this.gridColumns(view, fields), { unresolved: ctx.unresolved.size });
  }

  /// Cột theo đúng thứ tự khai trong <view id="Grid">. Cột ẩn (hidden="true" hoặc width="0") không hiện trên lưới thật.
  gridColumns(view, fields) {
    const columns = [];
    let hidden = 0;
    for (const el of Array.from(view.children)) {
      if (el.tagName !== 'field') continue;
      const name = el.getAttribute('name');
      if (!name) continue;
      const f = fields.get(name);
      if (f && (f.hidden || f.width === 0)) { hidden++; continue; }
      columns.push({
        name,
        label: (f && f.label) || name,
        width: f && f.width ? Math.max(30, f.width) : 80,
        numeric: !!f && (f.type === 'Decimal' || f.itemsStyle === 'Numeric'),
      });
    }
    return { columns, hidden };
  }

  gridHtml(info, caption, attr, title) {
    const total = 30 + info.columns.reduce((a, c) => a + c.width, 0);
    const colgroup = '<col style="width:30px">' + info.columns.map((c) => '<col style="width:' + c.width + 'px">').join('');
    const dragAttr = (c) => (this.gridEditable ? ' draggable="true" data-gcol="' + this.esc(c.name) + '"' : '');
    const head = '<th class="dpGi"></th>' + info.columns.map((c) => '<th title="' + this.esc(c.name) + '"' + dragAttr(c) + '>' + this.esc(c.label) + '</th>').join('');
    const rows = [1, 2, 3, 4].map((n) =>
      '<tr><td class="dpGi">' + n + '</td>' + info.columns.map((c) => '<td' + (c.numeric ? ' class="num"' : '') + '></td>').join('') + '</tr>').join('');
    const note = info.hidden ? '<div class="dpGridNote">' + info.hidden + ' cột ẩn không hiển thị</div>' : '';
    return '<div class="dpGrid dpGridReal" ' + (attr || '') + ' title="' + this.esc(title || '') + '">' +
      '<div class="dpGridBar">' + this.esc(caption) + '</div>' +
      '<div class="dpGridScroll"><table class="dpGridTbl" style="width:' + total + 'px"><colgroup>' + colgroup + '</colgroup>' +
      '<thead><tr>' + head + '</tr></thead><tbody>' + rows + '</tbody></table></div>' + note + '</div>';
  }

  /// File đang mở là controller Grid (chi tiết): vẽ riêng cái lưới.
  drawGridFile({ title, fields, view }) {
    const info = this.gridColumns(view, fields);
    const base = String(this.bcode.activePath || '').split(/[\\/]/).pop() || 'Grid';
    this.root.innerHTML = '<div class="dpWindow"><div class="dpTitleBar">' + this.esc(title ? 'Lưới chi tiết — ' + title : 'Lưới chi tiết — ' + base) + '</div>' +
      '<div class="dpFormArea">' + this.gridHtml(info, base.replace(/\.xml$/i, ''), '', base) + '</div></div>';
  }

  message(text, isError = false) {
    this.root.innerHTML = `<div class="dpMessage${isError ? ' err' : ''}">${this.esc(text)}</div>`;
    this.status.textContent = '';
    this.footer.textContent = '';
  }

  esc(s) { return String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }

  cleanText(s) { return String(s || '').replace(/<[^>]*>/g, '').replace(/&nbsp;/g, ' ').trim(); }

  // Comment bỏ hẳn: code bị comment (kể cả entity/item trong đó) không được vẽ, và FBO chấp nhận '--' trong
  // comment còn XML chuẩn thì không → để lại sẽ báo "Double hyphen within comment".
  stripComments(s) { return String(s).replace(/<!--[\s\S]*?-->/g, ''); }

  parseXml(xml) {
    const doc = new DOMParser().parseFromString('<r>' + this.stripComments(xml) + '</r>', 'text/xml');
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
      const found = window.bcodeEntity
        ? (ctx.docPath ? await window.bcodeEntity.resolveTop(name, ctx.docPath, ctx.docText, 'general') : await window.bcodeEntity.resolveTopActiveGeneral(name))
        : null;
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
        width: f.hasAttribute('width') ? parseInt(f.getAttribute('width'), 10) : null,
        hidden: f.getAttribute('hidden') === 'true',
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
    this.rowById = new Map(); // id dòng thiết kế -> dòng; dùng cho kéo-thả (applyDrop)
    let rowSeq = 0;
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
        cells.push({ token, start, end, idx: cells.length });
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
      row.id = rowSeq++;
      // Giá trị GỐC trong source (còn nguyên [&Entity;]) — đó mới là thứ cần tìm và ghi lại.
      const rawValue = (this.rawMap && this.rawMap.get(v)) || v;
      row.value = rawValue;
      const colon = rawValue.indexOf(':');
      const rawTokens = rawValue.slice(colon + 1).split(',').map((t) => t.trim()).filter(Boolean);
      const specialFree = !/[&"<>]/.test(rawValue.replace(/&[A-Za-z_][\w.:$-]*;/g, ''));
      row.partial = !!(this.partialRaw && this.partialRaw.has(rawValue));
      if (row.partial) {
        // Dòng có entity ngoài [..]: giữ nguyên phần mặt nạ gốc (có thể chứa entity), chỉ tráo "phần đầu" của từng biến.
        // Đuôi entity gắn sau 1 biến, vd "[dien_giai]&ddDir2Views;" (khai triển ra thêm các biến phía sau), ở lại ĐÚNG vị trí
        // cuối danh sách — nên tráo được biến chính, còn các biến do entity sinh ra thì không động tới.
        row.editable = false;
        row.rawPrefix = rawValue.slice(0, colon + 1);
        row.rawTokens = rawTokens;
        row.rawTails = rawTokens.map(() => '');
        const tailRe = /((?:&[A-Za-z_][\w.:$-]*;)+)$/;
        for (let i = 0; i < rawTokens.length && i < cells.length; i++) {
          const m = tailRe.exec(rawTokens[i]);
          const head = m ? rawTokens[i].slice(0, m.index) : rawTokens[i];
          // Entity nằm giữa biến (ngoài [..]) thì không biết nó khai triển ra gì → dừng.
          if (/&[A-Za-z_][\w.:$-]*;/.test(head.replace(/\[[^\]]*\]/g, ''))) break;
          cells[i].raw = head;
          cells[i].swappable = specialFree;
          if (m) { row.rawTails[i] = m[1]; break; } // các ô sau đó là do đuôi entity sinh ra
        }
      } else {
        // Sửa hoàn toàn được khi dòng "thuần": không ký tự đặc biệt/entity ngoài [..], số '1' khớp số biến, token gốc khớp.
        row.editable = specialFree && ones.length === cells.length && rawTokens.length === cells.length;
        if (row.editable) cells.forEach((c, i) => { c.raw = rawTokens[i]; c.swappable = true; });
      }
      this.rowById.set(row.id, row);
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
    this.wireDirDnD();
    this.wireRowDnD();
    this.addPalette(fields);
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
        if (cell.start > col) tds += `<td colspan="${cell.start - col}" class="dpGap" data-r="${row.id}" data-gs="${col}" data-ge="${cell.start - 1}"></td>`;
        const span = Math.min(cell.end, n - 1) - cell.start + 1;
        const { html, required } = this.cellContent(cell.token, fields);
        const drag = cell.swappable ? ` draggable="true" data-r="${row.id}" data-c="${cell.idx}"` : '';
        tds += `<td colspan="${Math.max(1, span)}" class="dpTd${required ? ' Required' : ''}"${drag}>${html}</td>`;
        col = cell.end + 1;
      }
      if (col < n) tds += `<td colspan="${n - col}" class="dpGap" data-r="${row.id}" data-gs="${col}" data-ge="${n - 1}"></td>`;
      // Tay nắm ⋮⋮ ở mép trái ô đầu: kéo để chuyển CẢ DÒNG (nhãn + mọi biến) lên/xuống.
      tds = tds.replace(/^<td([^>]*)>/, (m, a) => '<td' + a + '><span class="dpGrip" draggable="true" data-grip="' + row.id + '" title="Kéo để chuyển cả dòng">⋮⋮</span>');
      body += `<tr data-rid="${row.id}">${tds}</tr>`;
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
      const info = field.controller ? this.gridInfo.get(field.controller) : null;
      if (info) {
        return { html: this.gridHtml(info, (field.label || name) + ' — ' + field.controller, attr, name), required: false };
      }
      return { html: `<div class="dpGrid" ${attr} title="${this.esc(name)}"><div class="dpGridBar">${this.esc(field.label || name)}</div><div class="dpGridBody">Lưới chi tiết${field.controller ? ' — controller ' + this.esc(field.controller) : ''}<br><small>(không đọc được file Grid\\${this.esc(field.controller || '')}.xml nên chỉ giữ chỗ)</small></div></div>`, required: false };
    }
    const cls = ['dpFC'];
    if (field.readOnly || field.external) cls.push('ro');
    if (!f) cls.push('unknown');
    const title = this.esc(name + (f ? '' : ' — chưa khai báo <field> trong file này'));
    return { html: `<div class="${cls.join(' ')}" ${attr} title="${title}">${this.controlInner(field, !!f)}</div>`, required: !field.allowNulls };
  }

  /// Ô nhập kèm TÊN BIẾN (chữ mờ) để biết ô trắng đó là biến nào khi kéo-thả.
  controlInner(f) {
    const v = `<span class="dpVar">${this.esc(f.name || '')}</span>`;
    if (f.type === 'Boolean') return `<span class="dpChk"></span>${v}`;
    if (f.rows > 1) return `<span class="dpBox multi" style="height:${f.rows * 17}px">${v}</span>`;
    if (f.itemsStyle === 'DropDownList') return `<span class="dpBox">${v}${this.esc(f.options[0] || '')}<b class="dpIco">▾</b></span>`;
    if (f.type === 'DateTime') return `<span class="dpBox">${v}<b class="dpIco cal">▦</b></span>`;
    if (f.itemsStyle === 'AutoComplete') return `<span class="dpBox">${v}<b class="dpIco look">▤</b></span>`;
    if (f.type === 'Decimal' || f.itemsStyle === 'Numeric') return `<span class="dpBox num"><span class="dpNum">0</span>${v}</span>`;
    return `<span class="dpBox">${v}</span>`;
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
