// Thú cưng trên thanh trạng thái (ong, bướm, chim, bọ rùa, mèo, ốc sên) — trang trí, không chặn chuột.
// Dùng: var pets = StatusPets.create(hostElement); pets.set({ enabled, counts: { bee: 1, ... }, speed, mouse, flowers, trail }).
// Bản native tương đương: Controls/StatusPets.cs — sửa hành vi / hình vẽ ở đây thì sửa cả bên đó (giữ cùng tên hàm, cùng con số).
// Toạ độ: px thiết kế, mỗi con có hộp w×h, (x, y) = góc trên trái; hình vẽ quay mặt sang phải, quay trái thì lật ngang.
(function () {
  var ORDER = ['snail', 'ladybug', 'cat', 'fish', 'bird', 'butterfly', 'bee', 'firefly'];   // thứ tự vẽ: con bò dưới đất trước, con bay sau
  var SIZE = { bee: [18, 16], butterfly: [18, 16], bird: [20, 14], ladybug: [14, 10], cat: [24, 18], snail: [18, 12], fish: [18, 10], firefly: [10, 8] };
  var BFLY = [['#ff9a2e', '#ffcf6b'], ['#5aa9ff', '#b9dcff'], ['#ff6fae', '#ffc2dc'], ['#b07cff', '#dcc6ff']];
  var BIRD = [['#5b8def', '#3d6fd0'], ['#e0703a', '#b5532a'], ['#7a8a99', '#58646f']];
  var CAT = [['#f0a04b', '#c9792c', '#222222'], ['#9aa0a6', '#6f757b', '#222222'], ['#3a3a3a', '#1f1f1f', '#c8f060'], ['#f3efe6', '#cfc7b6', '#4a7fd0']];
  var FISH = [['#ff8c3a', '#ffb36b'], ['#3aa0ff', '#8fd0ff'], ['#ff5a7a', '#ffa0b4']];
  var NAMES = { bee: 'Ong Vàng', butterfly: 'Bướm Xinh', bird: 'Chích Bông', ladybug: 'Bọ Rùa', cat: 'Mướp', snail: 'Ốc Sên', fish: 'Cá Vàng', firefly: 'Đom Đóm' };
  // Đỉnh đầu (hình quay phải) — chỗ đội mũ Noel.
  var HAT = { bee: [15, 7.2], butterfly: [14.4, 8.2], bird: [15, 3.4], ladybug: [12, 5.1], cat: [19, 4.4], snail: [16.2, 2], fish: [14.5, 2.4], firefly: [8, 2.3] };
  var STEP_MAX = 0.05;   // bước mô phỏng tối đa (s) — khung thưa thì chia nhỏ cho chuyển động vẫn mượt / ổn định

  function rnd(a, b) { return a + Math.random() * (b - a); }
  function pick(a) { return a[Math.floor(Math.random() * a.length)]; }
  function clamp(v, a, b) { return v < a ? a : v > b ? b : v; }

  // onAction(name): báo ra ngoài khi bấm con thú lúc đang nhắc nghỉ ('rest'). Không truyền = không làm gì.
  // opts.ignore: selector CSS — bấm / rê lên phần tử khớp (nút, link, tiêu đề menu…) thì không tính là bấm con thú.
  function create(host, onAction, opts) {
    var IGNORE = (opts && opts.ignore) || 'button,a,#prjLink';
    var cv = document.createElement('canvas');
    cv.style.cssText = 'position:absolute;left:0;top:0;width:100%;height:100%;pointer-events:none;z-index:5;display:none';
    host.appendChild(cv);
    var g = cv.getContext('2d');
    var S = { enabled: false, counts: {}, speed: 100, mouse: true, flowers: true, trail: true, reactSql: true, tips: true, seasonal: true, names: {}, adoptedOn: null };
    var ENV = { warn: false, season: '', night: false, active: true, hidden: false, tips: [] };
    var pets = [], parts = [], flower = null, flowerCd = 6;
    var W = 0, H = 0, t = 0, last = 0, raf = 0, tmr = 0, mouse = null, mouseAt = -10;
    var bubble = null, tipIdx = 0, hoverPet = null, hoverT = 0, burst = 0, running = false, runSince = 0, restAsk = false, restPet = null;
    var seasonCd = 0, warnCd = 4;

    host.addEventListener('mousemove', function (e) {
      var r = host.getBoundingClientRect(); mouse = { x: e.clientX - r.left, y: e.clientY - r.top }; mouseAt = t;
      host.style.cursor = S.enabled && !ENV.hidden && (S.tips || restAsk) && hitPet(mouse.x, mouse.y) && !(e.target.closest && e.target.closest(IGNORE)) ? 'pointer' : '';
      if (tmr) { clearTimeout(tmr); tmr = 0; schedule(); }   // đang vẽ thưa: chuột vào thì vẽ ngay
    });
    host.addEventListener('mouseleave', function () { mouse = null; host.style.cursor = ''; });
    host.addEventListener('click', function (e) {
      if (!S.enabled || ENV.hidden || (e.target.closest && e.target.closest(IGNORE))) return;
      var r = host.getBoundingClientRect(), p = hitPet(e.clientX - r.left, e.clientY - r.top);
      if (p) clickPet(p);
    });
    document.addEventListener('visibilitychange', schedule);

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
        loopA: 0, loopCx: 0, loopCy: 0, landX: 0, c1: '', c2: '', eye: '', runSleep: false };
      var c = kind === 'butterfly' ? pick(BFLY) : kind === 'bird' ? pick(BIRD) : kind === 'cat' ? pick(CAT) : kind === 'fish' ? pick(FISH) : null;
      if (c) { p.c1 = c[0]; p.c2 = c[1]; p.eye = c[2] || ''; }
      if (kind === 'fish') { p.mode = 'swim'; p.y = rnd(fishTop(p), Math.max(fishTop(p), H - p.h - 1)); pickFishTarget(p); }
      else if (kind === 'bee' || kind === 'butterfly' || kind === 'bird' || kind === 'firefly') { p.y = rnd(0, Math.max(0, H - p.h)); pickTarget(p); }
      else { p.mode = 'walk'; p.y = ground(p); p.modeT = rnd(2, 6); }
      return p;
    }
    function fishTop(p) { return Math.max(0, H * 0.3); }
    function pickFishTarget(p) { p.tx = rnd(2, Math.max(4, W - p.w - 2)); p.ty = rnd(fishTop(p), Math.max(fishTop(p), H - p.h - 1)); }

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
      // SQL Query chạy lâu (> 5 giây): mèo nằm ngủ chờ; chạy xong (event ok / err) thì thức dậy.
      if (running && S.reactSql && t - runSince > 5 && p.mode !== 'sleep' && p.mode !== 'pounce' && !mouseActive()) {
        p.mode = 'sleep'; p.modeT = 1e6; p.cd = 0.5; p.moving = false; p.runSleep = true;
      }
      if (mouseActive() && (p.mode === 'walk' || p.mode === 'stalk' || p.mode === 'sleep')) {
        if (p.mode === 'sleep') { p.mode = 'wake'; p.modeT = 0.8; p.runSleep = false; } else p.mode = 'chase';
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
        var sleepy = S.seasonal && ENV.night;   // buổi tối: mèo buồn ngủ hơn
        if (p.mode === 'walk') {
          if (r < (sleepy ? 0.3 : 0.45)) { p.mode = 'sit'; p.modeT = rnd(2, 5); }
          else if (r < (sleepy ? 0.75 : 0.65)) { p.mode = 'sleep'; p.modeT = rnd(8, 18) * (sleepy ? 1.6 : 1); p.cd = 0.5; }
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

    // Cá: bơi lượn sóng ở nửa dưới thanh, nhả bong bóng; chuột lại gần thì quẫy đuôi bơi vụt đi.
    function stepFish(p, dt) {
      if (S.mouse && mouse && p.mode !== 'dart' && distMouse(p) < 35) {
        p.mode = 'dart'; p.modeT = 0.9;
        p.tx = mouse.x > p.x + p.w / 2 ? rnd(0, Math.max(0, p.x - 70)) : rnd(Math.min(Math.max(0, W - p.w), p.x + 70), Math.max(0, W - p.w));
        p.ty = rnd(fishTop(p), Math.max(fishTop(p), H - p.h - 1));
      }
      if (p.mode === 'dart') { p.modeT -= dt; if (p.modeT <= 0) { p.mode = 'swim'; pickFishTarget(p); } }
      var sp = flyTo(p, dt, p.mode === 'dart' ? 170 : 38, 1.6, 2.2);
      p.y = clamp(p.y, fishTop(p) - 1, Math.max(fishTop(p), H - p.h));
      p.tilt *= 0.4;
      p.moving = sp > 6;
      if (p.mode === 'swim' && near(p, 6)) pickFishTarget(p);
      p.cd -= dt;
      if (p.cd <= 0) { p.cd = rnd(1.2, 3.5); parts.push({ k: 'bubble', x: p.x + (p.face > 0 ? p.w - 2 : 2), y: p.y + 3, vx: rnd(-1, 1), vy: -9, life: 1.6, max: 1.6, r: rnd(0.9, 1.6) }); }
    }

    // Đom đóm: bay lững lờ, đổi hướng chậm; chuột lại gần thì dạt ra xa.
    function stepFirefly(p, dt) {
      if (S.mouse && mouse && distMouse(p) < 28) { p.tx = clamp(p.x + (p.x + p.w / 2 > mouse.x ? 40 : -40), 0, Math.max(0, W - p.w)); p.ty = rnd(0, Math.max(0, H - p.h)); }
      flyTo(p, dt, 28, 3, 1.4);
      if (near(p, 4)) pickTarget(p);
    }

    var STEP = { bee: stepNectar, butterfly: stepNectar, bird: stepBird, ladybug: stepLadybug, cat: stepCat, snail: stepSnail, fish: stepFish, firefly: stepFirefly };

    // ---------- bong bóng chữ, bấm / rê chuột lên con thú ----------
    function hitPet(x, y) {
      for (var i = pets.length - 1; i >= 0; i--) {
        var p = pets[i];
        if (x >= p.x - 4 && x <= p.x + p.w + 4 && y >= p.y - 4 && y <= p.y + p.h + 4) return p;
      }
      return null;
    }
    function petName(p) {
      var same = pets.filter(function (q) { return q.kind === p.kind; });
      var n = ((S.names || {})[p.kind] || '').trim() || NAMES[p.kind] || p.kind;
      return same.length > 1 ? n + ' ' + (same.indexOf(p) + 1) : n;
    }
    function petDays() {
      if (!S.adoptedOn) return 0;
      var d = new Date(S.adoptedOn); if (isNaN(d)) return 0;
      var a = new Date(d.getFullYear(), d.getMonth(), d.getDate()), now = new Date(), b = new Date(now.getFullYear(), now.getMonth(), now.getDate());
      return Math.max(1, Math.round((b - a) / 864e5) + 1);
    }
    function say(p, text, kind, life) { bubble = { pet: p, text: text, kind: kind, life: life, max: life }; burst = Math.max(burst, 0.5); schedule(); }
    function clickPet(p) {
      if (restAsk) { restAsk = false; bubble = null; parts.push({ k: 'heart', x: p.x + p.w / 2, y: p.y, vx: 0, vy: -14, life: 1.4, max: 1.4 }); if (onAction) onAction('rest'); return; }
      if (!S.tips) return;
      var tips = ENV.tips || [];
      var text = tips.length ? '💡 ' + tips[tipIdx++ % tips.length] : '💡 Chưa có mẹo nào.';
      say(p, text, 'tip', 8);
      parts.push({ k: 'heart', x: p.x + p.w / 2, y: p.y, vx: 0, vy: -14, life: 1.2, max: 1.2 });
    }
    function hover(dt) {
      var p = S.tips && mouse && !bubble ? hitPet(mouse.x, mouse.y) : null;
      if (!p || p !== hoverPet) { hoverPet = p; hoverT = 0; return; }
      hoverT += dt;
      if (hoverT > 0.7) {
        var days = petDays();
        say(p, petName(p) + (days ? ' · ở với bạn ' + days + ' ngày' : '') + ' — bấm vào mình để xem mẹo', 'name', 3.5);
        hoverT = -1e6;   // rời chuột rồi rê lại mới hiện lần nữa
      }
    }

    // ---------- sự kiện từ ngoài (chạy SQL, nhắc nghỉ) ----------
    function wakeRunners() {
      pets.forEach(function (p) { if (p.runSleep) { p.runSleep = false; p.mode = 'wake'; p.modeT = 0.8; } });
    }
    function mark(p, text, color) { parts.push({ k: 'mark', x: p.x + p.w / 2, y: p.y + 2, vx: rnd(-3, 3), vy: -12, life: 1.4, max: 1.4, txt: text, c: color }); }
    function onEvent(name, data) {
      if (name === 'run') { if (S.reactSql) { running = true; runSince = t; } return; }
      if (name === 'ok' || name === 'err') {
        running = false; wakeRunners();
        if (!S.reactSql) return;
        var big = data && data.ms > 10000;
        pets.forEach(function (p, i) {
          if (name === 'ok') {
            for (var k = 0; k < (big ? 3 : 1); k++) parts.push({ k: 'heart', x: p.x + p.w / 2 + rnd(-4, 4), y: p.y - k * 4, vx: rnd(-6, 6), vy: -14 - k * 3, life: 1.6, max: 1.6 });
            if (p.kind === 'bee' && p.mode === 'fly' && W > 60) { p.mode = 'loop'; p.loopA = 0; p.loopCx = p.x; p.loopCy = p.y + 5; }
            if (p.kind === 'cat' && (p.mode === 'walk' || p.mode === 'sleep')) { p.mode = 'sit'; p.modeT = rnd(1.5, 3); }
          } else {
            mark(p, '!', '#e0262b');
            if (p.kind === 'bird') flee(p, p.x + p.w / 2 + (Math.random() < 0.5 ? -1 : 1));
            else if (p.kind === 'snail') { p.mode = 'hide'; p.modeT = 4; }
            else if (p.kind === 'cat' && p.mode !== 'pounce') { p.y = ground(p); pounce(p); p.vx = 0; }
            else if (p.kind === 'bee' || p.kind === 'butterfly' || p.kind === 'firefly') { if (p.mode !== 'loop') { p.mode = 'fly'; releaseFlower(p, false); p.vy = -80; pickTarget(p); } }
            else if (p.kind === 'fish') { p.mode = 'dart'; p.modeT = 0.9; pickFishTarget(p); }
          }
        });
        burst = 3; schedule();
        return;
      }
      if (name === 'rest') {
        restAsk = true;
        restPet = pets.filter(function (p) { return p.kind === 'cat'; })[0] || pets[0] || null;
        if (restPet) {
          if (restPet.kind === 'cat') { restPet.mode = 'sit'; restPet.modeT = 1e6; }
          say(restPet, '☕ Làm lâu rồi, nghỉ chút nhé — bấm vào mình để Giải lao', 'rest', 1e9);
        }
        return;
      }
      if (name === 'rest-off') {
        restAsk = false;
        if (restPet && restPet.kind === 'cat' && restPet.mode === 'sit' && restPet.modeT > 1e5) restPet.modeT = 1;
        restPet = null;
        if (bubble && bubble.kind === 'rest') bubble = null;
      }
    }

    function world(dt) {
      var lovers = pets.some(function (p) { return p.kind === 'bee' || p.kind === 'butterfly'; });
      var season = S.seasonal ? ENV.season : '';
      // Có mùa thì hoa mọc cả khi không có ong / bướm (trang trí), nở một lúc rồi tàn, mọc bông khác.
      var blooms = S.flowers && (lovers || season);
      flowerCd -= dt;
      if (blooms && !flower && flowerCd <= 0 && W > 120) flower = { x: flowerSpot(), grow: 0, life: 1, leaving: false, by: null };
      if (flower) {
        flower.grow = Math.min(1, flower.grow + dt * 1.5);
        if (flower.leaving) flower.life -= dt * 0.8;
        else if (!lovers) flower.life -= dt * 0.04;   // không ai hút mật: tự tàn sau ~25 giây
        if (flower.by && pets.indexOf(flower.by) < 0) flower.by = null;
        if (flower.life <= 0 || !blooms) { flower = null; flowerCd = lovers ? rnd(15, 35) : rnd(3, 8); }
      }
      // Theo mùa: Tết cánh mai rơi, Noel tuyết rơi, Trung thu đèn lồng bay lên.
      seasonCd -= dt;
      if (season && seasonCd <= 0 && W > 60) {
        if (season === 'tet') { seasonCd = rnd(0.9, 1.8); parts.push({ k: 'petal', x: rnd(0, W), y: -2, vx: rnd(-5, 5), vy: rnd(5, 9), life: (H + 4) / 6, max: (H + 4) / 6, r: rnd(1, 1.6), c: '#ffd23f' }); }
        else if (season === 'noel') { seasonCd = rnd(0.25, 0.5); parts.push({ k: 'snow', x: rnd(0, W), y: -2, vx: rnd(-3, 3), vy: rnd(6, 11), life: (H + 4) / 6, max: (H + 4) / 6, r: rnd(0.8, 1.5) }); }
        else if (season === 'trungthu') { seasonCd = rnd(6, 10); parts.push({ k: 'lantern', x: rnd(20, Math.max(21, W - 20)), y: H + 4, vx: rnd(-2, 2), vy: -3.2, life: (H + 12) / 3.2, max: (H + 12) / 3.2 }); }
      }
      // Workspace là DB thật của khách: thỉnh thoảng 1 con giơ "!".
      if (ENV.warn && pets.length) {
        warnCd -= dt;
        if (warnCd <= 0) { warnCd = rnd(6, 11); mark(pick(pets), '!', '#e0262b'); }
      }
      if (bubble) {
        bubble.life -= dt;
        if (bubble.life <= 0 || pets.indexOf(bubble.pet) < 0) bubble = null;
      }
      if (restPet && pets.indexOf(restPet) < 0) { restPet = pets[0] || null; if (restAsk && restPet) say(restPet, '☕ Làm lâu rồi, nghỉ chút nhé — bấm vào mình để Giải lao', 'rest', 1e9); }
      if (burst > 0) burst -= dt;
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

    function drawFish(p) {
      var wag = Math.sin((t + p.ph) * (p.mode === 'dart' ? 22 : 8)) * 1.8;
      P([4, 5, -0.5, 1.2 + wag, -0.5, 8.8 + wag], p.c2);                // đuôi
      P([8, 1.8, 11.5, -0.2, 12, 2.4], p.c2);                             // vây lưng
      E(10, 5, 6.8, 3.7, p.c1);
      E(9.6, 6.6, 2.4, 1, p.c2);                                          // vây bụng
      L(13, 2.2, 13, 7.8, '#00000022', 0.6);                              // mang
      E(15, 4.2, 1, 1, '#111111'); E(15.3, 3.9, 0.3, 0.3, '#ffffff');
      E(8, 3.4, 2.2, 0.8, '#ffffff55');
    }

    function drawFirefly(p) {
      var glow = 0.35 + 0.65 * Math.max(0, Math.sin((t + p.ph) * 2.2));
      E(2.4, 4.8, 5.5 * glow + 1, 5 * glow + 1, 'rgba(255,240,120,' + (0.28 * glow).toFixed(3) + ')');
      E(2.4, 4.8, 2.2, 1.8, 'rgba(255,236,90,' + (0.45 + 0.55 * glow).toFixed(3) + ')');
      var f = 0.4 + 0.6 * Math.abs(Math.sin((t + p.ph) * 26));
      E(5, 2.6 - 1.4 * f, 2.6, 1.6 * f + 0.2, '#ffffff88', '#9ab0c0', 0.3);
      E(5.2, 4.3, 2.8, 1.8, '#4a3a22');
      E(8.2, 3.9, 1.4, 1.4, '#5a3a2a');
      Q(8.8, 3, 9.6, 1.6, 10, 1.2, '#4a3a22', 0.4);
    }

    // ---------- cây + màu hoa theo mùa ----------
    // Hoa: [cánh, nhuỵ] — thường ngày hồng; Tết hoa mai; Trung thu hoa cúc; Noel hoa trạng nguyên.
    var FLOWER = { '': ['#ff8fb8', '#ffd23f'], tet: ['#ffd23f', '#e07b00'], trungthu: ['#ffb030', '#8a5a20'], noel: ['#d81b2c', '#ffd23f'] };
    function treeX() { return Math.round(clamp(W * 0.58, 40, Math.max(40, W - 40))); }
    // Hoa không mọc đè lên cây.
    function flowerSpot() {
      var season = S.seasonal ? ENV.season : '', tx = treeX();
      for (var i = 0; i < 6; i++) { var x = rnd(30, W - 30); if (!season || Math.abs(x - tx - 6) > 22) return x; }
      return rnd(30, Math.max(31, tx - 30));
    }
    // Cây đứng cố định ở ~58% bề ngang thanh, chân chạm đáy, cao ≤ 20 px thiết kế.
    function drawTree(season) {
      var x = treeX(), b = H;
      if (season === 'noel') {                     // cây thông + đèn nháy + ngôi sao
        g.fillStyle = '#6b3b12'; g.fillRect(x - 1.2, b - 3.5, 2.4, 3.5);
        P([x - 7, b - 3.5, x + 7, b - 3.5, x, b - 11], '#2e7d32');
        P([x - 5.6, b - 8.5, x + 5.6, b - 8.5, x, b - 15], '#388e3c');
        P([x - 4.2, b - 13, x + 4.2, b - 13, x, b - 19], '#43a047');
        var LIGHTS = [[-4, -5], [2.5, -6.5], [-2, -9.5], [3, -11.5], [-2.2, -14], [1.4, -16]], COL = ['#ff5252', '#ffd23f', '#40c4ff', '#ff80ab'];
        LIGHTS.forEach(function (l, i) { var on = Math.sin(t * 3 + i * 1.7) > -0.2; E(x + l[0], b + l[1], 0.9, 0.9, on ? COL[i % 4] : '#1b5e20'); });
        E(x, b - 19.5, 1.6, 1.6, '#ffd23f', '#e0a000', 0.4);
      } else if (season === 'tet') {               // chậu mai: cành nâu, hoa vàng nhuỵ cam
        P([x - 4, b - 4, x + 4, b - 4, x + 3, b, x - 3, b], '#b5651d');
        L(x - 4.5, b - 4, x + 4.5, b - 4, '#8a4a12', 0.8);
        Q(x, b - 4, x - 1, b - 11, x + 1, b - 19, '#5d3a1a', 1.3);
        Q(x - 0.5, b - 9, x - 4, b - 11, x - 6, b - 15, '#5d3a1a', 0.8);
        Q(x + 0.5, b - 12, x + 4, b - 13, x + 6, b - 17, '#5d3a1a', 0.8);
        [[x + 1, b - 19], [x - 6, b - 15], [x + 6, b - 17], [x - 3, b - 12.5], [x + 3.5, b - 14], [x - 1, b - 16]].forEach(function (f, i) {
          var s = 1 + 0.12 * Math.sin(t * 1.5 + i);
          for (var k = 0; k < 5; k++) { var a = k * Math.PI * 2 / 5; E(f[0] + Math.cos(a) * 1.2 * s, f[1] + Math.sin(a) * 1.2 * s, 0.9 * s, 0.9 * s, '#ffd23f'); }
          E(f[0], f[1], 0.6, 0.6, '#e07b00');
        });
      } else if (season === 'trungthu') {          // cột tre + dây treo 3 đèn lồng đung đưa
        L(x, b, x, b - 19, '#8a6a2a', 1.4);
        Q(x, b - 18, x + 9, b - 14, x + 18, b - 17, '#6b4a1a', 0.5);
        [[5, -15.6], [10.5, -14.6], [15.5, -15.8]].forEach(function (l, i) {
          var sw = Math.sin(t * 1.6 + i * 1.3) * 0.8, lx = x + l[0] + sw, ly = b + l[1];
          L(x + l[0], ly, lx, ly + 1.6, '#6b4a1a', 0.4);
          E(lx, ly + 4, 4, 4, 'rgba(255,176,48,0.22)');
          E(lx, ly + 4, 2, 2.5, i === 1 ? '#ffb030' : '#e0262b', '#ffd23f', 0.35);
          L(lx - 1, ly + 6.6, lx + 1, ly + 6.6, '#ffd23f', 0.6);
        });
      }
    }

    // Mũ Noel đội trên đầu (toạ độ cục bộ, quay phải).
    function drawHat(p) {
      var h = p.kind === 'cat' && p.mode === 'sleep' ? [18.3, 10.2] : HAT[p.kind];
      if (!h) return;
      var x = h[0], y = h[1];
      P([x - 3, y + 0.6, x + 3, y + 0.6, x - 1.2, y - 4.6], '#e0262b');
      E(x - 1.4, y - 4.6, 1, 1, '#ffffff');
      E(x, y + 0.6, 3.4, 0.9, '#ffffff');
    }

    var DRAW = { bee: drawBee, butterfly: drawButterfly, bird: drawBird, ladybug: drawLadybug, cat: drawCat, snail: drawSnail, fish: drawFish, firefly: drawFirefly };

    // Bong bóng chữ cạnh con thú: một dòng, nền sáng, cắt bớt cho vừa thanh.
    function drawBubble() {
      var b = bubble, p = b.pet;
      g.font = '11px "Segoe UI", sans-serif';
      var maxW = Math.max(80, Math.min(W * 0.62, 560)), text = b.text, tw = g.measureText(text).width;
      if (tw > maxW) { while (text.length > 4 && g.measureText(text + '…').width > maxW) text = text.slice(0, -1); text += '…'; tw = g.measureText(text).width; }
      var bw = tw + 14, bh = Math.min(17, H - 2), right = p.x + p.w / 2 < W / 2;
      var x = right ? p.x + p.w + 4 : p.x - bw - 4;
      x = clamp(x, 1, Math.max(1, W - bw - 1));
      var y = Math.max(1, (H - bh) / 2);
      var col = b.kind === 'rest' ? ['#e8f6ff', '#5aa9ff', '#0b3a66'] : b.kind === 'tip' ? ['#fff8dc', '#d9b54a', '#4a3b00'] : ['#ffffff', '#b8c4d0', '#333333'];
      g.globalAlpha = clamp(b.life * 3, 0, 1) * 0.97;
      g.beginPath();
      if (g.roundRect) g.roundRect(x, y, bw, bh, 8); else g.rect(x, y, bw, bh);
      g.fillStyle = col[0]; g.fill(); g.strokeStyle = col[1]; g.lineWidth = 1; g.stroke();
      g.fillStyle = col[2]; g.textAlign = 'left'; g.textBaseline = 'middle';
      g.fillText(text, x + 7, y + bh / 2 + 0.5);
      g.textBaseline = 'alphabetic'; g.globalAlpha = 1;
    }

    function draw() {
      var dpr = window.devicePixelRatio || 1;
      if (cv.width !== Math.round(W * dpr) || cv.height !== Math.round(H * dpr)) { cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr); }
      g.setTransform(dpr, 0, 0, dpr, 0, 0);
      g.clearRect(0, 0, W, H);
      if (ENV.warn) { g.fillStyle = '#e0262b'; g.globalAlpha = 0.85; g.fillRect(0, 0, W, 2); g.globalAlpha = 1; }   // DB thật của khách
      var season = S.seasonal ? ENV.season : '';
      if (season && W > 120) drawTree(season);
      if (flower) {
        var s = flower.grow * Math.max(0, flower.life), cx = flower.x, cy = H - 3 - 6 * s;
        var fc = FLOWER[season] || FLOWER[''], petal = fc[0], heart = fc[1];   // màu hoa theo mùa
        g.globalAlpha = clamp(flower.life * 1.5, 0, 1);
        L(cx, H, cx, cy, '#4c9a3a', 1);
        for (var k = 0; k < 5; k++) { var a = k * Math.PI * 2 / 5 + t * 0.3; E(cx + Math.cos(a) * 2.6 * s, cy + Math.sin(a) * 2.6 * s, 1.9 * s, 1.9 * s, petal); }
        E(cx, cy, 1.6 * s, 1.6 * s, heart);
        g.globalAlpha = 1;
      }
      parts.forEach(function (q) {
        var a = q.life / q.max;
        if (q.k === 'dot') { g.globalAlpha = Math.min(1, a) * 0.55; E(q.x, q.y, q.r, q.r, q.c); }
        else if (q.k === 'petal') { g.globalAlpha = Math.min(1, a * 3) * 0.9; E(q.x, q.y, q.r * 1.3, q.r, q.c, '#e0a000', 0.3); }
        else if (q.k === 'snow') { g.globalAlpha = Math.min(1, a * 3) * 0.95; E(q.x, q.y, q.r, q.r, '#ffffff', '#9fb0c2', 0.35); }
        else if (q.k === 'bubble') { g.globalAlpha = Math.min(1, a * 2) * 0.8; E(q.x, q.y, q.r, q.r, '#ffffff33', '#7fb8e0', 0.5); }
        else if (q.k === 'lantern') {
          g.globalAlpha = Math.min(1, a * 2, (1 - a) * 4 + 0.2);
          E(q.x, q.y, 5, 5, 'rgba(255,176,48,0.25)');
          L(q.x, q.y - 4.5, q.x, q.y - 3, '#8a5a20', 0.5);
          E(q.x, q.y, 2.4, 3, '#e0262b', '#ffd23f', 0.4);
          L(q.x - 1.2, q.y + 3, q.x + 1.2, q.y + 3, '#ffd23f', 0.8);
        }
        else {
          g.globalAlpha = Math.min(1, a * 2);
          g.fillStyle = q.k === 'heart' ? '#ff5a8a' : q.k === 'mark' ? q.c : '#8aa0b8';
          g.font = q.k === 'heart' ? '10px sans-serif' : 'bold 8px sans-serif';
          g.textAlign = 'center';
          g.fillText(q.k === 'heart' ? '♥' : q.k === 'mark' ? q.txt : 'z', q.x, q.y);
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
        if (season === 'noel' && !(p.kind === 'snail' && p.mode === 'hide')) drawHat(p);
        g.restore();
      });
      if (bubble) drawBubble();
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

    // Nhịp vẽ: Bcode đang được dùng → mỗi khung hình (vẽ thưa lúc rảnh trông giật — đã thử 20 hình/giây, bị chê lag);
    // cửa sổ khác đang active → ~20 hình/giây; trang ẩn / tạm ẩn (chia sẻ màn hình) → dừng hẳn.
    function schedule() {
      if (raf || tmr || !S.enabled || ENV.hidden || document.hidden) return;
      if (ENV.active || mouseActive() || bubble || burst > 0) raf = requestAnimationFrame(frame);
      else tmr = setTimeout(function () { tmr = 0; raf = requestAnimationFrame(frame); }, 50);
    }

    function frame(now) {
      raf = 0;
      if (!S.enabled || ENV.hidden) return;
      var dt = last ? Math.min((now - last) / 1000, 0.5) : 0;
      last = now;
      W = host.clientWidth; H = host.clientHeight;
      // Khung thưa thì chia nhỏ bước mô phỏng (≤ 50 ms) — chuyển động giữ đúng tốc độ, lò xo không bị bật nảy.
      var n = Math.max(1, Math.ceil(dt / STEP_MAX)), sub = dt / n, sp = clamp(+S.speed || 100, 25, 300) / 100;
      for (var i = 0; i < n; i++) {
        t += sub;
        var d = sub * sp;
        pets.forEach(function (p) { STEP[p.kind](p, d); });
        world(d);
      }
      hover(dt);
      draw();
      schedule();
    }

    function stop() { if (raf) cancelAnimationFrame(raf); clearTimeout(tmr); raf = 0; tmr = 0; last = 0; }

    return {
      set: function (s) {
        S = Object.assign({ enabled: false, counts: {}, speed: 100, mouse: true, flowers: true, trail: true, reactSql: true, tips: true, seasonal: true, names: {}, adoptedOn: null }, s || {});
        cv.style.display = S.enabled && !ENV.hidden ? 'block' : 'none';
        if (!S.enabled) { pets = []; parts = []; flower = null; bubble = null; restPet = null; stop(); return; }
        if (!S.trail) parts = parts.filter(function (q) { return q.k !== 'dot'; });
        if (!S.tips && bubble && bubble.kind !== 'rest') bubble = null;
        W = host.clientWidth; H = host.clientHeight;
        sync();
        if (!raf && !tmr) { last = 0; schedule(); }
      },
      // Trạng thái lúc chạy (C#: StatusPetEnv): warn, season, night, active, hidden, tips.
      env: function (e) {
        ENV = Object.assign({ warn: false, season: '', night: false, active: true, hidden: false, tips: [] }, e || {});
        cv.style.display = S.enabled && !ENV.hidden ? 'block' : 'none';
        if (ENV.hidden) { stop(); return; }
        if (tmr) { clearTimeout(tmr); tmr = 0; }
        schedule();
      },
      // Sự kiện: 'run' (bắt đầu chạy SQL), 'ok' / 'err' (chạy xong; data.ms = thời gian), 'rest' / 'rest-off' (nhắc nghỉ).
      event: function (name, data) { if (S.enabled) onEvent(name, data); },
      // Vẽ 1 con lên ctx khác (toạ độ cục bộ, quay mặt sang phải) — thú cưng trong editor (editorPet bên dưới) mượn hình vẽ ở đây.
      drawPet: function (ctx, p, time) {
        var g0 = g, t0 = t;
        g = ctx; t = time;
        try { DRAW[p.kind](p); } finally { g = g0; t = t0; }
      },
    };
  }

  // ---------- Thú cưng trong editor SQL Query (Settings → Thú cưng...): 1 con, 2 kiểu ----------
  // 'follow' = chạy / bay theo chuột, đứng lệch dưới-phải mũi chuột — chữ I giữ nguyên; canvas nhỏ position:fixed, pointer-events:none, dời bằng transform.
  // 'cursor' = con trỏ chuột trong vùng chữ thành hình con thú (ảnh CSS cursor ≤ 32 px; chấm đỏ ở mũi = điểm bấm).
  // Dùng: var ep = StatusPets.editor(hostElement, applyCursor); ep.set({ editorStyle, editorKind, editorScale }).
  // applyCursor(css) — bên gọi gán chuỗi CSS cursor cho vùng chữ ('' = trả con trỏ gốc). Không đụng tới editor, không chạy khi gõ phím.
  // Bản native tương đương: Controls/StatusPets.Editor.cs (cùng tên hàm, cùng con số).
  var NOSE = { bee: [17.4, 9.5], butterfly: [16, 9.5], bird: [20, 7], ladybug: [14, 7], cat: [23, 8.6], snail: [17.5, 3] };   // mũi (hình quay phải)
  var FOLLOW = { bee: 420, butterfly: 300, bird: 520, ladybug: 160, cat: 360, snail: 60 };   // tốc độ tối đa khi theo chuột (px thiết kế / giây)
  var REST = { bee: 'rest', butterfly: 'rest', bird: 'hop', ladybug: 'idle', cat: 'sit', snail: 'idle' };
  var CUR = 32;    // cỡ ảnh con trỏ (px CSS) — Chromium bỏ con trỏ ảnh > 32 px khi ở sát mép
  var PAD = 20;    // lề quanh con thú trên canvas 'follow' (chỗ cho tim / zZ)

  function editorPet(host, applyCursor) {
    var core = create(document.createElement('div'));   // chỉ mượn drawPet, không bật
    var cv = document.createElement('canvas');
    cv.style.cssText = 'position:fixed;left:0;top:0;pointer-events:none;z-index:60;display:none;will-change:transform';
    document.body.appendChild(cv);
    var g = cv.getContext('2d');
    var cc = document.createElement('canvas'), cg = cc.getContext('2d');   // ảnh con trỏ
    var S = { editorStyle: 'off', editorKind: 'cat', editorScale: 150 };
    var p = null, parts = [], t = 0, last = 0, raf = 0, tmr = 0, lastCss = '', rect = null, imgSet = null;
    var mouse = null, mouseAt = -100, inside = false;

    function style() { return S.editorStyle === 'follow' || S.editorStyle === 'cursor' ? S.editorStyle : 'off'; }
    function scale() { return clamp(+S.editorScale || 150, 100, 250) / 100; }
    function hostRect() { if (!rect) rect = host.getBoundingClientRect(); return rect; }
    window.addEventListener('resize', function () { rect = null; });
    window.addEventListener('scroll', function () { rect = null; }, true);   // trang có vùng cuộn (cửa sổ cài đặt)
    if (window.ResizeObserver) new ResizeObserver(function () { rect = null; }).observe(host);

    // Bắt chuột ở pha capture của window: editor có chặn lan truyền / giữ chuột khi bôi chọn thì vẫn nhận được.
    window.addEventListener('pointermove', function (e) {
      if (style() === 'off') return;
      var r = hostRect(), x = e.clientX, y = e.clientY;
      if (x < r.left || x >= r.right || y < r.top || y >= r.bottom) { inside = false; return; }
      if (mouse && p) {
        p.accX += x - mouse.x;
        if (Math.abs(p.accX) > 6) { if (style() === 'cursor') p.face = p.accX > 0 ? 1 : -1; p.accX = 0; }
      }
      if (!mouse || Math.abs(x - mouse.x) + Math.abs(y - mouse.y) > 0.5) mouseAt = t;
      mouse = { x: x, y: y }; inside = true;
      kick();
    }, true);
    document.documentElement.addEventListener('mouseleave', function () { inside = false; });
    document.addEventListener('visibilitychange', kick);

    function kick() { if (!raf && !tmr && p && style() !== 'off' && !document.hidden) { last = 0; raf = requestAnimationFrame(frame); } }

    function newPet() {
      var kind = S.editorKind, sz = SIZE[kind], r = hostRect(), k = scale();
      var c = kind === 'butterfly' ? pick(BFLY) : kind === 'bird' ? pick(BIRD) : kind === 'cat' ? pick(CAT) : null;
      p = { kind: kind, w: sz[0], h: sz[1], vx: 0, vy: 0, face: -1, mode: REST[kind], ph: rnd(0, 10), tilt: 0, moving: false, rest: 1, cd: 0, peck: 0, accX: 0,
        x: mouse ? mouse.x + 10 : r.right - sz[0] * k - 30, y: mouse ? mouse.y + 12 : r.bottom - sz[1] * k - 8,
        c1: c ? c[0] : '', c2: c ? c[1] : '', eye: c && c[2] ? c[2] : '' };
      parts = [];
    }

    function sleepZ(dt, bw) {
      p.cd -= dt;
      if (p.cd <= 0) { p.cd = 1.1; parts.push({ k: 'z', x: p.x + (p.face > 0 ? bw - 4 : 4), y: p.y + 6, vx: p.face * 3, vy: -6, life: 2, max: 2 }); }
    }
    function birdIdle(dt) {
      if (p.kind !== 'bird' || p.mode !== 'hop') return;
      p.cd -= dt;
      if (p.cd <= 0) { p.cd = rnd(0.8, 2.2); p.peck = 0.25; }
      if (p.peck > 0) { p.peck -= dt; p.tilt = 0.5; }
    }

    // Theo chuột: đích = lệch dưới-phải mũi chuột (chừa chữ I), kẹp trong editor (chừa thanh cuộn dọc).
    function stepFollow(dt) {
      var k = scale(), bw = p.w * k, bh = p.h * k, r = hostRect(), idle = t - mouseAt;
      var tx = mouse ? mouse.x + 10 : p.x, ty = mouse ? mouse.y + 12 : p.y;
      tx = clamp(tx, r.left, Math.max(r.left, r.right - bw - 14)); ty = clamp(ty, r.top, Math.max(r.top, r.bottom - bh - 2));
      var dx = tx - p.x, dy = ty - p.y, dist = Math.hypot(dx, dy), walker = !(p.kind in FLIER_KINDS);
      if (p.mode === 'sleep') {
        if (idle > 0.3) { sleepZ(dt, bw); return; }
        p.mode = 'wake'; p.cd = 0.5;
        parts.push({ k: 'heart', x: p.x + bw / 2, y: p.y, vx: 0, vy: -14, life: 1.2, max: 1.2 });
      }
      if (p.mode === 'wake') { p.cd -= dt; if (p.cd > 0) return; p.mode = REST[p.kind]; }
      if (!p.moving && dist > 8 * k) p.moving = true;
      if (p.moving && dist < 2 * k && (walker || Math.hypot(p.vx, p.vy) < 40 * k)) { p.moving = false; p.rest = 0; }
      if (p.moving) {
        var max = FOLLOW[p.kind] * k;
        p.mode = p.kind === 'cat' ? (dist > 40 * k ? 'chase' : 'walk')
          : p.kind === 'ladybug' ? (dist > 120 * k || (p.mode === 'air' && dist > 10 * k) ? 'air' : 'walk')
          : p.kind === 'snail' ? 'walk' : 'fly';
        if (p.mode === 'fly' || p.mode === 'air') {   // lò xo hơi lố + rung nhẹ (như flyTo)
          var wob = Math.sin((t + p.ph) * 5) * 2 * k;
          p.vx += (dx * 9 - p.vx * 4.5) * dt; p.vy += ((dy + wob) * 9 - p.vy * 4.5) * dt;
          var sp = Math.hypot(p.vx, p.vy);
          if (sp > max) { p.vx *= max / sp; p.vy *= max / sp; }
          p.x += p.vx * dt; p.y += p.vy * dt;
          p.tilt = clamp(Math.atan2(p.vy, Math.abs(p.vx) + 20 * k), -0.45, 0.45);
          if (Math.abs(p.vx) > 10 * k) p.face = p.vx > 0 ? 1 : -1;
        } else {                                       // bò / chạy thẳng tới đích
          var s = Math.min(dist, max * dt);
          p.vx = dx / dist * max; p.vy = dy / dist * max;
          p.x += dx / dist * s; p.y += dy / dist * s; p.tilt = 0;
          if (Math.abs(dx) > 1) p.face = dx > 0 ? 1 : -1;
        }
        return;
      }
      p.vx = p.vy = 0; p.tilt = 0; p.rest += dt;
      if (p.rest > 0.5 && p.mode !== 'sleep') {
        if (p.mode === 'fly' || p.mode === 'air' || p.mode === 'walk' || p.mode === 'chase') p.mode = REST[p.kind];
        if (mouse) p.face = mouse.x < p.x + bw / 2 ? -1 : 1;   // ngoảnh về phía chuột
      }
      if (p.kind === 'cat' && p.mode === 'sit' && idle > 20) { p.mode = 'sleep'; p.cd = 0.5; }
      birdIdle(dt);
    }

    // Thay con trỏ: chuột vừa di chuyển = chạy / bay, đứng yên = nghỉ, lâu = mèo ngủ.
    function stepCursor(dt) {
      var idle = t - mouseAt;
      p.moving = idle < 0.25; p.tilt = 0;
      if (p.moving) { p.mode = p.kind === 'cat' ? 'chase' : p.kind === 'ladybug' || p.kind === 'snail' ? 'walk' : 'fly'; p.rest = 0; return; }
      p.rest += dt;
      p.mode = p.kind === 'cat' && idle > 20 ? 'sleep' : REST[p.kind];
      birdIdle(dt);
    }

    function stepParts(dt) {
      for (var i = parts.length - 1; i >= 0; i--) {
        var q = parts[i];
        q.life -= dt;
        if (q.life <= 0) { parts.splice(i, 1); continue; }
        q.x += q.vx * dt; q.y += q.vy * dt;
      }
    }

    function drawBody(ctx, x, y, k) {
      var bob = p.mode === 'rest' ? Math.sin(t * 9) * 0.3 * k : 0;
      ctx.save();
      ctx.translate(x + p.w * k / 2, y + p.h * k / 2 + bob);
      ctx.rotate(p.tilt * p.face);
      ctx.scale(p.face * k, k);
      ctx.translate(-p.w / 2, -p.h / 2);
      core.drawPet(ctx, p, t);
      ctx.restore();
    }

    function drawFollow() {
      var k = scale(), bw = p.w * k, bh = p.h * k, dpr = window.devicePixelRatio || 1;
      var cw = Math.ceil(bw + PAD * 2), ch = Math.ceil(bh + PAD * 2);
      if (cv.width !== Math.round(cw * dpr) || cv.height !== Math.round(ch * dpr)) {
        cv.width = Math.round(cw * dpr); cv.height = Math.round(ch * dpr); cv.style.width = cw + 'px'; cv.style.height = ch + 'px';
      }
      var ox = Math.round(p.x - PAD), oy = Math.round(p.y - PAD);
      cv.style.transform = 'translate(' + ox + 'px,' + oy + 'px)';
      g.setTransform(dpr, 0, 0, dpr, 0, 0);
      g.clearRect(0, 0, cw, ch);
      parts.forEach(function (q) {
        g.globalAlpha = Math.min(1, q.life / q.max * 2);
        g.fillStyle = q.k === 'heart' ? '#ff5a8a' : '#8aa0b8';
        g.font = q.k === 'heart' ? '12px sans-serif' : 'bold 10px sans-serif';
        g.textAlign = 'center';
        g.fillText(q.k === 'heart' ? '♥' : 'z', q.x - ox, q.y - oy);
      });
      g.globalAlpha = 1;
      drawBody(g, p.x - ox, p.y - oy, k);
    }

    // Ảnh con trỏ: mũi con thú = điểm bấm (hotspot), quay theo hướng rê chuột; image-set cho màn hình phóng to (DPI).
    function drawCursor() {
      if (imgSet === null) {
        var css = window.CSS && CSS.supports ? CSS.supports.bind(CSS) : function () { return false; };
        imgSet = css('cursor', 'image-set(url("a.png") 1x) 0 0, text') ? 'image-set' : css('cursor', '-webkit-image-set(url("a.png") 1x) 0 0, text') ? '-webkit-image-set' : '';
      }
      var dpr = imgSet ? clamp(Math.round((window.devicePixelRatio || 1) * 4) / 4, 1, 3) : 1;
      var k = Math.min(scale(), (CUR - 2) / p.w, (CUR - 2) / p.h), px = Math.round(CUR * dpr);
      if (cc.width !== px) { cc.width = px; cc.height = px; }
      cg.setTransform(dpr, 0, 0, dpr, 0, 0);
      cg.clearRect(0, 0, CUR, CUR);
      var bw = p.w * k, bh = p.h * k, nose = NOSE[p.kind];
      var ox = p.face > 0 ? CUR - 1 - bw : 1, oy = (CUR - bh) / 2;
      var hx = Math.round(clamp(ox + (p.face > 0 ? nose[0] : p.w - nose[0]) * k, 1, CUR - 2));
      var hy = Math.round(clamp(oy + nose[1] * k, 1, CUR - 2));
      drawBody(cg, ox, oy, k);
      cg.beginPath(); cg.arc(hx + 0.5, hy + 0.5, 2, 0, Math.PI * 2); cg.fillStyle = '#ffffff'; cg.fill();
      cg.beginPath(); cg.arc(hx + 0.5, hy + 0.5, 1.2, 0, Math.PI * 2); cg.fillStyle = '#e0262b'; cg.fill();
      var url = 'url("' + cc.toDataURL('image/png') + '")';
      var cur = (imgSet ? imgSet + '(' + url + ' ' + dpr + 'x)' : url) + ' ' + hx + ' ' + hy + ', text';
      if (cur !== lastCss) { lastCss = cur; applyCursor(cur); }
    }

    function frame(now) {
      raf = 0;
      var st = style();
      if (st === 'off' || !p || document.hidden) return;
      var dt = last ? Math.min((now - last) / 1000, st === 'cursor' ? 0.4 : 0.05) : 0;
      last = now; t += dt;
      stepParts(dt);
      if (st === 'follow') { stepFollow(dt); drawFollow(); raf = requestAnimationFrame(frame); return; }
      stepCursor(dt);
      drawCursor();
      if (!inside && t - mouseAt > 1) return;   // chuột ra khỏi editor: nghỉ, chờ chuột vào lại (pointermove → kick)
      tmr = setTimeout(function () { tmr = 0; raf = requestAnimationFrame(frame); }, p.moving ? 90 : 240);
    }

    return {
      set: function (s) {
        S = Object.assign({ editorStyle: 'off', editorKind: 'cat', editorScale: 150 }, s || {});
        if (!SIZE[S.editorKind]) S.editorKind = 'cat';
        var st = style();
        cv.style.display = st === 'follow' ? 'block' : 'none';
        if (st !== 'cursor' && lastCss) { lastCss = ''; applyCursor(''); }
        if (st === 'off') {
          p = null; parts = [];
          if (raf) cancelAnimationFrame(raf);
          clearTimeout(tmr); raf = 0; tmr = 0;
          return;
        }
        if (!p || p.kind !== S.editorKind) newPet();
        lastCss = '';
        if (st === 'cursor' && p) { clearTimeout(tmr); tmr = 0; }
        kick();
      },
    };
  }
  var FLIER_KINDS = { bee: 1, butterfly: 1, bird: 1 };

  window.StatusPets = { create: create, editor: editorPet };
})();
