"use strict";
// Rạp Bcode — trang chính. Nói với C# (WatchForm) bằng chrome.webview.postMessage:
//   { t:"ui.*" } xử lý ở C#; { t:"net", m:{...} } chuyển nguyên văn cho máy chủ phòng.
// Máy chủ phòng giữ trạng thái chiếu THẬT (state: cur / playing / pos); trang chỉ BÁM THEO: lệch > ~1 s thì tua, lệch nhỏ chỉnh tốc độ phát.
// Chỉ chủ phòng gửi lệnh điều khiển (play / pause / seek / select / next / prev / remove / move / approve / ended).

const $ = (id) => document.getElementById(id);
const wv = window.chrome && window.chrome.webview;
const post = (m) => wv && wv.postMessage(m);
const send = (m) => post({ t: "net", m });
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const fmt = (sec) => {
  if (!isFinite(sec) || sec < 0) sec = 0;
  sec = Math.floor(sec);
  const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
  return (h ? h + ":" + String(m).padStart(2, "0") : m) + ":" + String(s).padStart(2, "0");
};
const REACTS = ["😂", "👏", "❤️", "😮", "😢", "🔥", "👍", "🎉"];

const S = {
  screen: "home", me: 0, host: false, name: "", room: "", hostAddr: "127.0.0.1", mediaPort: 0,
  st: null, recv: 0, viewers: [], mode: "theater", side: true, danmaku: true, volume: 0.8, muted: false,
};

function show(name) {
  S.screen = name;
  $("scrHome").classList.toggle("hidden", name !== "home");
  $("scrRoom").classList.toggle("hidden", name !== "room");
  if (name === "home") post({ t: "ui.find" }); else post({ t: "ui.stopfind" });
}

let toastTimer = 0;
function toast(text, ms = 2600) {
  const t = $("toast"); t.textContent = text; t.classList.remove("hidden");
  clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.add("hidden"), ms);
}

// ═══════════════════════ Màn chính ═══════════════════════

function renderRooms(rooms, error) {
  $("findErr").textContent = error || "";
  const ul = $("roomList");
  if (!rooms.length) { ul.innerHTML = `<li class="muted">Đang tìm phòng… (chưa thấy phòng chiếu nào)</li>`; return; }
  ul.innerHTML = rooms.map((r, i) => `<li><div class="ri"><div class="rn">${esc(r.name)}</div><div class="rm">${esc(r.host)} · ${esc(r.ip)}${r.port !== 47831 ? ":" + r.port : ""} · ${r.players}/${r.max} người · ${esc(r.state)}</div></div><button data-i="${i}">Vào xem</button></li>`).join("");
  ul.querySelectorAll("button").forEach((b) => b.onclick = () => { const r = rooms[+b.dataset.i]; join(r.ip + (r.port !== 47831 ? ":" + r.port : "")); });
}
function join(ip) { $("homeErr").textContent = ""; post({ t: "ui.join", name: $("inName").value, ip }); }
$("btnJoinIp").onclick = () => join($("inIp").value);
$("inIp").onkeydown = (e) => { if (e.key === "Enter") join($("inIp").value); };
$("btnHost").onclick = () => { $("homeErr").textContent = ""; post({ t: "ui.host", name: $("inName").value, room: $("inRoom").value }); };

// ═══════════════════════ Trình phát (video / audio / YouTube) ═══════════════════════

const V = $("vid");
const P = { item: null, kind: null, ready: false, err: null, ytApi: false, yt: null, ytReady: false, metaSent: false, endSent: false, seekGuard: 0, needClick: false, lastSeekReq: 0 };

function mediaUrl(item) {
  if (item.kind === "url") return item.src;
  if (item.kind === "file") return `http://${S.hostAddr.includes(":") && !S.hostAddr.startsWith("[") ? "[" + S.hostAddr + "]" : S.hostAddr}:${S.mediaPort}/m/${encodeURIComponent(item.src)}`;
  return "";
}
const isAudio = (item) => /\.(mp3|m4a|aac|wav|ogg|oga|opus|flac)(\?|$)/i.test(item.kind === "file" ? item.title : item.src);

function loadYtApi() {
  if (P.ytApi) return;
  P.ytApi = true;
  window.onYouTubeIframeAPIReady = () => { P.ytApiReady = true; if (P.kind === "yt" && P.item) ytCreate(P.item.src); };
  const s = document.createElement("script");
  s.src = "https://www.youtube.com/iframe_api";
  s.onerror = () => { P.ytApi = false; playerError("Không tải được trình phát YouTube — máy này có vào được Internet / youtube.com không?"); };
  document.head.appendChild(s);
}

function ytCreate(id) {
  if (P.yt) { P.ytReady = false; P.yt.cueVideoById(id); P.ytReady = true; P.ready = true; return; }
  P.yt = new YT.Player("yt", {
    videoId: id, width: "100%", height: "100%",
    playerVars: { controls: 0, disablekb: 1, rel: 0, modestbranding: 1, playsinline: 1, iv_load_policy: 3, fs: 0, autoplay: 0, origin: location.origin },
    events: {
      onReady: () => { P.ytReady = true; P.ready = true; applyVolume(); },
      onStateChange: (e) => { if (e.data === 0) onEnded(); if (e.data === 1) clearErr(); },
      onError: (e) => {
        const why = { 2: "link video không hợp lệ", 5: "trình phát HTML5 lỗi", 100: "video không tồn tại / đã bị xoá / riêng tư", 101: "chủ video không cho phát nhúng", 150: "chủ video không cho phát nhúng", 153: "YouTube từ chối trình phát nhúng (thiếu thông tin trang)" }[e.data] || "mã lỗi " + e.data;
        playerError(`YouTube không phát được: ${why}.`, `https://www.youtube.com/watch?v=${P.item?.src}`);
      },
    },
  });
}

function unload() {
  P.item = null; P.kind = null; P.ready = false; P.metaSent = false; P.endSent = false;
  try { V.pause(); } catch { }
  V.removeAttribute("src"); try { V.load(); } catch { }
  if (P.yt && P.ytReady) try { P.yt.stopVideo(); } catch { }
  $("ytWrap").classList.add("hidden"); $("audioArt").classList.add("hidden");
  clearErr();
}

function load(item) {
  unload();
  P.item = item; P.kind = item.kind === "yt" ? "yt" : "media";
  $("idle").classList.add("hidden");
  if (P.kind === "yt") {
    V.classList.add("hidden");
    $("ytWrap").classList.remove("hidden");
    if (window.YT && YT.Player) ytCreate(item.src); else loadYtApi();
  } else {
    V.classList.remove("hidden");
    V.src = mediaUrl(item);
    applyVolume();
    if (isAudio(item)) { $("audioTitle").textContent = item.title; $("audioArt").classList.remove("hidden"); }
  }
}

// File WebM quay bằng trình duyệt / ghi màn hình thường không ghi độ dài (duration = Infinity) → tua tới cuối để trình duyệt tính ra, rồi về 0.
V.addEventListener("loadedmetadata", () => {
  if (V.duration === Infinity) { P.fixDur = true; V.currentTime = 1e101; return; }
  P.ready = true;
});
V.addEventListener("durationchange", () => {
  if (P.fixDur && isFinite(V.duration)) { P.fixDur = false; V.currentTime = 0; P.ready = true; }
});
V.addEventListener("ended", () => onEnded());
V.addEventListener("seeked", () => { P.seekGuard = 0; });
V.addEventListener("error", () => {
  if (!P.item || P.kind !== "media") return;
  const code = V.error?.code;
  const why = code === 4 ? "định dạng không phát được (MKV / AVI / HEVC thường không chạy — đổi sang MP4 H.264 hoặc WebM)" : code === 2 ? "lỗi mạng khi tải" : "không đọc được file";
  playerError(`Không phát được "${P.item.title}": ${why}.`, P.item.kind === "url" ? P.item.src : null);
});

function playerError(text, openUrl) {
  P.err = text;
  const box = $("perr");
  box.innerHTML = `<div>⚠ ${esc(text)}</div>` + (openUrl ? `<button id="btnOpenExt">Mở bằng trình duyệt</button>` : "") + (S.host ? `<div class="muted">Chủ phòng có thể bấm ⏭ để chuyển bài.</div>` : "");
  box.classList.remove("hidden");
  if (openUrl) $("btnOpenExt").onclick = () => post({ t: "ui.open", url: openUrl });
}
function clearErr() { P.err = null; $("perr").classList.add("hidden"); }

function onEnded() {
  if (!S.host || !P.item || P.endSent) return;
  P.endSent = true;
  send({ t: "ended", id: P.item.id });
}

const pTime = () => P.kind === "yt" ? (P.ytReady ? P.yt.getCurrentTime() || 0 : 0) : V.currentTime || 0;
const pDur = () => P.kind === "yt" ? (P.ytReady ? P.yt.getDuration() || 0 : 0) : (isFinite(V.duration) ? V.duration : 0);
const pPaused = () => P.kind === "yt" ? (P.ytReady ? ![1, 3].includes(P.yt.getPlayerState()) : true) : V.paused;
function pPlay() {
  if (P.kind === "yt") { P.yt.playVideo(); return; }
  const pr = V.play();
  if (pr && pr.catch) pr.catch((e) => { if (e && e.name === "NotAllowedError") { P.needClick = true; $("needClick").classList.remove("hidden"); } });
}
function pPause() { if (P.kind === "yt") P.yt.pauseVideo(); else V.pause(); }
function pSeek(t) { P.seekGuard = performance.now() + 1500; if (P.kind === "yt") P.yt.seekTo(t, true); else V.currentTime = t; }
function pRate(r) { if (P.kind === "media" && Math.abs(V.playbackRate - r) > 0.001) V.playbackRate = r; }
function applyVolume() {
  const v = S.muted ? 0 : S.volume;
  V.volume = v; V.muted = S.muted;
  if (P.yt && P.ytReady) { P.yt.setVolume(Math.round(v * 100)); if (S.muted) P.yt.mute(); else P.yt.unMute(); }
  $("btnMute").textContent = S.muted || v === 0 ? "🔇" : v < 0.4 ? "🔉" : "🔊";
}
$("needClick").onclick = () => { P.needClick = false; $("needClick").classList.add("hidden"); pPlay(); };

/** Vị trí chủ phòng lúc này = pos nhận được + thời gian trôi từ lúc nhận (LAN trễ ~1 ms, bỏ qua). */
function expected() {
  const st = S.st; if (!st) return 0;
  return st.playing ? st.pos + (performance.now() - S.recv) / 1000 : st.pos;
}
const curItem = () => S.st?.queue.find((i) => i.id === S.st.cur) || null;

function syncTick() {
  if (S.screen !== "room") return;
  const st = S.st;
  const item = curItem();
  if (!item) {
    if (P.item) unload();
    V.classList.add("hidden");
    $("idle").classList.remove("hidden");
    $("idleHint").textContent = S.host ? "Mở tab 📃 Danh sách: dán link YouTube / MP4 / MP3 hoặc 📂 chiếu file trên máy." : "Chờ chủ phòng chiếu — bạn có thể đề xuất link ở tab 📃 Danh sách.";
    updateBar(0, 0);
    return;
  }
  if (!P.item || P.item.id !== item.id) load(item);
  const dur = item.dur || pDur();
  const exp = Math.min(expected(), dur > 0 ? dur : Infinity);
  updateBar(exp, dur);
  $("audioArt").classList.toggle("playing", st.playing);
  if (!P.ready || P.err) return;
  if (S.host && !P.metaSent && pDur() > 0) {
    P.metaSent = true;
    send({ t: "meta", id: item.id, dur: pDur(), title: P.kind === "yt" ? (P.yt.getVideoData?.().title || "") : "" });
  }
  // hết bài theo đồng hồ chủ phòng: trình phát của chủ phòng báo "ended" (máy chủ chuyển bài kế), mọi máy để video tự dừng ở cuối
  if (st.playing && dur > 0 && exp >= dur - 0.25) { onEnded(); return; }
  if (st.playing && pPaused() && !P.needClick) pPlay();
  if (!st.playing && !pPaused()) pPause();
  if (performance.now() < P.seekGuard) return;
  const d = pTime() - exp;
  const limit = P.kind === "yt" ? 1.2 : 1.0;
  if (Math.abs(d) > limit || (!st.playing && Math.abs(d) > 0.4)) { pSeek(exp); pRate(1); }
  else if (st.playing && P.kind === "media") pRate(Math.abs(d) > 0.12 ? 1 - Math.max(-0.08, Math.min(0.08, d * 0.35)) : 1);
}
setInterval(syncTick, 250);

let seekDrag = false;
function updateBar(pos, dur) {
  $("tCur").textContent = fmt(pos);
  $("tDur").textContent = fmt(dur);
  if (!seekDrag) $("seek").value = dur > 0 ? Math.round((pos / dur) * 1000) : 0;
  $("btnPlay").textContent = S.st?.playing ? "⏸" : "▶";
  const it = curItem();
  $("nowTitle").textContent = it ? it.title : "";
}
$("seek").addEventListener("input", () => { seekDrag = true; const it = curItem(); const dur = it?.dur || pDur(); $("tCur").textContent = fmt(($("seek").value / 1000) * dur); });
$("seek").addEventListener("change", () => {
  seekDrag = false;
  if (!S.host) return;
  const it = curItem(); const dur = it?.dur || pDur();
  if (dur > 0) send({ t: "seek", pos: ($("seek").value / 1000) * dur });
});
$("btnPlay").onclick = () => send({ t: S.st?.playing ? "pause" : "play" });
$("btnPrev").onclick = () => send({ t: "prev" });
$("btnNext").onclick = () => send({ t: "next" });
$("btnSync").onclick = () => { if (P.item && P.ready) { pSeek(expected()); toast("Đã đồng bộ lại với chủ phòng."); } };
$("vol").oninput = () => { S.volume = $("vol").value / 100; S.muted = false; applyVolume(); };
$("vol").onchange = () => post({ t: "ui.prefs", volume: S.volume });
$("btnMute").onclick = () => { S.muted = !S.muted; applyVolume(); };
$("btnDanmaku").onclick = () => { S.danmaku = !S.danmaku; $("btnDanmaku").classList.toggle("off", !S.danmaku); toast(S.danmaku ? "Bật bình luận bay trên màn hình." : "Tắt bình luận bay."); };
$("btnFull").onclick = toggleFull;
$("shield").ondblclick = toggleFull;
function toggleFull() { if (document.fullscreenElement) document.exitFullscreen(); else $("stage").requestFullscreen().catch(() => { }); }
document.addEventListener("fullscreenchange", () => { $("fsChat").classList.add("hidden"); layoutStage(); });

// ═══════════════════════ Rạp (canvas) + chế độ xem ═══════════════════════

const cv = $("cv"), cx = cv.getContext("2d");
let hoverSeat = -1;
function setMode(m) {
  S.mode = m;
  $("scrRoom").classList.toggle("mode-theater", m === "theater");
  $("scrRoom").classList.toggle("mode-screen", m === "screen");
  $("btnModeTheater").classList.toggle("on", m === "theater");
  $("btnModeScreen").classList.toggle("on", m === "screen");
  layoutStage();
}
/** Ẩn / hiện cột bình luận — ẩn thì bình luận vẫn hiện: bong bóng trên đầu nhân vật (rạp) hoặc chữ bay trên màn chiếu (màn hình). */
function setSide(show) {
  S.side = show;
  $("scrRoom").classList.toggle("side-hidden", !show);
  $("btnSide").textContent = show ? "💬 Ẩn bình luận" : "💬 Hiện bình luận";
  layoutStage();
}
$("btnSide").onclick = () => { setSide(!S.side); post({ t: "ui.prefs", side: S.side }); };
$("btnModeTheater").onclick =() => { setMode("theater"); post({ t: "ui.prefs", mode: "theater" }); };
$("btnModeScreen").onclick = () => { setMode("screen"); post({ t: "ui.prefs", mode: "screen" }); };

function layoutStage() {
  const hall = $("hall"), box = $("screenBox");
  const W = hall.clientWidth, H = hall.clientHeight;
  const dpr = window.devicePixelRatio || 1;
  if (cv.width !== Math.round(W * dpr) || cv.height !== Math.round(H * dpr)) { cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr); }
  if (S.mode === "theater" && !document.fullscreenElement) {
    const L = Theater.layout(W, H);
    Object.assign(box.style, { left: L.screen.x + "px", top: L.screen.y + "px", width: L.screen.w + "px", height: L.screen.h + "px" });
  } else Object.assign(box.style, { left: "", top: "", width: "", height: "" });
}
window.addEventListener("resize", layoutStage);

function frame(t) {
  requestAnimationFrame(frame);
  if (S.screen !== "room" || S.mode !== "theater" || document.fullscreenElement) return;
  const dpr = window.devicePixelRatio || 1;
  const W = cv.width / dpr, H = cv.height / dpr;
  if (!W || !H) return;
  cx.setTransform(dpr, 0, 0, dpr, 0, 0);
  Theater.draw(cx, W, H, t, S.viewers, S.me, hoverSeat);
}
requestAnimationFrame(frame);

cv.addEventListener("mousemove", (e) => {
  const r = cv.getBoundingClientRect();
  const i = Theater.seatAt(e.clientX - r.left, e.clientY - r.top);
  hoverSeat = i;
  const v = S.viewers.find((x) => x.seat === i);
  const tip = $("seatTip");
  if (i < 0) { tip.classList.add("hidden"); cv.style.cursor = ""; return; }
  const p = Theater.L.seats[i];
  tip.textContent = v ? (v.host ? "★ " : "") + v.name + (v.id === S.me ? " (bạn)" : "") : `Ghế ${seatName(i)} — bấm để ngồi đây`;
  tip.style.left = p.x + "px"; tip.style.top = (p.y - 40 * p.s) + "px";
  tip.classList.remove("hidden");
  cv.style.cursor = v ? "default" : "pointer";
});
cv.addEventListener("mouseleave", () => { hoverSeat = -1; $("seatTip").classList.add("hidden"); });
cv.addEventListener("click", (e) => {
  const r = cv.getBoundingClientRect();
  const i = Theater.seatAt(e.clientX - r.left, e.clientY - r.top);
  if (i >= 0 && !S.viewers.some((x) => x.seat === i)) send({ t: "seat", i });
});
const seatName = (i) => "ABC"[Math.floor(i / Theater.COLS)] + ((i % Theater.COLS) + 1);

/** Cảm xúc trong chế độ màn hình / toàn màn hình: bay lên ở góc phải màn chiếu. */
function fxPop(e) {
  const s = document.createElement("span");
  s.textContent = e;
  s.style.right = (4 + Math.random() * 14) + "%";
  $("fx").appendChild(s);
  setTimeout(() => s.remove(), 2700);
}

/** Bình luận bay ngang màn chiếu (kiểu danmaku) — chế độ màn hình / toàn màn hình. */
let lane = 0;
function danmaku(name, color, text) {
  const box = $("danmaku");
  const el = document.createElement("div");
  el.className = "dm";
  el.innerHTML = `<b style="color:${esc(color)}">${esc(name)}</b>${esc(text)}`;
  const lanes = Math.max(3, Math.floor(box.clientHeight / 34));
  lane = (lane + 1 + Math.floor(Math.random() * 2)) % lanes;
  el.style.top = (lane * 34 + 6) + "px";
  box.appendChild(el);
  const W = box.clientWidth + el.offsetWidth + 20;
  const dur = Math.max(6500, Math.min(11000, W * 9));
  el.animate([{ transform: "translateX(0)" }, { transform: `translateX(-${W}px)` }], { duration: dur, easing: "linear" }).onfinish = () => el.remove();
}

// ═══════════════════════ Cột bên: bình luận / danh sách / người xem ═══════════════════════

document.querySelectorAll(".tabs button").forEach((b) => b.onclick = () => openTab(b.dataset.tab));
function openTab(name) {
  document.querySelectorAll(".tabs button").forEach((x) => x.classList.toggle("on", x.dataset.tab === name));
  for (const n of ["chat", "queue", "people"]) $("tab-" + n).classList.toggle("hidden", n !== name);
  if (name === "chat") scrollChat(true);
}

$("reacts").innerHTML = REACTS.map((e) => `<button data-e="${e}" title="Thả ${e}">${e}</button>`).join("");
$("reacts").querySelectorAll("button").forEach((b) => b.onclick = () => send({ t: "react", e: b.dataset.e }));

function sendChat(input) {
  const text = input.value.trim();
  if (!text) return;
  send({ t: "chat", text });
  input.value = "";
}
$("btnSend").onclick = () => sendChat($("chatIn"));
$("chatIn").onkeydown = (e) => { if (e.key === "Enter" || e.code === "Enter" || e.code === "NumpadEnter") { e.preventDefault(); sendChat($("chatIn")); } };
$("fsInput").onkeydown = (e) => {
  if (e.key === "Enter" || e.code === "Enter") { e.preventDefault(); sendChat($("fsInput")); $("fsChat").classList.add("hidden"); }
  else if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); $("fsChat").classList.add("hidden"); }
};

function scrollChat(force) {
  const ul = $("chatList");
  if (force || ul.scrollHeight - ul.scrollTop - ul.clientHeight < 80) ul.scrollTop = ul.scrollHeight;
}
function addChat(m) {
  const ul = $("chatList");
  const li = document.createElement("li");
  if (m.sys) li.innerHTML = `<span class="sys">${esc(m.text)}</span>`;
  else {
    const emo = /^\p{Extended_Pictographic}[\p{Extended_Pictographic}️‍\s]{0,10}$/u.test(m.text);
    li.innerHTML = (curItem() ? `<span class="ts" data-at="${m.at}" title="${S.host ? "Bấm để tua tới đoạn này" : "Thời điểm trong video"}">${fmt(m.at)}</span>` : "") +
      `<span class="nm" style="color:${esc(m.color)}">${esc(m.name)}</span><span class="${emo ? "big-emo" : ""}">${esc(m.text)}</span>`;
    const ts = li.querySelector(".ts");
    if (ts && S.host) ts.onclick = () => send({ t: "seek", pos: +ts.dataset.at });
  }
  ul.appendChild(li);
  while (ul.children.length > 400) ul.firstChild.remove();
  scrollChat(m.from === S.me);
}

function renderPeople() {
  $("pCount").textContent = S.viewers.length;
  $("people").innerHTML = S.viewers.map((v) => `<li class="${v.id === S.me ? "me" : ""}"><span class="dot" style="background:${esc(v.color)}"></span><span>${v.host ? "★ " : ""}${esc(v.name)}${v.id === S.me ? " <span class='muted'>(bạn)</span>" : ""}</span><span class="seat">ghế ${seatName(v.seat)}</span></li>`).join("");
}

function renderQueue() {
  const q = S.st?.queue || [];
  $("qCount").textContent = q.length;
  $("btnAdd").textContent = S.host ? "Thêm" : "Đề xuất";
  const ul = $("queue");
  if (!q.length) { ul.innerHTML = `<li class="muted" style="background:transparent">Danh sách trống.</li>`; return; }
  ul.innerHTML = q.map((it, i) => {
    const th = it.kind === "yt" ? `style="background-image:url('https://i.ytimg.com/vi/${encodeURIComponent(it.src)}/mqdefault.jpg')"` : "";
    const icon = it.kind === "yt" ? "" : isAudio(it) ? "🎵" : "🎞";
    const where = it.kind === "yt" ? "YouTube" : it.kind === "file" ? "file chủ phòng" : "link";
    const cur = it.id === S.st.cur;
    const acts = S.host
      ? (it.sug ? `<button data-a="approve" title="Duyệt đề xuất">✓</button>` : "") +
        `<button data-a="select" title="Chiếu mục này">▶</button>` +
        (i > 0 ? `<button data-a="up" title="Lên">↑</button>` : "") + (i < q.length - 1 ? `<button data-a="down" title="Xuống">↓</button>` : "") +
        `<button data-a="remove" title="Xoá">✕</button>`
      : "";
    return `<li class="${cur ? "cur" : ""} ${it.sug ? "sug" : ""}" data-id="${esc(it.id)}"><div class="th" ${th}>${icon}</div><div class="qi"><div class="qt" title="${esc(it.title)}">${esc(it.title)}</div>` +
      `<div class="qm">${cur ? `<span class="tag">▶ đang chiếu</span> · ` : it.sug ? `<span class="tag">đề xuất</span> · ` : ""}${where}${it.dur ? " · " + fmt(it.dur) : ""} · ${esc(it.by)}</div></div><div class="qa">${acts}</div></li>`;
  }).join("");
  ul.querySelectorAll("li[data-id]").forEach((li) => li.querySelectorAll("button").forEach((b) => b.onclick = () => {
    const id = li.dataset.id, a = b.dataset.a;
    if (a === "up" || a === "down") send({ t: "move", id, dir: a === "up" ? -1 : 1 });
    else send({ t: a, id });
  }));
}

function addLink() {
  const url = $("addIn").value.trim();
  if (!url) return;
  $("qErr").textContent = "";
  send({ t: "add", url });
  $("addIn").value = "";
  if (!S.host) toast("Đã gửi đề xuất cho chủ phòng.");
}
$("btnAdd").onclick = addLink;
$("addIn").onkeydown = (e) => { if (e.key === "Enter" || e.code === "Enter") addLink(); };
$("btnFile").onclick = () => post({ t: "ui.pickfile" });
$("btnFw").onclick = () => post({ t: "ui.firewall" });
$("btnLeave").onclick = () => {
  if (S.host && S.viewers.length > 1 && !confirm("Bạn là chủ phòng — rời phòng sẽ kết thúc buổi chiếu cho mọi người. Rời?")) return;
  post({ t: "ui.leave" });
};

// ═══════════════════════ Phím tắt ═══════════════════════

document.addEventListener("keydown", (e) => {
  if (S.screen !== "room") return;
  const typing = e.target instanceof HTMLInputElement && e.target.type !== "range";
  if (typing) return;
  const k = e.key.toLowerCase();
  if (k === " " && S.host) { e.preventDefault(); $("btnPlay").click(); }
  else if (k === "arrowright" && S.host) { e.preventDefault(); send({ t: "seek", pos: expected() + 10 }); }
  else if (k === "arrowleft" && S.host) { e.preventDefault(); send({ t: "seek", pos: Math.max(0, expected() - 10) }); }
  else if (k === "f") toggleFull();
  else if (k === "t") setMode(S.mode === "theater" ? "screen" : "theater");
  else if (k === "m") $("btnMute").click();
  else if (k === "c") $("btnSide").click();
  else if (e.key === "Enter" || e.code === "Enter") {
    e.preventDefault();
    if (document.fullscreenElement) { $("fsChat").classList.remove("hidden"); $("fsInput").focus(); }
    else { openTab("chat"); $("chatIn").focus(); }
  }
});

// ═══════════════════════ Tin từ C# / máy chủ phòng ═══════════════════════

function enterRoom() {
  show("room");
  $("chatList").innerHTML = "";
  $("scrRoom").classList.toggle("viewer", !S.host);
  $("rmName").textContent = S.room;
  if (!S.host) { $("rmAddr").textContent = `Chủ phòng: ${S.hostAddr}`; $("rmFw").classList.add("hidden"); }
  setMode(S.mode);
  setSide(S.side);
  openTab("chat");
  requestAnimationFrame(layoutStage);
}

function handle(m) {
  switch (m.t) {
    case "ui.init":
      $("inName").value = m.name || "";
      $("inRoom").value = m.room || "";
      $("inIp").value = m.lastIp || "";
      S.volume = m.volume ?? 0.8; $("vol").value = Math.round(S.volume * 100);
      S.mode = m.mode === "screen" ? "screen" : "theater";
      S.side = m.side !== false;
      $("ver").textContent = `Rạp Bcode v${m.version} · máy ${m.machine} · IP: ${m.ips.join(", ") || "?"}`;
      show("home");
      if (m.auto === "host") post({ t: "ui.host", name: m.name, room: m.autoRoom || m.room });
      else if (m.auto === "join") post({ t: "ui.join", name: m.name, ip: m.autoJoin });
      break;
    case "ui.rooms": renderRooms(m.rooms || [], m.error); break;
    case "ui.error":
      if (S.screen === "room") { $("qErr").textContent = m.text; toast(m.text, 4000); } else $("homeErr").textContent = m.text;
      break;
    case "ui.hosted":
      {
        const addrs = m.ips.map((ip) => ip + (m.port !== 47831 ? ":" + m.port : ""));
        $("rmAddr").textContent = `Người khác vào bằng danh sách phòng, hoặc gõ IP: ${addrs.join("  ·  ") || "(không có IP LAN)"}`;
        $("rmAddr").title = addrs.join("\n");
      }
      $("rmFw").classList.add("hidden");
      if (m.warn) toast(m.warn, 5000);
      break;
    case "ui.connected": S.hostAddr = m.host; break;
    case "ui.fw":
      $("rmFw").classList.remove("hidden");
      $("rmFw").dataset.level = m.level;
      $("rmFwText").textContent = (m.level === "ok" ? "✅ " : m.level === "bad" ? "⛔ " : "⚠ ") + m.text;
      $("btnFw").disabled = !!m.busy;
      $("btnFw").textContent = m.level === "ok" ? "🛡 Mở lại tường lửa" : "🛡 Mở tường lửa";
      break;
    case "ui.left":
      unload(); S.st = null; S.viewers = []; show("home");
      break;
    case "ui.closed":
      unload(); S.st = null; S.viewers = []; show("home");
      $("homeErr").textContent = S.rejected || "Mất kết nối với phòng: " + m.reason;
      S.rejected = null;
      break;

    // ── từ máy chủ phòng ──
    case "welcome":
      S.me = m.id; S.host = m.host; S.name = m.name; S.room = m.room; S.mediaPort = m.mediaPort;
      enterRoom();
      break;
    case "reject": S.rejected = m.text; break;
    case "state":
      S.st = m; S.recv = performance.now();
      renderQueue();
      syncTick();
      break;
    case "viewers": {
      const old = new Set(S.viewers.map((v) => v.id));
      S.viewers = m.list;
      for (const id of old) if (!m.list.some((v) => v.id === id)) Theater.forget(id);
      renderPeople();
      break;
    }
    case "chat":
      addChat(m);
      if (!m.sys) {
        Theater.say(m.from, m.text);
        if (S.danmaku && (S.mode === "screen" || document.fullscreenElement)) danmaku(m.name, m.color, m.text);      }
      break;
    case "react":
      Theater.pop(m.from, m.e);
      if (S.mode === "screen" || document.fullscreenElement) fxPop(m.e);
      break;
    case "clearchat": $("chatList").innerHTML = ""; break;
    case "error":
      $("qErr").textContent = m.text;
      toast(m.text, 3500);
      break;
  }
}

if (wv) {
  wv.addEventListener("message", (e) => { try { handle(e.data); } catch (err) { console.error(err); } });
  post({ t: "ui.init" });
} else {
  document.body.insertAdjacentHTML("beforeend", `<p style="position:fixed;bottom:8px;left:8px" class="muted">Mở trong BcodeWatch.exe để dùng.</p>`);
}
