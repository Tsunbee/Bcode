// Ctrl+G — hộp "Go to": liệt kê các TAG của controller (fields, command, script, response, css, ENTITY)
// và phần CHI TIẾT của tag đang chọn; nhấp đúp (hoặc Enter) vào 1 mục để nhảy tới đúng chỗ code đó.
//
// Chọn sẵn tag đang chứa con trỏ (chấm ●) và mục gần nhất phía trước con trỏ, nên mở ra là biết đang ở đâu.
//   fields   : Start (<fields>), End Fields, Categories (<category> đầu tiên), End Views
//   command  : từng <command event="…">
//   script   : từng function trong <script>
//   response : từng <action id="…"> trong <response>
//   css      : Start / End của <css>
//   ENTITY   : từng <!ENTITY …> khai báo ở đầu file

// Mục hay dùng: tô màu trong hộp Go to VÀ ngay trong editor (chữ đậm + nền nhạt + vạch ở thanh cuộn).
// 'a' = cam, 'b' = đỏ. Khoá là tên viết thường.
const GOTO_HOT = {
  command: { inserted: 'a', updated: 'b' },
  script: {
    'onchange$gridvoucherdetail': 'a',
    'on$voucher$executecommand': 'b',
    'on$voucher$responsecomplete': 'a',
  },
};
const GOTO_HOT_COLOR = { a: '#e5a00d', b: '#f14c4c' };

const GOTO_TAGS = ['fields', 'command', 'script', 'response', 'css', 'ENTITY'];

class BcodeGoto {
  constructor(bcode) {
    this.bcode = bcode;
    this.overlay = null;
    this.data = null;
    this.tag = null;
    this.index = 0;
    this.focusCol = 'detail';

    this.hotDecorations = bcode.editor.createDecorationsCollection([]);
    this._hotTimer = null;
    const refresh = () => { clearTimeout(this._hotTimer); this._hotTimer = setTimeout(() => this.refreshHot(), 400); };
    bcode.editor.onDidChangeModel(refresh);
    bcode.editor.onDidChangeModelContent(refresh);
    refresh();
  }

  /// Tô các mục hay dùng ngay trong editor (command Inserted/Updated, các function script quan trọng).
  refreshHot() {
    const model = this.bcode.currentModel;
    if (!model) { this.hotDecorations.set([]); return; }
    const data = this.scan(model.getValue());
    const decos = [];
    for (const tag of Object.keys(GOTO_HOT)) {
      for (const it of data[tag]) {
        if (!it.hot) continue;
        const start = model.getPositionAt(it.nameOffset);
        const end = model.getPositionAt(it.nameOffset + it.label.length);
        const color = GOTO_HOT_COLOR[it.hot];
        decos.push({
          range: new monaco.Range(start.lineNumber, start.column, end.lineNumber, end.column),
          options: {
            inlineClassName: 'gotoHotText gotoHot' + it.hot,
            className: 'gotoHotLine gotoHotLine' + it.hot,
            isWholeLine: true,
            overviewRuler: { color, position: monaco.editor.OverviewRulerLane.Right },
            minimap: { color, position: monaco.editor.MinimapPosition.Inline },
          },
        });
      }
    }
    this.hotDecorations.set(decos);
  }

  isOpen() { return !!this.overlay; }

  // ---- Phân tích file ----------------------------------------------------------------------

  /// Trả về { tag: [{label, offset}] } với offset là vị trí ký tự trong văn bản.
  scan(text) {
    const out = {};
    for (const t of GOTO_TAGS) out[t] = [];
    const add = (tag, label, offset, nameOffset) => {
      const hot = GOTO_HOT[tag] ? GOTO_HOT[tag][label.toLowerCase()] : undefined;
      out[tag].push({ label, offset, nameOffset: nameOffset == null ? offset : nameOffset, hot });
    };
    const first = (re) => { const m = re.exec(text); return m ? m.index : -1; };
    const section = (open, close) => {
      const s = text.search(open);
      if (s < 0) return null;
      const m = close.exec(text.slice(s));
      return { start: s, end: m ? s + m.index + m[0].length : text.length };
    };

    // fields / views
    const fStart = first(/<fields\b/i);
    if (fStart >= 0) add('fields', 'Start', fStart);
    const fEnd = text.search(/<\/fields>/i);
    if (fEnd >= 0) add('fields', 'End Fields', fEnd);
    const cat = first(/<categor(?:y|ies)\b/i);
    if (cat >= 0) add('fields', 'Categories', cat);
    const vEnd = text.search(/<\/views>/i);
    if (vEnd >= 0) add('fields', 'End Views', vEnd);

    // command
    let m;
    const cmdRe = /<command\b[^>]*\bevent\s*=\s*"([^"]*)"/gi;
    while ((m = cmdRe.exec(text))) add('command', m[1], m.index, m.index + m[0].lastIndexOf(m[1]));

    // script: function trong <script>…</script>
    const scr = section(/<script\b/i, /<\/script>/i);
    if (scr) {
      const body = text.slice(scr.start, scr.end);
      const fnRe = /function\s+([A-Za-z0-9_$]+)\s*\(/g;
      while ((m = fnRe.exec(body))) add('script', m[1], scr.start + m.index, scr.start + m.index + m[0].indexOf(m[1]));
    }

    // response: <action id>
    const resp = section(/<response\b/i, /<\/response>/i);
    if (resp) {
      const body = text.slice(resp.start, resp.end);
      const actRe = /<action\b[^>]*\bid\s*=\s*"([^"]*)"/gi;
      while ((m = actRe.exec(body))) add('response', m[1], resp.start + m.index);
    }

    // css
    const css = first(/<css\b/i);
    if (css >= 0) {
      add('css', 'Start', css);
      const cEnd = text.search(/<\/css>/i);
      if (cEnd >= 0) add('css', 'End', cEnd);
    }

    // ENTITY
    const entRe = /<!ENTITY\s+%?\s*([A-Za-z0-9_.$-]+)/g;
    while ((m = entRe.exec(text))) add('ENTITY', m[1], m.index);

    return out;
  }

  /// Tag chứa vị trí offset: lấy section có điểm bắt đầu gần nhất phía trước con trỏ.
  tagAt(data, offset) {
    let best = null;
    let bestStart = -1;
    for (const tag of GOTO_TAGS) {
      for (const it of data[tag]) {
        if (it.offset <= offset && it.offset > bestStart) { best = tag; bestStart = it.offset; }
      }
    }
    return best || 'fields';
  }

  // ---- Mở / đóng ---------------------------------------------------------------------------

  open() {
    if (this.overlay) { this.close(); return; }
    const model = this.bcode.currentModel;
    if (!model) return;
    const text = model.getValue();
    this.data = this.scan(text);
    const pos = this.bcode.editor.getPosition();
    const offset = pos ? model.getOffsetAt(pos) : 0;

    this.caretTag = this.tagAt(this.data, offset);
    this.tag = this.caretTag;
    // Mục gần nhất ở hoặc trước con trỏ trong tag đó.
    const items = this.data[this.tag];
    let idx = 0;
    items.forEach((it, i) => { if (it.offset <= offset) idx = i; });
    this.index = idx;
    this.focusCol = 'detail';
    this.build();
    this.renderLists();
  }

  close() {
    if (!this.overlay) return;
    this.overlay.remove();
    this.overlay = null;
    this.bcode.editor.focus();
  }

  build() {
    const ov = document.createElement('div');
    ov.id = 'gotoOverlay';
    ov.innerHTML = `
      <div id="gotoBox" tabindex="-1">
        <div id="gotoTitle"><span>Go to</span><span id="gotoGrip"></span><span id="gotoClose" title="Đóng (Esc)">✕</span></div>
        <div id="gotoHead"><div class="gotoColTag">TAG</div><div class="gotoColDetail">DETAIL</div></div>
        <div id="gotoBody"><div id="gotoTags"></div><div id="gotoDetails"></div></div>
      </div>`;
    document.body.appendChild(ov);
    this.overlay = ov;
    this.tagsEl = ov.querySelector('#gotoTags');
    this.detailsEl = ov.querySelector('#gotoDetails');
    this.box = ov.querySelector('#gotoBox');

    // Kéo thanh tiêu đề để dời khung: chuyển sang toạ độ tuyệt đối ngay lúc bắt đầu kéo.
    ov.querySelector('#gotoTitle').addEventListener('mousedown', (e) => {
      if (e.target.id === 'gotoClose') return;
      e.preventDefault();
      const r = this.box.getBoundingClientRect();
      this.box.style.position = 'fixed';
      this.box.style.left = r.left + 'px';
      this.box.style.top = r.top + 'px';
      const dx = e.clientX - r.left;
      const dy = e.clientY - r.top;
      const onMove = (ev) => {
        const x = Math.max(0, Math.min(window.innerWidth - 80, ev.clientX - dx));
        const y = Math.max(0, Math.min(window.innerHeight - 30, ev.clientY - dy));
        this.box.style.left = x + 'px';
        this.box.style.top = y + 'px';
      };
      const onUp = () => {
        document.removeEventListener('mousemove', onMove);
        document.removeEventListener('mouseup', onUp);
        this.box.focus();
      };
      document.addEventListener('mousemove', onMove);
      document.addEventListener('mouseup', onUp);
    });
    ov.querySelector('#gotoTitle').style.cursor = 'move';
    ov.querySelector('#gotoClose').onclick = () => this.close();
    ov.addEventListener('mousedown', (e) => { if (e.target === ov) this.close(); });
    this.box.addEventListener('keydown', (e) => this.onKey(e));
    this.box.focus();
  }

  renderLists() {
    this.tagsEl.innerHTML = '';
    for (const t of GOTO_TAGS) {
      const row = document.createElement('div');
      row.className = 'gotoRow' + (t === this.tag ? ' sel' : '');
      row.innerHTML = `<span>${t}</span>${t === this.caretTag ? '<b class="gotoDot">●</b>' : ''}`;
      row.onclick = () => { this.tag = t; this.index = 0; this.focusCol = 'tag'; this.renderLists(); };
      this.tagsEl.appendChild(row);
    }

    this.detailsEl.innerHTML = '';
    const items = this.data[this.tag];
    if (items.length === 0) {
      const empty = document.createElement('div');
      empty.className = 'gotoEmpty';
      empty.textContent = '(không có mục nào)';
      this.detailsEl.appendChild(empty);
      return;
    }
    items.forEach((it, i) => {
      const row = document.createElement('div');
      row.className = 'gotoRow' + (i === this.index ? ' sel' : '') + (it.hot ? ' hot hot' + it.hot : '');
      row.textContent = it.label;
      row.onclick = () => { this.index = i; this.focusCol = 'detail'; this.renderLists(); };
      row.ondblclick = () => this.go(it);
      this.detailsEl.appendChild(row);
    });
    const sel = this.detailsEl.querySelector('.sel');
    if (sel) sel.scrollIntoView({ block: 'nearest' });
    const selTag = this.tagsEl.querySelector('.sel');
    if (selTag) selTag.scrollIntoView({ block: 'nearest' });
  }

  onKey(e) {
    const items = this.data[this.tag];
    if (e.key === 'Escape') { e.preventDefault(); this.close(); return; }
    if (e.key === 'Enter') { e.preventDefault(); if (items[this.index]) this.go(items[this.index]); return; }
    if (e.key === 'ArrowLeft') { e.preventDefault(); this.focusCol = 'tag'; return; }
    if (e.key === 'ArrowRight') { e.preventDefault(); this.focusCol = 'detail'; return; }
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      const d = e.key === 'ArrowDown' ? 1 : -1;
      if (this.focusCol === 'tag') {
        const i = Math.max(0, Math.min(GOTO_TAGS.length - 1, GOTO_TAGS.indexOf(this.tag) + d));
        this.tag = GOTO_TAGS[i];
        this.index = 0;
      } else if (items.length) {
        this.index = Math.max(0, Math.min(items.length - 1, this.index + d));
      }
      this.renderLists();
    }
  }

  go(item) {
    const model = this.bcode.currentModel;
    if (!model) return;
    const pos = model.getPositionAt(item.offset);
    this.close();
    this.bcode.revealPosition(pos.lineNumber, pos.column);
  }
}
