// Bhoo — cửa sổ 1 cuộc chat (1-1 hoặc phòng nhóm).
'use strict';

const conv = new URLSearchParams(location.search).get('c') || '';
const isGroup = conv.startsWith('g:');
let me = null, title = '', group = null, buddies = [];
const msgs = new Map();          // mid → { m, mine, pending, saved, row }
const files = new Map();         // fid → { state, got, total, info, served: [] }
let lastDay = '', font = { style: '', fg: '#000000', size: 13 }, showTime = true;
let typingT = 0, lastTypingSent = 0, buzzLock = 0;

const buddy = id => buddies.find(b => b.id === id);
const peerId = () => isGroup ? null : conv.slice(2);
const nameOf = id => id === me?.id ? me.name : (buddy(id)?.name || (group?.members || []).find(x => x.id === id)?.name || id);

onHost(m => {
  switch (m.t) {
    case 'init':
      me = m.me; title = m.title; group = m.group; buddies = m.list || [];
      font = Object.assign(font, m.me.font || {});
      applyFont();
      $('#log').innerHTML = ''; msgs.clear(); lastDay = ''; Scene.set('');
      for (const h of m.history || []) addMsg(h.m, h.mine, h.pending, h.saved, true);
      if (!(m.history || []).length) sys(isGroup ? 'Phòng chat đã mở. Tin nhắn ở đây gửi tới mọi người trong phòng.' : `Bắt đầu trò chuyện với ${title}. Gõ :) để chèn mặt cười, bấm BUZZ! để gọi.`);
      renderHead(); scrollEnd(); $('#inp').focus();
      break;
    case 'msg': addMsg(m.m, m.mine, m.pending, null); scrollEnd(); if (!m.mine) $('#sb').textContent = ''; break;
    case 'delivered': {
      const x = msgs.get(m.mid);
      if (x && x.pending) { x.pending = x.pending.filter(id => id !== m.to); renderPending(x); }
      renderHead();
      break;
    }
    case 'typing':
      if (m.from === me?.id) break;
      $('#sb').textContent = `${m.name || nameOf(m.from)} đang gõ tin nhắn…`;
      clearTimeout(typingT); typingT = setTimeout(() => $('#sb').textContent = '', 4500);
      break;
    case 'buddies': buddies = m.list; renderHead(); break;
    case 'groups': if (isGroup) { const g = (m.list || []).find(x => 'g:' + x.gid === conv); if (g) { group = g; title = g.title; renderHead(); } } break;
    case 'title': title = m.title; if (m.group) group = m.group; renderHead(); break;
    case 'me': me = m.me; renderHead(); break;
    case 'file': fileState(m.fid, { state: m.state, got: m.got, total: m.total, info: m.info }); break;
    case 'served': { const f = files.get(m.fid) || {}; f.served = (f.served || []).concat(m.name); files.set(m.fid, f); refreshFile(m.fid); break; }
    case 'buzzfx': document.body.classList.remove('shake'); void document.body.offsetWidth; document.body.classList.add('shake'); break;
    case 'focus': $('#inp').focus(); break;
    case 'error': toast(m.text, 5000); break;
  }
});

// ───────────── Đầu cửa sổ + cột bên ─────────────
function renderHead() {
  if (!me) return;
  const info = $('#info'), side = $('#side');
  if (isGroup) {
    const mem = group?.members || [];
    info.innerHTML = `<span>👥</span><span>Bạn đang ở phòng <b>"${esc(title)}"</b></span><span class="stx">— ${mem.length} người</span>`;
    document.title = 'Phòng chat: ' + title;
    side.innerHTML = `<div class="hd">Người tham gia (${mem.length})</div><div id="members" class="sunken"></div>`;
    const box = $('#members');
    mem.slice().sort((a, b) => (a.id === me.id ? -1 : b.id === me.id ? 1 : a.name.localeCompare(b.name, 'vi'))).forEach(x => {
      const st = x.id === me.id ? me.status : (buddy(x.id)?.status || 'offline');
      const r = el('div', { class: 'mrow' + (st === 'offline' ? ' offl' : ''), title: x.id === me.id ? 'Bạn' : 'Nhấp đúp để nhắn riêng' }, `${stHtml(st)}<span>${esc(x.name)}${x.id === me.id ? ' (bạn)' : ''}</span>`);
      if (x.id !== me.id) r.ondblclick = () => send('open.buddy', { id: x.id });
      box.append(r);
    });
    const inv = el('button', { class: 'btn' }, '👥 Mời thêm…'); inv.onclick = invite; side.append(inv);
  } else {
    const b = buddy(peerId()) || { name: title, status: 'offline', color: '#888', text: '' };
    const stx = b.ignored ? 'bạn đã chặn nick này — tin của họ sẽ không hiện (bỏ chặn ở Tuỳ chọn của danh sách bạn)'
      : b.status === 'offline' ? 'đang offline — tin sẽ được gửi khi họ online' : (b.text || STATUS_TEXT[b.status]);
    info.innerHTML = `${stHtml(b.status)}<b>${esc(b.name)}</b><span class="stx">— ${esc(stx)}</span>`;
    document.title = b.name + ' — Bhoo';
    const pic = (p, label, mine) => `<div class="pic" style="${avStyle(p, mine)}">${p.av ? '' : esc((p.name || '?')[0].toUpperCase())}<span>${esc(label)}</span></div>`;
    side.innerHTML = `<div class="pics">${pic(b, b.name)}${pic(me, me.name, true)}</div>`;
  }
}

// ───────────── Dòng tin ─────────────
function dayMark(at) {
  const d = new Date(at).toDateString();
  if (d === lastDay) return;
  lastDay = d;
  $('#log').append(el('div', { class: 'day' }, esc(fmtDay(at))));
}

function sys(text) { $('#log').append(el('div', { class: 'sys' }, esc(text))); }


function addMsg(m, mine, pending, saved, fromHistory) {
  if (!m || msgs.has(m.mid)) return;
  const x = { m, mine, pending: pending || null, row: null };
  msgs.set(m.mid, x);
  dayMark(m.at);
  const who = mine ? me.name : (m.fromName || nameOf(m.from));
  const tm = showTime ? ` <span class="tm">(${fmtTime(m.at)})</span>` : '';
  let row;
  switch (m.kind) {
    case 'buzz':
      row = el('div', { class: 'buzz' }, `BUZZ!!! <small>${mine ? 'Bạn đã gửi BUZZ!' : esc(who) + ' gửi BUZZ!'} (${fmtTime(m.at)})</small>`);
      break;
    case 'env':
      Scene.set(m.text);
      row = el('div', { class: 'sys' }, `🎨 ${mine ? 'Bạn' : esc(who)} đã đổi khung cảnh: <b>${esc(SCENES[m.text]?.name || 'không có')}</b> <span class="tm">(${fmtTime(m.at)})</span>`);
      break;
    case 'sys': case 'leave':
      row = el('div', { class: 'sys' }, esc(m.text) + ` <span class="tm">(${fmtTime(m.at)})</span>`);
      break;
    case 'file':
      row = el('div', { class: 'ln' }, `<span class="who ${mine ? 'me' : 'them'}">${esc(who)}</span>${tm}:<br>`);
      row.append(fileCard(m, mine));
      if (saved) files.set(m.file.fid, Object.assign(files.get(m.file.fid) || {}, { state: 'done', info: saved }));
      break;
    default:
      row = el('div', { class: 'ln' }, `<span class="who ${mine ? 'me' : 'them'}">${esc(who)}</span>${tm}: <span class="txt" style="${msgStyle(m)}">${richText(m.text)}</span>`);
  }
  x.row = row;
  $('#log').append(row);
  if (mine && m.kind !== 'sys' && m.kind !== 'env') renderPending(x);
  if (m.kind === 'file') refreshFile(m.file.fid);
}

function renderPending(x) {
  if (!x.row) return;
  let p = $('.pend, .ok', x.row);
  if (p) p.remove();
  if (x.m.kind === 'file') return;
  const left = x.pending || [];
  if (left.length) {
    p = el('span', { class: 'pend', title: 'Chờ gửi tới: ' + left.map(nameOf).join(', ') + '\nTin sẽ tự gửi khi họ online.' }, '⏳');
  } else p = el('span', { class: 'ok', title: 'Đã tới máy người nhận' }, '✓');
  (x.m.kind === 'buzz' ? $('small', x.row) : x.row).append(p);
}

// ───────────── File ─────────────
function fileCard(m, mine) {
  const f = m.file;
  const card = el('div', { class: 'filecard', 'data-fid': f.fid },
    `<span class="fi">📄</span><div><div class="fn">${esc(f.name)}</div><div class="fs">${fmtSize(f.size)}</div><div class="fst muted"></div><div class="acts"></div></div>`);
  card.dataset.mine = mine ? '1' : '';
  card.dataset.mid = m.mid;
  return card;
}

function fileState(fid, s) {
  const f = files.get(fid) || {};
  Object.assign(f, s);
  files.set(fid, f);
  refreshFile(fid);
}

function refreshFile(fid) {
  const card = $(`.filecard[data-fid="${CSS.escape(fid)}"]`);
  if (!card) return;
  const x = msgs.get(card.dataset.mid);
  const f = files.get(fid) || {};
  const st = $(".fst", card), acts = $('.acts', card);
  acts.innerHTML = '';
  const btn = (label, on) => { const b = el('button', { class: 'btn' }, label); b.onclick = on; acts.append(b); return b; };
  if (card.dataset.mine) {
    const left = x?.pending || [];
    const parts = [];
    if (left.length) parts.push('⏳ Chờ ' + left.map(nameOf).join(', ') + ' online để nhận lời mời');
    else parts.push('Đã gửi lời mời nhận file — giữ Bhoo mở để người nhận tải được');
    if (f.served && f.served.length) parts.push('✓ ' + [...new Set(f.served)].join(', ') + ' đã nhận xong');
    st.innerHTML = parts.map(esc).join('<br>');
    return;
  }
  if (f.state === 'run') {
    const pct = f.total ? Math.floor(f.got * 100 / f.total) : 0;
    st.innerHTML = `Đang nhận… ${pct}% (${fmtSize(f.got)} / ${fmtSize(f.total)})<div class="bar"><i style="width:${pct}%"></i></div>`;
  } else if (f.state === 'done') {
    st.textContent = '✓ Đã lưu: ' + f.info;
    btn('Mở', () => send('file.open', { path: f.info }));
    btn('Mở thư mục', () => send('file.folder', { path: f.info }));
  } else {
    st.innerHTML = f.state === 'err' ? `<span style="color:#c0281c">${esc(f.info)}</span>` : 'Bấm Nhận để tải về Downloads\\Bhoo';
    btn(f.state === 'err' ? 'Thử lại' : '⬇ Nhận', () => { fileState(fid, { state: 'run', got: 0, total: x?.m.file.size || 0 }); send('file.get', { mid: card.dataset.mid }); });
  }
}

// ───────────── Gõ + gửi ─────────────
function applyFont() {
  const inp = $('#inp');
  inp.style.fontWeight = font.style.includes('b') ? 'bold' : '';
  inp.style.fontStyle = font.style.includes('i') ? 'italic' : '';
  inp.style.textDecoration = font.style.includes('u') ? 'underline' : '';
  inp.style.color = font.fg;
  inp.style.fontSize = font.size + 'px';
  $('#fB').classList.toggle('on', font.style.includes('b'));
  $('#fI').classList.toggle('on', font.style.includes('i'));
  $('#fU').classList.toggle('on', font.style.includes('u'));
  $('#fg').value = font.fg;
  $('#fSize').value = String(font.size);
}
function toggleStyle(c) {
  font.style = font.style.includes(c) ? font.style.replace(c, '') : font.style + c;
  applyFont(); saveFont(); $('#inp').focus();
}
let fontT = 0;
function saveFont() { clearTimeout(fontT); fontT = setTimeout(() => send('font', font), 500); }

function doSend() {
  const inp = $('#inp');
  const text = inp.value;
  if (!text.trim()) return;
  send('send', { text, style: font.style || null, fg: font.fg !== '#000000' ? font.fg : null, size: font.size !== 13 ? font.size : null });
  inp.value = '';
  inp.focus();
}

function doBuzz() {
  const now = Date.now();
  if (now < buzzLock) { toast('Chờ chút rồi BUZZ! tiếp nhé.'); return; }
  buzzLock = now + 3000;
  ['#tBuzz', '#fBuzz'].forEach(s => { $(s).disabled = true; setTimeout(() => $(s).disabled = false, 3000); });
  send('buzz');
}

function invite() {
  const inRoom = new Set((group?.members || []).map(x => x.id).concat(isGroup ? [] : [peerId()]));
  const cands = buddies.filter(b => !b.hidden && !inRoom.has(b.id));
  if (!cands.length) { toast('Không còn ai trong danh sách bạn để mời.'); return; }
  dialog(isGroup ? 'Mời thêm vào phòng' : 'Mời thêm bạn — mở phòng chat nhóm', `
    ${isGroup ? '' : `<label>Tên phòng:</label><input id="rt" maxlength="60" value="${esc('Phòng của ' + me.name)}">`}
    <div class="pick sunken">${cands.map(b => `<label>${stHtml(b.status)}<input type="checkbox" value="${esc(b.id)}"> ${esc(b.name)}</label>`).join('')}</div>
    <div class="err" id="ierr"></div>`,
    [{ label: 'Mời', primary: true, on: d => {
        const ids = $$('.pick input:checked', d).map(x => x.value);
        if (!ids.length) { $('#ierr', d).textContent = 'Chọn ít nhất 1 người.'; return false; }
        send('group.invite', { ids, title: isGroup ? null : $('#rt', d).value });
      } }, { label: 'Huỷ' }]);
}

function smileyPicker() {
  const old = $('.smgrid'); if (old) { old.remove(); return; }
  const g = el('div', { class: 'smgrid' });
  g.append(el('div', { class: 'cap' }, 'Mặt cười Bhoo — hoặc gõ mã, vd :) :D <3'));
  for (const [code, kind, name] of SMILEYS) {
    const c = el('div', { title: name + '   ' + code }, `<span class="sm">${smileySvg(kind)}</span>`);
    c.onclick = () => { insertAtCursor(code + ' '); g.remove(); };
    g.append(c);
  }
  document.body.append(g);
  const r = $('#fSm').getBoundingClientRect();
  const gr = g.getBoundingClientRect();
  g.style.left = Math.max(2, r.left) + 'px';
  g.style.top = Math.max(2, r.top - gr.height - 2) + 'px';
  setTimeout(() => document.addEventListener('mousedown', function h(e) { if (!g.contains(e.target)) { g.remove(); document.removeEventListener('mousedown', h); } }), 0);
}
function insertAtCursor(s) {
  const inp = $('#inp');
  const a = inp.selectionStart, b = inp.selectionEnd;
  inp.value = inp.value.slice(0, a) + s + inp.value.slice(b);
  inp.selectionStart = inp.selectionEnd = a + s.length;
  inp.focus();
}

function scrollEnd() { const l = $('#log'); l.scrollTop = l.scrollHeight; }

$('#inp').addEventListener('keydown', e => {
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); doSend(); return; }
  if (e.ctrlKey && !e.shiftKey && !e.altKey) {
    const k = e.key.toLowerCase();
    if (k === 'b' || k === 'i' || k === 'u') { e.preventDefault(); toggleStyle(k); }
    if (k === 'g') { e.preventDefault(); doBuzz(); }
  }
});
document.addEventListener('keydown', e => { if (e.ctrlKey && e.key.toLowerCase() === 'g' && e.target.id !== 'inp') { e.preventDefault(); doBuzz(); } if (e.key === 'Escape' && !$('.dlg-bg') && !$('.menu')) send('close'); });
$('#inp').addEventListener('input', () => {
  const now = Date.now();
  if (now - lastTypingSent > 3000 && $('#inp').value.trim()) { lastTypingSent = now; send('typing'); }
});
$('#sendBtn').onclick = doSend;
$('#tBuzz').onclick = doBuzz; $('#fBuzz').onclick = doBuzz;
$('#tFile').onclick = () => send('file.pick');
$('#tInvite').onclick = invite;
$('#tClear').onclick = () => dialog('Xoá lịch sử', 'Xoá toàn bộ lịch sử cuộc chat này <b>trên máy bạn</b>? (Máy người kia vẫn giữ.)',
  [{ label: 'Xoá', primary: true, on: () => { send('history.clear'); $('#log').innerHTML = ''; msgs.clear(); lastDay = ''; sys('Đã xoá lịch sử trên máy này.'); } }, { label: 'Huỷ' }]);
$('#fB').onclick = () => toggleStyle('b'); $('#fI').onclick = () => toggleStyle('i'); $('#fU').onclick = () => toggleStyle('u');
$('#fg').oninput = () => { font.fg = $('#fg').value; applyFont(); saveFont(); };
$('#fSize').onchange = () => { font.size = parseInt($('#fSize').value, 10); applyFont(); saveFont(); };
$('#fSm').onclick = smileyPicker;
$('#fSmIc').innerHTML = `<span class="sm">${smileySvg('cuoi')}</span>`;

// Thả file từ Explorer: gửi kèm File objects để C# lấy đường dẫn thật.
let dragN = 0;
document.addEventListener('dragenter', e => { if (e.dataTransfer && [...e.dataTransfer.types].includes('Files')) { dragN++; document.body.classList.add('drop'); e.preventDefault(); } });
document.addEventListener('dragleave', () => { if (--dragN <= 0) { dragN = 0; document.body.classList.remove('drop'); } });
document.addEventListener('dragover', e => e.preventDefault());
document.addEventListener('drop', e => {
  e.preventDefault(); dragN = 0; document.body.classList.remove('drop');
  const fl = e.dataTransfer && e.dataTransfer.files;
  if (fl && fl.length && host && host.postMessageWithAdditionalObjects) host.postMessageWithAdditionalObjects({ t: 'file.drop' }, fl);
  else if (fl && fl.length) toast('Không lấy được file thả vào — bấm 📎 Gửi file.');
});

$$('.menubar span').forEach(s => s.addEventListener('click', () => {
  const r = s.getBoundingClientRect(), x = r.left, y = r.bottom;
  switch (s.dataset.m) {
    case 'conv': showMenu(x, y, [
      { label: 'Mời bạn…', on: invite },
      { label: 'Gửi file…', on: () => send('file.pick') }, '-',
      ...(isGroup ? [{ label: 'Rời phòng', on: () => dialog('Rời phòng', `Rời phòng "${esc(title)}"? Bạn sẽ không nhận tin của phòng nữa.`, [{ label: 'Rời phòng', primary: true, on: () => send('group.leave') }, { label: 'Huỷ' }]) }] : []),
      { label: 'Đóng cửa sổ', on: () => send('close') }]); break;
    case 'edit': showMenu(x, y, [
      { label: 'Chép', on: () => document.execCommand('copy') },
      { label: 'Chọn hết khung chat', on: () => { const r2 = document.createRange(); r2.selectNodeContents($('#log')); const sl = getSelection(); sl.removeAllRanges(); sl.addRange(r2); } }]); break;
    case 'view': showMenu(x, y, [
      { label: (showTime ? '✓ ' : '') + 'Hiện giờ gửi', on: () => { showTime = !showTime; send('ui.init'); } },
      { label: 'Khung cảnh…', on: pickScene }]); break;
    case 'actions': showMenu(x, y, [
      { label: 'BUZZ!', on: doBuzz },
      ...(isGroup ? [{ label: 'Đổi tên phòng…', on: () => dialog('Đổi tên phòng', `<input id="nt" maxlength="60" value="${esc(title)}">`, [{ label: 'OK', primary: true, on: d => send('group.rename', { title: $('#nt', d).value }) }, { label: 'Huỷ' }]) }] : []),
      { label: 'Xem lịch sử…', on: () => send('history.view') },
      { label: 'Xoá lịch sử…', on: () => $('#tClear').click() }]); break;
  }
}));

function pickScene() { scenePicker(k => { if (k !== Scene.id) send('env', { scene: k }); }); }
$('#tScene').onclick = pickScene;

setInterval(() => { $('#clock').textContent = new Date().toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' }); }, 1000);
send('ui.init');
