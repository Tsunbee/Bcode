// "Xem trước mail mẫu": ở file cấu hình mail (Message.xml, Comment.xml... hoặc 1 file <action> như MOApproval.txt) bấm Alt+P (cùng phím với "Xem trước Dir")
// để xem email / SMS sẽ trông thế nào — khỏi gửi thử mail thật.
//
// CẤU TRÚC (theo Message.xsd):
//   message > mail > template > action  (email)   |   message > sms > template > action  (SMS: <content><text> thay cho <body>)
//   <action id v e table> <fields><field name="h_so_ct"><header v e/></field>...</fields>  <query id="report"><command id="master|detail">...  <body>/<body2> <header|detail|footer><text>CDATA
//   {!h_xxx} = tiêu đề lấy từ <field name="h_xxx"> theo ngôn ngữ V/E · {!tên_cột} khác = dữ liệu lúc chạy.
//
// TÍNH NĂNG
//   1. Dữ liệu mẫu: gõ "so_ct = PN001", "ma_vt = A | B | C" (nhiều giá trị cách nhau | = mỗi dòng chi tiết một giá trị) — nhớ theo từng form.
//   2. Nhảy qua lại: bấm tiêu đề / biến trên mail → editor nhảy tới <field> / {!biến} trong file; đặt con trỏ ở code → phần tương ứng trên mail sáng lên.
//   3. Sửa tiêu đề ngay trên mail: nhấp đúp chữ tiêu đề, gõ, Enter → ghi vào <header v="…"/ e="…"> của ngôn ngữ đang xem.
//   4. Thử độ rộng (điện thoại / hộp thư hẹp / rộng) + cảnh báo tràn ngang, CSS hay bị client mail bỏ.
//   5. Dữ liệu thật: chạy query master + detail của form trên database của workspace (LUÔN ở chế độ chạy thử rollback, không ghi gì) rồi đổ vào mail.
//
// GIỚI HẠN: mô phỏng — không có engine gửi mail thật; mỗi client mail (Outlook, Gmail...) vẽ khác nhau; link bị chặn; style/ảnh ngoài không tải.

const MP_ACTION_RE = /<action\b[^>]*\bid\s*=/i;
const MP_MESSAGE_RE = /urn:schemas-fast-com:data-message|<message\b/i;
const MP_WIDTHS = [['auto', 'Tự động'], ['375', 'Điện thoại 375px'], ['600', 'Hộp thư hẹp 600px'], ['900', 'Hộp thư rộng 900px']];
const MP_MAX_ROWS = 40;

// Script chạy TRONG iframe (sandbox allow-scripts, không allow-same-origin): chỉ bắt click / nhấp đúp / nhận lệnh sáng, nói chuyện với panel qua postMessage.
const MP_FRAME_SCRIPT = `(function(){
  var post=function(o){o.mp=1;parent.postMessage(o,'*');};
  var sel=function(n){return '[data-v="'+n+'"],[data-h="'+n+'"]';};
  document.addEventListener('click',function(e){
    var a=e.target.closest('a'); if(a)e.preventDefault();
    var t=e.target.closest('[data-v],[data-h]'); if(!t||t.isContentEditable)return;
    post({type:'click',name:t.getAttribute('data-h')||t.getAttribute('data-v'),kind:t.hasAttribute('data-h')?'hdr':'var'});
  },true);
  document.addEventListener('dblclick',function(e){
    var t=e.target.closest('[data-h]'); if(!t)return; e.preventDefault();
    var old=t.textContent; t.contentEditable='true'; t.focus();
    var r=document.createRange(); r.selectNodeContents(t); var s=getSelection(); s.removeAllRanges(); s.addRange(r);
    var finish=function(save){ t.removeEventListener('blur',onBlur); t.removeEventListener('keydown',onKey); t.contentEditable='false';
      var now=t.textContent; if(save&&now!==old) post({type:'edit',name:t.getAttribute('data-h'),text:now}); else t.textContent=old; };
    var onBlur=function(){finish(true);};
    var onKey=function(k){ if(k.key==='Enter'){k.preventDefault();finish(true);} else if(k.key==='Escape'){k.preventDefault();finish(false);} };
    t.addEventListener('blur',onBlur); t.addEventListener('keydown',onKey);
  },true);
  var base='<style>html{background:#fff}body{margin:10px;font-family:Verdana,sans-serif}.mpVar{background:#fff4c2;border-radius:2px}.mpVal{border-bottom:1px dotted #3b82f6}[data-h]{cursor:pointer;border-bottom:1px dashed #94a3b8}.mpHl{outline:2px solid #3b82f6;background:#dbeafe!important;border-radius:2px}</style>';
  var measure=function(){ post({type:'size',sw:document.documentElement.scrollWidth,cw:document.documentElement.clientWidth}); };
  window.addEventListener('message',function(e){
    var d=e.data; if(!d)return;
    if(d.mp===3){ var sy=document.documentElement.scrollTop, sx=document.documentElement.scrollLeft;
      document.head.innerHTML=base+d.head; document.body.innerHTML=d.body; document.documentElement.scrollTop=sy; document.documentElement.scrollLeft=sx; measure(); }
    else if(d.mp===2){
      document.querySelectorAll('.mpHl').forEach(function(x){x.classList.remove('mpHl');});
      if(!d.name)return; var l=document.querySelectorAll(sel(d.name)); l.forEach(function(x){x.classList.add('mpHl');});
      if(l[0])l[0].scrollIntoView({block:'nearest'}); }
  });
  window.addEventListener('resize',measure);
  post({type:'ready'});
})();`;

class BcodeMailPreview {
  constructor(bcode) {
    this.bcode = bcode;
    this.open = false;
    this.timer = null;
    this.version = 0;
    this.lang = 'v';          // 'v' | 'e'
    this.actionId = null;     // khoá "kênh:id" của form đang xem (giữ qua các lần vẽ lại)
    this.bodyName = null;     // body / body2 ...
    this.showSource = false;
    this.actions = [];
    this.real = new Map();    // khoá form → { master:{}, rows:[{}] } (dữ liệu thật đã chạy)
    this.sample = {};         // dữ liệu mẫu của form đang xem: tên → [giá trị...]
    this.frameReady = false;
    this.pending = null;      // nội dung chờ iframe sẵn sàng
    this.hlTimer = null;
    this.width = 'auto';
    try { this.width = localStorage.getItem('mp.width') || 'auto'; } catch { /* storage blocked */ }

    this.panel = document.createElement('div');
    this.panel.id = 'mailPreviewPanel';
    this.panel.style.display = 'none';
    this.panel.innerHTML = `
      <div class="mpResizer"></div>
      <div class="mpHeader">
        <span class="mpTitle">Xem trước mail</span>
        <span class="mpStatus"></span>
        <span class="mpBtn mpMode" title="Đổi vị trí: bên phải → bên trái → cửa sổ nổi (kéo thả được)">⇄</span>
        <span class="mpBtn mpRefresh" title="Dựng lại ngay (đọc lại cả file include)">⟳</span>
        <span class="mpBtn mpClose" title="Đóng">✕</span>
      </div>
      <div class="mpBar">
        <label>Form <select class="mpAction"></select></label>
        <label>Mẫu <select class="mpBody"></select></label>
        <span class="mpLang"><button data-l="v" class="on">V</button><button data-l="e">E</button></span>
        <label>Rộng <select class="mpWidth"></select></label>
        <label class="mpChk"><input type="checkbox" class="mpSrc"> HTML</label>
        <span class="mpToggles"><button class="mpTg" data-t="sample" title="Gõ giá trị thử cho {!biến}">Dữ liệu mẫu</button><button class="mpTg" data-t="real" title="Chạy query master/detail của form trên database (chỉ đọc, rollback)">Dữ liệu thật</button></span>
      </div>
      <div class="mpSample" style="display:none">
        <textarea class="mpSampleText" rows="4" spellcheck="false" placeholder="so_ct = PN001&#10;ma_vt = A001 | A002 | A003&#10;ten_vt = Vải cotton | Chỉ may | Cúc áo"></textarea>
        <div class="mpHint">Mỗi dòng <code>tên_cột = giá trị</code>. Chi tiết: nhiều giá trị cách nhau <code>|</code> (mỗi dòng một giá trị). Nhớ theo từng form.</div>
      </div>
      <div class="mpReal" style="display:none">
        <div class="mpRealFields"></div>
        <div class="mpRealBar"><button class="mpRun">Chạy query &amp; đổ vào mail</button><button class="mpClear">Bỏ dữ liệu thật</button><span class="mpRealStatus"></span></div>
      </div>
      <div class="mpInfo"></div>
      <div class="mpBox"><iframe class="mpFrame" sandbox="allow-scripts" title="Xem trước mail"></iframe><pre class="mpCode" style="display:none"></pre></div>
      <div class="mpVars"></div>
      <div class="mpFooter"></div>`;
    document.getElementById('main').appendChild(this.panel);

    const q = (s) => this.panel.querySelector(s);
    this.status = q('.mpStatus'); this.info = q('.mpInfo'); this.frame = q('.mpFrame'); this.code = q('.mpCode');
    this.vars = q('.mpVars'); this.footer = q('.mpFooter');
    this.selAction = q('.mpAction'); this.selBody = q('.mpBody'); this.selWidth = q('.mpWidth');
    this.sampleBox = q('.mpSample'); this.sampleText = q('.mpSampleText');
    this.realBox = q('.mpReal'); this.realFields = q('.mpRealFields'); this.realStatus = q('.mpRealStatus');
    this.selWidth.innerHTML = MP_WIDTHS.map(([v, t]) => `<option value="${v}">${t}</option>`).join('');
    this.selWidth.value = this.width;

    q('.mpClose').onclick = () => this.close();
    q('.mpMode').onclick = () => this.setMode(this.mode === 'right' ? 'left' : this.mode === 'left' ? 'float' : 'right');
    q('.mpRefresh').onclick = () => this.render(true);
    this.selAction.onchange = () => { this.actionId = this.selAction.value; this.bodyName = null; this.onActionChanged(); this.draw(); };
    this.selBody.onchange = () => { this.bodyName = this.selBody.value; this.draw(); };
    this.selWidth.onchange = () => { this.width = this.selWidth.value; try { localStorage.setItem('mp.width', this.width); } catch { /* storage blocked */ } this.applyWidth(); this.frameReady && this.postFrame(); };
    q('.mpSrc').onchange = (e) => { this.showSource = e.target.checked; this.draw(); };
    this.panel.querySelectorAll('.mpLang button').forEach((b) => {
      b.onclick = () => { this.lang = b.dataset.l; this.panel.querySelectorAll('.mpLang button').forEach((x) => x.classList.toggle('on', x === b)); this.draw(); };
    });
    this.panel.querySelectorAll('.mpTg').forEach((b) => {
      b.onclick = () => { const box = b.dataset.t === 'sample' ? this.sampleBox : this.realBox; const on = box.style.display === 'none'; box.style.display = on ? '' : 'none'; b.classList.toggle('on', on); if (b.dataset.t === 'real' && on) this.buildRealFields(); };
    });
    let sampleTimer = null;
    this.sampleText.addEventListener('input', () => {
      clearTimeout(sampleTimer);
      sampleTimer = setTimeout(() => { this.sample = this.parseSample(this.sampleText.value); this.saveSample(this.sampleText.value); this.draw(); }, 300);
    });
    q('.mpRun').onclick = () => this.runReal();
    q('.mpClear').onclick = () => { this.real.delete(this.actionId); this.realStatus.textContent = 'Đã bỏ dữ liệu thật.'; this.draw(); };

    this.setupResize(q('.mpResizer'));
    this.setupFloat(q('.mpHeader'));
    this.mode = 'right';
    try { this.mode = localStorage.getItem('mp.mode') || 'right'; } catch { /* storage blocked */ }
    this.applyMode();
    this.applyWidth();

    window.addEventListener('message', (e) => this.onFrameMessage(e));
    this.frame.srcdoc = '<!doctype html><html><head><meta charset="utf-8"></head><body></body><script>' + MP_FRAME_SCRIPT.replace(/<\/script/gi, '<\\/script') + '<\/script></html>';

    const editor = bcode.editor;
    editor.onDidChangeModelContent(() => this.schedule(700));
    editor.onDidChangeModel(() => { this.actionId = null; this.bodyName = null; this.schedule(0); });
    editor.onDidChangeCursorPosition(() => { if (this.open) { clearTimeout(this.hlTimer); this.hlTimer = setTimeout(() => this.highlightFromCursor(), 150); } });
  }

  /// File đang mở có phải file cấu hình mail mẫu không (Alt+P dùng để chọn xem mail hay xem Dir).
  applies() {
    const model = this.bcode.currentModel;
    if (!model) return false;
    const path = (this.bcode.activePath || '').toLowerCase();
    if (/\.(dir|grid)\.xml$/.test(path)) return false;
    const text = model.getValue();
    if (/<view\b[^>]*\bid\s*=\s*"(?:Dir|Grid)"/i.test(text)) return false; // controller Dir/Grid → xem Dir như cũ
    return MP_MESSAGE_RE.test(text) || (MP_ACTION_RE.test(text) && /<body\d*\b/i.test(text));
  }

  toggle() { this.open ? this.close() : this.openPanel(); }
  openPanel() { this.open = true; this.panel.style.display = 'flex'; this.render(false); }
  close() { this.open = false; this.panel.style.display = 'none'; }
  schedule(delay) { if (!this.open) return; clearTimeout(this.timer); this.timer = setTimeout(() => this.render(), delay); }

  // ---- Vị trí: bên phải (mặc định) / bên trái / cửa sổ nổi kéo thả được (nhớ lại lần sau) ----------------------------------------------

  setMode(mode) {
    this.mode = mode;
    try { localStorage.setItem('mp.mode', mode); } catch { /* storage blocked */ }
    this.applyMode();
  }

  applyMode() {
    const p = this.panel, floating = this.mode === 'float';
    p.classList.toggle('mpLeft', this.mode === 'left');
    p.classList.toggle('mpFloat', floating);
    if (floating) {
      let r = null;
      try { r = JSON.parse(localStorage.getItem('mp.rect') || 'null'); } catch { /* hỏng thì dùng mặc định */ }
      const w = Math.min(r ? r.w : 620, window.innerWidth - 40), h = Math.min(r ? r.h : 520, window.innerHeight - 80);
      p.style.width = w + 'px'; p.style.height = h + 'px';
      p.style.left = Math.max(0, Math.min(r ? r.x : window.innerWidth - w - 40, window.innerWidth - 120)) + 'px';
      p.style.top = Math.max(0, Math.min(r ? r.y : 80, window.innerHeight - 60)) + 'px';
    } else {
      p.style.left = p.style.top = p.style.height = '';
    }
    this.panel.querySelector('.mpMode').textContent = floating ? '⧉' : this.mode === 'left' ? '⇥' : '⇤';
  }

  saveRect() {
    if (this.mode !== 'float') return;
    const r = this.panel.getBoundingClientRect();
    try { localStorage.setItem('mp.rect', JSON.stringify({ x: r.left, y: r.top, w: r.width, h: r.height })); } catch { /* storage blocked */ }
  }

  /// Kéo thanh tiêu đề để di chuyển cửa sổ nổi; kéo góc dưới phải để đổi cỡ (CSS resize).
  setupFloat(header) {
    header.addEventListener('mousedown', (e) => {
      if (this.mode !== 'float' || e.target.closest('.mpBtn')) return;
      e.preventDefault();
      const sx = e.clientX, sy = e.clientY, r = this.panel.getBoundingClientRect();
      const onMove = (ev) => {
        this.panel.style.left = Math.max(0, Math.min(window.innerWidth - 120, r.left + ev.clientX - sx)) + 'px';
        this.panel.style.top = Math.max(0, Math.min(window.innerHeight - 40, r.top + ev.clientY - sy)) + 'px';
      };
      const onUp = () => { document.removeEventListener('mousemove', onMove); document.removeEventListener('mouseup', onUp); this.saveRect(); };
      document.addEventListener('mousemove', onMove); document.addEventListener('mouseup', onUp);
    });
    this.panel.addEventListener('mouseup', () => this.saveRect());
  }

  setupResize(handle) {
    handle.addEventListener('mousedown', (e) => {
      e.preventDefault();
      const startX = e.clientX, startW = this.panel.getBoundingClientRect().width;
      const dir = this.mode === 'left' ? -1 : 1; // panel bên trái: kéo sang phải = rộng ra
      const onMove = (ev) => { this.panel.style.width = Math.max(380, Math.min(window.innerWidth - 300, startW + dir * (startX - ev.clientX))) + 'px'; };
      const onUp = () => { document.removeEventListener('mousemove', onMove); document.removeEventListener('mouseup', onUp); };
      document.addEventListener('mousemove', onMove); document.addEventListener('mouseup', onUp);
    });
  }

  applyWidth() {
    if (this.width === 'auto') { this.frame.style.width = ''; this.frame.style.flex = '1'; }
    else { this.frame.style.flex = 'none'; this.frame.style.width = this.width + 'px'; }
  }

  // ---- Dựng ---------------------------------------------------------------------------------

  async render(force) {
    if (!this.open) return;
    const run = ++this.version;
    const model = this.bcode.currentModel;
    if (!model) { this.message('Chưa mở file nào.'); return; }
    if (!this.applies()) { this.message('File này không phải file cấu hình mail mẫu (Message.xml hoặc file có <action> … <body>).'); return; }

    this.status.textContent = 'Đang dựng…';
    let text = model.getValue();
    const unresolved = [];
    try {
      if (window.bcodeEntity) {
        if (force) window.bcodeEntity.invalidate();
        const r = await window.bcodeEntity.expandFull(text);
        text = r.text;
        unresolved.push(...(r.unresolved || []));
      }
    } catch { /* không đọc được include: dựng với phần đang có */ }
    if (run !== this.version) return; // đã có lần vẽ mới hơn

    // Bỏ khai báo DOCTYPE / <?xml?>, entity còn sót (không khai triển được) → rỗng, rồi bọc <root> để file nhiều <action> vẫn parse được.
    let xml = text.replace(/^\uFEFF/, '').replace(/<\?xml[^>]*\?>/gi, '').replace(/<!DOCTYPE[\s\S]*?\n[ \t]*\]>/i, '');
    xml = xml.replace(/&([A-Za-z_][\w.:$-]*);/g, (m, n) => (['amp', 'lt', 'gt', 'quot', 'apos'].includes(n) ? m : ''));
    xml = xml.replace(/<message\b[^>]*>/i, '<message>');   // bỏ xmlns cho querySelector đơn giản
    const doc = new DOMParser().parseFromString('<root>' + xml + '</root>', 'application/xml');
    const err = doc.querySelector('parsererror');
    if (err) { this.message('File chưa đúng cú pháp XML nên chưa xem trước được:\n' + err.textContent.split('\n')[0].slice(0, 300), true); return; }

    // Hai kênh có thể trùng id (vd PurchaseRequisition) nên khoá = kênh + id. File chỉ có <action> rời (MOApproval.txt) coi là mail.
    const mailNodes = [...doc.querySelectorAll('mail > template > action')];
    const smsNodes = [...doc.querySelectorAll('sms > template > action')];
    const rest = [...doc.getElementsByTagName('action')].filter((a) => !mailNodes.includes(a) && !smsNodes.includes(a));
    this.smsMax = Number((doc.querySelector('sms > setting > maxLength') || { getAttribute: () => 0 }).getAttribute('value')) || 0;
    this.actions = [
      ...mailNodes.concat(rest).map((a) => this.readAction(a, 'mail')),
      ...smsNodes.map((a) => this.readAction(a, 'sms')),
    ].filter((a) => a.id);
    // Cùng kênh + id xuất hiện nhiều lần (vd 1 bản trong file include, 1 bản ngay trong file này): đánh số để chọn / nhảy đúng bản.
    const seen = {};
    for (const a of this.actions) { seen[a.key] = (seen[a.key] || 0) + 1; }
    const idx = {};
    for (const a of this.actions) { if (seen[a.key] > 1) { idx[a.key] = (idx[a.key] || 0) + 1; a.dup = idx[a.key]; a.key += '#' + a.dup; } }
    this.unresolved = unresolved;
    if (!this.actions.length) { this.message('Không thấy <action> nào trong file (kiểm tra entity khai ở DOCTYPE có trỏ đúng file không).', true); return; }
    this.status.textContent = this.actions.length + ' form';
    this.fillSelectors();
    this.onActionChanged();
    this.draw();
  }

  readAction(a, channel) {
    const fields = new Map();
    for (const f of a.querySelectorAll('fields > field')) {
      const h = f.querySelector('header');
      if (h) fields.set(f.getAttribute('name'), { v: h.getAttribute('v') || '', e: h.getAttribute('e') || h.getAttribute('v') || '' });
    }
    const bodies = [];
    for (const child of a.children) {
      if (!/^body\d*$/i.test(child.localName)) continue;
      const part = (n) => { const t = child.querySelector(':scope > ' + n + ' > text'); return t ? t.textContent : ''; };
      bodies.push({ name: child.localName, header: part('header'), detail: part('detail'), footer: part('footer') });
    }
    const note = a.querySelector(':scope > notification');
    const content = a.querySelector(':scope > content > text');
    const commands = {};
    for (const c of a.querySelectorAll(':scope > query[id="report"] > command')) {
      const t = c.querySelector(':scope > text');
      if (t) commands[c.getAttribute('id')] = t.textContent;
    }
    return {
      fieldNames: [...a.querySelectorAll('fields > field')].map((f) => f.getAttribute('name')),
      channel, key: channel + ':' + a.getAttribute('id'), content: content ? content.textContent : null, commands,
      id: a.getAttribute('id'), table: a.getAttribute('table') || '', v: a.getAttribute('v') || '', e: a.getAttribute('e') || a.getAttribute('v') || '',
      notification: note ? { v: note.getAttribute('v') || '', e: note.getAttribute('e') || '' } : null,
      fields, bodies, queries: [...a.querySelectorAll(':scope > query')].map((q) => q.getAttribute('id')).filter(Boolean),
    };
  }

  fillSelectors() {
    const keep = this.actionId;
    const opt = (a) => `<option value="${mpEsc(a.key)}">${mpEsc(a.id)}${a.dup ? ' (#' + a.dup + ')' : ''}${a.v ? ' — ' + mpEsc(a.v) : ''}</option>`;
    const mail = this.actions.filter((a) => a.channel === 'mail'), sms = this.actions.filter((a) => a.channel === 'sms');
    this.selAction.innerHTML = (mail.length ? `<optgroup label="Email (${mail.length})">${mail.map(opt).join('')}</optgroup>` : '') +
      (sms.length ? `<optgroup label="SMS (${sms.length})">${sms.map(opt).join('')}</optgroup>` : '');
    this.actionId = this.actions.some((a) => a.key === keep) ? keep : this.actions[0].key;
    this.selAction.value = this.actionId;
  }

  current() { return this.actions.find((a) => a.key === this.actionId) || this.actions[0]; }

  /// Đổi form đang xem: nạp dữ liệu mẫu đã nhớ của form đó, dựng lại ô nhập dữ liệu thật.
  onActionChanged() {
    const a = this.current();
    if (!a) return;
    let raw = '';
    try { raw = localStorage.getItem('mp.sample.' + a.key) || ''; } catch { /* storage blocked */ }
    this.sampleText.value = raw;
    this.sample = this.parseSample(raw);
    this.realStatus.textContent = this.real.has(a.key) ? this.realSummary(this.real.get(a.key)) : '';
    if (this.realBox.style.display !== 'none') this.buildRealFields();
  }

  // ---- 1. Dữ liệu mẫu -----------------------------------------------------------------------

  parseSample(text) {
    const out = {};
    for (const line of String(text || '').split(/\r?\n/)) {
      const i = line.indexOf('=');
      if (i <= 0) continue;
      const name = line.slice(0, i).trim().toLowerCase();
      if (!name) continue;
      out[name] = line.slice(i + 1).split('|').map((s) => s.trim());
    }
    return out;
  }

  saveSample(raw) {
    const a = this.current();
    if (!a) return;
    try { if (raw.trim()) localStorage.setItem('mp.sample.' + a.key, raw); else localStorage.removeItem('mp.sample.' + a.key); } catch { /* storage blocked */ }
  }

  // ---- Ghép nội dung ------------------------------------------------------------------------

  /// Giá trị của {!name} ở dòng thứ `row` (-1 = phần đầu/cuối, không lặp): dữ liệu thật > dữ liệu mẫu > null (giữ nguyên {!name}).
  valueOf(a, name, row) {
    const key = name.toLowerCase();
    const real = this.real.get(a.key);
    if (real) {
      if (row >= 0 && real.rows[row] && key in real.rows[row]) return real.rows[row][key];
      if (key in real.master) return real.master[key];
    }
    const s = this.sample[key];
    if (s && s.length) return s[row >= 0 ? row % s.length : 0];
    return null;
  }

  /// Thay {!...} trong 1 đoạn HTML (header/detail/footer). row = chỉ số dòng chi tiết, -1 cho đầu/cuối.
  substitute(a, s, row, used) {
    const L = this.lang;
    return s.replace(/\{!([\w.]+)\}/g, (m, name, offset, whole) => {
      const before = whole.slice(0, offset);
      const inTag = before.lastIndexOf('<') > before.lastIndexOf('>');   // đang nằm trong thẻ (thuộc tính) → không chèn <span>
      const f = a.fields.get(name);
      if (f) {
        used.set(name, 'header');
        const t = mpEsc(f[L] || f.v);
        return inTag ? t : `<span data-h="${name}">${t}</span>`;
      }
      if (/^s\d+$/.test(name)) return '';                                           // {!s0}... = lớp CSS chọn lúc chạy
      if (name === 'slink' || name === 'nlink' || name === 'smsg') return '';       // display:{!slink} → hiện
      used.set(name, 'data');
      const val = this.valueOf(a, name, row);
      if (/link$/i.test(name)) return val != null ? mpEsc(val) : '#';
      if (val == null) return inTag ? m : `<span class="mpVar" data-v="${name}">{!${name}}</span>`;
      return inTag ? mpEsc(val) : `<span class="mpVal" data-v="${name}">${mpEsc(val)}</span>`;
    });
  }

  /// Ghép header + các dòng detail + footer. Số dòng: dữ liệu thật (tối đa MP_MAX_ROWS) > số giá trị nhiều nhất trong dữ liệu mẫu > 3.
  compose(a, body) {
    const used = new Map();
    const real = this.real.get(a.key);
    let n = 3;
    if (real && real.rows.length) n = Math.min(real.rows.length, MP_MAX_ROWS);
    else { const lens = Object.values(this.sample).map((v) => v.length); if (lens.length) n = Math.max(3, Math.min(MP_MAX_ROWS, Math.max(...lens))); }
    const rows = Array.from({ length: n }, (_, i) => this.substitute(a, body.detail, i, used)).join('');
    const html = this.substitute(a, body.header, -1, used) + rows + this.substitute(a, body.footer, -1, used);
    return { html, used, rowCount: n };
  }

  draw() {
    if (!this.actions.length) return;
    const a = this.current();
    if (a.channel === 'sms') { this.drawSms(a); return; }
    this.selBody.innerHTML = a.bodies.map((b) => `<option value="${mpEsc(b.name)}">${mpEsc(b.name)}</option>`).join('');
    this.bodyName = a.bodies.some((b) => b.name === this.bodyName) ? this.bodyName : (a.bodies[0] && a.bodies[0].name);
    this.selBody.value = this.bodyName || '';
    this.selBody.disabled = a.bodies.length < 2;

    const body = a.bodies.find((b) => b.name === this.bodyName);
    this.info.innerHTML = `<b>${mpEsc(this.lang === 'e' ? a.e : a.v)}</b>` +
      (a.table ? ` · bảng <code>${mpEsc(a.table)}</code>` : '') +
      (a.notification ? ` · thông báo: ${mpEsc(this.lang === 'e' ? (a.notification.e || a.notification.v) : a.notification.v)}` : '') +
      (a.queries.length ? ` · query: ${a.queries.map(mpEsc).join(', ')}` : '') + (this.real.has(a.key) ? ' · <span class="mpReal1">dữ liệu thật</span>' : '');
    if (!body) { this.showHtml('<p style="font:13px sans-serif;color:#b3261e;padding:16px">Form này chưa có phần &lt;body&gt;.</p>'); this.vars.innerHTML = ''; return; }

    const { html, used } = this.compose(a, body);
    this.lastHtml = html;
    this.showHtml(html);
    this.code.textContent = html.replace(/<span [^>]*data-(?:v|h)="[^"]*"[^>]*>([\s\S]*?)<\/span>/g, '$1');
    this.code.style.display = this.showSource ? 'block' : 'none';
    this.frame.style.display = this.showSource ? 'none' : 'block';

    const data = [...used].filter(([, k]) => k === 'data').map(([n]) => n);
    const heads = [...used].filter(([, k]) => k === 'header').length;
    this.vars.innerHTML = `<span class="mpVarsT">Biến dữ liệu (${data.length}) — bấm để nhảy tới code:</span> ` +
      (data.map((n) => `<span class="mpVar mpChip" data-name="${mpEsc(n)}">${mpEsc(n)}</span>`).join(' ') || '—') + `<span class="mpVarsT"> · ${heads} tiêu đề lấy từ &lt;fields&gt; (nhấp đúp trên mail để sửa)</span>`;
    this.vars.querySelectorAll('.mpChip').forEach((c) => { c.onclick = () => this.jump(c.dataset.name, 'var'); });
    this.footerWarnings(html);
  }

  /// SMS: <content><text> là chữ thuần (xuống dòng giữ nguyên) — vẽ dạng bong bóng tin nhắn, báo độ dài so với <maxLength> của <sms><setting>.
  drawSms(a) {
    this.selBody.innerHTML = ''; this.selBody.disabled = true;
    const used = new Map();
    const raw = (a.content || '').replace(/^\s*\n/, '').replace(/\s+$/, '');
    const L = this.lang;
    let plain = raw.replace(/\{!([\w.]+)\}/g, (m, name) => {
      const f = a.fields.get(name);
      if (f) { used.set(name, 'header'); return f[L] || f.v; }
      used.set(name, 'data');
      const v = this.valueOf(a, name, -1);
      return v != null ? v : m;
    });
    const len = plain.length, over = this.smsMax && len > this.smsMax;
    this.info.innerHTML = `<b>SMS ${mpEsc(a.id)}</b>` + (a.table ? ` · bảng <code>${mpEsc(a.table)}</code>` : '') +
      ` · ${len} ký tự` + (this.smsMax ? ` / tối đa ${this.smsMax} (cấu hình <code>maxLength</code>)` : '') +
      (over ? ' — <span style="color:#e5484d">vượt, tin sẽ bị cắt / tách</span>' : '') + ' <span style="opacity:.7">(độ dài chỉ chính xác khi đã có dữ liệu)</span>';
    const pieces = raw.split(/(\{![\w.]+\})/).map((seg) => {
      const m = /^\{!([\w.]+)\}$/.exec(seg);
      if (!m) return mpEsc(seg);
      const name = m[1], f = a.fields.get(name);
      if (f) return `<span data-h="${name}">${mpEsc(f[L] || f.v)}</span>`;
      const v = this.valueOf(a, name, -1);
      return v != null ? `<span class="mpVal" data-v="${name}">${mpEsc(v)}</span>` : `<span class="mpVar" data-v="${name}">{!${name}}</span>`;
    }).join('');
    const html = `<div style="font:14px 'Segoe UI',sans-serif;padding:8px"><div style="max-width:340px;margin:0 auto;background:#e9eef7;border-radius:14px;padding:12px 14px;white-space:pre-wrap;word-break:break-word;line-height:1.5;box-shadow:0 1px 2px rgba(0,0,0,.15)">` +
      (a.content == null ? '<i>Form SMS này chưa có &lt;content&gt;.</i>' : pieces) + '</div></div>';
    this.showHtml(html);
    this.code.textContent = plain;
    this.code.style.display = this.showSource ? 'block' : 'none';
    this.frame.style.display = this.showSource ? 'none' : 'block';
    const data = [...used].filter(([, k]) => k === 'data').map(([n]) => n);
    this.vars.innerHTML = `<span class="mpVarsT">Biến dữ liệu (${data.length}) — thay lúc gửi SMS:</span> ` + (data.map((n) => `<span class="mpVar mpChip" data-name="${mpEsc(n)}">${mpEsc(n)}</span>`).join(' ') || '—');
    this.vars.querySelectorAll('.mpChip').forEach((c) => { c.onclick = () => this.jump(c.dataset.name, 'var'); });
    this.footer.textContent = this.unresolved && this.unresolved.length ? 'Entity chưa đọc được (bỏ qua): ' + this.unresolved.slice(0, 8).join(', ') : '';
  }

  // ---- Iframe -------------------------------------------------------------------------------

  /// Làm sạch HTML của mẫu (bỏ script / iframe / on*= / link ngoài...) rồi đẩy vào iframe (giữ vị trí cuộn, không nháy).
  showHtml(html) {
    const parsed = new DOMParser().parseFromString(/<html[\s>]/i.test(html) ? html : '<html><body>' + html + '</body></html>', 'text/html');
    parsed.querySelectorAll('script,iframe,object,embed,link,meta,base,form').forEach((n) => n.remove());
    parsed.querySelectorAll('*').forEach((el) => {
      for (const at of [...el.attributes]) {
        if (/^on/i.test(at.name) || (/^(href|src|xlink:href|action)$/i.test(at.name) && /^\s*javascript:/i.test(at.value))) el.removeAttribute(at.name);
      }
    });
    this.pending = { head: parsed.head.innerHTML, body: parsed.body.innerHTML };
    if (this.frameReady) this.postFrame();
  }

  postFrame() {
    if (!this.pending || !this.frame.contentWindow) return;
    this.frame.contentWindow.postMessage({ mp: 3, head: this.pending.head, body: this.pending.body }, '*');
  }

  onFrameMessage(e) {
    if (e.source !== this.frame.contentWindow || !e.data || e.data.mp !== 1) return;
    const d = e.data;
    if (d.type === 'ready') { this.frameReady = true; this.postFrame(); }
    else if (d.type === 'click') this.jump(d.name, d.kind);
    else if (d.type === 'edit') this.editHeader(d.name, d.text);
    else if (d.type === 'size') this.onSize(d.sw, d.cw);
  }

  /// Nội dung rộng hơn khung → tràn ngang (cuộn ngang trên điện thoại, bị cắt ở vài client).
  onSize(sw, cw) {
    this.overflow = sw > cw + 2 ? { sw, cw } : null;
    this.footerWarnings(this.lastHtml || '');
  }

  // ---- 4. Cảnh báo tương thích client mail --------------------------------------------------

  footerWarnings(html) {
    const w = [];
    if (this.overflow) w.push(`Nội dung rộng ${this.overflow.sw}px > khung ${this.overflow.cw}px — sẽ tràn ngang (cuộn ngang trên điện thoại). Thu nhỏ width cố định của cột / bảng.`);
    const css = (/<style[\s\S]*?<\/style>/i.exec(html) || [''])[0] + ' ' + (html.match(/style\s*=\s*"[^"]*"/gi) || []).join(' ');
    const risky = [['border-radius', /border-radius/i], ['box-shadow', /box-shadow/i], ['display:flex/grid', /display\s*:\s*(?:flex|grid)/i],
      ['position:absolute/fixed', /position\s*:\s*(?:absolute|fixed)/i], ['max-width', /max-width/i], ['background-image', /background-image/i], ['gap', /\bgap\s*:/i]]
      .filter(([, re]) => re.test(css)).map(([n]) => n);
    if (risky.length) w.push('Outlook desktop (dùng engine Word) bỏ qua một số CSS: ' + risky.join(', ') + '.');
    if (/<style\b/i.test(html)) w.push('Màu / cỡ chữ đặt bằng <style> ở <head> (class .ts, .r4...): một số client (Gmail app với tài khoản ngoài Gmail, vài webmail) bỏ khối này — nên chuyển thành style="..." trong từng thẻ nếu cần chắc chắn.');
    const un = this.unresolved && this.unresolved.length ? 'Entity chưa đọc được (bỏ qua): ' + this.unresolved.slice(0, 8).join(', ') + (this.unresolved.length > 8 ? '…' : '') : '';
    this.footer.innerHTML = [...w, un].filter(Boolean).map(mpEsc).join('<br>');
  }

  // ---- 2 + 3. Nhảy qua lại mail ↔ code, sửa tiêu đề -----------------------------------------

  /// Khoảng [start,end) của <action> đang xem trong text GỐC của file (không phải bản đã khai triển); null nếu form nằm ở file include.
  actionRange(a) {
    const model = this.bcode.currentModel;
    if (!model) return null;
    const text = model.getValue();
    const re = new RegExp('<action\\b[^>]*\\bid\\s*=\\s*"' + a.id.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '"', 'gi');
    const smsAt = text.search(/<sms\b/i);
    const want = (a.fieldNames || []).join('|');
    let m, firstOnly = null, count = 0;
    while ((m = re.exec(text))) {
      const isSms = smsAt >= 0 && m.index > smsAt;
      if ((a.channel === 'sms') !== isSms) continue;
      const end = text.indexOf('</action>', m.index);
      const range = { text, start: m.index, end: end < 0 ? text.length : end };
      count++; if (!firstOnly) firstOnly = range;
      // Trùng id: bản nào trong file này có đúng danh sách <field> của form đang xem thì là bản đó (bản kia nằm ở file include).
      const names = [...text.slice(range.start, range.end).matchAll(/<field\b[^>]*\bname\s*=\s*"([^"]+)"/gi)].map((x) => x[1]).join('|');
      if (names === want) return range;
    }
    return a.dup ? null : (count === 1 ? firstOnly : null);
  }

  select(text, from, to) {
    const model = this.bcode.currentModel, ed = this.bcode.editor;
    const s = model.getPositionAt(from), e = model.getPositionAt(to);
    const range = new monaco.Range(s.lineNumber, s.column, e.lineNumber, e.column);
    ed.setSelection(range);
    ed.revealRangeInCenterIfOutsideViewport(range);
    ed.focus();
  }

  jump(name, kind) {
    const a = this.current();
    const r = a && this.actionRange(a);
    if (!r) { this.flash('Form này nằm trong file include (&entity;) — mở file đó để sửa.'); return; }
    const esc = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    if (kind === 'hdr') {
      const m = new RegExp('<field\\b[^>]*\\bname\\s*=\\s*"' + esc + '"[^>]*>', 'i').exec(r.text.slice(r.start, r.end));
      if (m) { this.select(r.text, r.start + m.index, r.start + m.index + m[0].length); return; }
      this.flash(`Không thấy <field name="${name}"> trong file này (có thể nằm ở file include).`);
      return;
    }
    const i = r.text.indexOf('{!' + name + '}', r.start);
    if (i >= 0 && i < r.end) { this.select(r.text, i, i + name.length + 3); return; }
    this.flash(`Không thấy {!${name}} trong form này ở file đang mở (có thể nằm ở file include).`);
  }

  /// Sửa <header v="..."/e="..."> của <field name="name"> theo ngôn ngữ đang xem.
  editHeader(name, newText) {
    const a = this.current();
    const r = a && this.actionRange(a);
    if (!r) { this.flash('Form này nằm trong file include — mở file đó để sửa tiêu đề.'); this.draw(); return; }
    const esc = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const slice = r.text.slice(r.start, r.end);
    const f = new RegExp('<field\\b[^>]*\\bname\\s*=\\s*"' + esc + '"[^>]*>', 'i').exec(slice);
    if (!f) { this.flash(`Không thấy <field name="${name}"> trong file này — không sửa được.`); this.draw(); return; }
    const hFrom = f.index + f[0].length;
    const h = /<header\b[^>]*>/i.exec(slice.slice(hFrom, hFrom + 400));
    if (!h) { this.flash(`<field name="${name}"> chưa có <header>.`); this.draw(); return; }
    const tagStart = r.start + hFrom + h.index, tag = h[0];
    const attr = this.lang;                                        // v | e
    const value = newText.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;');
    const at = new RegExp('(\\s' + attr + '\\s*=\\s*")[^"]*(")', 'i');
    const newTag = at.test(tag) ? tag.replace(at, (m0, p1, p2) => p1 + value + p2) : tag.replace(/\s*\/?>$/, (end) => ` ${attr}="${value}"${end.trim() === '/>' ? '/>' : '>'}`);
    const model = this.bcode.currentModel;
    const s = model.getPositionAt(tagStart), e = model.getPositionAt(tagStart + tag.length);
    this.bcode.editor.executeEdits('mailpreview', [{ range: new monaco.Range(s.lineNumber, s.column, e.lineNumber, e.column), text: newTag }]);
    this.flash(`Đã sửa ${attr === 'v' ? 'tiêu đề tiếng Việt' : 'tiêu đề tiếng Anh'} của ${name}.`);
  }

  /// Con trỏ ở code → sáng phần tương ứng trên mail: {!biến} dưới con trỏ, hoặc <field name="..."> (kể cả dòng <header> bên trong nó).
  highlightFromCursor() {
    if (!this.open || !this.frameReady || !this.frame.contentWindow) return;
    const model = this.bcode.currentModel, pos = this.bcode.editor.getPosition();
    if (!model || !pos) return;
    let name = '';
    const line = model.getLineContent(pos.lineNumber);
    const re = /\{!([\w.]+)\}/g;
    let m;
    while ((m = re.exec(line))) if (pos.column > m.index && pos.column <= m.index + m[0].length + 1) { name = m[1]; break; }
    if (!name) {
      for (let ln = pos.lineNumber; ln >= Math.max(1, pos.lineNumber - 3); ln--) {
        const f = /<field\b[^>]*\bname\s*=\s*"([^"]+)"/i.exec(model.getLineContent(ln));
        if (f) { name = f[1]; break; }
        if (/<\/field>|<fields\b|<action\b/i.test(model.getLineContent(ln)) && ln !== pos.lineNumber) break;
      }
    }
    this.frame.contentWindow.postMessage({ mp: 2, name }, '*');
  }

  flash(text) {
    this.status.textContent = text;
    this.footer.textContent = text;
    this.footer.style.fontWeight = '600';
    clearTimeout(this.flashTimer);
    this.flashTimer = setTimeout(() => { this.status.textContent = this.actions.length ? this.actions.length + ' form' : ''; this.footer.style.fontWeight = ''; this.draw(); }, 5000);
  }

  // ---- 5. Dữ liệu thật: chạy query master + detail (luôn rollback) -----------------------------

  /// Macro @@xxx dùng trong 2 query master / detail của form → ô nhập giá trị (nhớ giữa các lần và dùng chung với "Chạy SQL" của viewer).
  macrosOf(a) {
    const set = new Set();
    for (const id of ['master', 'detail']) for (const m of (a.commands[id] || '').matchAll(/@@\w+/g)) set.add(m[0]);
    return [...set];
  }

  macroStore() { return (window.bcodeSqlRun && window.bcodeSqlRun.macroValues) || (this._macros = this._macros || {}); }

  buildRealFields() {
    const a = this.current();
    if (!a || a.channel === 'sms') { this.realFields.innerHTML = '<span class="mpHint">Dữ liệu thật chỉ dùng cho email (form có query master / detail).</span>'; return; }
    if (!a.commands.master && !a.commands.detail) { this.realFields.innerHTML = '<span class="mpHint">Form này không có &lt;query id="report"&gt; với command master / detail.</span>'; return; }
    const store = this.macroStore();
    const macros = this.macrosOf(a);
    const defaults = { '@@table': a.table, '@@language': "'v'" };
    this.realFields.innerHTML = macros.map((n) => {
      let val = store[n];
      if (val == null) { try { val = localStorage.getItem('mp.macro.' + n); } catch { val = null; } }
      if (val == null) val = defaults[n] || '';
      return `<label class="mpMacro"><span>${mpEsc(n)}</span><input type="text" data-m="${mpEsc(n)}" value="${mpEsc(val)}" spellcheck="false"></label>`;
    }).join('') + '<div class="mpHint">Giá trị là SQL thô (vd <code>\'v\'</code>, <code>\'ABC123\'</code>, tên database). <code>@@stt_rec</code> = khoá chứng từ cần xem thử. Chỉ chạy ở chế độ thử (rollback), không ghi dữ liệu.</div>';
  }

  realSummary(r) { return `Đã nạp: master ${r.masterRows} dòng, detail ${r.rows.length} dòng` + (r.truncated ? ` (hiện ${MP_MAX_ROWS})` : '') + '.'; }

  async runReal() {
    const a = this.current();
    if (!a || a.channel === 'sms' || (!a.commands.master && !a.commands.detail)) { this.realStatus.textContent = 'Form này không có query master / detail để chạy.'; return; }
    if (this.running) return;
    const host = window.chrome && window.chrome.webview && window.chrome.webview.hostObjects && window.chrome.webview.hostObjects.host;
    if (!host) { this.realStatus.textContent = 'Không gọi được host để chạy SQL.'; return; }
    const store = this.macroStore();
    this.realFields.querySelectorAll('input[data-m]').forEach((i) => { store[i.dataset.m] = i.value; try { localStorage.setItem('mp.macro.' + i.dataset.m, i.value); } catch { /* storage blocked */ } });
    const sr = window.bcodeSqlRun;
    if (!sr || typeof sr.applyMacros !== 'function') { this.realStatus.textContent = 'Thiếu mô-đun chạy SQL của viewer.'; return; }

    this.running = true;
    const run = async (id) => {
      const raw = (a.commands[id] || '').trim();
      if (!raw) return null;
      let info;
      try { info = JSON.parse(await host.InspectSql(raw)); } catch (e) { throw new Error('Phân tích ' + id + ': ' + e); }
      const macros = info.macros || [];
      if (/(\w)%l(?![\w$#])/i.test(raw) && !macros.some((m) => m.name.toLowerCase() === '@@language')) macros.push({ name: '@@language', value: "'v'" });
      const params = {};
      for (const n of info.parameters || []) params[n] = (sr.paramValues && sr.paramValues[n]) || '';
      const sql = sr.applyMacros(raw, macros);
      const started = JSON.parse(await host.StartSql(sql, JSON.stringify(params), true));   // true = LUÔN rollback
      if (started.error) throw new Error(started.error);
      for (let t = 0; t < 240; t++) {                                                       // tối đa ~60s
        await new Promise((r) => setTimeout(r, 250));
        const res = JSON.parse(await host.PollSql(started.runId));
        if (res.status === 'running') { this.realStatus.textContent = `Đang chạy ${id}… ${Math.round(t / 4)}s`; continue; }
        if (res.error) throw new Error(res.error);
        const bad = (res.batches || []).find((b) => b.error);
        if (bad) throw new Error(`${id}: ${bad.error}`);
        const tables = (res.batches || []).flatMap((b) => b.tables || []);
        return tables;
      }
      throw new Error('Quá thời gian chờ query ' + id);
    };
    const toMaps = (table) => (table ? table.rows.map((row) => Object.fromEntries(table.columns.map((c, i) => [String(c.name).toLowerCase(), row[i] == null ? '' : row[i]]))) : []);
    try {
      this.realStatus.textContent = 'Đang chạy master…';
      const m = await run('master');
      const masterRows = toMaps(m && m[0]);
      this.realStatus.textContent = 'Đang chạy detail…';
      const d = await run('detail');
      const all = toMaps(d && d[0]);
      const data = { master: masterRows[0] || {}, masterRows: masterRows.length, rows: all.slice(0, MP_MAX_ROWS), truncated: all.length > MP_MAX_ROWS };
      this.real.set(a.key, data);
      this.realStatus.textContent = this.realSummary(data) + (masterRows.length ? '' : ' (master không có dòng nào — kiểm tra @@stt_rec)');
      this.draw();
    } catch (e) {
      this.realStatus.textContent = 'Lỗi: ' + (e && e.message ? e.message : e);
    } finally { this.running = false; }
  }

  message(text, isError) {
    this.status.textContent = '';
    this.actions = [];
    this.info.textContent = '';
    this.vars.innerHTML = '';
    this.footer.textContent = '';
    this.showHtml('<p style="font:13px Segoe UI,sans-serif;padding:16px;white-space:pre-wrap;color:' + (isError ? '#b3261e' : '#555') + '">' + mpEsc(text) + '</p>');
    this.frame.style.display = 'block'; this.code.style.display = 'none';
  }
}

function mpEsc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
