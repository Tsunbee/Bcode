// Hỏi Fast — chat tra kiến thức source Fast, KHÔNG dùng AI (Host/FastSource/FastSourceService.Ask.cs).
//
// Hỏi về controller (lưu bảng nào, field, toolbar), field (khai báo lookup), entity / include (khai ở file nào), lookup, hàm JS mẫu, quy ước
// viết controller và lịch sử chỉnh sửa theo phiên bản source Fast ("AIDetail sửa gì ở SP22.5"). Mỗi thẻ: Chèn vào editor / Copy / Mở file,
// ✔ Đúng / ✘ Chưa đúng (lần sau xếp lại), ✎ Sửa (gõ câu trả lời đúng — lần sau hiện đầu tiên). Lịch sử phiên chat lưu ở máy này.
// Khung bên phải (cạnh Outline); Ctrl+Shift+J bật / tắt (keys.js panel.fastAsk); Ctrl+F trong khung = tìm trong cuộc trò chuyện.

class BcodeFastAsk {
  constructor(bcode) {
    this.bcode = bcode;
    this.session = this.newSession();
    this.build();
  }

  newSession() { return { id: 'fa' + Date.now().toString(36), title: '', messages: [] }; }

  // ---- khung ---------------------------------------------------------------------------

  build() {
    const p = document.createElement('div');
    p.id = 'fastAskPanel';
    p.style.display = 'none';
    p.innerHTML = `
      <div id="fastAskHeader">
        <span>Hỏi Fast <span class="faSub">(không AI)</span></span>
        <span class="faHeadBtns">
          <span class="faBtn" data-a="history" title="Lịch sử hỏi đáp">🕘</span>
          <span class="faBtn" data-a="new" title="Phiên mới">＋</span>
          <span class="faBtn" data-a="find" title="Tìm trong cuộc trò chuyện (Ctrl+F)">🔍</span>
          <span class="faBtn" data-a="close" title="Đóng">✕</span>
        </span>
      </div>
      <div id="fastAskFind" style="display:none;"><input type="text" placeholder="Tìm (Enter / Shift+Enter, Esc đóng)"/><span class="faFindCount"></span></div>
      <div id="fastAskHistory" style="display:none;"></div>
      <div id="fastAskLog"></div>
      <div id="fastAskStatus"></div>
      <div id="fastAskInputRow">
        <textarea id="fastAskInput" rows="2" placeholder="Hỏi về controller, field, lookup, include / entity, toolbar, JS, phiên bản… (Enter gửi, Shift+Enter xuống dòng)"></textarea>
        <button id="fastAskSend" class="dlgButton primary">Gửi</button>
      </div>`;
    const outline = document.getElementById('outlinePanel');
    outline.parentNode.insertBefore(p, outline.nextSibling);
    this.panel = p;
    this.log = p.querySelector('#fastAskLog');
    this.input = p.querySelector('#fastAskInput');
    this.status = p.querySelector('#fastAskStatus');
    this.findBar = p.querySelector('#fastAskFind');
    this.historyBox = p.querySelector('#fastAskHistory');
    p.querySelector('#fastAskSend').onclick = () => this.send();
    this.input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); this.send(); }
    });
    p.querySelector('#fastAskHeader').addEventListener('click', (e) => {
      const a = e.target.closest('.faBtn')?.dataset.a;
      if (a === 'close') this.hide();
      else if (a === 'new') this.startNew();
      else if (a === 'history') this.toggleHistory();
      else if (a === 'find') this.openFind();
    });
    // Ctrl+F khi đang ở trong khung: tìm trong cuộc trò chuyện (không mở thanh tìm của Monaco)
    p.addEventListener('keydown', (e) => {
      if ((e.ctrlKey || e.metaKey) && !e.altKey && e.code === 'KeyF') { e.preventDefault(); e.stopPropagation(); this.openFind(); }
    });
    const fi = this.findBar.querySelector('input');
    fi.addEventListener('input', () => this.find(fi.value, 0));
    fi.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') { e.preventDefault(); this.closeFind(); }
      else if (e.key === 'Enter') { e.preventDefault(); this.find(fi.value, e.shiftKey ? -1 : 1); }
    });
    this.renderEmpty();
  }

  isVisible() { return this.panel.style.display !== 'none'; }
  toggle() { if (this.isVisible()) this.hide(); else this.show(); }
  show() {
    this.panel.style.display = 'flex';
    this.input.focus();
    this.refreshStatus();
  }
  hide() { this.panel.style.display = 'none'; this.bcode.editor && this.bcode.editor.focus(); }

  async refreshStatus() {
    try { this.status.textContent = await window.chrome.webview.hostObjects.host.GetFastSourceStatus(); } catch { /* host cũ */ }
  }

  renderEmpty() {
    this.log.innerHTML = '';
    const d = document.createElement('div');
    d.className = 'faHint';
    d.appendChild(document.createTextNode('Ví dụ (bấm để hỏi):'));
    for (const q of ['field ma_kh khai báo lookup thế nào', 'include nào khai báo DetailTax', 'controller AIDetail lưu bảng nào',
      'ví dụ js hàm load$GridVoucherDetail$', 'cách khai báo entity', 'AIDetail sửa gì ở SP22.5', 'bản SP23 sửa những file nào']) {
      const a = Object.assign(document.createElement('div'), { className: 'faExample', textContent: '• ' + q });
      a.onclick = () => this.send(q);
      d.appendChild(a);
    }
    this.log.appendChild(d);
  }

  // ---- hỏi / trả lời -------------------------------------------------------------------

  async send(text) {
    const q = (text ?? this.input.value).trim();
    if (!q) return;
    this.input.value = '';
    if (this.session.messages.length === 0) this.log.innerHTML = '';
    if (!this.session.title) this.session.title = q.slice(0, 80);
    this.session.messages.push({ role: 'user', text: q });
    this.renderUser(q);
    const wait = document.createElement('div');
    wait.className = 'faWait';
    wait.textContent = 'Đang tra…';
    this.log.appendChild(wait);
    this.scrollEnd();
    let reply;
    try {
      reply = JSON.parse(await window.bcodeHost.call('BeginFastAsk', q, this.bcode.activePath || ''));
    } catch (e) {
      reply = { question: q, cards: [], more: [], empty: 'Lỗi tra cứu: ' + e.message };
    }
    wait.remove();
    this.session.messages.push({ role: 'bot', reply });
    this.renderReply(reply);
    if (reply.status) this.status.textContent = reply.status;
    this.saveSession();
  }

  renderUser(q) {
    const u = document.createElement('div');
    u.className = 'faMsg faUser';
    u.textContent = q;
    this.log.appendChild(u);
  }

  renderReply(reply) {
    const box = document.createElement('div');
    box.className = 'faMsg faBot';
    if (!reply.cards || reply.cards.length === 0) {
      box.appendChild(Object.assign(document.createElement('div'), { className: 'faText', textContent: reply.empty || 'Chưa tìm thấy gì.' }));
    }
    for (const c of reply.cards || []) box.appendChild(this.renderCard(c, reply.question));
    if (reply.more && reply.more.length) {
      const m = document.createElement('div');
      m.className = 'faMore';
      m.appendChild(document.createTextNode('Kết quả khác: '));
      reply.more.forEach((x, i) => {
        const a = document.createElement('a');
        a.textContent = x.title;
        a.href = '#';
        a.onclick = async (e) => {
          e.preventDefault();
          try {
            const raw = await window.bcodeHost.call('BeginFastAskDoc', x.key, reply.question);
            if (raw) { const card = JSON.parse(raw); m.parentNode.insertBefore(this.renderCard(card, reply.question), m); }
          } catch { /* bỏ qua */ }
        };
        if (i) m.appendChild(document.createTextNode(' · '));
        m.appendChild(a);
      });
      box.appendChild(m);
    }
    this.log.appendChild(box);
    this.scrollEnd();
  }

  renderCard(c, question) {
    const card = document.createElement('div');
    card.className = 'faCard' + (c.kind === 'custom' ? ' faCustom' : '');
    const head = document.createElement('div');
    head.className = 'faCardHead';
    head.appendChild(Object.assign(document.createElement('span'), { className: 'faTitle', textContent: c.title }));
    if (c.source) head.appendChild(Object.assign(document.createElement('span'), { className: 'faSrc', textContent: c.source }));
    card.appendChild(head);
    if (c.text) card.appendChild(Object.assign(document.createElement('div'), { className: 'faText', textContent: c.text }));
    if (c.code) {
      const pre = document.createElement('pre');
      pre.className = 'faCode';
      pre.textContent = c.code;
      card.appendChild(pre);
      // tô màu như editor (xml / js theo loại thẻ); lỗi thì giữ chữ thường
      const lang = c.kind === 'func' ? 'javascript' : 'xml';
      try { monaco.editor.colorize(c.code, lang, { tabSize: 2 }).then((html) => { pre.innerHTML = html; }).catch(() => {}); } catch { /* chưa có monaco */ }
    }
    const bar = document.createElement('div');
    bar.className = 'faBar';
    const btn = (label, title, fn) => { const b = document.createElement('button'); b.className = 'faAct'; b.textContent = label; b.title = title; b.onclick = fn; bar.appendChild(b); return b; };
    if (c.code) {
      btn('Chèn', 'Chèn đoạn code vào editor tại con trỏ', () => this.insert(c.code));
      btn('Copy', 'Chép đoạn code', async () => { try { await navigator.clipboard.writeText(c.code); this.toast('Đã chép'); } catch { /* không có quyền clipboard */ } });
    }
    if (c.path) btn('Mở file', c.path, () => this.bcode.openFile(c.path));
    const votes = document.createElement('span');
    votes.className = 'votes';
    if (c.kind !== 'custom') {
      const up = btn('✔ Đúng', 'Câu trả lời đúng — lần sau đưa lên đầu', () => this.vote(question, c.key, 1, up, down));
      const down = btn('✘ Chưa đúng', 'Sai — lần sau bỏ thẻ này cho câu hỏi giống', () => this.vote(question, c.key, -1, up, down));
      votes.append(up, down);
    }
    votes.appendChild(btn('✎ Sửa', 'Gõ câu trả lời đúng — lần sau hiện đầu tiên', () => this.edit(card, question, c)));
    bar.appendChild(votes);
    card.appendChild(bar);
    return card;
  }

  insert(code) {
    const ed = this.bcode.editor;
    if (!ed || !ed.getModel()) return;
    const sel = ed.getSelection();
    ed.executeEdits('fastask', [{ range: sel, text: code, forceMoveMarkers: true }]);
    ed.focus();
  }

  async vote(question, key, v, up, down) {
    try { await window.bcodeHost.call('BeginFastAskVote', question, key, v); } catch { return; }
    up.classList.toggle('on', v > 0);
    down.classList.toggle('on', v < 0);
    this.toast(v > 0 ? 'Đã ghi nhận: đúng' : 'Đã ghi nhận: chưa đúng — bấm ✎ Sửa để gõ câu trả lời đúng');
  }

  edit(card, question, c) {
    if (card.querySelector('.faEdit')) return;
    const f = document.createElement('div');
    f.className = 'faEdit';
    const t = Object.assign(document.createElement('textarea'), { rows: 3, value: c.text || '', placeholder: 'Câu trả lời đúng' });
    const code = Object.assign(document.createElement('textarea'), { rows: 4, value: c.code || '', placeholder: 'Đoạn code (nếu có)', className: 'faEditCode' });
    const save = Object.assign(document.createElement('button'), { className: 'dlgButton primary', textContent: 'Lưu' });
    const cancel = Object.assign(document.createElement('button'), { className: 'dlgButton', textContent: 'Huỷ' });
    save.onclick = async () => {
      try { await window.bcodeHost.call('BeginFastAskCorrect', question, t.value, code.value); } catch (e) { this.toast('Không lưu được: ' + e.message); return; }
      f.remove();
      this.toast('Đã lưu — lần sau hỏi câu giống sẽ hiện câu trả lời này đầu tiên');
    };
    cancel.onclick = () => f.remove();
    const row = document.createElement('div');
    row.className = 'faEditBtns';
    row.append(save, cancel);
    f.append(t, code, row);
    card.appendChild(f);
    t.focus();
  }

  toast(text) { if (this.bcode.showToast) this.bcode.showToast(text, 2200); }
  scrollEnd() { this.log.scrollTop = this.log.scrollHeight; }

  // ---- phiên chat ----------------------------------------------------------------------

  async saveSession() {
    try { await window.bcodeHost.call('BeginFastAskSaveSession', this.session.id, JSON.stringify(this.session)); } catch { /* lỗi ghi: chỉ mất lịch sử */ }
  }

  startNew() {
    this.session = this.newSession();
    this.historyBox.style.display = 'none';
    this.renderEmpty();
    this.input.focus();
  }

  async toggleHistory() {
    if (this.historyBox.style.display !== 'none') { this.historyBox.style.display = 'none'; return; }
    let list = [];
    try { list = JSON.parse(await window.bcodeHost.call('BeginFastAskSessions')) || []; } catch { /* trống */ }
    this.historyBox.innerHTML = '';
    if (!list.length) this.historyBox.appendChild(Object.assign(document.createElement('div'), { className: 'faHint', textContent: 'Chưa có phiên hỏi đáp nào.' }));
    for (const s of list) {
      const r = document.createElement('div');
      r.className = 'faHistRow';
      r.appendChild(Object.assign(document.createElement('span'), { className: 'faHistTitle', textContent: s.title || '(trống)' }));
      r.appendChild(Object.assign(document.createElement('span'), { className: 'faHistAt', textContent: new Date(s.at).toLocaleString('vi-VN') }));
      const del = Object.assign(document.createElement('span'), { className: 'faBtn', textContent: '🗑', title: 'Xoá phiên này' });
      del.onclick = async (e) => {
        e.stopPropagation();
        try { await window.bcodeHost.call('BeginFastAskDeleteSession', s.id); } catch { return; }
        r.remove();
      };
      r.appendChild(del);
      r.onclick = () => this.loadSession(s.id);
      this.historyBox.appendChild(r);
    }
    this.historyBox.style.display = '';
  }

  async loadSession(id) {
    let s;
    try { s = JSON.parse(await window.bcodeHost.call('BeginFastAskLoadSession', id)); } catch { return; }
    this.session = s;
    this.historyBox.style.display = 'none';
    this.log.innerHTML = '';
    for (const m of s.messages || []) {
      if (m.role === 'user') this.renderUser(m.text);
      else if (m.reply) this.renderReply(m.reply);
    }
  }

  // ---- Ctrl+F --------------------------------------------------------------------------

  openFind() {
    this.findBar.style.display = '';
    const i = this.findBar.querySelector('input');
    i.focus();
    i.select();
  }

  closeFind() {
    this.findBar.style.display = 'none';
    this.clearMarks();
    this.input.focus();
  }

  clearMarks() {
    for (const m of [...this.log.querySelectorAll('mark.fh')]) m.replaceWith(document.createTextNode(m.textContent));
    this.log.normalize();
  }

  /// Tìm không dấu: so từng ký tự đã bỏ dấu (độ dài giữ nguyên nên vị trí khớp đúng chữ gốc).
  find(text, step) {
    const fold = (s) => s.normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/đ/g, 'd').replace(/Đ/g, 'D').toLowerCase();
    const foldChars = (s) => [...s].map((ch) => fold(ch).charAt(0) || ch).join('');
    if (step === 0 || !this._marks || this._findText !== text) {
      this.clearMarks();
      this._marks = [];
      this._findText = text;
      this._cur = -1;
      const needle = fold(text.trim());
      if (needle) {
        const walker = document.createTreeWalker(this.log, NodeFilter.SHOW_TEXT);
        const nodes = [];
        while (walker.nextNode()) nodes.push(walker.currentNode);
        for (const n of nodes) {
          const hay = foldChars(n.nodeValue);
          let at = hay.indexOf(needle);
          if (at < 0) continue;
          const frag = document.createDocumentFragment();
          let last = 0;
          while (at >= 0) {
            frag.appendChild(document.createTextNode(n.nodeValue.slice(last, at)));
            const mk = document.createElement('mark');
            mk.className = 'fh';
            mk.textContent = n.nodeValue.slice(at, at + needle.length);
            frag.appendChild(mk);
            this._marks.push(mk);
            last = at + needle.length;
            at = hay.indexOf(needle, last);
          }
          frag.appendChild(document.createTextNode(n.nodeValue.slice(last)));
          n.replaceWith(frag);
        }
      }
    }
    const count = this.findBar.querySelector('.faFindCount');
    if (!this._marks.length) { count.textContent = text.trim() ? '0' : ''; return; }
    this._cur = (this._cur + (step < 0 ? -1 : 1) + this._marks.length) % this._marks.length;
    this._marks.forEach((m, i) => m.classList.toggle('cur', i === this._cur));
    this._marks[this._cur].scrollIntoView({ block: 'center' });
    count.textContent = (this._cur + 1) + '/' + this._marks.length;
  }
}
