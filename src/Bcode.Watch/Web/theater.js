"use strict";
// Rạp chiếu vẽ bằng canvas: tường + rèm nhung, màn chiếu (khung cho <video> / YouTube đặt chồng lên), luồng sáng máy chiếu,
// 3 dãy ghế nhìn từ phía sau khán phòng — người xem là nhân vật tự vẽ (đầu + vai nhìn từ sau lưng), bình luận hiện bong bóng trên đầu,
// cảm xúc bay lên từ chỗ ngồi. Hình vẽ tự làm, không dùng ảnh ngoài.
const Theater = (() => {
  const ROWS = 3, COLS = 8;
  const fx = new Map();      // viewerId → { bubble:{text,until}, pops:[{e,t0}], bounce:t0 }
  let speaking = new Set();  // chat voice: id người đang nói
  let L = null;              // bố cục lần vẽ gần nhất

  function layout(W, H) {
    const top = Math.max(18, H * 0.05);
    const maxH = H * 0.52;
    let sw = Math.min(W * 0.76, maxH * 16 / 9);
    let sh = sw * 9 / 16;
    const screen = { x: (W - sw) / 2, y: top, w: sw, h: sh };
    const floorTop = top + sh + Math.max(10, H * 0.025);
    const seats = [];
    for (let r = 0; r < ROWS; r++) {
      const k = r / (ROWS - 1);                       // 0 = dãy đầu (gần màn), 1 = dãy cuối (gần người nhìn)
      const s = (0.95 + k * 0.4) * Math.min(1.3, Math.max(0.6, H / 700));
      const y = floorTop + (H - floorTop) * (0.3 + k * 0.42);
      const span = Math.min(W * (0.6 + k * 0.32), sw * (1.0 + k * 0.35));
      const step = span / COLS;
      for (let c = 0; c < COLS; c++) {
        const aisle = c >= COLS / 2 ? step * 0.35 : -step * 0.35;   // lối đi giữa
        seats.push({ x: W / 2 - span / 2 + step * (c + 0.5) + aisle, y, s, row: r });
      }
    }
    L = { W, H, screen, seats, floorTop };
    return L;
  }

  function hashHue(str) { let h = 0; for (const ch of str) h = (h * 31 + ch.codePointAt(0)) >>> 0; return h; }

  function rr(c, x, y, w, h, r) { c.beginPath(); c.roundRect(x, y, w, h, r); }

  function drawHall(c, t) {
    const { W, H, screen: S, floorTop } = L;
    // tường tối
    let g = c.createLinearGradient(0, 0, 0, H);
    g.addColorStop(0, "#120d16"); g.addColorStop(0.55, "#0b080e"); g.addColorStop(1, "#060408");
    c.fillStyle = g; c.fillRect(0, 0, W, H);
    // ánh màn hắt lên tường
    g = c.createRadialGradient(W / 2, S.y + S.h / 2, S.h * 0.2, W / 2, S.y + S.h / 2, S.w * 0.9);
    g.addColorStop(0, "rgba(150,170,230,.16)"); g.addColorStop(1, "rgba(150,170,230,0)");
    c.fillStyle = g; c.fillRect(0, 0, W, H);
    // rèm nhung hai bên
    for (const side of [-1, 1]) {
      const x0 = side < 0 ? 0 : S.x + S.w + 18, x1 = side < 0 ? S.x - 18 : W;
      if (x1 - x0 < 6) continue;
      const folds = Math.max(3, Math.round((x1 - x0) / 26));
      const fw = (x1 - x0) / folds;
      for (let i = 0; i < folds; i++) {
        const gx = c.createLinearGradient(x0 + i * fw, 0, x0 + (i + 1) * fw, 0);
        gx.addColorStop(0, "#3d0c15"); gx.addColorStop(0.5, "#7a1828"); gx.addColorStop(1, "#33090f");
        c.fillStyle = gx; c.fillRect(x0 + i * fw, 0, fw + 1, floorTop + 8);
      }
      const shade = c.createLinearGradient(0, 0, 0, floorTop);
      shade.addColorStop(0, "rgba(0,0,0,.55)"); shade.addColorStop(0.3, "rgba(0,0,0,.05)"); shade.addColorStop(1, "rgba(0,0,0,.45)");
      c.fillStyle = shade; c.fillRect(x0, 0, x1 - x0, floorTop + 8);
    }
    // diềm trên màn
    g = c.createLinearGradient(0, 0, 0, S.y);
    g.addColorStop(0, "#2a0710"); g.addColorStop(1, "#5e1220");
    c.fillStyle = g; c.fillRect(S.x - 30, 0, S.w + 60, Math.max(6, S.y - 6));
    // khung màn
    c.fillStyle = "#020203"; rr(c, S.x - 8, S.y - 8, S.w + 16, S.h + 16, 6); c.fill();
    // bục trước màn
    c.fillStyle = "#1a1219"; c.fillRect(S.x - 40, S.y + S.h + 8, S.w + 80, Math.max(4, floorTop - S.y - S.h - 8));
    // sàn + bậc từng dãy
    g = c.createLinearGradient(0, floorTop, 0, H);
    g.addColorStop(0, "#150f17"); g.addColorStop(1, "#0a070c");
    c.fillStyle = g; c.fillRect(0, floorTop, W, H - floorTop);
    for (let r = 0; r < ROWS; r++) {
      const s = L.seats[r * COLS];
      const y = s.y + 34 * s.s;
      c.fillStyle = "rgba(255,255,255,.035)"; c.fillRect(0, y, W, 2);
      // đèn bậc lối đi
      c.fillStyle = `rgba(255,190,90,${0.35 + 0.15 * Math.sin(t / 900 + r)})`;
      c.beginPath(); c.arc(W / 2, y - 2, 2.2 * s.s, 0, Math.PI * 2); c.fill();
    }
  }

  function drawBeam(c, t) {
    const { W, H, screen: S } = L;
    const px = W / 2, py = H + 30;
    const g = c.createLinearGradient(px, py, px, S.y + S.h);
    g.addColorStop(0, "rgba(255,240,200,.10)"); g.addColorStop(1, "rgba(200,215,255,.025)");
    c.fillStyle = g;
    c.beginPath(); c.moveTo(px - 10, py); c.lineTo(S.x + S.w * 0.04, S.y + S.h * 0.15); c.lineTo(S.x + S.w * 0.96, S.y + S.h * 0.15); c.lineTo(px + 10, py); c.closePath(); c.fill();
    // bụi lơ lửng trong luồng sáng
    c.fillStyle = "rgba(255,245,220,.22)";
    for (let i = 0; i < 26; i++) {
      const k = ((i * 0.137 + t / 26000 * (1 + (i % 3) * 0.3)) % 1);
      const y = py - (py - S.y - S.h) * k;
      const half = 10 + (S.w * 0.46) * k;
      const x = px + Math.sin(i * 12.9898) * half * 0.9;
      c.fillRect(x, y, 1.6, 1.6);
    }
  }

  function chair(c, x, y, s, empty, hover) {
    const w = 46 * s, h = 34 * s;
    c.fillStyle = hover ? "#a42a3d" : empty ? "#55131f" : "#6a1724";
    rr(c, x - w / 2, y - h * 0.15, w, h, [10 * s, 10 * s, 4 * s, 4 * s]); c.fill();
    c.fillStyle = "rgba(255,255,255,.08)"; rr(c, x - w / 2 + 3 * s, y - h * 0.15 + 2 * s, w - 6 * s, 5 * s, 4 * s); c.fill();
    // tay vịn
    c.fillStyle = "#2a0a10";
    c.fillRect(x - w / 2 - 4 * s, y + h * 0.35, 5 * s, h * 0.5);
    c.fillRect(x + w / 2 - 1 * s, y + h * 0.35, 5 * s, h * 0.5);
  }

  /** Người xem nhìn từ sau lưng: vai áo màu riêng, gáy, tóc (kiểu tóc theo tên). */
  function person(c, x, y, s, v, t, isMe) {
    const h = hashHue(v.name);
    const st = fx.get(v.id);
    let bob = Math.sin(t / 700 + v.id) * 0.8 * s;
    if (st?.bounce) { const k = (t - st.bounce) / 450; if (k < 1) bob -= Math.sin(k * Math.PI) * 9 * s; }
    const hy = y - 30 * s + bob;
    // vai
    c.fillStyle = v.color;
    rr(c, x - 20 * s, y - 14 * s + bob * 0.5, 40 * s, 26 * s, [12 * s, 12 * s, 4 * s, 4 * s]); c.fill();
    c.fillStyle = "rgba(0,0,0,.18)"; c.fillRect(x - 1 * s, y - 12 * s + bob * 0.5, 2 * s, 22 * s);
    // cổ
    c.fillStyle = "#e7b892"; c.fillRect(x - 5 * s, hy + 8 * s, 10 * s, 9 * s);
    // tai
    c.beginPath(); c.ellipse(x - 12.5 * s, hy + 1 * s, 2.6 * s, 4 * s, 0, 0, Math.PI * 2); c.ellipse(x + 12.5 * s, hy + 1 * s, 2.6 * s, 4 * s, 0, 0, Math.PI * 2); c.fill();
    // tóc
    const hair = ["#1b1410", "#2d1d14", "#3b2416", "#111014", "#5a3a22", "#2a2228"][h % 6];
    c.fillStyle = hair;
    c.beginPath(); c.ellipse(x, hy, 13 * s, 14 * s, 0, 0, Math.PI * 2); c.fill();
    const style = (h >> 4) % 4;
    if (style === 1) { c.beginPath(); c.ellipse(x, hy + 14 * s, 5 * s, 9 * s, 0, 0, Math.PI * 2); c.fill(); }                 // tóc buộc
    if (style === 2) { rr(c, x - 13 * s, hy, 26 * s, 20 * s, 6 * s); c.fill(); }                                                   // tóc dài
    if (style === 3) { c.beginPath(); c.arc(x, hy - 12 * s, 5 * s, 0, Math.PI * 2); c.fill(); }                                     // búi
    c.fillStyle = "rgba(255,255,255,.07)"; c.beginPath(); c.ellipse(x - 4 * s, hy - 6 * s, 5 * s, 3 * s, -0.5, 0, Math.PI * 2); c.fill();
    // chat voice: đang nói → viền xanh nhấp nháy quanh đầu (như ô sáng của Google Meet)
    if (speaking.has(v.id)) {
      c.strokeStyle = `rgba(76,195,138,${0.65 + 0.35 * Math.sin(t / 90)})`; c.lineWidth = 2.6 * s;
      c.beginPath(); c.ellipse(x, hy, 17 * s, 18 * s, 0, 0, Math.PI * 2); c.stroke();
    }
    if (isMe) {
      c.fillStyle = "#ffc94a";
      c.beginPath(); c.moveTo(x, hy - 22 * s); c.lineTo(x - 5 * s, hy - 30 * s); c.lineTo(x + 5 * s, hy - 30 * s); c.closePath(); c.fill();
    }
  }

  function nameTag(c, x, y, s, v) {
    c.font = `${Math.max(10, 11 * s)}px "Segoe UI", sans-serif`;
    c.textAlign = "center"; c.textBaseline = "top";
    c.fillStyle = "rgba(230,220,240,.72)";
    c.fillText((v.voice === 2 ? "🎙" : v.voice === 1 ? "🔇" : "") + (v.host ? "★ " : "") + v.name, x, y + 24 * s, 64 * s);
  }

  function wrap(c, text, maxW) {
    const words = text.split(/\s+/); const lines = []; let cur = "";
    for (const w of words) {
      const test = cur ? cur + " " + w : w;
      if (c.measureText(test).width > maxW && cur) { lines.push(cur); cur = w; } else cur = test;
      if (lines.length === 2) break;
    }
    if (lines.length < 2 && cur) lines.push(cur);
    if (lines.length === 2 && (lines.join(" ").length < text.length)) {
      let l = lines[1]; while (l.length > 1 && c.measureText(l + "…").width > maxW) l = l.slice(0, -1); lines[1] = l + "…";
    }
    return lines.map((l) => { let x = l; while (x.length > 1 && c.measureText(x).width > maxW) x = x.slice(0, -1); return x; });
  }

  function bubble(c, x, y, s, text, alpha) {
    const fs = Math.max(11, 12.5 * Math.min(s, 1.1));
    c.font = `${fs}px "Segoe UI", sans-serif`;
    const lines = wrap(c, text, 170);
    const w = Math.max(...lines.map((l) => c.measureText(l).width)) + 16;
    const h = lines.length * (fs + 3) + 10;
    const bx = Math.max(4, Math.min(L.W - w - 4, x - w / 2)), by = y - h - 10;
    c.globalAlpha = alpha;
    c.fillStyle = "rgba(250,247,252,.96)";
    rr(c, bx, by, w, h, 8); c.fill();
    c.beginPath(); c.moveTo(x - 6, by + h - 1); c.lineTo(x + 6, by + h - 1); c.lineTo(x, by + h + 8); c.closePath(); c.fill();
    c.fillStyle = "#1c1622"; c.textAlign = "left"; c.textBaseline = "top";
    lines.forEach((l, i) => c.fillText(l, bx + 8, by + 5 + i * (fs + 3)));
    c.globalAlpha = 1;
  }

  function draw(c, W, H, t, viewers, meId, hover) {
    layout(W, H);
    drawHall(c, t);
    const bySeat = new Map(viewers.map((v) => [v.seat, v]));
    // dãy đầu vẽ trước, dãy sau đè lên
    for (let r = 0; r < ROWS; r++) {
      for (let col = 0; col < COLS; col++) {
        const i = r * COLS + col, p = L.seats[i], v = bySeat.get(i);
        if (v) person(c, p.x, p.y, p.s, v, t, v.id === meId);
        chair(c, p.x, p.y, p.s, !v, hover === i && !v);
        if (v) nameTag(c, p.x, p.y, p.s, v);
      }
    }
    drawBeam(c, t);
    // bong bóng + cảm xúc trên cùng
    for (const v of viewers) {
      const p = L.seats[v.seat]; if (!p) continue;
      const st = fx.get(v.id); if (!st) continue;
      const headY = p.y - 46 * p.s;
      if (st.bubble && t < st.bubble.until) {
        const left = st.bubble.until - t;
        bubble(c, p.x, headY, p.s, st.bubble.text, Math.min(1, left / 400));
      }
      st.pops = st.pops.filter((q) => t - q.t0 < 2200);
      for (const q of st.pops) {
        const k = (t - q.t0) / 2200;
        c.globalAlpha = k < 0.12 ? k / 0.12 : 1 - Math.max(0, (k - 0.55) / 0.45);
        c.font = `${Math.round(26 * p.s * (k < 0.12 ? 0.7 + k * 3 : 1.05))}px "Segoe UI Emoji", sans-serif`;
        c.textAlign = "center"; c.textBaseline = "middle";
        c.fillText(q.e, p.x + Math.sin(k * 6 + q.dx) * 10 * p.s, headY - k * 110 * p.s);
      }
      c.globalAlpha = 1;
    }
    return L;
  }

  function seatAt(x, y) {
    if (!L) return -1;
    let best = -1, bd = 1e9;
    L.seats.forEach((p, i) => {
      const dx = (x - p.x) / (30 * p.s), dy = (y - (p.y - 8 * p.s)) / (36 * p.s);
      const d = dx * dx + dy * dy;
      if (d < 1 && d < bd) { bd = d; best = i; }
    });
    return best;
  }

  function st(id) { let s = fx.get(id); if (!s) fx.set(id, (s = { pops: [] })); return s; }
  return {
    ROWS, COLS, layout, draw, seatAt,
    get L() { return L; },
    say(id, text) { st(id).bubble = { text, until: performance.now() + Math.min(9000, 3500 + text.length * 60) }; },
    pop(id, e) { const s = st(id); s.pops.push({ e, t0: performance.now(), dx: Math.random() * 6 }); s.bounce = performance.now(); },
    forget(id) { fx.delete(id); },
    /** Chat voice (voice.js): tập id người đang nói. */
    setSpeaking(set) { speaking = set; },
  };
})();
