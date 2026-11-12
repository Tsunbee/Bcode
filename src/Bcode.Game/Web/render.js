// Vẽ "Đêm Go-Live" bằng canvas 2D: bản đồ văn phòng, bàn / máy, nhân vật nhân viên văn phòng (hình khối tự vẽ), sương tầm nhìn, bản đồ nhỏ.
(function () {
  const COLORS = ["#e5484d", "#3e9bff", "#30a46c", "#f5a524", "#a46cf5", "#ff7ab8", "#2ec4c4", "#c9ced6"];
  const COLOR_NAMES = ["Đỏ", "Xanh dương", "Xanh lá", "Cam", "Tím", "Hồng", "Ngọc", "Bạc"];
  const HAIR = ["#2b1d14", "#16120f", "#5a3a22", "#1b1b22", "#3b2a1a", "#6b4a2b", "#111", "#4a4a52"];
  const FLOOR = { module: "#202838", dev: "#1b2a29", test: "#271f33", meet: "#2a2620", hall: "#171d28" };
  const LABEL = { module: "#8fb4ff", dev: "#58d6a0", test: "#c9a2ff", meet: "#ffcc4d" };

  function shade(hex, f) {
    const n = parseInt(hex.slice(1), 16);
    let r = (n >> 16) & 255, g = (n >> 8) & 255, b = n & 255;
    r = Math.round(Math.min(255, Math.max(0, r * f))); g = Math.round(Math.min(255, Math.max(0, g * f))); b = Math.round(Math.min(255, Math.max(0, b * f)));
    return `rgb(${r},${g},${b})`;
  }

  function rr(c, x, y, w, h, r) {
    c.beginPath();
    c.moveTo(x + r, y); c.arcTo(x + w, y, x + w, y + h, r); c.arcTo(x + w, y + h, x, y + h, r);
    c.arcTo(x, y + h, x, y, r); c.arcTo(x, y, x + w, y, r); c.closePath();
  }

  /** Nhân viên văn phòng: áo màu người chơi, thẻ nhân viên đeo cổ, tóc theo màu. o: {walk, face(-1/1), alpha, lying}. Gốc (x,y) = tâm thân. */
  function person(c, x, y, colorIdx, o = {}) {
    const col = COLORS[colorIdx % COLORS.length];
    c.save();
    c.translate(x, y);
    if (o.alpha != null) c.globalAlpha = o.alpha;
    if (o.lying) c.rotate(Math.PI / 2);
    const face = o.face || 1;
    const bob = o.walk ? Math.sin(o.walk) * 1.6 : 0;
    // bóng
    if (!o.lying && !o.ghost) { c.fillStyle = "rgba(0,0,0,.35)"; c.beginPath(); c.ellipse(0, 17, 15, 5, 0, 0, Math.PI * 2); c.fill(); }
    // chân (quần tối)
    if (!o.ghost) {
      const s1 = o.walk ? Math.sin(o.walk) * 4 : 0;
      c.fillStyle = "#283041";
      rr(c, -10 + s1 * 0.4, 8, 8, 10, 3); c.fill();
      rr(c, 2 - s1 * 0.4, 8, 8, 10, 3); c.fill();
    }
    // thân (áo)
    c.fillStyle = col;
    c.strokeStyle = shade(col, 0.6); c.lineWidth = 1.5;
    if (o.ghost) {
      c.beginPath(); c.moveTo(-14, -10 + bob);
      c.lineTo(14, -10 + bob); c.lineTo(14, 14 + bob);
      for (let i = 0; i <= 4; i++) c.lineTo(14 - i * 7, 14 + bob + (i % 2 ? -5 : 0));
      c.closePath(); c.fill(); c.stroke();
    } else { rr(c, -14, -10 + bob, 28, 22, 8); c.fill(); c.stroke(); }
    // cổ áo + dây thẻ
    c.fillStyle = "#f4f4f4";
    c.beginPath(); c.moveTo(-6, -10 + bob); c.lineTo(0, -3 + bob); c.lineTo(6, -10 + bob); c.closePath(); c.fill();
    c.strokeStyle = "#1d2433"; c.lineWidth = 1.2;
    c.beginPath(); c.moveTo(-4, -9 + bob); c.lineTo(face * 3, 2 + bob); c.lineTo(4, -9 + bob); c.stroke();
    c.fillStyle = "#f8f8f8"; rr(c, face * 3 - 4, 1 + bob, 8, 9, 1.5); c.fill();
    c.fillStyle = shade(col, 0.75); c.fillRect(face * 3 - 3, 2 + bob, 6, 2.5);
    // đầu
    c.fillStyle = "#f2c79a";
    c.beginPath(); c.arc(0, -19 + bob, 10, 0, Math.PI * 2); c.fill();
    // tóc
    c.fillStyle = HAIR[colorIdx % HAIR.length];
    c.beginPath(); c.arc(0, -21 + bob, 10.5, Math.PI * 1.02, Math.PI * 1.98); c.fill();
    c.beginPath(); c.ellipse(-face * 6, -24 + bob, 6, 4, -face * 0.5, 0, Math.PI * 2); c.fill();
    // mắt
    c.fillStyle = "#1a1a1a";
    if (o.lying) {
      c.lineWidth = 1.4; c.strokeStyle = "#1a1a1a";
      for (const ex of [-3, 4]) { c.beginPath(); c.moveTo(ex - 2, -20); c.lineTo(ex + 2, -16); c.moveTo(ex + 2, -20); c.lineTo(ex - 2, -16); c.stroke(); }
    } else {
      c.beginPath(); c.arc(face * 3 - 2, -18 + bob, 1.4, 0, Math.PI * 2); c.arc(face * 3 + 3, -18 + bob, 1.4, 0, Math.PI * 2); c.fill();
    }
    c.restore();
  }

  /** Ảnh đại diện nhỏ cho danh sách (canvas 2x). */
  function avatar(cv, colorIdx, dead) {
    const d = window.devicePixelRatio || 1;
    const w = cv.clientWidth || 34, h = cv.clientHeight || 34;
    cv.width = w * d; cv.height = h * d;
    const c = cv.getContext("2d");
    c.scale(d * w / 46, d * h / 46);
    c.fillStyle = "rgba(255,255,255,.04)"; c.beginPath(); c.arc(23, 23, 22, 0, Math.PI * 2); c.fill();
    person(c, 23, 28, colorIdx, { alpha: dead ? 0.45 : 1, ghost: dead });
  }

  function desk(c, x, y, w, h) {
    c.fillStyle = "rgba(0,0,0,.35)"; rr(c, x - w / 2 + 3, y - h / 2 + 5, w, h, 6); c.fill();
    c.fillStyle = "#4a4f5e"; rr(c, x - w / 2, y - h / 2, w, h, 6); c.fill();
    c.fillStyle = "#5a6072"; rr(c, x - w / 2 + 3, y - h / 2 + 3, w - 6, h - 10, 4); c.fill();
  }

  function monitor(c, x, y, screen) {
    c.fillStyle = "#11151d"; rr(c, x - 17, y - 14, 34, 22, 3); c.fill();
    c.fillStyle = screen; rr(c, x - 15, y - 12, 30, 17, 2); c.fill();
    c.fillStyle = "#2b3140"; c.fillRect(x - 3, y + 8, 6, 4);
  }

  function station(c, s, t, hl) {
    if (s.kind === "task") {
      desk(c, s.x, s.y, 96, 46);
      monitor(c, s.x - 18, s.y - 6, "#3d6fb8");
      c.fillStyle = "#e9e4d4"; c.save(); c.translate(s.x + 22, s.y - 2); c.rotate(0.12); c.fillRect(-10, -8, 20, 15); c.restore();
      c.fillStyle = "#cfc8b4"; c.save(); c.translate(s.x + 26, s.y); c.rotate(-0.18); c.fillRect(-10, -8, 20, 15); c.restore();
      c.fillStyle = "#c56a3c"; c.beginPath(); c.arc(s.x + 4, s.y + 8, 4, 0, Math.PI * 2); c.fill();   // cốc cà phê
    } else if (s.kind === "dev") {
      desk(c, s.x, s.y, 84, 44);
      const lines = ["#5fe3a1", "#9be38f", "#e3d35f"];
      monitor(c, s.x - 14, s.y - 6, "#0f2a1f");
      for (let i = 0; i < 4; i++) { c.fillStyle = lines[(i + s.num) % 3]; c.fillRect(s.x - 27, s.y - 15 + i * 4, 8 + ((i * 7 + s.num * 5) % 14), 2); }
      monitor(c, s.x + 18, s.y - 6, "#0f2a1f");
      c.fillStyle = "#ffcc4d"; c.font = "bold 13px Segoe UI"; c.textAlign = "center"; c.fillText("Dev " + s.num, s.x, s.y + 36);
    } else if (s.kind === "test") {
      desk(c, s.x, s.y, 96, 46);
      monitor(c, s.x, s.y - 6, "#2b1f3d");
      c.strokeStyle = "#c9a2ff"; c.lineWidth = 2; c.beginPath(); c.arc(s.x - 2, s.y - 5, 5, 0, Math.PI * 2); c.moveTo(s.x + 2, s.y - 1); c.lineTo(s.x + 7, s.y + 3); c.stroke();
      c.fillStyle = "#c9a2ff"; c.font = "bold 13px Segoe UI"; c.textAlign = "center"; c.fillText("Máy test " + s.num, s.x, s.y + 38);
    } else if (s.kind === "button") {
      c.fillStyle = "rgba(0,0,0,.4)"; c.beginPath(); c.ellipse(s.x + 4, s.y + 8, 122, 82, 0, 0, Math.PI * 2); c.fill();
      c.fillStyle = "#5b4630"; c.beginPath(); c.ellipse(s.x, s.y, 120, 80, 0, 0, Math.PI * 2); c.fill();
      c.fillStyle = "#6c543a"; c.beginPath(); c.ellipse(s.x, s.y - 3, 112, 72, 0, 0, Math.PI * 2); c.fill();
      for (let i = 0; i < 5; i++) { c.fillStyle = "#e9e4d4"; c.save(); c.translate(s.x + Math.cos(i * 1.3) * 70, s.y + Math.sin(i * 1.3) * 42); c.rotate(i); c.fillRect(-8, -6, 16, 12); c.restore(); }
      c.fillStyle = "#3a1416"; c.beginPath(); c.arc(s.x, s.y, 22, 0, Math.PI * 2); c.fill();
      c.fillStyle = "#e5484d"; c.beginPath(); c.arc(s.x, s.y - 3, 17, 0, Math.PI * 2); c.fill();
      c.fillStyle = "#ff8a8f"; c.beginPath(); c.arc(s.x - 5, s.y - 8, 5, 0, Math.PI * 2); c.fill();
      c.fillStyle = "#ffd5d5"; c.font = "bold 12px Segoe UI"; c.textAlign = "center"; c.fillText("HỌP KHẨN", s.x, s.y + 40);
    }
    if (hl) {
      const p = 0.5 + 0.5 * Math.sin(t * 5);
      c.strokeStyle = `rgba(255,204,77,${0.35 + p * 0.5})`; c.lineWidth = 3;
      c.beginPath(); c.ellipse(s.x, s.y, s.kind === "button" ? 130 : 62, s.kind === "button" ? 90 : 40, 0, 0, Math.PI * 2); c.stroke();
      c.fillStyle = "#ffcc4d"; c.font = "bold 22px Segoe UI"; c.textAlign = "center"; c.fillText("!", s.x, s.y - 34 - p * 6);
    }
  }

  function plant(c, x, y) {
    c.fillStyle = "#6b4a2b"; rr(c, x - 9, y, 18, 14, 3); c.fill();
    c.fillStyle = "#2f8f5b";
    for (let i = 0; i < 6; i++) { c.beginPath(); c.ellipse(x + Math.cos(i) * 7, y - 4 + Math.sin(i * 2) * 4, 8, 5, i, 0, Math.PI * 2); c.fill(); }
  }

  function drawMap(c, map, t, hlStations) {
    c.fillStyle = "#0a0d13"; c.fillRect(-200, -200, map.w + 400, map.h + 400);
    // sàn: hành lang + cửa
    c.fillStyle = FLOOR.hall;
    for (const r of map.walk) c.fillRect(r[0], r[1], r[2], r[3]);
    // phòng
    for (const room of map.rooms) {
      const [x, y, w, h] = room.rect;
      c.fillStyle = FLOOR[room.kind] || FLOOR.module;
      c.fillRect(x, y, w, h);
      c.strokeStyle = "rgba(255,255,255,.025)"; c.lineWidth = 1;
      for (let gx = x + 40; gx < x + w; gx += 40) { c.beginPath(); c.moveTo(gx, y); c.lineTo(gx, y + h); c.stroke(); }
      for (let gy = y + 40; gy < y + h; gy += 40) { c.beginPath(); c.moveTo(x, gy); c.lineTo(x + w, gy); c.stroke(); }
    }
    // vách kính
    c.strokeStyle = "rgba(140,190,255,.38)"; c.lineWidth = 5;
    for (const room of map.rooms) { const [x, y, w, h] = room.rect; c.strokeRect(x, y, w, h); }
    c.strokeStyle = "rgba(140,190,255,.2)"; c.lineWidth = 5;
    for (const r of map.halls) c.strokeRect(r[0], r[1], r[2], r[3]);
    // cửa + chỗ nối hành lang: tô lại sàn đè lên vạch kính
    c.fillStyle = FLOOR.hall;
    for (const r of map.doors) c.fillRect(r[0] - 3, r[1] - 3, r[2] + 6, r[3] + 6);
    for (const r of map.halls) if (r[2] < r[3]) c.fillRect(r[0] + 3, r[1] - 3, r[2] - 6, r[3] + 6);
    // tên phòng
    c.textAlign = "left";
    for (const room of map.rooms) {
      const [x, y] = room.rect;
      c.fillStyle = LABEL[room.kind] || "#aaa"; c.font = "bold 20px Segoe UI";
      c.fillText(room.name.toUpperCase(), x + 16, y + 30);
    }
    // cây xanh góc phòng
    for (const room of map.rooms) { const [x, y, w, h] = room.rect; plant(c, x + w - 26, y + 24); if (room.kind !== "module") plant(c, x + 24, y + h - 34); }
    for (const s of map.stations) station(c, s, t, hlStations.has(s.id));
  }

  function nameTag(c, x, y, text, col) {
    c.font = "bold 13px Segoe UI"; c.textAlign = "center";
    c.lineWidth = 3; c.strokeStyle = "rgba(0,0,0,.8)"; c.strokeText(text, x, y);
    c.fillStyle = col || "#fff"; c.fillText(text, x, y);
  }

  function bubble(c, x, y, text, t) {
    c.font = "bold 12px Segoe UI"; c.textAlign = "center";
    const w = c.measureText(text).width + 14;
    c.fillStyle = "rgba(10,14,20,.88)"; rr(c, x - w / 2, y - 16, w, 20, 8); c.fill();
    c.fillStyle = "#ffcc4d"; c.fillText(text + ".".repeat(1 + Math.floor(t * 3) % 3), x, y - 2);
  }

  /** v: {map, cam:{x,y,scale}, t, me:{x,y,alive,color,walk,face,name,act}, others:[{x,y,color,name,ghost,flags,walk,face,mate}], bodies:[{x,y,color}], hl:Set, vision} */
  function render(c, W, H, v) {
    c.save();
    c.setTransform(1, 0, 0, 1, 0, 0);
    c.clearRect(0, 0, W, H);
    c.translate(W / 2, H / 2); c.scale(v.cam.scale, v.cam.scale); c.translate(-v.cam.x, -v.cam.y);
    drawMap(c, v.map, v.t, v.hl);
    for (const b of v.bodies) {
      c.fillStyle = "rgba(170,20,30,.35)"; c.beginPath(); c.ellipse(b.x, b.y + 6, 30, 14, 0.2, 0, Math.PI * 2); c.fill();
      for (let i = 0; i < 4; i++) { c.fillStyle = "#e9e4d4"; c.save(); c.translate(b.x + Math.cos(i * 2.1 + b.x) * 30, b.y + Math.sin(i * 1.7) * 18); c.rotate(i * 0.8); c.fillRect(-7, -5, 14, 10); c.restore(); }
      person(c, b.x, b.y, b.color, { lying: true });
    }
    const list = v.others.slice();
    list.push({ ...v.me, isMe: true, ghost: !v.me.alive });
    list.sort((a, b) => a.y - b.y);
    for (const p of list) {
      const ghostFloat = p.ghost ? Math.sin(v.t * 2 + p.x * 0.01) * 3 : 0;
      person(c, p.x, p.y + ghostFloat, p.color, { walk: p.walk, face: p.face, ghost: p.ghost, alpha: p.ghost ? 0.5 : 1 });
      nameTag(c, p.x, p.y - 36 + ghostFloat, p.name, p.mate ? "#ff7b86" : p.isMe ? "#ffe39a" : "#fff");
      const fl = p.isMe ? (p.act === "dev" ? 2 : p.act === "test" ? 4 : 0) : p.flags;
      if (fl & 2) bubble(c, p.x, p.y - 52, "⌨ đang gõ", v.t);
      if (fl & 4) bubble(c, p.x, p.y - 52, "🔍 đang test", v.t);
    }
    c.restore();
    // sương tầm nhìn (người còn sống)
    if (v.me.alive) {
      const sx = W / 2 + (v.me.x - v.cam.x) * v.cam.scale, sy = H / 2 + (v.me.y - v.cam.y) * v.cam.scale;
      const R = v.vision * v.cam.scale;
      const g = c.createRadialGradient(sx, sy, R * 0.72, sx, sy, R);
      g.addColorStop(0, "rgba(3,5,9,0)"); g.addColorStop(1, "rgba(3,5,9,.86)");
      c.fillStyle = g; c.fillRect(0, 0, W, H);
    }
  }

  function mini(c, W, H, v) {
    const s = Math.min(W / v.map.w, H / v.map.h) * 0.94;
    const ox = (W - v.map.w * s) / 2, oy = (H - v.map.h * s) / 2;
    c.setTransform(1, 0, 0, 1, 0, 0); c.clearRect(0, 0, W, H);
    c.translate(ox, oy); c.scale(s, s);
    c.fillStyle = "#2a3446"; for (const r of v.map.walk) c.fillRect(r[0], r[1], r[2], r[3]);
    for (const room of v.map.rooms) {
      const [x, y, w, h] = room.rect;
      c.fillStyle = v.hlRooms.has(room.id) ? "rgba(255,204,77,.55)" : "#34405a";
      c.fillRect(x, y, w, h);
      c.fillStyle = "#cfd8ea"; c.font = "bold 70px Segoe UI"; c.textAlign = "center";
      const label = room.kind === "module" ? room.name.split(/[.\s]/)[0] || room.name : room.kind === "dev" ? "DEV" : room.kind === "test" ? "TEST" : "HỌP";
      c.fillText(label, x + w / 2, y + h / 2 + 24);
    }
    c.fillStyle = COLORS[v.me.color % 8]; c.strokeStyle = "#fff"; c.lineWidth = 14;
    c.beginPath(); c.arc(v.me.x, v.me.y, 42, 0, Math.PI * 2); c.fill(); c.stroke();
  }

  window.R = { COLORS, COLOR_NAMES, person, avatar, render, mini };
})();
