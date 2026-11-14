// Bhoo — phần dùng chung của các trang: nói chuyện với C#, mặt cười tự vẽ, biểu tượng trạng thái, menu / hộp thoại kiểu XP.
'use strict';

const host = window.chrome && window.chrome.webview;
function send(t, o) { if (host) host.postMessage(Object.assign({ t }, o || {})); }
function onHost(fn) { if (host) host.addEventListener('message', e => fn(e.data)); }
const $ = (s, el) => (el || document).querySelector(s);
const $$ = (s, el) => Array.from((el || document).querySelectorAll(s));
function esc(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }
function el(tag, attrs, html) {
  const e = document.createElement(tag);
  for (const k in attrs || {}) { if (k === 'class') e.className = attrs[k]; else if (k.startsWith('on')) e.addEventListener(k.slice(2), attrs[k]); else e.setAttribute(k, attrs[k]); }
  if (html != null) e.innerHTML = html;
  return e;
}

// ───────────── Mặt cười tự vẽ (SVG) ─────────────
// Bộ riêng của Bhoo: mặt tròn vàng, nét nâu. Mã gõ tắt là kiểu emoticon chữ phổ biến.
const SMILEYS = [
  [':))', 'cuoilan', 'Cười lăn'], [':((', 'khoc', 'Khóc'], [':)', 'cuoi', 'Cười'], [':D', 'cuoito', 'Cười to'],
  [';)', 'nhay', 'Nháy mắt'], [':(', 'buon', 'Buồn'], [':P', 'le', 'Lè lưỡi'], [':O', 'ngac', 'Ngạc nhiên'],
  [':x', 'yeu', 'Yêu quá'], ['B-)', 'ngau', 'Ngầu'], [':-/', 'langtang', 'Lăn tăn'], ['>:)', 'gian', 'Gian gian'],
  [':-*', 'hon', 'Hôn'], [':|', 'dodo', 'Đơ'], ['X(', 'gian2', 'Giận'], [':">', 'ngung', 'Ngượng'],
  [':-?', 'nghi', 'Suy nghĩ'], ['I-)', 'ngu', 'Buồn ngủ'], ['=))', 'cuoibo', 'Cười bò'], ['<3', 'tim', 'Trái tim'],
];
const SM_CODES = SMILEYS.map(s => s[0]).sort((a, b) => b.length - a.length);
const SM_BY_CODE = Object.fromEntries(SMILEYS.map(s => [s[0].toLowerCase(), s]));

function smileySvg(kind) {
  if (kind === 'tim') return `<svg viewBox="0 0 20 20"><path d="M10 17.5 2.8 10.6C.4 8.3 1.2 4 4.6 3.4c2-.3 3.6.7 5.4 2.6 1.8-1.9 3.4-2.9 5.4-2.6 3.4.6 4.2 4.9 1.8 7.2Z" fill="#e8384f" stroke="#8c1426" stroke-width="1"/><ellipse cx="6" cy="6.5" rx="1.6" ry="1" fill="#fff" opacity=".6"/></svg>`;
  const red = kind === 'gian2';
  const face = red ? ['#ff9a7a', '#e2462a'] : ['#ffe680', '#f4b400'];
  let eyes = '<ellipse cx="7" cy="8" rx="1.2" ry="1.7" fill="#3b2400"/><ellipse cx="13" cy="8" rx="1.2" ry="1.7" fill="#3b2400"/>';
  let mouth = '<path d="M6 12q4 4 8 0" fill="none" stroke="#3b2400" stroke-width="1.3" stroke-linecap="round"/>';
  let extra = '';
  switch (kind) {
    case 'cuoito': mouth = '<path d="M5.5 11.5h9q-.5 4.5-4.5 4.5t-4.5-4.5Z" fill="#7a1d00" stroke="#3b2400" stroke-width=".9"/><path d="M6.6 11.6h6.8v1.2H6.6Z" fill="#fff"/>'; break;
    case 'cuoilan': case 'cuoibo':
      eyes = '<path d="M5.5 8.5l2-1.5-2-1.2M14.5 8.5l-2-1.5 2-1.2" fill="none" stroke="#3b2400" stroke-width="1.2" stroke-linecap="round"/>';
      mouth = '<path d="M5 11h10q-.6 5-5 5t-5-5Z" fill="#7a1d00" stroke="#3b2400" stroke-width=".9"/>';
      extra = '<path d="M3.2 10.5q-1 2 .2 3" stroke="#4aa8ff" stroke-width="1.2" fill="none"/><path d="M16.8 10.5q1 2-.2 3" stroke="#4aa8ff" stroke-width="1.2" fill="none"/>';
      if (kind === 'cuoibo') extra += '<path d="M3 4l2 1M17 4l-2 1" stroke="#3b2400" stroke-width="1"/>';
      break;
    case 'nhay': eyes = '<ellipse cx="7" cy="8" rx="1.2" ry="1.7" fill="#3b2400"/><path d="M11.5 8.3q1.5-1.4 3 0" fill="none" stroke="#3b2400" stroke-width="1.3" stroke-linecap="round"/>'; break;
    case 'buon': mouth = '<path d="M6.5 14.5q3.5-3 7 0" fill="none" stroke="#3b2400" stroke-width="1.3" stroke-linecap="round"/>'; break;
    case 'khoc':
      mouth = '<path d="M6.5 15q3.5-3.4 7 0" fill="none" stroke="#3b2400" stroke-width="1.3" stroke-linecap="round"/>';
      extra = '<path d="M6.3 9.8v5.5M13.7 9.8v5.5" stroke="#4aa8ff" stroke-width="1.6" stroke-linecap="round"/>'; break;
    case 'le': mouth = '<path d="M6 12q4 3 8 0" fill="none" stroke="#3b2400" stroke-width="1.3" stroke-linecap="round"/><path d="M9 13.2h3.2v2.3q-1.6 1.6-3.2 0Z" fill="#ff6f8f" stroke="#a3243d" stroke-width=".7"/>'; break;
    case 'ngac': eyes = '<circle cx="7" cy="7.6" r="1.8" fill="#fff" stroke="#3b2400"/><circle cx="13" cy="7.6" r="1.8" fill="#fff" stroke="#3b2400"/><circle cx="7" cy="7.8" r=".8" fill="#3b2400"/><circle cx="13" cy="7.8" r=".8" fill="#3b2400"/>';
      mouth = '<ellipse cx="10" cy="13.6" rx="1.7" ry="2.1" fill="#7a1d00" stroke="#3b2400" stroke-width=".9"/>'; break;
    case 'yeu': eyes = '<path d="M7 9.8 4.9 7.8c-.8-.8-.2-2.2 1-2 .5.1.8.4 1.1.8.3-.4.6-.7 1.1-.8 1.2-.2 1.8 1.2 1 2Z" fill="#e8384f"/><path d="M13 9.8l-2.1-2c-.8-.8-.2-2.2 1-2 .5.1.8.4 1.1.8.3-.4.6-.7 1.1-.8 1.2-.2 1.8 1.2 1 2Z" fill="#e8384f"/>'; break;
    case 'ngau': eyes = '<path d="M3.5 6.5h13l-.6 2.6q-.4 1.6-2.4 1.6h-1.4q-1.6 0-2-1.5Q9.7 8 9.4 9.2q-.4 1.5-2 1.5H6q-2 0-2.4-1.6Z" fill="#1d1d1d"/><path d="M5 7.3l1.5 1.8" stroke="#fff" stroke-width=".6" opacity=".7"/>'; break;
    case 'langtang': mouth = '<path d="M6.5 13.5l7-1.6" stroke="#3b2400" stroke-width="1.3" stroke-linecap="round"/>'; break;
    case 'gian': eyes = '<path d="M5 6l3.5 1.6M15 6l-3.5 1.6" stroke="#3b2400" stroke-width="1.2" stroke-linecap="round"/>' + eyes; mouth = '<path d="M6 12q4 4.5 8 0" fill="none" stroke="#3b2400" stroke-width="1.3" stroke-linecap="round"/>';
      extra = '<path d="M4.5 3.5 3 .8 6 2.6M15.5 3.5 17 .8 14 2.6" fill="#d42" />'; break;
    case 'hon': eyes = '<path d="M5.5 8.3q1.5-1.4 3 0M11.5 8.3q1.5-1.4 3 0" fill="none" stroke="#3b2400" stroke-width="1.2" stroke-linecap="round"/>';
      mouth = '<circle cx="10" cy="13.3" r="1.4" fill="#d9304a"/>'; extra = '<path d="M15 12.5l1.8-1.4c.4-.4 1.1.1.8.6l-.3.4.5.1c.6.1.6.9 0 1Z" fill="#e8384f"/>'; break;
    case 'dodo': mouth = '<path d="M6.5 13h7" stroke="#3b2400" stroke-width="1.3" stroke-linecap="round"/>'; break;
    case 'gian2': eyes = '<path d="M4.8 6.2l3.6 1.8M15.2 6.2l-3.6 1.8" stroke="#3b0000" stroke-width="1.3" stroke-linecap="round"/><ellipse cx="7" cy="9.2" rx="1.1" ry="1.2" fill="#3b0000"/><ellipse cx="13" cy="9.2" rx="1.1" ry="1.2" fill="#3b0000"/>';
      mouth = '<path d="M6.5 15q3.5-3 7 0" fill="none" stroke="#3b0000" stroke-width="1.4" stroke-linecap="round"/>'; break;
    case 'ngung': eyes = '<path d="M5.5 8.3q1.5-1.4 3 0M11.5 8.3q1.5-1.4 3 0" fill="none" stroke="#3b2400" stroke-width="1.2" stroke-linecap="round"/>';
      extra = '<ellipse cx="5" cy="11.5" rx="1.9" ry="1.1" fill="#ff7b9a" opacity=".8"/><ellipse cx="15" cy="11.5" rx="1.9" ry="1.1" fill="#ff7b9a" opacity=".8"/>';
      mouth = '<path d="M8 13q2 1.6 4 0" fill="none" stroke="#3b2400" stroke-width="1.2" stroke-linecap="round"/>'; break;
    case 'nghi': mouth = '<path d="M7 13.5q1.5-1 3 0t3 0" fill="none" stroke="#3b2400" stroke-width="1.2"/>';
      extra = '<text x="14.5" y="5.5" font-size="6" font-weight="bold" fill="#2b5a9a" font-family="Tahoma">?</text>'; break;
    case 'ngu': eyes = '<path d="M5.5 8h3M11.5 8h3" stroke="#3b2400" stroke-width="1.3" stroke-linecap="round"/>';
      mouth = '<ellipse cx="10" cy="13.5" rx="1.2" ry="1.4" fill="#7a1d00"/>'; extra = '<text x="13.5" y="5" font-size="5" font-weight="bold" fill="#2b5a9a" font-family="Tahoma">z</text><text x="16" y="3" font-size="4" font-weight="bold" fill="#2b5a9a" font-family="Tahoma">z</text>'; break;
  }
  return `<svg viewBox="0 0 20 20"><defs><radialGradient id="g${kind}" cx=".38" cy=".32" r=".75"><stop offset="0" stop-color="#fff8d0"/><stop offset=".35" stop-color="${face[0]}"/><stop offset="1" stop-color="${face[1]}"/></radialGradient></defs>` +
    `<circle cx="10" cy="10" r="8.6" fill="url(#g${kind})" stroke="${red ? '#7a1500' : '#8a5a00'}" stroke-width="1"/>${eyes}${mouth}${extra}</svg>`;
}
const smHtml = code => { const s = SM_BY_CODE[code.toLowerCase()]; return s ? `<span class="sm" title="${esc(s[2] + '  ' + s[0])}">${smileySvg(s[1])}</span>` : esc(code); };

// Biểu tượng trạng thái trong danh sách bạn: mặt cười nhỏ đổi màu theo trạng thái.
function statusSvg(st) {
  const c = { online: ['#bff07a', '#3fa21c', '#245c0c'], busy: ['#ffb4a6', '#d23a2a', '#6e1209'], away: ['#ffe680', '#e3a400', '#6b4a00'], invisible: ['#eee', '#b9b9b9', '#666'], offline: ['#eee', '#c4c4c4', '#8a8a8a'] }[st] || ['#eee', '#c4c4c4', '#8a8a8a'];
  const mouth = st === 'busy' ? '<path d="M5.5 11h5" stroke="' + c[2] + '" stroke-width="1.2" stroke-linecap="round"/>'
    : st === 'away' ? '<path d="M5.5 11.2q2.5-1 5 0" fill="none" stroke="' + c[2] + '" stroke-width="1.1"/>'
    : '<path d="M5 10q3 3 6 0" fill="none" stroke="' + c[2] + '" stroke-width="1.2" stroke-linecap="round"/>';
  const eyes = st === 'away' ? `<path d="M4.6 7h2.2M9.2 7h2.2" stroke="${c[2]}" stroke-width="1.1"/>` : `<circle cx="5.8" cy="6.6" r="1" fill="${c[2]}"/><circle cx="10.2" cy="6.6" r="1" fill="${c[2]}"/>`;
  const extra = st === 'busy' ? `<circle cx="13" cy="13" r="2.6" fill="#d23a2a" stroke="#fff" stroke-width=".8"/><path d="M11.7 13h2.6" stroke="#fff" stroke-width="1"/>` : '';
  return `<svg viewBox="0 0 16 16"><defs><radialGradient id="s${st}" cx=".35" cy=".3" r=".8"><stop offset="0" stop-color="#fff"/><stop offset=".4" stop-color="${c[0]}"/><stop offset="1" stop-color="${c[1]}"/></radialGradient></defs><circle cx="8" cy="8" r="6.8" fill="url(#s${st})" stroke="${c[2]}" stroke-width=".9"/>${eyes}${mouth}${extra}</svg>`;
}
const stHtml = st => `<span class="st">${statusSvg(st)}</span>`;
const STATUS_TEXT = { online: 'Online', busy: 'Bận', away: 'Vắng', invisible: 'Ẩn', offline: 'Offline' };

// Tin nhắn: chữ thường → HTML (thoát ký tự, link bấm được, mã mặt cười → hình).
const SM_RE = new RegExp(SM_CODES.map(c => c.replace(/[.*+?^${}()|[\]\\\/]/g, '\\$&')).join('|'), 'gi');
function richText(text) {
  const out = [];
  const urlRe = /\bhttps?:\/\/[^\s<>"']+/gi;
  let last = 0, m;
  const plain = s => {
    let r = '', i = 0, k;
    SM_RE.lastIndex = 0;
    while ((k = SM_RE.exec(s))) { r += esc(s.slice(i, k.index)) + smHtml(k[0]); i = k.index + k[0].length; }
    return (r + esc(s.slice(i))).replace(/\n/g, '<br>');
  };
  while ((m = urlRe.exec(text))) {
    out.push(plain(text.slice(last, m.index)));
    out.push(`<a href="${esc(m[0])}" target="_blank">${esc(m[0])}</a>`);
    last = m.index + m[0].length;
  }
  out.push(plain(text.slice(last)));
  return out.join('');
}

// ───────────── Menu + hộp thoại + thông báo ─────────────
let _menu = null;
function closeMenu() { if (_menu) { _menu.remove(); _menu = null; } }
document.addEventListener('mousedown', e => { if (_menu && !_menu.contains(e.target)) closeMenu(); });
document.addEventListener('keydown', e => { if (e.key === 'Escape') { closeMenu(); } });
/** items: [{label, icon (html), on, dis}] hoặc '-' */
function showMenu(x, y, items) {
  closeMenu();
  const m = _menu = el('div', { class: 'menu' });
  for (const it of items) {
    if (it === '-') { m.append(el('div', { class: 'sep' })); continue; }
    const mi = el('div', { class: 'mi' + (it.dis ? ' dis' : '') }, (it.icon || '') + esc(it.label));
    if (!it.dis) mi.addEventListener('click', () => { closeMenu(); it.on(); });
    m.append(mi);
  }
  document.body.append(m);
  const r = m.getBoundingClientRect();
  m.style.left = Math.max(0, Math.min(x, innerWidth - r.width - 2)) + 'px';
  m.style.top = Math.max(0, Math.min(y, innerHeight - r.height - 2)) + 'px';
}

/** Hộp thoại kiểu cửa sổ XP. body: HTML; buttons: [{label, primary, on(dlg) → false để giữ hộp}] */
function dialog(title, body, buttons) {
  const bg = el('div', { class: 'dlg-bg' });
  const d = el('div', { class: 'dlg' });
  const close = () => bg.remove();
  d.append(el('div', { class: 'dt' }, `<span>${esc(title)}</span>`));
  const x = el('div', { class: 'x', title: 'Đóng' }, '✕'); x.onclick = close; d.firstChild.append(x);
  const b = el('div', { class: 'db' }, body); d.append(b);
  const f = el('div', { class: 'df' });
  for (const bt of buttons || [{ label: 'Đóng' }]) {
    const btn = el('button', { class: 'btn' + (bt.primary ? ' primary' : '') }, esc(bt.label));
    btn.onclick = () => { if (!bt.on || bt.on(d) !== false) close(); };
    f.append(btn);
  }
  d.append(f);
  bg.append(d);
  bg.addEventListener('keydown', e => { if (e.key === 'Escape') close(); if (e.key === 'Enter' && e.target.tagName === 'INPUT') { const p = $('.btn.primary', f); if (p) p.click(); } });
  document.body.append(bg);
  const first = $('input,textarea,select', b); if (first) first.focus();
  return d;
}

let _toastT = 0;
function toast(text, ms) {
  let t = $('.toast');
  if (!t) { t = el('div', { class: 'toast' }); document.body.append(t); }
  t.textContent = text;
  clearTimeout(_toastT);
  _toastT = setTimeout(() => t.remove(), ms || 3500);
}

function fmtSize(n) { return n < 1024 ? n + ' B' : n < 1048576 ? (n / 1024).toFixed(0) + ' KB' : n < 1073741824 ? (n / 1048576).toFixed(1) + ' MB' : (n / 1073741824).toFixed(2) + ' GB'; }
function fmtTime(ms) { const d = new Date(ms); return d.toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' }); }
function fmtDay(ms) { const d = new Date(ms); return d.toLocaleDateString('vi-VN', { weekday: 'long', day: '2-digit', month: '2-digit', year: 'numeric' }); }

// Logo Bhoo: chữ tự vẽ + mặt cười trong bong bóng chat (thiết kế riêng).
function logoSvg(big) {
  return `<svg viewBox="0 0 170 64" class="${big ? 'logo-big' : 'logo'}"><defs>
    <linearGradient id="lgB" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ffd54a"/><stop offset="1" stop-color="#f08c00"/></linearGradient>
    <radialGradient id="lgF" cx=".35" cy=".3" r=".8"><stop offset="0" stop-color="#fff8d0"/><stop offset=".4" stop-color="#ffe680"/><stop offset="1" stop-color="#f4b400"/></radialGradient></defs>
    <path d="M142 4c13 0 24 8 24 18s-11 18-24 18c-2.6 0-5-.3-7.4-1L126 45l2.6-9.2C122.5 32.6 118 27.6 118 22c0-10 11-18 24-18Z" fill="#1f4882" stroke="#0f2a52" stroke-width="1.4"/>
    <circle cx="142" cy="22" r="11.5" fill="url(#lgF)" stroke="#8a5a00" stroke-width="1.1"/>
    <ellipse cx="137.6" cy="19" rx="1.6" ry="2.3" fill="#3b2400"/><ellipse cx="146.4" cy="19" rx="1.6" ry="2.3" fill="#3b2400"/>
    <path d="M136 24.6q6 6.2 12 0" fill="none" stroke="#3b2400" stroke-width="1.8" stroke-linecap="round"/>
    <text x="2" y="44" font-family="'Arial Black','Segoe UI Black',Tahoma" font-weight="900" font-size="38" fill="url(#lgB)" stroke="#7a3d00" stroke-width="1.3" letter-spacing="-1">Bhoo</text>
    <text x="4" y="60" font-family="Tahoma" font-size="8" fill="#1f4882" letter-spacing="1">CHAT LAN · BCODE</text></svg>`;
}

// Ảnh đại diện (đã tải về máy, C# phục vụ qua https://av.chat.local/). '' = chưa có ảnh → vẽ chữ cái đầu trên nền màu.
function avUrl(p, mine) {
  if (!p || !p.av) return '';
  return `https://av.chat.local/${mine ? '_me' : encodeURIComponent(p.id)}.jpg?h=${encodeURIComponent(p.av)}`;
}
function avStyle(p, mine, darker) {
  const u = avUrl(p, mine);
  return u ? `background:#fff url('${u}') center/cover no-repeat` : `background:linear-gradient(135deg, ${p.color || '#3a6ea5'}, ${darker || '#0f2a52'})`;
}

// Kiểu chữ người gửi chọn (đậm / nghiêng / gạch chân / màu / cỡ) → CSS
function msgStyle(m) {
  const css = [];
  const s = m.style || '';
  if (s.includes('b')) css.push('font-weight:bold');
  if (s.includes('i')) css.push('font-style:italic');
  if (s.includes('u')) css.push('text-decoration:underline');
  if (m.fg && /^#[0-9a-f]{6}$/i.test(m.fg)) css.push('color:' + m.fg);
  if (m.size >= 10 && m.size <= 22) css.push('font-size:' + m.size + 'px');
  return css.join(';');
}
