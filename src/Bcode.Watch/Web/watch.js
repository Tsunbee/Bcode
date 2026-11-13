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
  // Từ phòng về sảnh: xoá danh sách phòng cũ. C# mở bộ tìm phòng MỚI và chỉ báo khi có phòng trả lời — không còn phòng nào thì không báo gì,
  // danh sách cũ (vd "Rạp của X · 1/24 người" lúc bấm Vào xem) nằm mãi dù phòng đã đóng, bấm vào thì treo.
  if (name === "home" && S.screen !== "home") renderRooms([]);
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
  ul.innerHTML = rooms.map((r, i) => `<li><div class="ri"><div class="rn">${r.lock ? `<span class="lock" title="Phòng có mật khẩu">🔒</span>` : ""}${esc(r.name)}</div><div class="rm">${esc(r.host)} · ${esc(r.ip)}${r.port !== 47831 ? ":" + r.port : ""} · ${r.players}/${r.max} người · ${esc(r.state)}</div></div><button data-i="${i}">Vào xem</button></li>`).join("");
  ul.querySelectorAll("button").forEach((b) => b.onclick = () => { const r = rooms[+b.dataset.i]; join(r.ip + (r.port !== 47831 ? ":" + r.port : "")); });
}
function join(ip) { $("homeErr").textContent = ""; post({ t: "ui.join", name: $("inName").value, ip }); }
$("btnJoinIp").onclick = () => join($("inIp").value);
$("inIp").onkeydown = (e) => { if (e.key === "Enter") join($("inIp").value); };
$("btnHost").onclick = () => { $("homeErr").textContent = ""; post({ t: "ui.host", name: $("inName").value, room: $("inRoom").value, pass: $("inRoomPass").value }); };
$("inRoomPass").onkeydown = (e) => { if (e.key === "Enter") $("btnHost").click(); };

// ───────────── Mật khẩu phòng ─────────────
// Vào phòng có khoá: C# báo "ui.needpass" (lần đầu text = null, sai thì text = lời báo) → hỏi ở hộp passBox → "ui.pass".
// C# tự tính HMAC trên nonce chủ phòng gửi — trang không giữ mật khẩu sau khi gửi.
function showPass(room, text) {
  $("passRoom").textContent = room || "Phòng có mật khẩu";
  $("passErr").textContent = text || "";
  $("passOk").disabled = false; $("passOk").textContent = "Vào phòng";
  $("passBox").classList.remove("hidden");
  $("inPass").value = "";
  setTimeout(() => $("inPass").focus(), 0);
}
function hidePass() { $("passBox").classList.add("hidden"); $("inPass").value = ""; }
$("passForm").onsubmit = (e) => {
  e.preventDefault();
  const pass = $("inPass").value;
  if (!pass) { $("passErr").textContent = "Nhập mật khẩu."; return; }
  $("passOk").disabled = true; $("passOk").textContent = "Đang kiểm tra…";
  post({ t: "ui.pass", pass });
  $("inPass").value = "";
};
$("passCancel").onclick = () => { hidePass(); post({ t: "ui.leave" }); };
$("inPass").onkeydown = (e) => { if (e.key === "Escape") $("passCancel").click(); };


// ═══════════════════════ Trình phát (video / audio / YouTube) ═══════════════════════

const V = $("vid");
const P = { item: null, kind: null, ready: false, err: null, ytApi: false, yt: null, ytReady: false, metaSent: false, endSent: false, seekGuard: 0, needClick: false, lastSeekReq: 0 };

function mediaUrl(item) {
  if (item.kind === "url") return item.src;
  if (item.kind === "file") return `http://${S.hostAddr.includes(":") && !S.hostAddr.startsWith("[") ? "[" + S.hostAddr + "]" : S.hostAddr}:${S.mediaPort}/m/${encodeURIComponent(item.src)}`;
  return "";
}
const isAudio = (item) => /\.(mp3|m4a|aac|wav|ogg|oga|opus|flac)(\?|$)/i.test(item.kind === "file" ? item.title : item.src);

// Tải YouTube IFrame API, lỗi thì TỰ THỬ LẠI (4 lần, cách 1,5 / 3 / 4,5 s) rồi mới báo kèm nút Thử lại. Trước đây chỉ thử 1 lần: mạng văn phòng
// chập chờn / proxy chưa sẵn lúc WebView2 vừa mở → báo "Không tải được trình phát YouTube" và kẹt tới khi rời phòng.
// iframe_api tải xong lại tải tiếp widgetapi — phần này hỏng thì onYouTubeIframeAPIReady không bao giờ gọi (màn đen im lặng) → hết 20 s coi như lỗi.
function loadYtApi() {
  if (P.ytApi) return;
  P.ytApi = true;
  const attempt = P.ytTry = (P.ytTry || 0) + 1;
  window.onYouTubeIframeAPIReady = () => {
    P.ytApiReady = true; P.ytTry = 0; clearTimeout(P.ytTimer);
    if (P.kind === "yt" && P.item) { clearErr(); ytCreate(P.item.src); }
  };
  if (window.YT && !YT.Player) { try { delete window.YT; } catch { window.YT = undefined; } }   // lần trước dở dang: cho iframe_api tải lại widgetapi
  const s = document.createElement("script");
  s.src = "https://www.youtube.com/iframe_api" + (attempt > 1 ? "?r=" + attempt : "");
  let done = false;
  const fail = () => {
    if (done || (window.YT && YT.Player)) return;
    done = true; clearTimeout(P.ytTimer); s.remove(); P.ytApi = false;
    if (attempt < 4) {
      if (P.kind === "yt") playerError(`Đang tải lại trình phát YouTube (lần ${attempt + 1})…`);
      setTimeout(() => { if (P.kind === "yt" && !P.ytApi) loadYtApi(); }, 1500 * attempt);
    } else {
      P.ytTry = 0;
      if (P.kind === "yt") playerError("Không tải được trình phát YouTube (đã thử 4 lần) — máy này có vào được youtube.com không (mạng / proxy / tường lửa)?", null, () => loadYtApi());
    }
  };
  s.onerror = fail;
  P.ytTimer = setTimeout(fail, 20000);
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

function playerError(text, openUrl, retry) {
  P.err = text;
  const box = $("perr");
  box.innerHTML = `<div>⚠ ${esc(text)}</div>` + (openUrl ? `<button id="btnOpenExt">Mở bằng trình duyệt</button>` : "") + (retry ? `<button id="btnRetry">Thử lại</button>` : "") +
    (S.host ? `<div class="muted">Chủ phòng có thể bấm ⏭ để chuyển bài.</div>` : "");
  box.classList.remove("hidden");
  if (openUrl) $("btnOpenExt").onclick = () => post({ t: "ui.open", url: openUrl });
  if (retry) $("btnRetry").onclick = () => { clearErr(); retry(); };
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
  if (SH.id !== S.me) { SV.volume = v; SV.muted = S.muted; }   // màn hình người khác chia sẻ (của mình thì luôn tắt tiếng)
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
  // YouTube: trình phát dùng lại (cueVideoById) vẫn trả độ dài / tên video CŨ tới khi tải xong video mới — chờ đúng mã video mới báo
  const ytStale = P.kind === "yt" && P.yt.getVideoData && (P.yt.getVideoData() || {}).video_id !== P.item.src;
  if (S.host && !P.metaSent && pDur() > 0 && !ytStale) {
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
  $("nowTitle").textContent = sharing() ? (SH.id === S.me ? "📡 Bạn đang chia sẻ màn hình" : `📡 ${SH.name} đang chia sẻ màn hình`) + (it ? " · danh sách tạm dừng" : "") : it ? it.title : "";
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

// ═══════════════════════ Chia sẻ màn hình (WebRTC máy-máy trong LAN, như Discord) ═══════════════════════
// Người chia sẻ: getDisplayMedia (hộp chọn màn hình / cửa sổ / tab của Chromium) → mỗi người xem 1 RTCPeerConnection riêng.
// Người xem xin luồng ({k:"want"}) → người chia sẻ gửi offer → người xem trả answer; hai bên gửi nhau ứng viên ICE. Máy chủ phòng chỉ chuyển
// các tin này ("rtc"), hình / tiếng đi thẳng giữa hai máy. Không dùng STUN / TURN: cùng LAN thì IP LAN là đủ (C# tắt giấu IP bằng mDNS).
// Mỗi lần gửi offer có mã sid riêng — tin cũ của kết nối đã bỏ thì lờ đi.

const SV = $("shareVid");
const SH = { id: 0, name: "", local: null, pcs: new Map(), pc: null, seq: 0, fake: false, got: false, fail: false, tries: 0, waitTimer: 0, startTimer: 0, fakeStop: null };
const RTC_CFG = { iceServers: [] };
const sharing = () => SH.id !== 0;

async function startShare() {
  if (SH.local) return;
  let stream;
  try {
    stream = SH.fake ? fakeStream() : await navigator.mediaDevices.getDisplayMedia({
      video: { frameRate: { ideal: 30, max: 30 }, width: { max: 2560 }, height: { max: 1440 } },   // tới 1440p: xem toàn màn hình 27" vẫn nét
      audio: { echoCancellation: false, noiseSuppression: false, autoGainControl: false },
      systemAudio: "include", surfaceSwitching: "include", selfBrowserSurface: "exclude",
    });
  } catch (e) {
    // Bấm Huỷ trong hộp chọn → NotAllowedError / AbortError: im lặng
    if (e?.name !== "NotAllowedError" && e?.name !== "AbortError") toast("Không chụp được màn hình: " + (e?.message || e), 5000);
    return;
  }
  if (SH.local || S.screen !== "room") { stream.getTracks().forEach((t) => t.stop()); return; }
  SH.local = stream;
  for (const t of stream.getVideoTracks()) {
    try { t.contentHint = "motion"; } catch { }   // phim: ưu tiên đủ khung hình hơn nét chữ
    t.onended = () => stopShare();                 // bấm "Dừng chia sẻ" trên thanh của Chromium / đóng cửa sổ đang chia sẻ
  }
  if (!stream.getAudioTracks().length && !SH.fake) toast("Đang chia sẻ KHÔNG kèm tiếng — muốn có tiếng: dừng rồi chia sẻ lại, chọn tab \"Entire Screen\" (cả màn hình) và bật \"Share with system audio\". Chia sẻ 1 cửa sổ thì Windows không cho lấy tiếng.", 8000);
  send({ t: "share.start" });
  clearTimeout(SH.startTimer);
  // Máy chủ phòng bản cũ không biết "share.start" → không ai trả lời; người khác đang chia sẻ → máy chủ đã báo lỗi.
  SH.startTimer = setTimeout(() => {
    if (SH.local && SH.id !== S.me) { dropLocal(); if (!sharing()) toast("Máy chủ phòng chưa có chia sẻ màn hình (Bcode bản cũ) — chủ phòng cập nhật Bcode.", 5000); }
  }, 4000);
}

/** Bấm nút dừng: người chia sẻ tự dừng, hoặc chủ phòng tắt chia sẻ của người khác. */
function stopShare() {
  if (SH.id === S.me || (S.host && sharing())) send({ t: "share.stop" });
  dropLocal();
}

function dropLocal() {
  clearTimeout(SH.startTimer);
  for (const pc of SH.pcs.values()) try { pc.close(); } catch { }
  SH.pcs.clear();
  if (SH.local) { SH.local.getTracks().forEach((t) => { t.onended = null; t.stop(); }); SH.local = null; }
  if (SH.fakeStop) { SH.fakeStop(); SH.fakeStop = null; }
  if (SV.srcObject && SH.id === S.me) SV.srcObject = null;
}

function closeViewerPc() {
  if (SH.pc) { try { SH.pc.close(); } catch { } SH.pc = null; }
  clearTimeout(SH.waitTimer);
}

/** Tin "share" từ máy chủ phòng: ai đang chia sẻ (id 0 = không ai). */
function onShare(m) {
  const prev = SH.id;
  SH.id = m.id; SH.name = m.name || "";
  if (m.id !== prev) {
    closeViewerPc();
    for (const pc of SH.pcs.values()) try { pc.close(); } catch { }
    SH.pcs.clear();
    SV.srcObject = null;
    SH.got = false; SH.tries = 0;
  }
  if (m.id === S.me) {
    clearTimeout(SH.startTimer);
    if (!SH.local) { send({ t: "share.stop" }); return; }   // đã huỷ ở máy này trước khi máy chủ kịp nhận
    SV.srcObject = SH.local; SV.muted = true;                 // xem trước màn hình của mình, không phát lại tiếng
    SV.play().catch(() => { });
  } else {
    if (SH.local) dropLocal();                                // người khác giành trước
    if (m.id !== 0 && m.id !== prev) wantStream();
  }
  renderShare();
}

function wantStream() {
  closeViewerPc();
  SH.got = false; SH.fail = false;
  send({ t: "rtc", to: SH.id, data: { k: "want" } });
  renderShare();
  SH.waitTimer = setTimeout(() => {
    if (!sharing() || SH.id === S.me || SH.got) return;
    if (++SH.tries < 3) { wantStream(); return; }
    SH.fail = true;
    shareMsg(`Không nhận được hình từ máy ${SH.name}`,
      "Hai máy không nối thẳng được với nhau — thường do tường lửa (một trong hai máy bấm 🛡 Mở tường lửa trong phòng, hoặc cho phép BcodeWatch trên mạng Private) hoặc khác mạng LAN / VPN.", true, true);
  }, 9000);
}

function iceOut(pc, peer) {
  pc.onicecandidate = (e) => { if (e.candidate) send({ t: "rtc", to: peer, data: { k: "ice", sid: pc.sid, c: e.candidate.toJSON() } }); };
}
async function addIce(pc, d) {
  if (!pc || d.sid !== pc.sid || !d.c) return;
  if (!pc.remoteDescription) { pc.q.push(d.c); return; }
  try { await pc.addIceCandidate(d.c); } catch { }
}
async function flushIce(pc) {
  const q = pc.q.splice(0);
  for (const c of q) try { await pc.addIceCandidate(c); } catch { }
}

/** Ưu tiên H.264 (Windows có bộ nén phần cứng / nhanh hơn): VP8 mặc định nén bằng CPU, đo trên máy văn phòng 1440p chỉ ~16 khung/giây
 *  rồi tự tụt về 1080p (qualityLimitationReason = cpu). Trình duyệt không có H.264 thì giữ thứ tự cũ. */
function preferH264(pc) {
  try {
    const caps = RTCRtpSender.getCapabilities("video")?.codecs || [];
    const rank = (c) => c.mimeType === "video/H264" ? (/packetization-mode=1/.test(c.sdpFmtpLine || "") ? 0 : 1) : 2;
    const sorted = [...caps].sort((a, b) => rank(a) - rank(b));
    if (!sorted.length || rank(sorted[0]) !== 0) return;
    for (const tr of pc.getTransceivers()) if (tr.sender.track?.kind === "video") tr.setCodecPreferences(sorted);
  } catch { }
}

/** Người chia sẻ: mở kết nối tới 1 người xem và gửi offer. */
async function offerTo(peer) {
  if (!SH.local || SH.id !== S.me) return;
  SH.pcs.get(peer)?.close();
  const pc = new RTCPeerConnection(RTC_CFG);
  pc.sid = ++SH.seq; pc.q = [];
  SH.pcs.set(peer, pc);
  for (const t of SH.local.getTracks()) pc.addTrack(t, SH.local);
  preferH264(pc);
  iceOut(pc, peer);
  pc.onconnectionstatechange = () => { if (["failed", "closed"].includes(pc.connectionState) && SH.pcs.get(peer) === pc) SH.pcs.delete(peer); };
  try {
    await pc.setLocalDescription(await pc.createOffer());
    send({ t: "rtc", to: peer, data: { k: "offer", sid: pc.sid, sdp: pc.localDescription.sdp } });
    // Mỗi người xem máy này nén riêng 1 luồng — đông người thì hạ bitrate cho máy đỡ nặng / LAN đỡ nghẽn.
    const n = SH.pcs.size;
    const br = n <= 3 ? 12e6 : n <= 8 ? 6e6 : 3e6;   // 1440p30 phim cần ~10–12 Mbps; LAN 100 Mbps chịu được 8 người × 6 Mbps
    for (const s of pc.getSenders()) {
      if (s.track?.kind !== "video") continue;
      const p = s.getParameters();
      if (!p.encodings || !p.encodings.length) p.encodings = [{}];
      p.encodings[0].maxBitrate = br;
      p.encodings[0].maxFramerate = 30;
      await s.setParameters(p).catch(() => { });
    }
  } catch (e) { console.error("offer", e); }
}

/** Người xem: nhận offer của người chia sẻ, trả answer. */
async function acceptOffer(from, d) {
  closeViewerPc();
  const pc = new RTCPeerConnection(RTC_CFG);
  pc.sid = d.sid; pc.q = [];
  SH.pc = pc;
  pc.ontrack = (e) => {
    if (SH.pc !== pc) return;
    const ms = e.streams[0] || new MediaStream([e.track]);
    if (SV.srcObject !== ms) SV.srcObject = ms;
    SV.muted = false; applyVolume();
    SV.play().catch(() => { });
  };
  iceOut(pc, from);
  pc.onconnectionstatechange = () => {
    if (SH.pc !== pc) return;
    if (pc.connectionState === "failed" && SH.tries < 3) { SH.tries++; setTimeout(() => { if (SH.pc === pc) wantStream(); }, 1000); }
  };
  try {
    await pc.setRemoteDescription({ type: "offer", sdp: d.sdp });
    await flushIce(pc);
    await pc.setLocalDescription(await pc.createAnswer());
    send({ t: "rtc", to: from, data: { k: "answer", sid: pc.sid, sdp: pc.localDescription.sdp } });
  } catch (e) { console.error("answer", e); }
}

async function onRtc(from, d) {
  if (!d || !sharing()) return;
  if (SH.id === S.me) {
    if (d.k === "want") offerTo(from);
    else if (d.k === "answer") {
      const pc = SH.pcs.get(from);
      if (!pc || pc.sid !== d.sid || pc.signalingState !== "have-local-offer") return;
      try { await pc.setRemoteDescription({ type: "answer", sdp: d.sdp }); await flushIce(pc); } catch (e) { console.error("setAnswer", e); }
    } else if (d.k === "ice") addIce(SH.pcs.get(from), d);
  } else if (from === SH.id) {
    if (d.k === "offer") acceptOffer(from, d);
    else if (d.k === "ice") addIce(SH.pc, d);
  }
}

/** Người chia sẻ: ai rời phòng thì đóng kết nối tới người đó. */
function sharePeersGone() {
  for (const [peer, pc] of SH.pcs) if (!S.viewers.some((v) => v.id === peer)) { try { pc.close(); } catch { } SH.pcs.delete(peer); }
}

SV.addEventListener("loadeddata", () => { if (SH.id !== S.me) { SH.got = true; SH.tries = 0; clearTimeout(SH.waitTimer); renderShare(); } });

function shareMsg(title, text, bad, retry) {
  const el = $("shareMsg");
  el.className = "share-msg" + (bad ? " bad" : "");
  el.innerHTML = `<b>${esc(title)}</b>` + (text ? `<span>${esc(text)}</span>` : "") + (retry ? `<button id="btnShareRetry">Thử lại</button>` : "");
  if (retry) $("btnShareRetry").onclick = () => { SH.tries = 0; wantStream(); };
}

function renderShare() {
  const on = sharing(), me = SH.id === S.me;
  $("shareBox").classList.toggle("hidden", !on);
  const b = $("btnShare");
  b.classList.toggle("on", me);
  b.disabled = on && !me && !S.host;
  b.textContent = me ? "⏹ Dừng chia sẻ" : on && S.host ? `⏹ Tắt chia sẻ của ${SH.name}` : "📡 Chia sẻ màn hình";
  b.title = me ? "Dừng chia sẻ màn hình" : on ? (S.host ? `Tắt chia sẻ màn hình của ${SH.name}` : `${SH.name} đang chia sẻ — mỗi lúc chỉ 1 người`)
    : "Cho cả phòng xem màn hình / cửa sổ / tab bạn đang mở (như Discord) — hình và tiếng đi thẳng qua mạng LAN";
  if (!on) { $("shareMsg").classList.add("hidden"); return; }
  const tag = $("shareTag");
  tag.classList.toggle("me", me);
  const n = [...SH.pcs.values()].filter((pc) => pc.connectionState === "connected").length;
  tag.textContent = me ? `📡 Bạn đang chia sẻ màn hình · ${n ? n + " người đang xem" : "chờ người xem kết nối"}` : `📡 ${SH.name} đang chia sẻ màn hình`;
  if (me || SH.got) $("shareMsg").classList.add("hidden");
  else {
    if (!SH.fail) shareMsg(`Đang kết nối tới màn hình của ${SH.name}…`);
    $("shareMsg").classList.remove("hidden");
  }
}
setInterval(() => { if (SH.id && SH.id === S.me) renderShare(); }, 2000);   // số người đang xem

function resetShare() {
  dropLocal(); closeViewerPc();
  SH.id = 0; SH.name = ""; SV.srcObject = null;
  renderShare();
}

$("btnShare").onclick = () => { if (SH.id === S.me || (S.host && sharing())) stopShare(); else startShare(); };

/** --fakeshare: hình vẽ chạy (đồng hồ + ô màu) + tiếng bíp thay cho chụp màn hình thật — để thử WebRTC tự động khi chạy ngầm. */
function fakeStream() {
  const c = document.createElement("canvas"); c.width = 2560; c.height = 1440;
  const g = c.getContext("2d");
  const t0 = performance.now();
  const timer = setInterval(() => {
    const t = (performance.now() - t0) / 1000;
    g.fillStyle = `hsl(${(t * 40) % 360} 45% 18%)`; g.fillRect(0, 0, 2560, 1440);
    g.fillStyle = "#ffc94a"; g.fillRect(200 + ((t * 600) % 2000), 600, 240, 240);
    g.fillStyle = "#fff"; g.font = "bold 128px Segoe UI"; g.fillText(`Chia sẻ thử · ${t.toFixed(1)} s`, 120, 240);
    g.font = "28px Segoe UI"; g.fillText("Chữ nhỏ để kiểm tra độ nét: SELECT ma_kh, ten_kh FROM dmkh WHERE status = '1'", 120, 1300);
  }, 33);
  const ms = c.captureStream(30);
  let ac = null;
  try {
    ac = new AudioContext();
    const o = ac.createOscillator(), dst = ac.createMediaStreamDestination();
    o.frequency.value = 440; o.connect(dst); o.start();
    ms.addTrack(dst.stream.getAudioTracks()[0]);
  } catch { }
  SH.fakeStop = () => { clearInterval(timer); try { ac?.close(); } catch { } };
  return ms;
}

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
// Link mời: chủ phòng — C# dựng từ IP LAN chính (ui.hosted); người xem — từ địa chỉ chủ phòng máy này đã nối được (ui.connected + welcome).
// Link http://ip:cổng-phát-file/join mở trang nhỏ ở máy chủ phòng có nút mở Rạp Bcode (bcwatch://); dán link vào ô IP ở sảnh cũng vào được.
function viewerInvite() {
  if (!S.mediaPort || !S.hostAddr || /^127\.|^localhost$/i.test(S.hostAddr)) return "";
  const h = S.hostAddr.includes(":") ? "[" + S.hostAddr + "]" : S.hostAddr;
  return `http://${h}:${S.mediaPort}/join` + (S.hostPort && S.hostPort !== 47831 ? "?p=" + S.hostPort : "");
}
$("btnInvite").onclick = () => {
  if (!S.invite) { toast("Chưa có link mời — máy chủ phòng không có IP mạng LAN.", 4000); return; }
  post({ t: "ui.copy", text: S.invite });
};
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
      $("inRoomPass").value = m.roomPass || "";
      S.volume = m.volume ?? 0.8; $("vol").value = Math.round(S.volume * 100);
      S.mode = m.mode === "screen" ? "screen" : "theater";
      S.side = m.side !== false;
      SH.fake = !!m.fakeShare;
      $("ver").textContent = `Rạp Bcode v${m.version} · máy ${m.machine} · IP: ${m.ips.join(", ") || "?"}`;
      show("home");
      if (m.auto === "host") post({ t: "ui.host", name: m.name, room: m.autoRoom || m.room, pass: m.roomPass || "" });
      else if (m.auto === "join") post({ t: "ui.join", name: m.name, ip: m.autoJoin });
      break;
    case "ui.rooms": renderRooms(m.rooms || [], m.error); break;
    case "ui.needpass": showPass(m.room, m.text); break;
    case "ui.error":
      if (S.screen === "room") { $("qErr").textContent = m.text; toast(m.text, 4000); } else $("homeErr").textContent = m.text;
      break;
    case "ui.hosted":
      {
        const addrs = m.ips.map((ip) => ip + (m.port !== 47831 ? ":" + m.port : ""));
        $("rmAddr").textContent = `Người khác vào bằng danh sách phòng, hoặc gõ IP: ${addrs.join("  ·  ") || "(không có IP LAN)"}`;
        if (m.locked) $("rmAddr").textContent += "  ·  🔒 có mật khẩu";
        $("rmAddr").title = addrs.join("\n");
      }
      $("rmFw").classList.add("hidden");
      if (m.warn) toast(m.warn, 5000);
      S.invite = m.invite || "";
      $("btnInvite").title = S.invite ? `Chép link mời: ${S.invite}\nDán vào Zalo / Teams / mail — người cùng mạng LAN bấm link là vào phòng` : "Máy chủ phòng không có IP mạng LAN";
      break;
    case "ui.connected": S.hostAddr = m.host; S.hostPort = m.port; break;
    case "ui.copied": toast(`Đã chép link mời ${S.invite} — dán vào Zalo / Teams / mail cho mọi người cùng mạng LAN bấm vào.`, 5000); break;
    case "ui.fw":
      $("rmFw").classList.remove("hidden");
      $("rmFw").dataset.level = m.level;
      $("rmFwText").textContent = (m.level === "ok" ? "✅ " : m.level === "bad" ? "⛔ " : "⚠ ") + m.text;
      $("btnFw").disabled = !!m.busy;
      $("btnFw").textContent = m.level === "ok" ? "🛡 Mở lại tường lửa" : "🛡 Mở tường lửa";
      break;
    case "ui.left":
      hidePass();
      unload(); resetShare(); S.st = null; S.viewers = []; show("home");
      break;
    case "ui.closed":
      hidePass();
      unload(); resetShare(); S.st = null; S.viewers = []; show("home");
      $("homeErr").textContent = S.rejected || "Mất kết nối với phòng: " + m.reason;
      S.rejected = null;
      break;

    // ── từ máy chủ phòng ──
    case "welcome":
      hidePass();
      S.me = m.id; S.host = m.host; S.name = m.name; S.room = m.room; S.mediaPort = m.mediaPort;
      if (!S.host) { S.invite = viewerInvite(); $("btnInvite").title = S.invite ? `Chép link mời: ${S.invite}` : "Không dựng được link mời"; }
      resetShare();
      enterRoom();
      break;
    case "share": onShare(m); syncTick(); break;
    case "rtc": onRtc(m.from, m.data); break;
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
      sharePeersGone();
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
