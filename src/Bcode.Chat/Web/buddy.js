// Bhoo — cửa sổ danh sách bạn.
'use strict';

let me = null, list = [], groups = [], sel = null, fw = null;
const store = (() => { try { return window.localStorage; } catch { return null; } })();
const pref = (k, d) => { try { const v = store && store.getItem('bhoo.' + k); return v == null ? d : JSON.parse(v); } catch { return d; } };
const setPref = (k, v) => { try { store && store.setItem('bhoo.' + k, JSON.stringify(v)); } catch { } };
let collapsed = pref('collapsed', {}), showOffline = pref('showOffline', true);
const COLORS = ['#3a6ea5', '#1f4882', '#2e9e3e', '#0f7b6c', '#d23a2a', '#c2185b', '#7b3fb5', '#e8a400', '#ef6c00', '#795548', '#455a64', '#000000'];
const ORDER = { online: 0, busy: 1, away: 2, offline: 3 };

onHost(m => {
  switch (m.t) {
    case 'init': me = m.me; list = m.list; groups = m.groups || []; renderMe(); render(); break;
    case 'me': me = m.me; renderMe(); break;
    case 'buddies': list = m.list; render(); break;
    case 'groups': groups = m.list; render(); break;
    case 'fw': fw = m; renderWarns(); break;
    case 'warn': addWarn(m.text, 'warn'); break;
    case 'error': toast(m.text, 5000); break;
  }
});

function renderMe() {
  if (!me) return;
  const av = $('#avatar');
  av.setAttribute('style', avStyle(me, true, shade(me.color, -35)));
  av.textContent = me.av ? '' : (me.name || '?')[0].toUpperCase();
  $('#myName').textContent = me.name;
  $('#mySt').innerHTML = stHtml(me.status === 'invisible' ? 'invisible' : me.status);
  $('#myRow').title = 'Trạng thái: ' + STATUS_TEXT[me.status] + ' — bấm để đổi';
  if (document.activeElement !== $('#myText')) $('#myText').value = me.text || '';
  const ip = (me.ips || [])[0];
  $('#sb').textContent = ip ? `IP máy bạn: ${(me.ips || []).join(', ')} · cổng ${me.port}` : 'Chưa thấy mạng LAN';
  document.title = 'Bhoo — ' + me.name;
  renderWarns();
}

function shade(hex, pct) {
  const n = parseInt(hex.slice(1), 16);
  const f = c => Math.max(0, Math.min(255, Math.round(c + c * pct / 100)));
  return '#' + [f(n >> 16), f((n >> 8) & 255), f(n & 255)].map(x => x.toString(16).padStart(2, '0')).join('');
}

const extraWarns = [];
function addWarn(text, cls) { extraWarns.push({ text, cls }); renderWarns(); }
function renderWarns() {
  const w = $('#warns');
  w.innerHTML = '';
  if (me && me.udpError) w.append(el('div', { class: 'warn' }, esc(me.udpError)));
  if (fw && fw.level !== 'ok') {
    const d = el('div', { class: 'warn' + (fw.level === 'bad' ? ' bad' : '') }, '🛡 ' + esc(fw.text) + '<br>');
    const b = el('button', { class: 'btn' }, '🛡 Mở tường lửa');
    b.onclick = () => { b.disabled = true; b.textContent = 'Đang mở…'; send('fw.open'); };
    d.append(b); w.append(d);
  }
  extraWarns.forEach((x, i) => {
    const d = el('div', { class: 'warn' }, esc(x.text) + ' ');
    const c = el('a', { href: '#' }, 'Ẩn'); c.onclick = e => { e.preventDefault(); extraWarns.splice(i, 1); renderWarns(); };
    d.append(c); w.append(d);
  });
  $('#fw').innerHTML = fw ? (fw.level === 'ok' ? '🛡✔' : '🛡⚠') : '';
  $('#fw').title = fw ? fw.text : '';
}

function matches(b, q) { return !q || b.name.toLowerCase().includes(q) || (b.text || '').toLowerCase().includes(q) || (b.ip || '').includes(q); }

function render() {
  const tree = $('#tree');
  const q = $('#search').value.trim().toLowerCase();
  tree.innerHTML = '';
  const visible = list.filter(b => !b.hidden && matches(b, q));

  // Phòng chat nhóm
  const rooms = groups.filter(g => !q || g.title.toLowerCase().includes(q));
  if (rooms.length) {
    section(tree, '__rooms', `Phòng chat của tôi (${rooms.length})`, rooms.map(g => {
      const r = el('div', { class: 'row', 'data-conv': 'g:' + g.gid, title: g.members.map(x => x.name).join(', ') },
        `<span class="room-ic">👥</span><span class="nm">${esc(g.title)}</span><span class="stx">${g.members.length} người</span>`);
      bindRow(r, 'g:' + g.gid, null);
      return r;
    }));
  }

  // Nhóm bạn bè
  const names = [...new Set(visible.map(b => b.group || 'Bạn bè'))].sort((a, b) => a === 'Bạn bè' ? -1 : b === 'Bạn bè' ? 1 : a.localeCompare(b, 'vi'));
  if (!names.length) names.push('Bạn bè');
  for (const gname of names) {
    const items = visible.filter(b => (b.group || 'Bạn bè') === gname)
      .sort((a, b) => (ORDER[a.status] ?? 3) - (ORDER[b.status] ?? 3) || a.name.localeCompare(b.name, 'vi'));
    const on = items.filter(b => b.status !== 'offline').length;
    const shown = showOffline ? items : items.filter(b => b.status !== 'offline');
    section(tree, gname, `${gname} (${on}/${items.length})`, shown.map(b => {
      const off = b.status === 'offline';
      const stx = off ? '' : (b.text ? b.text : (b.status !== 'online' ? STATUS_TEXT[b.status] : ''));
      const r = el('div', { class: 'row' + (off ? ' off' : ''), 'data-conv': 'u:' + b.id, title: tip(b) },
        `${stHtml(b.status)}${b.av ? `<span class="mini" style="${avStyle(b)}"></span>` : ''}<span class="nm">${esc(b.name)}</span>${stx ? `<span class="stx">— ${esc(stx)}</span>` : '<span class="stx"></span>'}` +
        (b.pending ? `<span class="badge" title="${b.pending} tin đang chờ ${esc(b.name)} online">✉ ${b.pending}</span>` : ''));
      bindRow(r, 'u:' + b.id, b);
      return r;
    }));
  }

  if (!list.length) {
    tree.append(el('div', { class: 'empty' }, `Chưa thấy ai trong mạng LAN.<br><br>
      • Bạn bè cần mở <b>Bhoo</b> trên máy họ (Bcode → Giải trí → Bhoo).<br>
      • Khác mạng con (VLAN / Wi-Fi khác)? Bấm <b>➕</b> và gõ IP máy họ.<br>
      • Máy khác không thấy bạn? Xem cảnh báo tường lửa phía trên.`));
  }
  if (sel) { const r = $(`.row[data-conv="${CSS.escape(sel)}"]`); if (r) r.classList.add('sel'); }
}

function tip(b) {
  const parts = [b.name + ' — ' + STATUS_TEXT[b.status]];
  if (b.text) parts.push('“' + b.text + '”');
  if (b.ip) parts.push('IP: ' + b.ip);
  if (b.status === 'offline' && b.lastSeen) parts.push('Online lần cuối: ' + new Date(b.lastSeen).toLocaleString('vi-VN'));
  return parts.join('\n');
}

function section(tree, key, label, rows) {
  const open = !collapsed[key];
  const h = el('div', { class: 'grp' }, `<span class="tw">${open ? '▼' : '▶'}</span>${esc(label)}`);
  h.onclick = () => { collapsed[key] = open; setPref('collapsed', collapsed); render(); };
  tree.append(h);
  if (open) rows.forEach(r => tree.append(r));
}

function bindRow(r, conv, b) {
  r.addEventListener('mousedown', () => { sel = conv; $$('.row.sel').forEach(x => x.classList.remove('sel')); r.classList.add('sel'); });
  r.addEventListener('dblclick', () => send('open', { conv }));
  r.addEventListener('contextmenu', e => {
    e.preventDefault();
    sel = conv; $$('.row.sel').forEach(x => x.classList.remove('sel')); r.classList.add('sel');
    if (!b) { showMenu(e.clientX, e.clientY, [{ label: 'Mở phòng chat', icon: '<span class="mic">💬</span>', on: () => send('open', { conv }) }]); return; }
    showMenu(e.clientX, e.clientY, [
      { label: 'Gửi tin nhắn', icon: '<span class="mic">💬</span>', on: () => send('open', { conv }) },
      { label: 'Mời vào phòng chat mới…', icon: '<span class="mic">👥</span>', on: () => newRoom([b.id]) },
      '-',
      { label: 'Chuyển sang nhóm…', on: () => moveTo(b) },
      { label: 'Ẩn khỏi danh sách', on: () => send('buddy.hide', { id: b.id, hidden: true }) },
      '-',
      { label: 'Thông tin', on: () => dialog('Thông tin — ' + b.name, `<div style="white-space:pre-line">${esc(tip(b))}</div>`) },
    ]);
  });
}

document.addEventListener('keydown', e => {
  if (e.key === 'Enter' && sel && document.activeElement === document.body) send('open', { conv: sel });
});

function moveTo(b) {
  const existing = [...new Set(list.map(x => x.group || 'Bạn bè'))];
  dialog('Chuyển ' + b.name + ' sang nhóm', `<label>Tên nhóm (có thì chọn, chưa có thì gõ mới):</label>
    <input id="dg" list="dgl" value="${esc(b.group || 'Bạn bè')}" maxlength="40"><datalist id="dgl">${existing.map(x => `<option value="${esc(x)}">`).join('')}</datalist>`,
    [{ label: 'OK', primary: true, on: d => send('buddy.group', { id: b.id, group: $('#dg', d).value }) }, { label: 'Huỷ' }]);
}

function newRoom(preset) {
  const cands = list.filter(b => !b.hidden).sort((a, b) => (ORDER[a.status] ?? 3) - (ORDER[b.status] ?? 3) || a.name.localeCompare(b.name, 'vi'));
  if (!cands.length) { toast('Chưa có ai trong danh sách bạn để mời.'); return; }
  dialog('Mở phòng chat nhóm', `<label>Tên phòng:</label><input id="rt" maxlength="60" value="${esc('Phòng của ' + me.name)}">
    <label>Mời (người offline sẽ nhận lời mời khi online):</label>
    <div class="pick sunken">${cands.map(b => `<label>${stHtml(b.status)}<input type="checkbox" value="${esc(b.id)}" ${preset && preset.includes(b.id) ? 'checked' : ''}> ${esc(b.name)}</label>`).join('')}</div>
    <div class="err" id="rerr"></div>`,
    [{ label: 'Mở phòng', primary: true, on: d => {
        const ids = $$('.pick input:checked', d).map(x => x.value);
        if (!ids.length) { $('#rerr', d).textContent = 'Chọn ít nhất 1 người.'; return false; }
        send('group.new', { title: $('#rt', d).value, ids });
      } }, { label: 'Huỷ' }]);
}

function addIp() {
  dialog('Thêm máy bằng IP', `<div>Máy ở mạng con khác (VLAN / Wi-Fi khác) không tự thấy nhau. Gõ IP máy bạn bè (họ xem ở dòng dưới cùng cửa sổ Bhoo của họ):</div>
    <input id="ip" placeholder="vd 192.168.1.25" maxlength="40">
    ${(me.peers || []).length ? `<div>Đã thêm:</div><div class="sunken" style="padding:3px">${me.peers.map(p => `<div style="display:flex;justify-content:space-between;padding:1px 2px">${esc(p)} <a href="#" data-del="${esc(p)}">xoá</a></div>`).join('')}</div>` : ''}`,
    [{ label: 'Thêm', primary: true, on: d => { const v = $('#ip', d).value.trim(); if (v) send('peer.add', { ip: v }); } }, { label: 'Đóng' }]);
  $$('[data-del]').forEach(a => a.onclick = e => { e.preventDefault(); send('peer.del', { ip: a.dataset.del }); a.parentElement.remove(); });
}

function statusMenu(x, y) {
  const it = (st, label) => ({ label, icon: stHtml(st), on: () => send('status', { status: st }) });
  showMenu(x, y, [it('online', 'Online'), it('busy', 'Bận'), it('away', 'Vắng'), it('invisible', 'Ẩn (mọi người thấy bạn offline)'), '-',
    { label: 'Đặt dòng trạng thái…', on: () => $('#myText').focus() }]);
}

function settings() {
  const hidden = list.filter(b => b.hidden);
  dialog('Tuỳ chọn Bhoo', `
    <label class="row"><input type="checkbox" id="oSound" ${me.sound ? 'checked' : ''}> Âm báo (tin mới, BUZZ!, bạn online)</label>
    <label class="row"><input type="checkbox" id="oTray" ${me.closeToTray ? 'checked' : ''}> Bấm ✕ thì thu xuống khay đồng hồ (không thoát)</label>
    <label class="row"><input type="checkbox" id="oOff" ${showOffline ? 'checked' : ''}> Hiện bạn đang offline</label>
    <label class="row">Tự chuyển "Vắng" sau <input id="oAway" type="number" min="0" max="240" value="${me.autoAway}" style="width:50px"> phút không dùng máy (0 = tắt)</label>
    ${hidden.length ? `<div>Bạn đã ẩn (bấm để hiện lại):</div><div class="sunken" style="padding:3px">${hidden.map(b => `<div><a href="#" data-show="${esc(b.id)}">${esc(b.name)}</a></div>`).join('')}</div>` : ''}`,
    [{ label: 'Lưu', primary: true, on: d => {
        showOffline = $('#oOff', d).checked; setPref('showOffline', showOffline);
        send('prefs', { sound: $('#oSound', d).checked, closeToTray: $('#oTray', d).checked, autoAway: parseInt($('#oAway', d).value, 10) || 0 });
        render();
      } }, { label: 'Huỷ' }]);
  $$('[data-show]').forEach(a => a.onclick = e => { e.preventDefault(); send('buddy.hide', { id: a.dataset.show, hidden: false }); a.remove(); });
}

function about() {
  dialog('Giới thiệu Bhoo', `<div style="text-align:center">${logoSvg(false)}</div>
    <p><b>Bhoo</b> — chat LAN của Bcode, không cần máy chủ Internet.</p>
    <p>• Nhấp đúp vào tên bạn để nhắn tin. Nút <b>BUZZ!</b> làm rung cửa sổ chat của bạn bè.<br>
    • Bạn đang offline vẫn nhắn được: tin nằm chờ (✉) và tự gửi khi cả hai cùng online.<br>
    • Gửi file: kéo thả vào cửa sổ chat hoặc bấm 📎. Người nhận bấm <b>Nhận</b>, file lưu ở Downloads\\Bhoo.<br>
    • Tin đi thẳng giữa các máy dạng chữ thường (không mã hoá) — đừng gửi mật khẩu.</p>
    <p class="muted">Bhoo ID: <b>${esc(me.name)}</b> · IP: ${esc((me.ips || []).join(', '))} · cổng ${me.port}</p>`, [{ label: 'OK', primary: true }]);
}

$$('.menubar span').forEach(s => s.addEventListener('click', e => {
  const r = s.getBoundingClientRect();
  const x = r.left, y = r.bottom;
  switch (s.dataset.m) {
    case 'bhoo': showMenu(x, y, [
      { label: 'Trạng thái…', on: () => statusMenu(x, y) }, '-',
      { label: 'Đăng xuất', on: () => send('signout') },
      { label: 'Thoát Bhoo', on: () => send('quit') }]); break;
    case 'contacts': showMenu(x, y, [
      { label: 'Gửi tin nhắn', dis: !sel, on: () => send('open', { conv: sel }) },
      { label: 'Thêm máy bằng IP…', on: addIp },
      { label: showOffline ? 'Ẩn bạn đang offline' : 'Hiện bạn đang offline', on: () => { showOffline = !showOffline; setPref('showOffline', showOffline); render(); } }]); break;
    case 'actions': showMenu(x, y, [
      { label: 'Mở phòng chat nhóm…', on: () => newRoom([]) },
      { label: 'Kiểm tra tường lửa', on: () => send('fw.check') },
      '-', { label: 'Tuỳ chọn…', on: settings }]); break;
    case 'help': showMenu(x, y, [{ label: 'Giới thiệu Bhoo', on: about }]); break;
  }
}));

$('#myRow').onclick = e => { const r = $('#myRow').getBoundingClientRect(); statusMenu(r.left, r.bottom); };
$('#avatar').onclick = e => {
  const r = $('#avatar').getBoundingClientRect();
  showMenu(r.left, r.bottom, [
    { label: 'Chọn ảnh từ máy…', icon: '<span class="mic">🖼</span>', on: () => send('avatar.pick') },
    { label: 'Bỏ ảnh (dùng chữ cái + màu)', dis: !me.av, on: () => send('avatar.clear') }, '-']
    .concat(COLORS.map(c => ({ label: c === me.color ? 'Màu nền này ✓' : 'Màu nền này', icon: `<span class="mic" style="display:inline-block;width:14px;height:14px;background:${c};border:1px solid #000"></span>`, on: () => send('me.set', { color: c }) }))));
};
$('#myText').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); $('#myText').blur(); } if (e.key === 'Escape') { $('#myText').value = me.text || ''; $('#myText').blur(); } });
$('#myText').addEventListener('blur', () => { if (me && $('#myText').value.trim() !== (me.text || '')) send('me.set', { text: $('#myText').value.trim() }); });
$('#search').addEventListener('input', render);
$('#bNewRoom').onclick = () => newRoom([]);
$('#bAddIp').onclick = addIp;

// Ô "quảng cáo" kiểu cũ ở chân danh sách — chỉ là mẹo dùng Bhoo, đổi mỗi 12 giây.
const TIPS = ['Gõ :) :D ;) :(( <3 để chèn mặt cười.', 'Bấm BUZZ! để rung cửa sổ của bạn bè.', 'Kéo thả file vào khung chat để gửi.',
  'Bạn offline vẫn nhắn được — tin tự gửi khi họ online.', 'Đăng nhập ẩn: mọi người thấy bạn offline.', 'Chuột phải vào tên bạn để chuyển nhóm.'];
let tipI = 0;
function showTip() { $('#ad').innerHTML = `${logoSvg(false)}<div><b>Mẹo Bhoo</b><br>${esc(TIPS[tipI++ % TIPS.length])}</div>`; }
showTip(); setInterval(showTip, 12000);

send('ui.init');
