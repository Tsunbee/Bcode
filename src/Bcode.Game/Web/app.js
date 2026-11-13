// "Đêm Go-Live" — phía người chơi. Mọi luật nằm ở máy chủ phòng; trang chỉ hiển thị + gửi thao tác.
// Kênh: chrome.webview.postMessage({t:"ui.*"}) cho C# của chính máy; {t:"net", m} = gửi nguyên văn cho máy chủ phòng.
"use strict";
const $ = (id) => document.getElementById(id);
const wv = window.chrome && window.chrome.webview;
const post = (m) => wv && wv.postMessage(m);
const send = (m) => post({ t: "net", m });
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const fmt = (s) => { s = Math.max(0, Math.ceil(s)); return Math.floor(s / 60) + ":" + String(s % 60).padStart(2, "0"); };
const LETTERS = "ABCDE";
const SPEED = 230, VISION = 360, USE = 85, BTN = 120, KILL = 95, REPORT = 120;

const ROLE = {
  tk: { name: "Triển khai", cls: "c-good", goal: "Viết tài liệu khảo sát ở các phòng phân hệ (E), rồi bàn giao ở Phòng họp. Tìm và vote loại Lập trình." },
  tester: { name: "Tester", cls: "c-good", goal: "Ra Khu Tester kiểm lỗi (E). Mỗi lỗi bắt được lộ manh mối: máy Dev số mấy, bao lâu trước." },
  dev: { name: "Lập trình", cls: "c-bad", goal: "Hạ gục đội dự án (Q). Ngồi máy Khu Dev 4 giây (E) để gài lỗi. Giả vờ làm khảo sát cho khỏi lộ." },
  bh: { name: "Bảo hành", cls: "c-bad", goal: "Phe xấu, ẩn vai, không giết được. 2 lượt report lỗi: đứng trong phòng phân hệ bấm B, hoặc từ chối khi khách nghiệm thu." },
};

const S = { screen: "home", me: 0, host: false, name: "", lobby: null, ips: [], port: 0, g: null, rooms: [] };

// ───────────────────────── Màn hình ─────────────────────────
function show(name) {
  S.screen = name;
  for (const id of ["scrHome", "scrLobby", "scrGame"]) $(id).classList.toggle("hidden", id !== "scr" + name[0].toUpperCase() + name.slice(1));
  if (name === "home") post({ t: "ui.find" }); else post({ t: "ui.stopfind" });
}

function toast(text, ms = 2600) {
  const t = $("toast");
  t.textContent = text; t.classList.add("on");
  clearTimeout(toast._h); toast._h = setTimeout(() => t.classList.remove("on"), ms);
}

function addChat(box, m) {
  const el = document.createElement("div");
  el.className = "m";
  if (m.sys) el.innerHTML = `<span class="sys">${esc(m.text)}</span>`;
  else el.innerHTML = `<span class="who" style="color:${R.COLORS[m.color % 8]}">${m.ghost ? "👻 " : ""}${esc(m.name)}</span>${esc(m.text)}`;
  box.appendChild(el);
  box.scrollTop = box.scrollHeight;
}

// ───────────────────────── Home ─────────────────────────
function renderRooms(rooms, error) {
  S.rooms = rooms;
  const ul = $("roomList");
  ul.innerHTML = "";
  if (!rooms.length) ul.innerHTML = `<li class="muted">Chưa thấy phòng nào… (đang tìm)</li>`;
  rooms.forEach((r, i) => {
    const li = document.createElement("li");
    li.innerHTML = `<div><div class="rn">${esc(r.name)}</div><div class="rm">${esc(r.host)} · ${esc(r.ip)}:${r.port} · ${r.players}/${r.max} người · ${r.state === "lobby" ? "đang chờ" : "đang chơi"}</div></div>`;
    const b = document.createElement("button");
    b.textContent = "Vào"; b.disabled = r.state !== "lobby" || r.players >= r.max;
    b.onclick = () => join(`${r.ip}:${r.port}`);
    li.appendChild(b);
    ul.appendChild(li);
  });
  $("findErr").textContent = error || "";
}

function join(ip) { $("homeErr").textContent = ""; S.name = $("inName").value.trim(); post({ t: "ui.join", name: S.name, ip }); }

$("btnJoinIp").onclick = () => join($("inIp").value);
$("inIp").onkeydown = (e) => { if (e.key === "Enter") join($("inIp").value); };
$("btnHost").onclick = () => { $("homeErr").textContent = ""; S.name = $("inName").value.trim(); post({ t: "ui.host", name: S.name, room: $("inRoom").value }); };
for (const id of ["btnRules", "btnRules2"]) $(id).onclick = () => $("rules").classList.remove("hidden");
$("rulesX").onclick = () => $("rules").classList.add("hidden");
$("rules").onclick = (e) => { if (e.target.id === "rules") $("rules").classList.add("hidden"); };
$("sound").onclick = () => { $("sound").textContent = Sfx.toggle() ? "🔇" : "🔊"; };
$("sound").textContent = Sfx.muted ? "🔇" : "🔊";

// ───────────────────────── Phòng chờ ─────────────────────────
function renderLobby(l) {
  S.lobby = l;
  document.body.classList.toggle("guest", !S.host);
  $("lbRoom").textContent = l.room;
  $("lbCount").textContent = `${l.players.length}/${l.max}`;
  const ul = $("lbPlayers");
  ul.innerHTML = "";
  for (const p of l.players) {
    const li = document.createElement("li");
    const cv = document.createElement("canvas");
    li.appendChild(cv);
    const nm = document.createElement("span");
    nm.innerHTML = `${esc(p.name)} ${p.id === S.me ? '<span class="muted">(bạn)</span>' : ""} ${p.host ? '<span class="host">★ chủ phòng</span>' : ""}`;
    li.appendChild(nm);
    if (S.host && !p.host) { const k = document.createElement("button"); k.className = "kick"; k.textContent = "Mời ra"; k.onclick = () => send({ t: "kick", id: p.id }); li.appendChild(k); }
    ul.appendChild(li);
    R.avatar(cv, p.color);
  }
  const n = l.players.length;
  const tbl = { 5: "3 Triển khai · 1 Tester · 1 Lập trình (hệ thống tự nghiệm thu)", 6: "3 Triển khai · 1 Tester · 1 Lập trình · 1 Bảo hành", 7: "4 Triển khai · 1 Tester · 1 Lập trình · 1 Bảo hành", 8: "4 Triển khai · 1 Tester · 2 Lập trình · 1 Bảo hành" };
  $("lbRoles").textContent = n < l.min ? `Cần ít nhất ${l.min} người (5–8).` : (tbl[n] || "Chế độ thử: 1 Lập trình, 1 Tester, còn lại Triển khai");
  const b = l.bank;
  $("bankBox").innerHTML = b
    ? `📘 <b>${esc(b.file)}</b><br>${b.total} câu (${b.multi} câu nhiều đáp án) · ${b.modules.length} phân hệ<div class="mods">${b.modules.map((m) => `${esc(m.name)}: ${m.count}`).join(" · ")}</div>`
    : S.host ? "Chưa có bộ câu hỏi — chọn file Excel trắc nghiệm (mỗi sheet = 1 phân hệ). File chỉ đọc trên máy bạn." : "Chủ phòng chưa chọn bộ câu hỏi.";
  for (const inp of document.querySelectorAll("#setBox input")) {
    if (document.activeElement !== inp) inp.value = l.settings[inp.dataset.k];
    inp.disabled = !S.host;
  }
  $("btnStart").disabled = !b || n < l.min;
  if (S.screen !== "lobby" && l.phase === "lobby") { closeGame(); show("lobby"); }
}

let setTimer = 0;
for (const inp of document.querySelectorAll("#setBox input")) {
  inp.oninput = () => {
    clearTimeout(setTimer);
    setTimer = setTimeout(() => {
      const s = {};
      for (const i of document.querySelectorAll("#setBox input")) s[i.dataset.k] = parseInt(i.value, 10) || 0;
      send({ t: "settings", s });
    }, 400);
  };
}
$("btnPick").onclick = () => post({ t: "ui.pickfile" });
$("btnFw").onclick = () => post({ t: "ui.firewall" });
$("btnStart").onclick =() => { $("lbErr").textContent = ""; send({ t: "start" }); };
$("btnLeave").onclick = () => { post({ t: "ui.leave" }); };
$("lbChatIn").onkeydown = (e) => { if (e.key === "Enter" && e.target.value.trim()) { send({ t: "chat", text: e.target.value }); e.target.value = ""; } };

// ───────────────────────── Ván chơi ─────────────────────────
function startGame(m) {
  const g = {
    role: m.role, pm: m.pm, mates: new Set(m.mates), map: m.map, tasks: m.tasks, settings: m.settings, counts: m.counts,
    players: new Map(m.players.map((p) => [p.id, p])), others: new Map(), bodies: [], snap: null,
    pred: null, alive: true, walk: 0, face: 1, q: null, msg: null, meeting: null, picks: null, feedEls: [],
    keys: {}, dx: 0, dy: 0, lastInput: 0, phase: "play",
  };
  S.g = g;
  for (const id of ["meet", "resultBox", "overBox", "qModal", "msgBox", "bhPrompt"]) $(id).classList.add("hidden");
  $("ghostTag").classList.add("hidden"); $("ghostChat").classList.add("hidden");
  $("feed").innerHTML = ""; $("mtChat").innerHTML = ""; $("gcLog").innerHTML = "";
  show("game");
  renderTasks();
  roleIntro();
  resize();
}

function closeGame() { S.g = null; for (const id of ["meet", "resultBox", "overBox", "qModal", "msgBox", "bhPrompt", "roleIntro"]) $(id).classList.add("hidden"); }

function me() { return S.g && S.g.players.get(S.me); }
function roleName(g) { return g.pm ? "Triển khai · Trưởng dự án" : ROLE[g.role].name; }

function roleIntro() {
  const g = S.g, r = ROLE[g.role];
  const mates = [...g.mates].map((id) => g.players.get(id)?.name).filter(Boolean);
  let extra = "";
  if (g.role === "dev" && mates.length) extra = `<p>Đồng đội Lập trình: <b class="c-bad">${esc(mates.join(", "))}</b></p>`;
  if (g.role === "bh") extra = `<p>Lập trình là: <b class="c-bad">${esc(mates.join(", "))}</b> (họ không biết bạn).</p>`;
  if (g.pm) extra = `<p>Bạn là <b>Trưởng dự án</b>: được xử lý 1 người (Q) sau 30 giây đầu. Xử lý nhầm phe mình thì chính bạn bị loại.</p>`;
  $("introCard").innerHTML = `<div class="muted">Vai của bạn</div><div class="big-role ${r.cls}">${esc(roleName(g))}</div><p>${esc(r.goal)}</p>${extra}<p class="muted">(bấm để đóng)</p>`;
  $("roleIntro").classList.remove("hidden");
  $("roleIntro").onclick = () => $("roleIntro").classList.add("hidden");
  setTimeout(() => $("roleIntro").classList.add("hidden"), 7000);
}

function renderTasks() {
  const g = S.g; if (!g) return;
  const names = moduleNames();
  let h = "";
  if (g.role === "tester") h = `<h3>Việc của bạn</h3><div class="t">🔍 Kiểm lỗi ở Khu Tester (E)</div>`;
  else {
    const done = g.tasks.filter((t) => t.done).length;
    h = `<h3>Tài liệu khảo sát ${done}/${g.tasks.length}${g.role === "dev" || g.role === "bh" ? ' <span class="muted">(giả vờ)</span>' : ""}</h3>`;
    for (const t of g.tasks)
      h += `<div class="t ${t.done ? "done" : ""}">${t.done ? "✅" : "📝"} <span><b>${esc(names[t.module] || "")}</b> — ${esc(t.name)} ${t.hint ? '<span class="hintmark" title="Có gợi ý">💡</span>' : ""}</span><span class="p">${t.got}/${t.need}</span></div>`;
  }
  if (g.role === "dev") h += `<div class="t">🐞 Gài lỗi ở Khu Dev (E, 4 giây)</div>`;
  $("taskBox").innerHTML = h;
}

function moduleNames() {
  const g = S.g, n = [];
  for (const r of g.map.rooms) if (r.kind === "module") n[r.module] = r.name;
  return n;
}

function feed(text, kind) {
  const el = document.createElement("div");
  el.className = "f " + (kind || "");
  el.textContent = text;
  $("feed").appendChild(el);
  while ($("feed").children.length > 6) $("feed").firstChild.remove();
  setTimeout(() => el.remove(), 30000);
}

// Ảnh chụp trạng thái 20 lần/giây
function onSnap(m) {
  const g = S.g; if (!g) return;
  g.snap = m;
  g.phase = m.ph;
  const meS = m.me;
  if (g.alive && !meS.alive) g.alive = false;
  // vị trí bản thân: đoán trước + hoà dần với máy chủ
  if (!g.pred) g.pred = { x: meS.x, y: meS.y };
  const d = Math.hypot(g.pred.x - meS.x, g.pred.y - meS.y);
  const locked = m.ph !== "play" || meS.q || meS.act;
  const moving = g.dx !== 0 || g.dy !== 0;
  if (locked || d > 70) { g.pred.x = meS.x; g.pred.y = meS.y; }
  else if (!moving) { g.pred.x += (meS.x - g.pred.x) * 0.35; g.pred.y += (meS.y - g.pred.y) * 0.35; }
  else if (d > 26) { g.pred.x += (meS.x - g.pred.x) * 0.12; g.pred.y += (meS.y - g.pred.y) * 0.12; }
  // người khác: đích mới để nội suy
  const seen = new Set();
  for (const [id, x, y, flags] of m.ps) {
    seen.add(id);
    let o = g.others.get(id);
    if (!o || o.gone) { o = { id, x, y, tx: x, ty: y, walk: 0, face: 1 }; g.others.set(id, o); }
    o.tx = x; o.ty = y; o.flags = flags; o.gone = false;
  }
  for (const o of g.others.values()) if (!seen.has(o.id)) o.gone = true;
  g.bodies = m.bodies.map(([id, x, y]) => ({ id, x, y, color: g.players.get(id)?.color ?? 0 }));
  updateHud();
}

function updateHud() {
  const g = S.g, m = g.snap; if (!m) return;
  $("clock").textContent = fmt(m.clock);
  $("clock").parentElement.classList.toggle("warn", m.clock <= 60);
  const [done, total] = m.prog;
  $("progTxt").textContent = `${done}/${total}`;
  $("progBar").style.width = (total ? (100 * done) / total : 0) + "%";
  $("review").classList.toggle("hidden", m.rv == null);
  if (m.rv != null) $("review").textContent = `📋 Khách hàng đang nghiệm thu… ${Math.ceil(m.rv)}s`;
  const cd = m.cd || {};
  const chips = [];
  if (cd.k != null) chips.push(`Hạ gục: ${cd.k > 0 ? Math.ceil(cd.k) + "s" : "sẵn sàng"}`);
  if (cd.pl != null) chips.push(`Gài lỗi: ${cd.pl > 0 ? Math.ceil(cd.pl) + "s" : "sẵn sàng"}`);
  if (cd.pk != null) chips.push(`Xử lý: ${cd.pk < 0 ? "đã dùng" : cd.pk > 0 ? Math.ceil(cd.pk) + "s" : "sẵn sàng"}`);
  if (cd.bh != null) chips.push(`Report lỗi: ${cd.bh} lượt${cd.bhc > 0 ? " (" + Math.ceil(cd.bhc) + "s)" : ""}`);
  if (cd.e != null) chips.push(`Họp khẩn: ${cd.e}`);
  if (cd.bugs != null) chips.push(`🐞 Lỗi đang mở: ${cd.bugs}`);
  const r = ROLE[g.role];
  $("roleCard").innerHTML = `<div class="rname ${r.cls}">${esc(roleName(g))}${g.alive ? "" : " 👻"}</div><div class="goal">${esc(r.goal)}</div><div class="cds">${chips.map((c) => `<span class="cd">${esc(c)}</span>`).join("")}</div>`;
  $("ghostTag").classList.toggle("hidden", g.alive || m.ph !== "play");
  $("ghostChat").classList.toggle("hidden", g.alive || m.ph !== "play");
  if (g.meeting && m.vt) meetingTimer(m.vt);
}

// ───────────────────────── Hành động theo ngữ cảnh ─────────────────────────
function near(kind, range) {
  const g = S.g, p = g.pred; let best = null, bd = range;
  for (const s of g.map.stations) if (s.kind === kind) { const d = Math.hypot(s.x - p.x, s.y - p.y); if (d <= bd) { bd = d; best = s; } }
  return best;
}
function roomAt(x, y) { for (const r of S.g.map.rooms) { const [rx, ry, rw, rh] = r.rect; if (x >= rx && x <= rx + rw && y >= ry && y <= ry + rh) return r; } return null; }

function context() {
  const g = S.g, m = g.snap, cd = (m && m.cd) || {};
  const res = { use: null, hand: false, bh: false, report: false, kill: null, killLabel: "Hạ gục" };
  if (!m || m.ph !== "play" || !g.pred) return res;
  const t = near("task", USE);
  if (t && g.tasks.some((x) => x.module === t.num && !x.done)) res.use = { a: "task", label: "Khảo sát" };
  if (g.alive) {
    const d = near("dev", USE), te = near("test", USE), b = near("button", BTN);
    if (!res.use && d) res.use = { a: "plant", label: g.role === "dev" ? (cd.pl > 0 ? `Gài lỗi ${Math.ceil(cd.pl)}s` : "Gài lỗi") : "Xem code" };
    if (!res.use && te) res.use = { a: "check", label: "Kiểm lỗi" };
    if (!res.use && b) res.use = { a: "emergency", label: "Họp khẩn" };
    if (b && m.prog[1] > 0 && m.prog[0] >= m.prog[1] && m.rv == null) res.hand = true;
    if (g.role === "bh") { const rm = roomAt(g.pred.x, g.pred.y); res.bh = !!(rm && rm.kind === "module" && cd.bh > 0); }
    res.report = g.bodies.some((b2) => Math.hypot(b2.x - g.pred.x, b2.y - g.pred.y) <= REPORT);
    const canKill = (g.role === "dev" && cd.k === 0) || (g.pm && cd.pk === 0);
    if (g.pm) res.killLabel = "Xử lý";
    if (canKill) {
      let bd = KILL;
      for (const o of g.others.values()) {
        if (o.gone || (o.flags & 1) || (g.role === "dev" && g.mates.has(o.id))) continue;
        const dd = Math.hypot(o.x - g.pred.x, o.y - g.pred.y);
        if (dd <= bd) { bd = dd; res.kill = o.id; }
      }
    }
  }
  return res;
}

function renderActions(c) {
  const g = S.g, cd = (g.snap && g.snap.cd) || {};
  const setBtn = (el, visible, ready, label) => {
    el.classList.toggle("hidden", !visible); el.classList.toggle("ready", !!ready); el.disabled = !ready;
    if (label != null) el.querySelector("span").textContent = label;
  };
  setBtn($("aUse"), true, !!c.use, c.use ? c.use.label : "Dùng");
  setBtn($("aHand"), g.alive, c.hand);
  setBtn($("aRep"), g.alive, c.report);
  setBtn($("aBh"), g.role === "bh" && g.alive, c.bh);
  const killVis = g.alive && (g.role === "dev" || (g.pm && cd.pk !== -1));
  setBtn($("aKill"), killVis, c.kill != null, c.killLabel + (g.role === "dev" && cd.k > 0 ? ` ${Math.ceil(cd.k)}s` : ""));
  $("aKill").classList.add("red");
}

function doAction(a) {
  const g = S.g; if (!g || g.phase !== "play" || g.q) return;
  const c = context();
  if (a === "use" && c.use) { send({ t: c.use.a }); Sfx.click(); }
  else if (a === "report" && c.report) send({ t: "report" });
  else if (a === "kill" && c.kill != null) send({ t: "kill", target: c.kill });
  else if (a === "handover" && c.hand) send({ t: "handover" });
  else if (a === "bh" && c.bh) send({ t: "bhreport" });
}
for (const b of document.querySelectorAll(".actions button")) b.onclick = () => doAction(b.dataset.a);

// ───────────────────────── Câu hỏi ─────────────────────────
function optsHtml(q, name) {
  return q.opts.map((o) => `<label class="opt" data-i="${o.i}"><input type="checkbox" name="${name}" value="${o.i}"><span class="k">${LETTERS[o.i]}</span><span>${esc(o.text)}</span></label>`).join("");
}

function bindOpts(root, onChange) {
  for (const l of root.querySelectorAll(".opt")) {
    const cb = l.querySelector("input");
    cb.onchange = () => { l.classList.toggle("sel", cb.checked); onChange(); };
  }
}

function openQuestion(q) {
  const g = S.g;
  g.q = q;
  sendInput(0, 0, true);
  $("qCard").innerHTML = `<div><span class="qt">${esc(q.title)}</span><span class="qs">${esc(q.sub || "")}</span></div>
    <div class="qtext">${esc(q.text)}</div>${q.menu ? `<div class="qmenu">Menu: ${esc(q.menu)}</div>` : ""}
    ${q.hint ? `<div class="qhint">💡 Gợi ý từ khách hàng: đã loại bớt phương án sai.</div>` : ""}
    <div id="qOpts">${optsHtml(q, "qo")}</div>
    <div class="qfoot"><button id="qSubmit" class="primary" disabled>Nộp (Enter)</button><button id="qClose">Đóng (Esc)</button>
    <span class="note">Có thể có nhiều đáp án đúng — chọn đủ mới tính đúng.</span></div>`;
  $("qModal").classList.remove("hidden");
  bindOpts($("qCard"), () => { $("qSubmit").disabled = !$("qCard").querySelector("input:checked"); });
  $("qSubmit").onclick = () => submitQ(q, $("qCard"));
  $("qClose").onclick = closeQuestion;
}

function submitQ(q, root) {
  const picks = [...root.querySelectorAll("input:checked")].map((i) => +i.value);
  if (!picks.length || q.answered) return;
  q.answered = true; q.picks = picks;
  send({ t: "answer", token: q.token, picks });
  for (const i of root.querySelectorAll("input")) i.disabled = true;
}

function closeQuestion() {
  const g = S.g; if (!g || !g.q) return;
  if (!g.q.answered) send({ t: "closeq" });
  g.q = null;
  $("qModal").classList.add("hidden");
}

function markResult(root, q, res) {
  for (const l of root.querySelectorAll(".opt")) {
    const i = +l.dataset.i;
    if (res.correct.includes(i)) l.classList.add("right");
    else if (q.picks && q.picks.includes(i)) l.classList.add("wrong");
  }
}

function onQres(m) {
  const g = S.g; if (!g) return;
  if (g.q && g.q.token === m.token) {
    markResult($("qCard"), g.q, m);
    (m.ok ? (m.done ? Sfx.done : Sfx.ok) : Sfx.wrong)();
    const foot = $("qCard").querySelector(".qfoot");
    foot.innerHTML = `<span class="qres ${m.ok ? "ok" : "no"}">${m.ok ? (m.done ? "✅ Đúng — xong tài liệu!" : `✔ Đúng (${m.got}/${m.need})`) : "✖ Chưa đúng — xem đáp án đúng (màu xanh). Chờ 3 giây rồi làm lại."}</span>
      <span class="spacer"></span>${m.more && m.ok ? '<button id="qNext" class="primary">Câu tiếp (E)</button>' : ""}<button id="qClose2">Đóng (Esc)</button>`;
    g.q.done = true;
    if ($("qNext")) $("qNext").onclick = () => { closeQuestion(); send({ t: "task" }); };
    $("qClose2").onclick = closeQuestion;
  } else if (g.msg && g.msg.token === m.token) {
    markResult($("msgBox"), g.msg, m);
    (m.ok ? Sfx.ok : Sfx.wrong)();
    $("msgFoot").innerHTML = `<span class="qres ${m.ok ? "ok" : "no"}">${esc(m.msg || "")}</span>`;
    g.msg.done = true;
    const tok = m.token;
    setTimeout(() => { if (S.g && S.g.msg && S.g.msg.token === tok) { S.g.msg = null; $("msgBox").classList.add("hidden"); } }, 5000);
  }
}

function openMsg(q) {
  const g = S.g;
  g.msg = q; q.until = performance.now() + q.limit * 1000;
  const box = $("msgBox");
  box.innerHTML = `<div class="mh">${esc(q.title)} <span class="muted" style="font-weight:400">· ${esc(q.sub || "")}</span><span class="tl" id="msgTl"></span></div>
    <div class="bubble">${esc(q.text)}</div><div>${optsHtml(q, "mo")}</div>
    <div class="qfoot" id="msgFoot"><button id="msgSend" class="primary" disabled>Trả lời</button><button id="msgLater">Bỏ qua</button></div>`;
  box.classList.remove("hidden");
  bindOpts(box, () => { $("msgSend").disabled = !box.querySelector("input:checked"); });
  $("msgSend").onclick = () => submitQ(q, box);
  $("msgLater").onclick = () => { g.msg = null; box.classList.add("hidden"); };
  Sfx.msg();
}

// ───────────────────────── Họp ─────────────────────────
function openMeeting(m) {
  const g = S.g;
  closeQuestion();
  g.msg = null; $("msgBox").classList.add("hidden"); $("bhPrompt").classList.add("hidden");
  g.meeting = { alive: new Set(m.alive), voted: new Set(), my: null };
  g.others.clear();
  $("mtTitle").textContent = m.reason === "body" ? `🚨 ${m.byName} phát hiện ${m.bodyName} bị hạ gục!` : `📣 ${m.byName} gọi họp khẩn`;
  $("mtSub").textContent = g.alive ? "Thảo luận bằng chat, hết giờ thảo luận thì bỏ phiếu. Chưa chắc thì bỏ phiếu trắng." : "Bạn là ma — chỉ xem, chat của bạn chỉ ma khác thấy.";
  $("mtChat").innerHTML = "";
  addChat($("mtChat"), { sys: true, text: "Cuộc họp bắt đầu." });
  $("mtChatIn").placeholder = g.alive ? "Thảo luận… (Enter)" : "Ma chỉ chat với ma… (Enter)";
  renderVoteGrid();
  $("meet").classList.remove("hidden");
  Sfx.meeting();
  setTimeout(() => $("mtChatIn").focus(), 50);
}

function renderVoteGrid() {
  const g = S.g, mt = g.meeting;
  const grid = $("mtGrid");
  grid.innerHTML = "";
  for (const p of g.players.values()) {
    const alive = mt.alive.has(p.id);
    const el = document.createElement("div");
    el.className = "vcard" + (alive ? "" : " dead") + (p.id === S.me ? " me" : "") + (mt.my === p.id ? " picked" : "");
    const cv = document.createElement("canvas");
    el.appendChild(cv);
    const nm = document.createElement("div");
    const mate = g.mates.has(p.id);
    nm.innerHTML = `<div style="font-weight:600;${mate ? "color:var(--bad)" : ""}">${esc(p.name)}${p.id === S.me ? " (bạn)" : ""}</div><div class="muted" style="font-size:.78rem">${alive ? R.COLOR_NAMES[p.color % 8] : "đã bị loại"}</div>`;
    el.appendChild(nm);
    if (mt.voted.has(p.id)) { const vb = document.createElement("span"); vb.className = "vb"; vb.textContent = "✔ đã bầu"; el.appendChild(vb); }
    if (alive) el.onclick = () => vote(p.id);
    grid.appendChild(el);
    R.avatar(cv, p.color, !alive);
  }
}

function vote(target) {
  const g = S.g, mt = g.meeting;
  if (!mt || !g.alive || mt.my != null || !g.snap || !g.snap.vt || g.snap.vt[0] > 0) {
    if (g.snap && g.snap.vt && g.snap.vt[0] > 0) toast("Chưa hết giờ thảo luận.");
    return;
  }
  mt.my = target;
  send({ t: "vote", target });
  Sfx.vote();
  renderVoteGrid();
  $("mtNote").textContent = target === -1 ? "Bạn đã bỏ phiếu trắng." : `Bạn đã bầu ${g.players.get(target)?.name}.`;
}
$("btnSkip").onclick = () => vote(-1);

function meetingTimer(vt) {
  const [dLeft, vLeft] = vt;
  $("mtTimer").textContent = dLeft > 0 ? `💬 Thảo luận ${fmt(dLeft)}` : `🗳 Bỏ phiếu ${fmt(vLeft)}`;
  $("btnSkip").disabled = dLeft > 0 || !S.g.alive || S.g.meeting.my != null;
  if (!S.g.meeting.my && dLeft <= 0 && S.g.alive) $("mtNote").textContent = "Bấm vào một người để bầu loại.";
}

function showResult(m) {
  const g = S.g;
  g.meeting = null;
  $("meet").classList.add("hidden");
  const tally = new Map();
  for (const v of m.votes) { const k = v.to ?? -1; if (!tally.has(k)) tally.set(k, []); tally.get(k).push(v.from); }
  const dots = (ids) => ids.map((id) => `<i style="background:${R.COLORS[(g.players.get(id)?.color ?? 0) % 8]}" title="${esc(g.players.get(id)?.name)}"></i>`).join("");
  let rows = "";
  for (const [k, ids] of tally) rows += `<div class="vcard" style="cursor:default"><div>${k === -1 ? "⬜ Phiếu trắng" : esc(g.players.get(k)?.name)}</div><span class="vs">${dots(ids)}</span></div>`;
  $("resultCard").innerHTML = m.ejected != null
    ? `<h2>${esc(m.name)} bị loại khỏi dự án</h2><p style="font-size:1.2rem" class="${m.dev ? "c-good" : "c-bad"}">${m.dev ? "…và đúng là Lập trình gài lỗi! 😈" : "…nhưng KHÔNG phải Lập trình 😰"}</p>`
    : `<h2>Không ai bị loại</h2><p class="muted">Phiếu trắng nhiều nhất hoặc hoà phiếu.</p>`;
  $("resultCard").innerHTML += `<div class="vote-grid" style="padding:0;margin-top:12px">${rows || '<span class="muted">Không ai bỏ phiếu.</span>'}</div>`;
  $("resultBox").classList.remove("hidden");
}

function showOver(m) {
  const g = S.g;
  closeQuestion();
  for (const id of ["meet", "resultBox", "msgBox", "bhPrompt"]) $(id).classList.add("hidden");
  const myRole = m.roles.find((r) => r.id === S.me);
  const iWin = myRole && ((m.winner === "good") === (myRole.role === "tk" || myRole.role === "tester"));
  (iWin ? Sfx.win : Sfx.lose)();
  const roles = m.roles.map((r) => `<div><canvas data-c="${r.color}" data-d="${r.alive ? 0 : 1}"></canvas><span><b>${esc(r.name)}</b> — <span class="${ROLE[r.role].cls}">${r.pm ? "Trưởng dự án" : ROLE[r.role].name}</span> <span class="muted">${r.left ? "(rời phòng)" : r.alive ? "" : "(bị loại)"}</span></span></div>`).join("");
  $("overCard").innerHTML = `<h2 class="${m.winner === "good" ? "c-good" : "c-bad"}">${m.winner === "good" ? "🎉 ĐỘI DỰ ÁN THẮNG" : "😈 PHE GÀI LỖI THẮNG"}</h2>
    <p>${esc(m.reason)}</p><p style="font-size:1.15rem"><b>${iWin ? "Bạn thắng!" : "Bạn thua…"}</b></p><div class="roles">${roles}</div>
    ${S.host ? '<button id="btnAgain" class="primary">Về phòng chờ — chơi ván mới</button> ' : '<span class="muted">Chờ chủ phòng mở ván mới… </span>'}<button id="btnQuit">Rời phòng</button>`;
  $("overBox").classList.remove("hidden");
  for (const cv of $("overCard").querySelectorAll("canvas")) R.avatar(cv, +cv.dataset.c, cv.dataset.d === "1");
  if ($("btnAgain")) $("btnAgain").onclick = () => send({ t: "lobby" });
  $("btnQuit").onclick = () => post({ t: "ui.leave" });
  if (g) g.phase = "over";
}

// chat trong họp / chat ma
function chatKey(e) { if (e.key === "Enter" && e.target.value.trim()) { send({ t: "chat", text: e.target.value }); e.target.value = ""; } if (e.key === "Escape") e.target.blur(); }
$("mtChatIn").onkeydown = chatKey;
$("gcIn").onkeydown = chatKey;

// ───────────────────────── Bàn phím & di chuyển ─────────────────────────
const KEYMAP = { KeyW: "u", ArrowUp: "u", KeyS: "d", ArrowDown: "d", KeyA: "l", ArrowLeft: "l", KeyD: "r", ArrowRight: "r" };
function typing() { const a = document.activeElement; return a && (a.tagName === "INPUT" && a.type !== "checkbox" || a.tagName === "TEXTAREA"); }

function sendInput(dx, dy, force) {
  const g = S.g; if (!g) return;
  if (!force && dx === g.dx && dy === g.dy) return;
  g.dx = dx; g.dy = dy;
  send({ t: "input", dx, dy });
  g.lastInput = performance.now();
}

function recomputeInput() {
  const g = S.g; if (!g) return;
  if (g.q || g.phase !== "play" || typing()) { sendInput(0, 0); return; }
  const k = g.keys;
  let dx = (k.r ? 1 : 0) - (k.l ? 1 : 0), dy = (k.d ? 1 : 0) - (k.u ? 1 : 0);
  if (dx && dy) { dx *= Math.SQRT1_2; dy *= Math.SQRT1_2; }
  sendInput(dx, dy);
}

document.addEventListener("keydown", (e) => {
  const g = S.g;
  if (!g || S.screen !== "game") return;
  if (e.key === "Escape" || e.code === "Escape") { if (g.q) closeQuestion(); else if (!$("rules").classList.contains("hidden")) $("rules").classList.add("hidden"); return; }
  if (typing()) return;
  if (g.q) {
    if ((e.key === "Enter" || e.code === "Enter" || e.code === "NumpadEnter") && !g.q.answered && $("qSubmit") && !$("qSubmit").disabled) $("qSubmit").click();
    else if (e.code === "KeyE" && $("qNext")) $("qNext").click();
    else if (/^Digit[1-5]$/.test(e.code) || /^Key[A-E]$/.test(e.code) && e.code !== "KeyE") {
      const i = /^Digit/.test(e.code) ? +e.code.slice(5) - 1 : e.code.charCodeAt(3) - 65;
      const cb = $("qCard").querySelector(`.opt[data-i="${i}"] input`);
      if (cb && !cb.disabled) { cb.checked = !cb.checked; cb.onchange(); }
    }
    return;
  }
  if (KEYMAP[e.code]) { g.keys[KEYMAP[e.code]] = true; recomputeInput(); e.preventDefault(); return; }
  if (e.repeat) return;
  switch (e.code) {
    case "KeyE": doAction("use"); break;
    case "KeyR": doAction("report"); break;
    case "KeyQ": doAction("kill"); break;
    case "KeyH": doAction("handover"); break;
    case "KeyB": doAction("bh"); break;
    case "KeyM": $("mini").classList.toggle("hidden"); break;
    case "Enter":
      if (g.meeting) $("mtChatIn").focus();
      else if (!g.alive) $("gcIn").focus();
      break;
  }
});
document.addEventListener("keyup", (e) => { const g = S.g; if (g && KEYMAP[e.code]) { g.keys[KEYMAP[e.code]] = false; recomputeInput(); } });
window.addEventListener("blur", () => { if (S.g) { S.g.keys = {}; recomputeInput(); } });
document.addEventListener("focusin", () => recomputeInput());

// Va chạm phía khách — CHÍNH luật của máy chủ (GameMap.CanStand / Move) để đoán trước bước đi
function pointFree(map, x, y) { for (const r of map.walk) if (x >= r[0] && x <= r[0] + r[2] && y >= r[1] && y <= r[1] + r[3]) return true; return false; }
function canStand(map, x, y) { const h = map.half; return pointFree(map, x - h, y - h) && pointFree(map, x + h, y - h) && pointFree(map, x - h, y + h) && pointFree(map, x + h, y + h); }
function moveOn(map, x, y, dx, dy) {
  const steps = Math.max(1, Math.ceil(Math.max(Math.abs(dx), Math.abs(dy)) / 8));
  const sx = dx / steps, sy = dy / steps;
  for (let i = 0; i < steps; i++) { if (canStand(map, x + sx, y)) x += sx; if (canStand(map, x, y + sy)) y += sy; }
  return [x, y];
}

// ───────────────────────── Vòng vẽ ─────────────────────────
const cv = $("cv"), ctx = cv.getContext("2d"), mini = $("mini"), mctx = mini.getContext("2d");
function resize() {
  const d = window.devicePixelRatio || 1;
  cv.width = Math.round(cv.clientWidth * d); cv.height = Math.round(cv.clientHeight * d);
  mini.width = Math.round(mini.clientWidth * d); mini.height = Math.round(mini.clientHeight * d);
}
window.addEventListener("resize", resize);

let last = performance.now(), stepAcc = 0;
function frame(now) {
  requestAnimationFrame(frame);
  const dt = Math.min(0.05, (now - last) / 1000); last = now;
  const g = S.g;
  if (!g || S.screen !== "game" || !g.pred) return;
  const m = g.snap;
  // đoán trước bước đi của bản thân
  if (m && m.ph === "play" && !m.me.q && !m.me.act && !g.q && (g.dx || g.dy)) {
    const step = SPEED * dt;
    const ox = g.pred.x;
    if (g.alive) [g.pred.x, g.pred.y] = moveOn(g.map, g.pred.x, g.pred.y, g.dx * step, g.dy * step);
    else { g.pred.x = Math.min(g.map.w - 20, Math.max(20, g.pred.x + g.dx * step * 1.15)); g.pred.y = Math.min(g.map.h - 20, Math.max(20, g.pred.y + g.dy * step * 1.15)); }
    g.walk += step * 0.25;
    if (g.dx) g.face = g.dx > 0 ? 1 : -1;
    stepAcc += step; if (g.alive && stepAcc > 60) { stepAcc = 0; Sfx.step(); }
    if (Math.abs(g.pred.x - ox) < 0.01 && g.dx) g.walk -= step * 0.25;
  }
  // nội suy người khác
  const others = [];
  for (const o of g.others.values()) {
    if (o.gone) continue;
    const k = Math.min(1, dt * 14);
    const ox = o.x, oy = o.y;
    o.x += (o.tx - o.x) * k; o.y += (o.ty - o.y) * k;
    const mv = Math.hypot(o.x - ox, o.y - oy);
    if (mv > 0.3) { o.walk += mv * 0.25; if (Math.abs(o.x - ox) > 0.2) o.face = o.x > ox ? 1 : -1; }
    const p = g.players.get(o.id) || { name: "?", color: 0 };
    others.push({ x: o.x, y: o.y, color: p.color, name: p.name, ghost: !!(o.flags & 1), flags: o.flags, walk: mv > 0.3 ? o.walk : 0, face: o.face, mate: g.mates.has(o.id) });
  }
  const meP = me() || { name: "", color: 0 };
  const W = cv.width, H = cv.height, d = window.devicePixelRatio || 1;
  const scale = Math.max(0.55, Math.min(2.4, Math.min(W / d / 1150, H / d / 780))) * d;
  // trạm cần chú ý
  const hl = new Set(), hlRooms = new Set();
  const todo = new Set(g.tasks.filter((t) => !t.done).map((t) => t.module));
  for (const s of g.map.stations) {
    if (s.kind === "task" && todo.has(s.num)) { hl.add(s.id); hlRooms.add(s.room); }
    if (g.alive && s.kind === "dev" && g.role === "dev") hl.add(s.id);
    if (g.alive && s.kind === "test" && (g.role === "tester")) hl.add(s.id);
    if (g.alive && s.kind === "button" && m && m.prog[1] > 0 && m.prog[0] >= m.prog[1]) hl.add(s.id);
  }
  R.render(ctx, W, H, {
    map: g.map, cam: { x: g.pred.x, y: g.pred.y, scale }, t: now / 1000, vision: VISION, hl,
    me: { x: g.pred.x, y: g.pred.y, alive: g.alive, color: meP.color, name: meP.name, walk: g.dx || g.dy ? g.walk : 0, face: g.face, act: m && m.me.act },
    others, bodies: g.bodies,
  });
  if (!mini.classList.contains("hidden")) R.mini(mctx, mini.width, mini.height, { map: g.map, me: { x: g.pred.x, y: g.pred.y, color: meP.color }, hlRooms });
  if (m && m.me.act) {
    // thanh tiến độ khi đang gõ / test
    const pct = 1 - m.me.actLeft / (m.me.actTotal || 1);
    ctx.fillStyle = "rgba(0,0,0,.6)"; ctx.fillRect(W / 2 - 80 * d, H / 2 + 40 * d, 160 * d, 10 * d);
    ctx.fillStyle = "#ffcc4d"; ctx.fillRect(W / 2 - 80 * d, H / 2 + 40 * d, 160 * d * Math.max(0, Math.min(1, pct)), 10 * d);
  }
  if (g.msg && !g.msg.done && $("msgTl")) $("msgTl").textContent = Math.max(0, Math.ceil((g.msg.until - now) / 1000)) + "s";
  renderActions(context());
  // giữ kết nối: gửi lại hướng đi mỗi 0,5 giây khi đang đi
  if ((g.dx || g.dy) && now - g.lastInput > 500) sendInput(g.dx, g.dy, true);
}
requestAnimationFrame(frame);

// ───────────────────────── Tin đến ─────────────────────────
function handle(m) {
  const g = S.g;
  switch (m.t) {
    case "ui.init":
      S.name = m.name;
      $("inName").value = m.name; $("inRoom").value = m.room; $("inIp").value = m.lastIp || "";
      S.ips = m.ips;
      $("ver").textContent = `Đêm Go-Live v${m.version} · máy ${m.machine} · IP: ${m.ips.join(", ") || "?"}`;
      if (m.auto === "host") post({ t: "ui.host", name: m.name, room: m.autoRoom || m.room });
      else if (m.auto === "join") post({ t: "ui.join", name: m.name, ip: m.autoJoin });
      else post({ t: "ui.find" });
      break;
    case "ui.rooms": renderRooms(m.rooms || [], m.error); break;
    case "ui.error":
      if (S.screen === "lobby") $("lbErr").textContent = m.text; else $("homeErr").textContent = m.text;
      break;
    case "ui.hosted":
      S.port = m.port; S.ips = m.ips;
      $("lbAddr").textContent = `Người khác vào bằng danh sách phòng, hoặc gõ IP: ${m.ips.map((ip) => ip + (m.port !== 47811 ? ":" + m.port : "")).join("  ·  ") || "(không có IP LAN)"}`;
      if (m.warn) $("lbErr").textContent = m.warn;
      $("lbFw").classList.add("hidden");
      break;
    case "ui.fw":
      $("lbFw").classList.remove("hidden");
      $("lbFw").dataset.level = m.level;
      $("lbFwText").textContent = (m.level === "ok" ? "✅ " : m.level === "bad" ? "⛔ " : "⚠ ") + m.text;
      $("btnFw").disabled = !!m.busy;
      $("btnFw").textContent = m.level === "ok" ? "🛡 Mở lại tường lửa" : "🛡 Mở tường lửa";
      break;
    case "ui.bank":
      if (m.loading) $("bankBox").textContent = `Đang đọc ${m.file}…`;
      else if (!m.ok) $("lbErr").textContent = m.error;
      else $("lbErr").textContent = "";
      break;
    case "ui.left":
      closeGame(); S.host = false; S.port = 0; show("home");
      break;
    case "ui.closed":
      closeGame(); S.host = false; show("home");
      $("homeErr").textContent = S.rejected || "Mất kết nối với phòng: " + m.reason;
      S.rejected = null;
      break;
    case "welcome":
      S.me = m.id; S.host = m.host; S.name = m.name;
      if (!S.host) { $("lbAddr").textContent = ""; $("lbFw").classList.add("hidden"); }
      $("lbChat").innerHTML = ""; $("lbErr").textContent = "";
      show("lobby");
      break;
    case "reject":
      closeGame(); show("home");
      $("homeErr").textContent = S.rejected = m.reason;
      break;
    case "lobby": renderLobby(m); break;
    case "start": startGame(m); break;
    case "s": onSnap(m); break;
    case "tasks": if (g) { g.tasks = m.tasks; renderTasks(); } break;
    case "q": if (g) { if (m.kind === "msg") openMsg(m); else openQuestion(m); } break;
    case "qres": onQres(m); break;
    case "qclose":
      if (g && g.q && g.q.token === m.token) { g.q.answered = true; closeQuestion(); }
      if (g && g.msg && g.msg.token === m.token) { g.msg = null; $("msgBox").classList.add("hidden"); if (m.text) toast(m.text); }
      break;
    case "toast":
      if (S.screen === "lobby") $("lbErr").textContent = m.text; else toast(m.text, 3200);
      if (/gài lỗi/.test(m.text)) Sfx.bug();
      break;
    case "feed": feed(m.text, m.kind); if (m.kind === "clue") Sfx.clue(); break;
    case "dead": if (g) { g.alive = false; toast(m.text, 5000); Sfx.dead(); } break;
    case "meeting": if (g) openMeeting(m); break;
    case "voted": if (g && g.meeting) { g.meeting.voted.add(m.id); renderVoteGrid(); Sfx.vote(); } break;
    case "chat":
      if (S.screen === "lobby" || !g) addChat($("lbChat"), m);
      else if (g.meeting) addChat($("mtChat"), m);
      else if (m.ghost) addChat($("gcLog"), m);
      break;
    case "result": if (g) showResult(m); break;
    case "resume": $("resultBox").classList.add("hidden"); if (g) { g.others.clear(); g.pred = null; } break;
    case "bhprompt":
      if (g) {
        $("bhPrompt").classList.remove("hidden");
        const end = performance.now() + m.secs * 1000;
        const tick = () => { const left = (end - performance.now()) / 1000; $("bhSecs").textContent = Math.max(0, Math.ceil(left)) + "s"; if (left > 0 && !$("bhPrompt").classList.contains("hidden")) setTimeout(tick, 250); else $("bhPrompt").classList.add("hidden"); };
        tick();
        $("btnBhReject").onclick = () => { send({ t: "bhreject" }); $("bhPrompt").classList.add("hidden"); };
      }
      break;
    case "over": showOver(m); break;
  }
}

if (wv) {
  wv.addEventListener("message", (e) => { try { handle(e.data); } catch (err) { console.error(err); } });
  post({ t: "ui.init" });
} else {
  $("homeErr").textContent = "Trang này chạy trong cửa sổ game của Bcode (WebView2) — mở từ Bcode → Giải trí → Giải lao.";
}
