// Bhoo — cửa sổ xem lại lịch sử chat: chọn cuộc chat bên trái, lọc theo chữ / người gửi / ngày; "Tìm mọi cuộc chat" thì C# tìm hộ.
'use strict';

let me = null, convs = [], cur = null, msgs = [], found = null;

onHost(m => {
  switch (m.t) {
    case 'init':
      me = m.me; convs = m.convs || [];
      renderConvs();
      if (m.select && convs.some(c => c.conv === m.select)) pick(m.select);
      else if (convs.length) pick(convs[0].conv);
      else $('#log').innerHTML = '<div class="empty">Chưa có lịch sử chat nào trên máy này.</div>';
      break;
    case 'select': if (convs.some(c => c.conv === m.conv)) pick(m.conv); break;
    case 'msgs':
      if (m.conv !== cur) break;
      msgs = m.list || []; found = null;
      fillWho();
      render();
      break;
    case 'found': found = m; render(); break;
    case 'error': toast(m.text, 4000); break;
  }
});

function renderConvs() {
  const box = $('#convs');
  box.innerHTML = '';
  for (const c of convs) {
    const pic = c.group ? `<div class="pic" style="background:linear-gradient(#5c8fcf,#2b5a9a)">👥</div>`
      : `<div class="pic" style="${avStyle({ id: c.conv.slice(2), av: c.av, color: c.color })}">${c.av ? '' : esc((c.title || '?')[0].toUpperCase())}</div>`;
    const r = el('div', { class: 'cv' + (c.conv === cur ? ' sel' : ''), 'data-conv': c.conv, title: 'Nhấp đúp để mở cửa sổ chat' },
      `${pic}<div class="t"><div class="n">${esc(c.title)}</div><div class="d">${new Date(c.modified).toLocaleDateString('vi-VN')} · ${fmtSize(c.bytes)}</div></div>`);
    r.onclick = () => pick(c.conv);
    r.ondblclick = () => send('open', { conv: c.conv });
    box.append(r);
  }
}

function pick(conv) {
  cur = conv; found = null;
  $$('.cv').forEach(x => x.classList.toggle('sel', x.dataset.conv === conv));
  $('#log').innerHTML = '<div class="empty">Đang đọc…</div>';
  send('load', { conv });
}

function fillWho() {
  const sel = $('#who'), keep = sel.value;
  const names = new Map();
  for (const m of msgs) if (m.kind !== 'sys' && m.kind !== 'leave') names.set(m.from, m.from === me?.id ? me.name + ' (bạn)' : m.fromName || m.from);
  sel.innerHTML = '<option value="">(tất cả)</option>' + [...names].map(([id, n]) => `<option value="${esc(id)}">${esc(n)}</option>`).join('');
  sel.value = names.has(keep) ? keep : '';
}

function dayStart(v) { return v ? new Date(v + 'T00:00:00').getTime() : null; }

function filtered() {
  const q = $('#q').value.trim().toLowerCase(), who = $('#who').value;
  const from = dayStart($('#from').value), to = dayStart($('#to').value);
  return msgs.filter(m =>
    (!who || m.from === who) &&
    (from === null || m.at >= from) &&
    (to === null || m.at < to + 86400e3) &&
    (!q || (m.text || '').toLowerCase().includes(q) || (m.file?.name || '').toLowerCase().includes(q)));
}

function line(m, mine, convTitle, conv) {
  const who = mine ? me.name : (m.fromName || m.from);
  const tm = `<span class="tm">(${fmtTime(m.at)})</span>`;
  const tag = convTitle ? `<span class="cvn" data-conv="${esc(conv)}" title="Xem cuộc chat này">${esc(convTitle)}</span>` : '';
  switch (m.kind) {
    case 'sys': case 'leave': return el('div', { class: 'sys' }, `${tag}${esc(m.text)} ${tm}`);
    case 'buzz': return el('div', { class: 'ln' }, `${tag}<span class="who ${mine ? 'me' : 'them'}">${esc(who)}</span> ${tm}: <span class="buzz">BUZZ!!!</span>`);
    case 'env': return el('div', { class: 'sys' }, `${tag}🎨 ${esc(who)} đã đổi khung cảnh ${tm}`);
    case 'file': return el('div', { class: 'ln' }, `${tag}<span class="who ${mine ? 'me' : 'them'}">${esc(who)}</span> ${tm}: 📎 <span class="txt">${esc(m.file?.name || '')}</span> <span class="tm">(${fmtSize(m.file?.size || 0)})</span>`);
    default: return el('div', { class: 'ln' }, `${tag}<span class="who ${mine ? 'me' : 'them'}">${esc(who)}</span> ${tm}: <span class="txt" style="${msgStyle(m)}">${richText(m.text || '')}</span>`);
  }
}

// tô vàng chữ cần tìm (chỉ trong phần chữ, không đụng thẻ / mặt cười)
function highlight(root, q) {
  if (!q) return;
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const nodes = [];
  while (walker.nextNode()) nodes.push(walker.currentNode);
  const ql = q.toLowerCase();
  for (const n of nodes) {
    if (!n.parentElement.closest('.txt')) continue;
    const t = n.nodeValue, tl = t.toLowerCase();
    let i = tl.indexOf(ql);
    if (i < 0) continue;
    const frag = document.createDocumentFragment();
    let last = 0;
    while (i >= 0) {
      frag.append(t.slice(last, i));
      const mk = document.createElement('mark'); mk.textContent = t.slice(i, i + q.length); frag.append(mk);
      last = i + q.length; i = tl.indexOf(ql, last);
    }
    frag.append(t.slice(last));
    n.replaceWith(frag);
  }
}

function render() {
  const log = $('#log');
  log.innerHTML = '';
  let day = '';
  const q = found ? found.q : $('#q').value.trim();
  const list = found ? found.list : filtered().map(m => ({ m, mine: m.from === me?.id }));
  for (const x of list) {
    const d = new Date(x.m.at).toDateString();
    if (d !== day) { day = d; log.append(el('div', { class: 'day' }, `<span>${esc(fmtDay(x.m.at))}</span>`)); }
    log.append(line(x.m, x.mine, found ? x.title : null, x.conv));
  }
  highlight(log, q);
  $$('.cvn', log).forEach(t => t.onclick = () => { $('#q').value = found.q; pick(t.dataset.conv); });
  if (!list.length) log.append(el('div', { class: 'empty' }, found ? `Không thấy "${esc(found.q)}" trong cuộc chat nào.` : msgs.length ? 'Không có tin nào khớp bộ lọc.' : 'Cuộc chat này chưa có tin nào.'));
  const conv = convs.find(c => c.conv === cur);
  $('#sb').textContent = found ? `Tìm "${found.q}" trong mọi cuộc chat: ${found.list.length}${found.list.length >= 500 ? '+' : ''} tin`
    : `${conv ? conv.title + ': ' : ''}${list.length} / ${msgs.length} tin`;
  if (found || q) log.scrollTop = 0; else log.scrollTop = log.scrollHeight;
}

let qT = 0;
$('#q').addEventListener('input', () => { clearTimeout(qT); qT = setTimeout(() => { found = null; render(); }, 200); });
$('#q').addEventListener('keydown', e => { if (e.key === 'Enter' && e.ctrlKey) $('#bAll').click(); });
['#who', '#from', '#to'].forEach(s => $(s).addEventListener('change', () => { found = null; render(); }));
$('#bAll').onclick = () => send('search', { q: $('#q').value });
$('#bOpen').onclick = () => cur && send('open', { conv: cur });
$('#bExport').onclick = () => cur && send('export', { conv: cur });
document.addEventListener('keydown', e => { if (e.key === 'Escape') send('close'); if (e.key === 'f' && e.ctrlKey) { e.preventDefault(); $('#q').focus(); } });

send('ui.init');
