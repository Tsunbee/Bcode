// Bhoo — "khung cảnh" trên đầu khung chat: dải tranh động tự vẽ bằng canvas (không dùng ảnh ngoài).
// Một người đổi thì cả cuộc chat cùng thấy (gửi tin kind "env" — cảnh hiện tại = tin env cuối trong lịch sử).
'use strict';

const SCENES = {
  bien:  { name: 'Biển xanh',   tint: '#f3f9ff' },
  mua:   { name: 'Mưa chiều',   tint: '#f4f6f8' },
  tet:   { name: 'Tết đến',     tint: '#fffaf0' },
  sao:   { name: 'Đêm sao',     tint: '#f5f5fc' },
  tuyet: { name: 'Mùa đông',    tint: '#f6fbff' },
  hoa:   { name: 'Vườn hoa',    tint: '#fbfff4' },
  pho:   { name: 'Phố đêm',     tint: '#f6f5fb' },
};

// số ngẫu nhiên cố định theo hạt giống (cảnh vẽ lại không bị nhảy)
function rng(seed) { let s = seed >>> 0 || 1; return () => ((s = (s * 1664525 + 1013904223) >>> 0) / 4294967296); }

function grad(g, h, stops) { const l = g.createLinearGradient(0, 0, 0, h); stops.forEach((c, i) => l.addColorStop(i / (stops.length - 1), c)); return l; }

function blossom(g, x, y, r, petal, center) {
  g.fillStyle = petal;
  for (let i = 0; i < 5; i++) {
    const a = i * Math.PI * 2 / 5;
    g.beginPath(); g.ellipse(x + Math.cos(a) * r * .55, y + Math.sin(a) * r * .55, r * .5, r * .32, a, 0, Math.PI * 2); g.fill();
  }
  g.fillStyle = center; g.beginPath(); g.arc(x, y, r * .22, 0, Math.PI * 2); g.fill();
}

/** Vẽ 1 khung hình của cảnh <id> lên canvas (w×h), t = giây. */
function drawScene(id, g, w, h, t) {
  const R = rng(id.length * 977 + 13);
  g.clearRect(0, 0, w, h);
  switch (id) {
    case 'bien': {
      g.fillStyle = grad(g, h, ['#7ec8f2', '#bfe6fb', '#fdf2d0']); g.fillRect(0, 0, w, h);
      g.fillStyle = '#ffd54a'; g.beginPath(); g.arc(w * .82, h * .38, h * .2, 0, Math.PI * 2); g.fill();
      g.fillStyle = 'rgba(255,255,255,.85)';
      for (let i = 0; i < 3; i++) { const cx = ((R() * w + t * 6 * (i + 1)) % (w + 80)) - 40, cy = h * (.15 + R() * .2); g.beginPath(); g.ellipse(cx, cy, 22, 6, 0, 0, Math.PI * 2); g.ellipse(cx + 12, cy - 4, 14, 6, 0, 0, Math.PI * 2); g.fill(); }
      for (let k = 0; k < 3; k++) {
        g.fillStyle = ['#3d8fd1', '#2a73b8', '#1d5c9b'][k];
        g.beginPath(); g.moveTo(0, h);
        for (let x = 0; x <= w; x += 6) g.lineTo(x, h * (.62 + k * .12) + Math.sin(x / (28 + k * 8) + t * (1.2 + k * .4) + k) * 3);
        g.lineTo(w, h); g.fill();
      }
      // thuyền buồm
      const bx = (t * 9) % (w + 60) - 30, by = h * .6 + Math.sin(t * 1.5) * 1.5;
      g.fillStyle = '#7a4a22'; g.beginPath(); g.moveTo(bx - 12, by); g.lineTo(bx + 12, by); g.lineTo(bx + 8, by + 5); g.lineTo(bx - 8, by + 5); g.fill();
      g.fillStyle = '#fff'; g.beginPath(); g.moveTo(bx, by - 20); g.lineTo(bx, by - 1); g.lineTo(bx + 11, by - 1); g.fill();
      // hải âu
      g.strokeStyle = '#33475c'; g.lineWidth = 1.3;
      for (let i = 0; i < 2; i++) { const sx = (w * (.3 + i * .15) + Math.sin(t * .7 + i) * 20), sy = h * (.22 + i * .08), f = Math.sin(t * 6 + i) * 2; g.beginPath(); g.moveTo(sx - 6, sy - f); g.quadraticCurveTo(sx - 3, sy - 3, sx, sy); g.quadraticCurveTo(sx + 3, sy - 3, sx + 6, sy - f); g.stroke(); }
      break;
    }
    case 'mua': {
      g.fillStyle = grad(g, h, ['#6f7d8c', '#9aa7b3', '#c3ccd3']); g.fillRect(0, 0, w, h);
      g.fillStyle = 'rgba(70,80,92,.75)';
      for (let i = 0; i < 6; i++) { const cx = (i / 5) * w + Math.sin(t * .2 + i) * 10, cy = 8 + (i % 2) * 6; g.beginPath(); g.ellipse(cx, cy, 50, 14, 0, 0, Math.PI * 2); g.fill(); }
      g.fillStyle = '#4c5a48'; g.fillRect(0, h - 8, w, 8);
      g.strokeStyle = 'rgba(225,235,245,.7)'; g.lineWidth = 1;
      const n = Math.floor(w / 6);
      for (let i = 0; i < n; i++) {
        const x0 = R() * w, sp = 70 + R() * 50, off = R() * h;
        const y = ((t * sp + off) % (h + 12)) - 6, x = x0 - y * .25;
        g.beginPath(); g.moveTo(x, y); g.lineTo(x - 2.5, y + 9); g.stroke();
      }
      // gợn nước
      g.strokeStyle = 'rgba(220,230,240,.6)';
      for (let i = 0; i < 5; i++) { const ph = (t * .9 + i * .37) % 1, x = (R() * w); g.beginPath(); g.ellipse(x, h - 4, 2 + ph * 9, 1 + ph * 2, 0, 0, Math.PI * 2); g.globalAlpha = 1 - ph; g.stroke(); g.globalAlpha = 1; }
      break;
    }
    case 'tet': {
      g.fillStyle = grad(g, h, ['#b3121b', '#d42a20', '#f0a020']); g.fillRect(0, 0, w, h);
      // cành mai 2 góc
      g.strokeStyle = '#5a3218'; g.lineWidth = 3; g.lineCap = 'round';
      const branch = (x, dir) => {
        g.beginPath(); g.moveTo(x, 0); g.quadraticCurveTo(x + dir * 30, h * .4, x + dir * 70, h * .55); g.stroke();
        g.lineWidth = 1.8; g.beginPath(); g.moveTo(x + dir * 25, h * .25); g.quadraticCurveTo(x + dir * 50, h * .2, x + dir * 80, h * .12); g.stroke(); g.lineWidth = 3;
        [[30, .32], [50, .5], [70, .55], [45, .2], [78, .12], [15, .12]].forEach(([dx, dy], i) => blossom(g, x + dir * dx, h * dy, 6 + (i % 2) * 2, '#ffd51e', '#d06b00'));
      };
      branch(4, 1); branch(w - 4, -1);
      // đèn lồng
      for (let i = 0; i < 2; i++) {
        const lx = w * (.38 + i * .24), sw = Math.sin(t * 1.6 + i) * 3, ly = 18;
        g.strokeStyle = '#ffd51e'; g.lineWidth = 1; g.beginPath(); g.moveTo(lx, 0); g.lineTo(lx + sw, ly - 9); g.stroke();
        g.fillStyle = '#ff3b2f'; g.beginPath(); g.ellipse(lx + sw, ly, 11, 9, 0, 0, Math.PI * 2); g.fill();
        g.fillStyle = '#ffd51e'; g.fillRect(lx + sw - 6, ly - 10, 12, 3); g.fillRect(lx + sw - 6, ly + 7, 12, 3);
        g.strokeStyle = '#ffd51e'; g.beginPath(); g.moveTo(lx + sw, ly + 10); g.lineTo(lx + sw, ly + 18); g.stroke();
      }
      g.fillStyle = '#ffe680'; g.font = 'bold 15px Tahoma'; g.textAlign = 'center'; g.shadowColor = 'rgba(0,0,0,.4)'; g.shadowBlur = 2;
      g.fillText('Chúc Mừng Năm Mới', w / 2, h * .82); g.shadowBlur = 0; g.textAlign = 'left';
      // cánh mai rơi
      for (let i = 0; i < 14; i++) {
        const x0 = R() * w, sp = 10 + R() * 14, off = R() * h;
        const y = ((t * sp + off) % (h + 10)) - 5, x = x0 + Math.sin(t + i) * 8;
        g.fillStyle = '#ffd51e'; g.beginPath(); g.ellipse(x, y, 3, 1.8, t + i, 0, Math.PI * 2); g.fill();
      }
      break;
    }
    case 'sao': {
      g.fillStyle = grad(g, h, ['#0b1030', '#1b2458', '#36407a']); g.fillRect(0, 0, w, h);
      for (let i = 0; i < Math.floor(w / 7); i++) {
        const x = R() * w, y = R() * h * .9, r = R() * 1.2 + .3, ph = R() * 6;
        g.fillStyle = `rgba(255,255,240,${.35 + .65 * Math.abs(Math.sin(t * (1 + r) + ph))})`; g.fillRect(x, y, r + .4, r + .4);
      }
      // trăng khuyết
      g.fillStyle = '#fff4c2'; g.beginPath(); g.arc(w * .85, h * .4, 13, 0, Math.PI * 2); g.fill();
      g.fillStyle = '#18214f'; g.beginPath(); g.arc(w * .85 + 6, h * .4 - 3, 11, 0, Math.PI * 2); g.fill();
      // sao băng mỗi ~7 giây
      const c = (t % 7) / 1.2;
      if (c < 1) {
        const sx = w * (.15 + ((Math.floor(t / 7) * 37) % 50) / 100) + c * 120, sy = 6 + c * 30;
        const lg = g.createLinearGradient(sx, sy, sx - 40, sy - 10); lg.addColorStop(0, 'rgba(255,255,255,.95)'); lg.addColorStop(1, 'rgba(255,255,255,0)');
        g.strokeStyle = lg; g.lineWidth = 1.6; g.beginPath(); g.moveTo(sx, sy); g.lineTo(sx - 40, sy - 10); g.stroke();
      }
      g.fillStyle = '#0a0d22';
      g.beginPath(); g.moveTo(0, h); for (let x = 0; x <= w; x += 20) g.lineTo(x, h - 6 - Math.abs(Math.sin(x / 70)) * 10); g.lineTo(w, h); g.fill();
      break;
    }
    case 'tuyet': {
      g.fillStyle = grad(g, h, ['#9cc6ea', '#d4e9f8', '#f4fbff']); g.fillRect(0, 0, w, h);
      g.fillStyle = '#ffffff'; g.beginPath(); g.moveTo(0, h); for (let x = 0; x <= w; x += 10) g.lineTo(x, h - 10 - Math.sin(x / 60) * 4); g.lineTo(w, h); g.fill();
      for (let i = 0; i < Math.max(3, Math.floor(w / 90)); i++) {
        const x = (i + .5) * w / Math.max(3, Math.floor(w / 90)) + (R() - .5) * 30, base = h - 9, th = 26 + R() * 14;
        g.fillStyle = '#2d6a4a';
        for (let k = 0; k < 3; k++) { const y = base - k * th / 3.2; g.beginPath(); g.moveTo(x - 12 + k * 3, y); g.lineTo(x + 12 - k * 3, y); g.lineTo(x, y - th / 2.2); g.fill(); }
        g.fillStyle = '#fff'; g.beginPath(); g.moveTo(x - 4, base - th * .78); g.lineTo(x + 4, base - th * .78); g.lineTo(x, base - th * .98); g.fill();
      }
      g.fillStyle = '#fff';
      for (let i = 0; i < Math.floor(w / 9); i++) {
        const x0 = R() * w, sp = 8 + R() * 14, off = R() * h, r = R() * 1.6 + .8;
        const y = ((t * sp + off) % (h + 6)) - 3, x = x0 + Math.sin(t * .8 + i) * 6;
        g.beginPath(); g.arc(x, y, r, 0, Math.PI * 2); g.fill();
      }
      break;
    }
    case 'hoa': {
      g.fillStyle = grad(g, h, ['#a9dcf7', '#dff3fb']); g.fillRect(0, 0, w, h);
      g.fillStyle = '#7cc25a'; g.beginPath(); g.moveTo(0, h); for (let x = 0; x <= w; x += 10) g.lineTo(x, h * .6 - Math.sin(x / 90) * 8); g.lineTo(w, h); g.fill();
      g.fillStyle = '#5aa63e'; g.beginPath(); g.moveTo(0, h); for (let x = 0; x <= w; x += 10) g.lineTo(x, h * .78 - Math.sin(x / 50 + 1) * 5); g.lineTo(w, h); g.fill();
      const cols = ['#ff6fa5', '#ffd23f', '#ffffff', '#c58cff', '#ff8a3d'];
      for (let i = 0; i < Math.floor(w / 14); i++) {
        const x = R() * w, y = h * (.7 + R() * .28), c = cols[Math.floor(R() * cols.length)], sw = Math.sin(t * 1.5 + i) * 1.2;
        g.strokeStyle = '#3e8a2a'; g.lineWidth = 1; g.beginPath(); g.moveTo(x, y + 6); g.lineTo(x + sw, y); g.stroke();
        blossom(g, x + sw, y, 4.5, c, '#f5b400');
      }
      // bướm
      for (let i = 0; i < 2; i++) {
        const bx = (w * (.2 + i * .5) + Math.sin(t * .5 + i * 2) * w * .15), by = h * .35 + Math.sin(t * 1.3 + i) * 8, f = Math.abs(Math.sin(t * 9 + i));
        g.fillStyle = i ? '#ffb300' : '#ff5c8a';
        g.beginPath(); g.ellipse(bx - 3 * f, by, 3.5 * f + .5, 4, 0, 0, Math.PI * 2); g.ellipse(bx + 3 * f, by, 3.5 * f + .5, 4, 0, 0, Math.PI * 2); g.fill();
      }
      break;
    }
    case 'pho': {
      g.fillStyle = grad(g, h, ['#1a1440', '#3a2a6a', '#7a4a7a']); g.fillRect(0, 0, w, h);
      g.fillStyle = 'rgba(255,255,255,.7)';
      for (let i = 0; i < 20; i++) g.fillRect(R() * w, R() * h * .4, 1, 1);
      let x = 0, b = 0;
      while (x < w) {
        const bw = 22 + R() * 30, bh = h * (.35 + R() * .55);
        g.fillStyle = ['#120e2a', '#1b1538', '#231a45'][b % 3]; g.fillRect(x, h - bh, bw, bh);
        for (let wy = h - bh + 5; wy < h - 6; wy += 7)
          for (let wx = x + 4; wx < x + bw - 4; wx += 6) {
            const on = Math.sin(wx * 12.9898 + wy * 78.233 + Math.floor(t / 3 + (wx + wy) % 5) * 0.7) > .25;
            if (on) { g.fillStyle = '#ffd77a'; g.fillRect(wx, wy, 3, 3); }
          }
        x += bw + 2; b++;
      }
      break;
    }
  }
}

/** Gắn cảnh vào 1 canvas (động, ~30 khung/giây; cửa sổ ẩn thì dừng). id rỗng = gỡ. */
const Scene = (() => {
  let cur = '', raf = 0, last = 0;
  function loop(ts) {
    raf = requestAnimationFrame(loop);
    if (document.hidden || ts - last < 33) return;
    last = ts;
    const c = $('#scene canvas');
    if (!c || !cur) return;
    const w = c.clientWidth, h = c.clientHeight, dpr = window.devicePixelRatio || 1;
    if (c.width !== Math.round(w * dpr)) { c.width = Math.round(w * dpr); c.height = Math.round(h * dpr); }
    const g = c.getContext('2d'); g.setTransform(dpr, 0, 0, dpr, 0, 0);
    drawScene(cur, g, w, h, ts / 1000);
  }
  return {
    get id() { return cur; },
    set(id) {
      cur = SCENES[id] ? id : '';
      document.body.classList.toggle('has-scene', !!cur);
      document.documentElement.style.setProperty('--logbg', cur ? SCENES[cur].tint : '#fff');
      $('#scene').title = cur ? 'Khung cảnh: ' + SCENES[cur].name + ' (Xem → Khung cảnh… để đổi)' : '';
      if (cur && !raf) raf = requestAnimationFrame(loop);
      if (!cur && raf) { cancelAnimationFrame(raf); raf = 0; }
    },
  };
})();

/** Ô chọn cảnh: lưới hình xem trước (vẽ tĩnh). */
function scenePicker(onPick) {
  const items = [['', 'Không có']].concat(Object.entries(SCENES).map(([k, v]) => [k, v.name]));
  const d = dialog('Khung cảnh cho cuộc chat', `<div class="scgrid">${items.map(([k, n]) =>
    `<div class="scitem${k === Scene.id ? ' on' : ''}" data-k="${k}"><canvas width="150" height="44"></canvas><span>${esc(n)}</span></div>`).join('')}</div>
    <div class="muted" style="margin-top:6px">Mọi người trong cuộc chat sẽ cùng thấy khung cảnh bạn chọn.</div>`, [{ label: 'Đóng' }]);
  $$('.scitem', d).forEach(it => {
    const c = $('canvas', it), g = c.getContext('2d');
    if (it.dataset.k) drawScene(it.dataset.k, g, 150, 44, 2.3);
    else { g.fillStyle = '#fff'; g.fillRect(0, 0, 150, 44); g.fillStyle = '#9a978a'; g.font = '11px Tahoma'; g.fillText('(nền trắng)', 46, 26); }
    it.onclick = () => { onPick(it.dataset.k); d.parentElement.remove(); };
  });
}
