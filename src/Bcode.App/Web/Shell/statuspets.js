// Thú cưng trên thanh trạng thái (ong, bướm, chim, bọ rùa, mèo, ốc sên) — trang trí, không chặn chuột.
// Dùng: var pets = StatusPets.create(hostElement); pets.set({ enabled, counts: { bee: 1, ... }, speed, mouse, flowers, trail }).
// Bản native tương đương: Controls/StatusPets.cs — sửa hành vi / hình vẽ ở đây thì sửa cả bên đó (giữ cùng tên hàm, cùng con số).
// Toạ độ: px thiết kế, mỗi con có hộp w×h, (x, y) = góc trên trái; hình vẽ quay mặt sang phải, quay trái thì lật ngang.
(function () {
  var ORDER = ['snail', 'ladybug', 'cat', 'bird', 'butterfly', 'bee'];   // thứ tự vẽ: con bò dưới đất trước, con bay sau
  var SIZE = { bee: [18, 16], butterfly: [18, 16], bird: [20, 14], ladybug: [14, 10], cat: [24, 18], snail: [18, 12] };
  var BFLY = [['#ff9a2e', '#ffcf6b'], ['#5aa9ff', '#b9dcff'], ['#ff6fae', '#ffc2dc'], ['#b07cff', '#dcc6ff']];
  var BIRD = [['#5b8def', '#3d6fd0'], ['#e0703a', '#b5532a'], ['#7a8a99', '#58646f']];
  var CAT = [['#f0a04b', '#c9792c', '#222222'], ['#9aa0a6', '#6f757b', '#222222'], ['#3a3a3a', '#1f1f1f', '#c8f060'], ['#f3efe6', '#cfc7b6', '#4a7fd0']];

  function rnd(a, b) { return a + Math.random() * (b - a); }
  function pick(a) { return a[Math.floor(Math.random() * a.length)]; }
  function clamp(v, a, b) { return v < a ? a : v > b ? b : v; }

  function create(host) {
    var cv = document.createElement('canvas');
    cv.style.cssText = 'position:absolute;left:0;top:0;width:100%;height:100%;pointer-events:none;z-index:5;display:none';
    host.appendChild(cv);
    var g = cv.getContext('2d');
    var S = { enabled: false, counts: {}, speed: 100, mouse: true, flowers: true, trail: true };
    var pets = [], parts = [], flower = null, flowerCd = 6;
    var W = 0, H = 0, t = 0, last = 0, raf = 0, mouse = null, mouseAt = -10;

    host.addEventListener('mousemove', function (e) { var r = host.getBoundingClientRect(); mouse = { x: e.clientX - r.left, y: e.clientY - r.top }; mouseAt = t; });
    host.addEventListener('mouseleave', function () { mouse = null; });

    // ---------- tiện ích ----------
    function ground(p) { return H - p.h; }
    function pickTarget(p) { p.tx = rnd(2, Math.max(4, W - p.w - 2)); p.ty = rnd(0, Math.max(0, H - p.h - (p.kind === 'bird' ? 4 : 0))); }
    function near(p, d) { return Math.hypot(p.tx - p.x, p.ty - p.y) < d; }
    function mouseActive() { return S.mouse && mouse && t - mouseAt < 2.5; }
    function distMouse(p) { return mouse ? Math.hypot(mouse.x - (p.x + p.w / 2), mouse.y - (p.y + p.h / 2)) : 1e9; }
    function trail(p, dt, color, every, life, dy, vy, r) {
      if (!S.trail) return;
      p.trailCd -= dt;
      if (p.trailCd > 0) return;
      p.trailCd = every;
      parts.push({ k: 'dot', x: p.x + (p.face > 0 ? 2 : p.w - 2), y: p.y + dy + rnd(-1, 1), vx: 0, vy: vy, life: life, max: life, c: color, r: r });
    }
    function heart(p, dt) {
      p.heartCd -= dt;
      if (p.heartCd <= 0 && distMouse(p) < 24) { p.heartCd = 1.2; parts.push({ k: 'heart', x: p.x + p.w / 2, y: p.y, vx: rnd(-6, 6), vy: -14, life: 1.4, max: 1.4 }); }
    }

    function makePet(kind) {
      var sz = SIZE[kind];
      var p = { kind: kind, w: sz[0], h: sz[1], x: rnd(0, Math.max(1, W - sz[0])), y: 0, vx: 0, vy: 0, face: Math.random() < 0.5 ? 1 : -1,
        mode: 'fly', modeT: 0, ph: rnd(0, 10), tx: 0, ty: 0, cd: 0, trailCd: 0, heartCd: 0, tilt: 0, jump: 0, peck: 0, moving: false,
        loopA: 0, loopCx: 0, loopCy: 0, landX: 0, c1: '', c2: '', eye: '' };
      var c = kind === 'butterfly' ? pick(BFLY) : kind === 'bird' ? pick(BIRD) : kind === 'cat' ? pick(CAT) : null;
      if (c) { p.c1 = c[0]; p.c2 = c[1]; p.eye = c[2] || ''; }
      if (kind === 'bee' || kind === 'butterfly' || kind === 'bird') { p.y = rnd(0, Math.max(0, H - p.h)); pickTarget(p); }
      else { p.mode = 'walk'; p.y = ground(p); p.modeT = rnd(2, 6); }
      return p;
    }

    // Bay mềm tới (tx, ty): lò xo + giảm chấn + rung lắc; trả về tốc độ.
    function flyTo(p, dt, max, wobA, wobF) {
      var wob = Math.sin((t + p.ph) * wobF) * wobA;
      var ax = (p.tx - p.x) * 5 - p.vx * 2.6 + rnd(-30, 30);
      var ay = (p.ty + wob - p.y) * 7 - p.vy * 3 + rnd(-25, 25);
      p.vx += ax * dt; p.vy += ay * dt;
      var sp = Math.hypot(p.vx, p.vy);
      if (sp > max) { p.vx *= max / sp; p.vy *= max / sp; sp = max; }
      p.x = clamp(p.x + p.vx * dt, 0, Math.max(0, W - p.w));
      p.y = clamp(p.y + p.vy * dt, -2, Math.max(-2, H - p.h));
      if (Math.abs(p.vx) > 10) p.face = p.vx > 0 ? 1 : -1;
      p.tilt = clamp(Math.atan2(p.vy, Math.abs(p.vx) + 20), -0.45, 0.45);
      return sp;
    }

    // Đi bộ dưới đáy; hết thời gian chế độ hiện tại thì gọi next() để chọn việc tiếp.
    function walkStep(p, dt, speed, next) {
      p.y = ground(p); p.tilt = 0; p.modeT -= dt;
      p.moving = p.mode === 'walk';
      if (p.moving) {
        p.x += p.face * speed * dt;
        if (p.x <= 0) { p.x = 0; p.face = 1; }
        if (p.x >= W - p.w) { p.x = Math.max(0, W - p.w); p.face = -1; }
      }
      if (p.modeT <= 0) next();
    }

    function releaseFlower(p, done) { if (flower && flower.by === p) { if (done) flower.leaving = true; else flower.by = null; } }

    // ---------- hành vi từng loài ----------
    // Ong / bướm: bay lượn, đậu nghỉ, hút mật hoa, bay theo chuột thả tim; ong còn nhào lộn.
    function stepNectar(p, dt) {
      var bf = p.kind === 'butterfly', max = bf ? 55 : 85;
      var chase = mouseActive();
      if (chase && p.mode !== 'loop') {
        if (p.mode === 'rest') { p.mode = 'fly'; releaseFlower(p, false); }
        p.tx = mouse.x + (mouse.x > p.x + p.w / 2 ? -20 : 4) + Math.sin((t + p.ph) * 3) * 8;
        p.ty = clamp(mouse.y - 8 + Math.cos((t + p.ph) * 3) * 3, 0, Math.max(0, H - p.h));
        heart(p, dt);
        max *= 1.8;
      }
      if (p.mode === 'rest') {
        p.modeT -= dt; p.vx = p.vy = 0; p.tilt = 0;
        if (flower && flower.by === p) flower.life -= dt * 0.25;   // đang hút mật: hoa tàn dần
        if (p.modeT <= 0) { p.mode = 'fly'; pickTarget(p); p.vy = -40; releaseFlower(p, true); }
        return;
      }
      if (p.mode === 'loop') {
        p.loopA += dt * 7;
        p.x = p.loopCx + Math.sin(p.loopA) * 7 * p.face; p.y = p.loopCy - (1 - Math.cos(p.loopA)) * 5; p.tilt = -p.loopA;
        if (p.loopA >= Math.PI * 2) { p.mode = 'fly'; p.vx = p.face * 50; p.vy = 0; p.tilt = 0; pickTarget(p); }
        return;
      }
      var sp = flyTo(p, dt, max, bf ? 4 : 2.5, bf ? 3 : 5.3);
      if (sp > 25) trail(p, dt, bf ? p.c2 : '#f5b400', bf ? 0.09 : 0.045, 0.9, p.h * 0.7, 4, 1.1);
      if (chase) return;
      var mine = flower && !flower.leaving && (flower.by === p || !flower.by);
      if (mine) { flower.by = p; p.tx = flower.x - p.w / 2; p.ty = Math.max(0, H - p.h - 4); }
      if (near(p, 5)) {
        if (mine) { p.mode = 'rest'; p.modeT = rnd(2.5, 4); p.x = p.tx; p.y = p.ty; return; }
        var r = Math.random();
        if (r < 0.2) { p.mode = 'rest'; p.modeT = rnd(1, 2.5); p.y = Math.max(0, H - p.h); }
        else if (!bf && r < 0.38 && W > 60) { p.mode = 'loop'; p.loopA = 0; p.loopCx = p.x; p.loopCy = p.y + 5; }
        else pickTarget(p);
      }
    }

    // Chim: bay vụt, đáp xuống nhảy lò cò + mổ, chuột lại gần (hoặc mèo rình) thì bay vụt đi.
    function flee(p, awayX) {
      p.mode = 'flee'; p.modeT = 1.6; p.vy = -90;
      p.tx = awayX > p.x + p.w / 2 ? rnd(0, Math.max(0, p.x - 60)) : rnd(Math.min(Math.max(0, W - p.w), p.x + 60), Math.max(0, W - p.w));
      p.ty = rnd(0, 3);
    }
    function stepBird(p, dt) {
      if (S.mouse && mouse && p.mode !== 'flee' && distMouse(p) < 45) flee(p, mouse.x);
      if (p.mode === 'hop') {
        p.modeT -= dt; p.cd -= dt; p.tilt = 0;
        if (p.jump > 0) {
          p.jump -= dt;
          p.x = clamp(p.x + p.face * 16 * dt, 0, Math.max(0, W - p.w));
          p.y = ground(p) - Math.sin(clamp(1 - p.jump / 0.25, 0, 1) * Math.PI) * 3;
        } else {
          p.y = ground(p);
          if (p.peck > 0) { p.peck -= dt; p.tilt = 0.5; }
        }
        if (p.cd <= 0 && p.jump <= 0) {
          p.cd = rnd(0.3, 0.9);
          var r = Math.random();
          if (r < 0.45) p.jump = 0.25; else if (r < 0.75) p.peck = 0.25; else p.face = -p.face;
          if (p.x < 2) p.face = 1;
          if (p.x > W - p.w - 2) p.face = -1;
        }
        if (p.modeT <= 0) { p.mode = 'fly'; p.vy = -70; pickTarget(p); }
        return;
      }
      if (p.mode === 'flee') { p.modeT -= dt; if (p.modeT <= 0) { p.mode = 'fly'; pickTarget(p); } }
      if (p.mode === 'land') { p.tx = p.landX; p.ty = ground(p); }
      flyTo(p, dt, p.mode === 'flee' ? 220 : 140, p.mode === 'land' ? 0 : 2, 4);
      if (p.mode === 'land' && Math.abs(p.y - ground(p)) < 1.5 && Math.abs(p.tx - p.x) < 6) {
        p.mode = 'hop'; p.modeT = rnd(3, 8); p.vx = p.vy = 0; p.cd = 0.3; p.jump = 0; p.peck = 0;
        return;
      }
      if (p.mode === 'fly' && near(p, 8)) {
        if (Math.random() < 0.35) { p.mode = 'land'; p.landX = clamp(p.x + p.face * rnd(10, 40), 0, Math.max(0, W - p.w)); }
        else pickTarget(p);
      }
    }

    // Bọ rùa: bò, dừng, thỉnh thoảng xoè cánh bay một quãng rồi đáp.
    function stepLadybug(p, dt) {
      if (p.mode === 'air') {
        p.modeT -= dt;
        if (p.modeT <= 0) p.ty = ground(p);
        flyTo(p, dt, 40, 1.5, 6);
        if (p.modeT <= 0 && Math.abs(p.y - ground(p)) < 1) { p.mode = 'walk'; p.modeT = rnd(3, 8); p.vx = p.vy = 0; p.tilt = 0; }
        else if (p.modeT > 0 && near(p, 4)) pickTarget(p);
        return;
      }
      walkStep(p, dt, 10, function () {
        var r = Math.random();
        if (p.mode === 'walk' && r < 0.3) { p.mode = 'idle'; p.modeT = rnd(0.8, 2.5); }
        else if (p.mode === 'walk' && r < 0.5) { p.mode = 'air'; p.modeT = rnd(1.5, 3); p.vy = -30; p.moving = false; pickTarget(p); }
        else { p.mode = 'walk'; p.modeT = rnd(3, 8); if (Math.random() < 0.4) p.face = -p.face; }
      });
    }

    // Mèo: đi dạo, ngồi vẫy đuôi, ngủ (zZ), đuổi + vồ chuột, rình chim đang nhảy dưới đất.
    function pounce(p) { p.mode = 'pounce'; p.modeT = 0.45; p.vx = p.face * 30; p.moving = false; }
    function stepCat(p, dt) {
      p.tilt = 0;
      if (mouseActive() && (p.mode === 'walk' || p.mode === 'stalk' || p.mode === 'sleep')) {
        if (p.mode === 'sleep') { p.mode = 'wake'; p.modeT = 0.8; } else p.mode = 'chase';
      }
      if (p.mode === 'wake') {
        p.y = ground(p); p.moving = false; p.modeT -= dt;
        if (p.modeT <= 0) { p.mode = mouseActive() ? 'chase' : 'sit'; p.modeT = 1; }
        return;
      }
      if (p.mode === 'chase') {
        p.y = ground(p);
        if (!mouseActive()) { p.mode = 'sit'; p.modeT = rnd(1, 2); p.moving = false; return; }
        var dx = mouse.x - p.w / 2 - p.x;
        p.face = dx > 0 ? 1 : -1;
        if (Math.abs(dx) < 10) pounce(p);
        else { p.x = clamp(p.x + p.face * Math.min(90, Math.abs(dx) * 3) * dt, 0, Math.max(0, W - p.w)); p.moving = true; }
        return;
      }
      if (p.mode === 'pounce') {
        p.modeT -= dt;
        var k = clamp(1 - p.modeT / 0.45, 0, 1);
        p.y = ground(p) - Math.sin(k * Math.PI) * 6;
        p.x = clamp(p.x + p.vx * dt, 0, Math.max(0, W - p.w));
        p.tilt = -0.3 * Math.cos(k * Math.PI);
        if (p.modeT <= 0) {
          p.mode = 'sit'; p.modeT = rnd(1.2, 2.5); p.y = ground(p); p.tilt = 0;
          parts.push({ k: 'heart', x: p.x + p.w / 2 + p.face * 8, y: p.y + 2, vx: 0, vy: -12, life: 1.2, max: 1.2 });
        }
        return;
      }
      if (p.mode === 'walk' || p.mode === 'stalk') {
        var b = null;
        for (var i = 0; i < pets.length; i++) if (pets[i].kind === 'bird' && pets[i].mode === 'hop') { b = pets[i]; break; }
        if (b) {
          p.mode = 'stalk'; p.y = ground(p);
          var d = b.x - p.x;
          p.face = d > 0 ? 1 : -1;
          p.x = clamp(p.x + p.face * 28 * dt, 0, Math.max(0, W - p.w)); p.moving = true;
          if (Math.abs(d) < 22) { flee(b, p.x + p.w / 2); pounce(p); }
          return;
        }
        if (p.mode === 'stalk') { p.mode = 'sit'; p.modeT = 1.5; }
      }
      if (p.mode === 'sleep') {
        p.y = ground(p); p.moving = false; p.modeT -= dt; p.cd -= dt;
        if (p.cd <= 0) { p.cd = 1.1; parts.push({ k: 'z', x: p.x + (p.face > 0 ? p.w - 4 : 4), y: p.y + 6, vx: p.face * 3, vy: -6, life: 2, max: 2 }); }
        if (p.modeT <= 0) { p.mode = 'sit'; p.modeT = 1; }
        return;
      }
      walkStep(p, dt, 22, function () {
        var r = Math.random();
        if (p.mode === 'walk') {
          if (r < 0.45) { p.mode = 'sit'; p.modeT = rnd(2, 5); }
          else if (r < 0.65) { p.mode = 'sleep'; p.modeT = rnd(8, 18); p.cd = 0.5; }
          else { p.modeT = rnd(3, 7); p.face = -p.face; }
        } else { p.mode = 'walk'; p.modeT = rnd(3, 8); if (Math.random() < 0.5) p.face = -p.face; }
      });
    }

    // Ốc sên: bò rất chậm để lại vệt nhớt; chuột lại gần thì rụt vào vỏ.
    function stepSnail(p, dt) {
      p.y = ground(p); p.tilt = 0;
      if (S.mouse && mouse && distMouse(p) < 30) { p.mode = 'hide'; p.modeT = 2.5; }
      if (p.mode === 'hide') { p.moving = false; p.modeT -= dt; if (p.modeT <= 0) { p.mode = 'walk'; p.modeT = rnd(5, 12); } return; }
      walkStep(p, dt, 4, function () {
        if (p.mode === 'walk' && Math.random() < 0.35) { p.mode = 'idle'; p.modeT = rnd(1.5, 4); }
        else { p.mode = 'walk'; p.modeT = rnd(5, 12); if (Math.random() < 0.3) p.face = -p.face; }
      });
      if (p.moving) trail(p, dt, '#bec8d2', 0.35, 6, p.h - 0.8, 0, 0.9);
    }

    var STEP = { bee: stepNectar, butterfly: stepNectar, bird: stepBird, ladybug: stepLadybug, cat: stepCat, snail: stepSnail };

    function world(dt) {
      var lovers = pets.some(function (p) { return p.kind === 'bee' || p.kind === 'butterfly'; });
      flowerCd -= dt;
      if (S.flowers && lovers && !flower && flowerCd <= 0 && W > 120) flower = { x: rnd(30, W - 30), grow: 0, life: 1, leaving: false, by: null };
      if (flower) {
        flower.grow = Math.min(1, flower.grow + dt * 1.5);
        if (flower.leaving) flower.life -= dt * 0.8;
        if (flower.by && pets.indexOf(flower.by) < 0) flower.by = null;
        if (flower.life <= 0 || !S.flowers || !lovers) { flower = null; flowerCd = rnd(15, 35); }
      }
      for (var i = parts.length - 1; i >= 0; i--) {
        var q = parts[i];
        q.life -= dt;
        if (q.life <= 0) { parts.splice(i, 1); continue; }
        q.x += q.vx * dt; q.y += q.vy * dt;
      }
    }

    // ---------- vẽ (toạ độ cục bộ, quay mặt sang phải) ----------
    function E(cx, cy, rx, ry, fill, stroke, sw) {
      if (rx <= 0 || ry <= 0) return;
      g.beginPath(); g.ellipse(cx, cy, rx, ry, 0, 0, Math.PI * 2);
      if (fill) { g.fillStyle = fill; g.fill(); }
      if (stroke) { g.strokeStyle = stroke; g.lineWidth = sw; g.stroke(); }
    }
    function L(x1, y1, x2, y2, c, w) { g.beginPath(); g.moveTo(x1, y1); g.lineTo(x2, y2); g.strokeStyle = c; g.lineWidth = w; g.lineCap = 'butt'; g.stroke(); }
    function Q(x1, y1, cx, cy, x2, y2, c, w) { g.beginPath(); g.moveTo(x1, y1); g.quadraticCurveTo(cx, cy, x2, y2); g.strokeStyle = c; g.lineWidth = w; g.lineCap = 'round'; g.stroke(); }
    function P(pts, fill) { g.beginPath(); g.moveTo(pts[0], pts[1]); for (var i = 2; i < pts.length; i += 2) g.lineTo(pts[i], pts[i + 1]); g.closePath(); g.fillStyle = fill; g.fill(); }

    function drawBee(p) {
      var f = p.mode === 'rest' ? 0.6 + 0.4 * Math.abs(Math.sin(t * 6)) : 0.35 + 0.65 * Math.abs(Math.sin((t + p.ph) * 35));
      var h1 = 7.2 * f, h2 = 6 * f;
      E(7, 7.5 - h1 / 2, 3.2, h1 / 2, '#dff3ffd9', '#8fb8d0', 0.6);
      E(10.5, 7.5 - h2 / 2, 2.6, h2 / 2, '#dff3ffcc', '#8fb8d0', 0.6);
      E(9, 10, 6.2, 4.2, '#ffc928', '#3a2a00', 0.7);
      L(7, 6.3, 7, 13.7, '#2b2100', 1.6); L(10, 6, 10, 14, '#2b2100', 1.6);
      L(3, 10, 0.8, 10.2, '#2b2100', 1);
      E(15, 9.5, 2.4, 2.4, '#2b2100'); E(15.8, 9, 0.6, 0.6, '#ffffff');
      Q(15.5, 7.4, 16.1, 5.4, 17.3, 5.1, '#2b2100', 0.6); Q(14.6, 7.3, 14.6, 5.3, 15.5, 4.7, '#2b2100', 0.6);
    }

    function drawButterfly(p) {
      var f = p.mode === 'rest' ? 0.3 + 0.7 * Math.abs(Math.sin(t * 2)) : 0.2 + 0.8 * Math.abs(Math.sin((t + p.ph) * 9));
      E(6.5, 9.5 - 2.6 * f, 3.2, 3.4 * f, p.c2, '#3a2a2a', 0.5);
      E(9, 10, 5.5, 1.4, '#3b2a20');
      E(14.4, 9.6, 1.5, 1.5, '#3b2a20');
      Q(14.8, 8.6, 15.5, 6, 17, 5.2, '#3b2a20', 0.5); Q(14.2, 8.6, 14.4, 6, 15.4, 4.8, '#3b2a20', 0.5);
      E(9, 9.5 - 4.6 * f, 4.4, 4.8 * f, p.c1, '#3a2a2a', 0.5);
      E(9.6, 9.5 - 6 * f, 1.1, 1.1 * f, '#ffffffcc');
      E(7.8, 9.5 - 3.8 * f, 0.8, 0.8 * f, '#ffffffaa');
    }

    function drawBird(p) {
      var flying = p.mode !== 'hop';
      P([4, 7.5, -0.5, 5.2, 0.2, 9.8], p.c2);
      if (!flying) { L(9, 11.5, 8.6, 14, '#a0522d', 0.8); L(11, 11.5, 11.4, 14, '#a0522d', 0.8); }
      E(9, 8.5, 6.5, 4.2, p.c1);
      E(10.3, 10, 4, 2.3, '#f3e6c8');
      E(15, 6.5, 3.2, 3.2, p.c1);
      E(16, 6, 0.8, 0.8, '#111111'); E(16.25, 5.75, 0.25, 0.25, '#ffffff');
      P([17.8, 6.1, 20, 7, 17.8, 7.8], '#f5a623');
      if (flying) {
        var s = Math.sin((t + p.ph) * 14);
        if (Math.sin((t + p.ph) * 1.7) > 0.6) s = 0.25;   // lướt
        P([6, 8, 12, 8, 8.5, 8 - 7 * s], p.c2);
      } else E(8.3, 8, 4.2, 2.1, p.c2);
    }

    function drawLadybug(p) {
      var air = p.mode === 'air';
      if (!air) for (var i = 0; i < 3; i++) {
        var x = 4 + i * 2.8, sw = p.moving ? Math.sin((t + p.ph) * 16 + i * 2) * 1.1 : 0;
        L(x, 8.5, x + sw, 10, '#1a1a1a', 0.7);
      }
      E(12, 7, 2, 2, '#1a1a1a'); E(12.9, 6.5, 0.45, 0.45, '#ffffff');
      if (air) { var f = 0.3 + 0.7 * Math.abs(Math.sin((t + p.ph) * 30)); E(5, 4 - 2 * f, 4.5, 2.2 * f + 0.3, '#ffffffb0', '#9ab0c0', 0.4); }
      E(6.5, 6.3, 5.5, 3.6, '#e0262b', '#3a0a0a', 0.5);
      E(4.3, 5.2, 1, 1, '#1a1a1a'); E(7.6, 4.6, 1, 1, '#1a1a1a'); E(8.6, 7.4, 0.9, 0.9, '#1a1a1a'); E(3.8, 7.9, 0.8, 0.8, '#1a1a1a');
      E(5.2, 4.2, 1.4, 0.7, '#ffffff55');
    }

    function drawCat(p) {
      var c1 = p.c1, c2 = p.c2;
      if (p.mode === 'sleep') {
        Q(4, 14.5, 1, 17.5, 9, 17.3, c1, 2);
        E(11, 14.2, 8, 3.6, c1);
        L(9, 11, 9.4, 12.6, c2, 1); L(12, 10.8, 12.3, 12.4, c2, 1);
        E(18.3, 13.3, 3.5, 3.2, c1);
        P([16, 11.4, 16.3, 8.4, 18, 10.3], c1); P([19, 10.2, 20.8, 8.2, 21.2, 11.4], c1);
        L(18.8, 13.3, 20.2, 13.5, '#222222', 0.6);
        E(21.6, 14.4, 0.45, 0.45, '#e88888');
        return;
      }
      var sit = p.mode === 'sit' || p.mode === 'wake';
      var by = sit ? 1.5 : 0, sway = sit ? 3 : 2;
      Q(4, 10 + by, 0.5, 9 + Math.sin(t * 3 + p.ph) * sway, 1.5, 3 + Math.sin(t * 2 + p.ph) * sway, c1, 1.8);
      if (!sit) {
        var ph = (t + p.ph) * (p.mode === 'chase' ? 18 : 11), lx = [6, 8.5, 14, 16.5];
        for (var i = 0; i < 4; i++) {
          var o = p.moving ? Math.sin(ph + (i % 2 ? Math.PI : 0) + (i > 1 ? Math.PI / 2 : 0)) * 1.6 : 0;
          L(lx[i], 13, lx[i] + o, 17.6, c1, 1.6);
        }
      } else { L(14, 13, 14, 17.6, c1, 1.6); L(16.3, 13, 16.3, 17.6, c1, 1.6); E(7, 15, 3.5, 2.5, c1); }
      E(11, 11 + by, 7.5, 4.2, c1);
      L(9, 7.2 + by, 9.5, 9.5 + by, c2, 1); L(12, 7 + by, 12.3, 9.3 + by, c2, 1);
      E(19, 8, 3.8, 3.8, c1);
      P([16.3, 5.6, 17, 1.8, 18.6, 4.6], c1); P([19.6, 4.4, 21.4, 2, 21.8, 5.8], c1);
      P([16.9, 5, 17.2, 3, 18.1, 4.6], '#f2a0a0');
      if (Math.sin(t * 0.9 + p.ph) > 0.97) L(20, 7.4, 21.2, 7.4, p.eye, 0.6);   // chớp mắt
      else E(20.6, 7.4, 0.75, 0.85, p.eye);
      E(22.6, 8.6, 0.5, 0.45, '#e88888');
      L(22, 9.2, 24, 8.8, '#00000055', 0.3); L(22, 9.6, 24, 9.9, '#00000055', 0.3);
    }

    function drawSnail(p) {
      var hide = p.mode === 'hide';
      if (!hide) {
        var wv = Math.sin((t + p.ph) * 2) * 0.5, st = p.moving ? Math.sin((t + p.ph) * 3) * 0.4 : 0;
        E(9.5, 10.4, 8 + st, 1.6, '#c9b28a');
        E(15.4, 8.4, 1.7, 2.8, '#c9b28a');
        L(15.9, 6, 17, 2.6 + wv, '#a8936c', 0.7); L(15, 6, 15.5, 2.9 - wv, '#a8936c', 0.7);
        E(17, 2.6 + wv, 0.75, 0.75, '#333333'); E(15.5, 2.9 - wv, 0.7, 0.7, '#333333');
      } else E(8, 10.6, 5, 1.3, '#c9b28a');
      var sy = hide ? 1 : 0;
      E(8, 6 + sy, 4.8, 4.8, '#b5651d', '#6b3b12', 0.8);
      E(8.6, 6.4 + sy, 2.6, 2.6, null, '#6b3b12', 0.7);
      E(9, 6.7 + sy, 1.1, 1.1, null, '#6b3b12', 0.6);
      E(6.5, 4 + sy, 1.2, 0.6, '#ffffff44');
    }

    var DRAW = { bee: drawBee, butterfly: drawButterfly, bird: drawBird, ladybug: drawLadybug, cat: drawCat, snail: drawSnail };

    function draw() {
      var dpr = window.devicePixelRatio || 1;
      if (cv.width !== Math.round(W * dpr) || cv.height !== Math.round(H * dpr)) { cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr); }
      g.setTransform(dpr, 0, 0, dpr, 0, 0);
      g.clearRect(0, 0, W, H);
      if (flower) {
        var s = flower.grow * Math.max(0, flower.life), cx = flower.x, cy = H - 3 - 6 * s;
        g.globalAlpha = clamp(flower.life * 1.5, 0, 1);
        L(cx, H, cx, cy, '#4c9a3a', 1);
        for (var k = 0; k < 5; k++) { var a = k * Math.PI * 2 / 5 + t * 0.3; E(cx + Math.cos(a) * 2.6 * s, cy + Math.sin(a) * 2.6 * s, 1.9 * s, 1.9 * s, '#ff8fb8'); }
        E(cx, cy, 1.6 * s, 1.6 * s, '#ffd23f');
        g.globalAlpha = 1;
      }
      parts.forEach(function (q) {
        var a = q.life / q.max;
        if (q.k === 'dot') { g.globalAlpha = Math.min(1, a) * 0.55; E(q.x, q.y, q.r, q.r, q.c); }
        else {
          g.globalAlpha = Math.min(1, a * 2);
          g.fillStyle = q.k === 'heart' ? '#ff5a8a' : '#8aa0b8';
          g.font = q.k === 'heart' ? '10px sans-serif' : 'bold 8px sans-serif';
          g.textAlign = 'center';
          g.fillText(q.k === 'heart' ? '♥' : 'z', q.x, q.y);
        }
      });
      g.globalAlpha = 1;
      pets.forEach(function (p) {
        var bob = p.mode === 'rest' ? Math.sin(t * 9) * 0.3 : 0;
        g.save();
        g.translate(p.x + p.w / 2, p.y + p.h / 2 + bob);
        g.rotate(p.tilt * p.face);
        g.scale(p.face, 1);
        g.translate(-p.w / 2, -p.h / 2);
        DRAW[p.kind](p);
        g.restore();
      });
    }

    // Thêm / bớt cho đúng số con mỗi loại (giữ các con đang có).
    function sync() {
      ORDER.forEach(function (kind) {
        var want = clamp(Math.floor(+(S.counts || {})[kind] || 0), 0, 5);
        var have = pets.filter(function (p) { return p.kind === kind; });
        for (var i = have.length; i < want; i++) pets.push(makePet(kind));
        for (var j = want; j < have.length; j++) pets.splice(pets.indexOf(have[j]), 1);
      });
      pets.sort(function (a, b) { return ORDER.indexOf(a.kind) - ORDER.indexOf(b.kind); });
      if (flower && flower.by && pets.indexOf(flower.by) < 0) flower.by = null;
    }

    function frame(now) {
      if (!S.enabled) { raf = 0; return; }
      var dt = last ? Math.min((now - last) / 1000, 0.05) : 0;
      last = now; t += dt;
      W = host.clientWidth; H = host.clientHeight;
      var d = dt * clamp(+S.speed || 100, 25, 300) / 100;
      pets.forEach(function (p) { STEP[p.kind](p, d); });
      world(d);
      draw();
      raf = requestAnimationFrame(frame);
    }

    return {
      set: function (s) {
        S = Object.assign({ enabled: false, counts: {}, speed: 100, mouse: true, flowers: true, trail: true }, s || {});
        cv.style.display = S.enabled ? 'block' : 'none';
        if (!S.enabled) { pets = []; parts = []; flower = null; return; }
        if (!S.trail) parts = parts.filter(function (q) { return q.k !== 'dot'; });
        W = host.clientWidth; H = host.clientHeight;
        sync();
        if (!raf) { last = 0; raf = requestAnimationFrame(frame); }
      },
    };
  }

  window.StatusPets = { create: create };
})();
