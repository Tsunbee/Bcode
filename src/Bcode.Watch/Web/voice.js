"use strict";
// Chat voice của Rạp (như Google Meet / Discord), nạp SAU watch.js (dùng S, send, post, toast, applyVolume, renderPeople, Theater).
//
// • Ai bấm 🎧 Voice là "vào voice" (máy chủ phòng ghi Viewer.Voice: 1 = tắt mic / chỉ nghe, 2 = mic bật, báo trong "viewers").
// • Tiếng đi THẲNG máy-máy trong LAN (WebRTC, codec Opus ~32 kbps): mỗi cặp người trong voice 1 RTCPeerConnection, người id NHỎ hơn
//   gửi offer (không bao giờ hai bên cùng offer). Máy chủ phòng chỉ chuyển tin bắt tay "vc" (offer / answer / ice) giữa 2 người cùng trong voice.
//   Âm thanh nhẹ: 24 người đều nói cũng chỉ ~0,7 Mbps mỗi máy — không cần máy trung chuyển.
// • Bật / tắt mic KHÔNG bắt tay lại: mỗi kết nối có sẵn 1 transceiver audio sendrecv, đổi rãnh bằng sender.replaceTrack(rãnh | null).
//   Tắt mic = dừng hẳn micro (đèn micro của Windows tắt), không chỉ gửi im lặng.
// • Đường micro: getUserMedia (khử vọng + tự cân âm lượng của trình duyệt) → AudioContext 48 kHz → [lọc tạp âm] → MediaStreamDestination
//   → rãnh gửi đi. Lọc tạp âm: "strong" = RNNoise trong AudioWorklet (voice-worklet.js, như Krisp của Discord) | "basic" = noiseSuppression
//   có sẵn của trình duyệt | "off".
// • Đang nói: đo mức tiếng mỗi 100 ms (AnalyserNode) → viền xanh quanh đầu nhân vật trong rạp + chấm ở tab 👥; người khác đang nói thì
//   tiếng video tự nhỏ còn 35% (applyVolume hỏi Voice.duck()).
// • Thử ngầm: --fakemic voice|noise|tone → micro giả (tiếng giống giọng nói tổng hợp / tiếng ồn trắng / tiếng bíp) thay micro thật.

const Voice = (() => {
  const VC = {
    on: false, mic: false, busy: false,
    raw: null, ctx: null, node: null, an: null, track: null, fakeStop: null,
    pcs: new Map(),          // id người kia → RTCPeerConnection
    peers: new Map(),        // id → { audio, src, an, loudAt }
    vols: new Map(),         // id → âm lượng riêng (0..1)
    meterCtx: null, seq: 0, loudMe: 0,
    speaking: new Set(), duck: false,
    noise: "strong", micId: "", fake: null,
    wasm: null, worklet: null, last: null,   // last = số đo gần nhất của worklet {vad,inRms,outRms}
  };
  const SPEAK = 0.012;       // ngưỡng RMS (biên độ −1..1) coi là đang nói
  const HOLD = 400;          // giữ trạng thái "đang nói" thêm 400 ms sau tiếng cuối

  const meMsg = () => S.viewers.find((v) => v.id === S.me);

  // ───────────── micro ─────────────

  async function wasmBytes() {
    if (!VC.wasm) VC.wasm = await (await fetch("vendor/rnnoise/rnnoise.wasm")).arrayBuffer();
    return VC.wasm;
  }

  /** Micro giả để thử tự động: "voice" = giọng tổng hợp (cao độ ~140 Hz + hài, nói ngắt quãng từng tiếng) + ồn nhẹ; "noise" = ồn trắng;
   *  "fan" = ồn tần thấp như quạt / điều hoà; "keys" = tiếng gõ phím; "tone" = bíp. */
  function fakeMicStream(ctx, kind) {
    const dst = ctx.createMediaStreamDestination();
    const nodes = [];
    const noiseBuf = ctx.createBuffer(1, ctx.sampleRate * 2, ctx.sampleRate);
    const d = noiseBuf.getChannelData(0); for (let i = 0; i < d.length; i++) d[i] = Math.random() * 2 - 1;
    const noise = ctx.createBufferSource(); noise.buffer = noiseBuf; noise.loop = true;
    const ng = ctx.createGain(); ng.gain.value = kind === "noise" ? 0.25 : kind === "fan" ? 0.6 : 0.02;
    if (kind === "fan") { const lp = ctx.createBiquadFilter(); lp.type = "lowpass"; lp.frequency.value = 600; noise.connect(lp).connect(ng); }
    else noise.connect(ng);
    ng.connect(dst); noise.start(); nodes.push(noise);
    if (kind === "keys") {
      // gõ phím: tiếng "tách" 8–20 ms, 6–10 lần / giây, ngẫu nhiên
      const hp = ctx.createBiquadFilter(); hp.type = "highpass"; hp.frequency.value = 1500;
      const kg = ctx.createGain(); kg.gain.value = 0;
      noise.connect(hp).connect(kg).connect(dst);
      let t = ctx.currentTime + 0.1;
      for (let k = 0; k < 15000; k++) {
        t += 0.06 + Math.random() * 0.12;
        kg.gain.setValueAtTime(0.5 + Math.random() * 0.4, t); kg.gain.setValueAtTime(0, t + 0.008 + Math.random() * 0.012);
      }
    }
    if (kind === "voice" || kind === "tone") {
      const g = ctx.createGain(); g.gain.value = 0; g.connect(dst);
      if (kind === "tone") { const o = ctx.createOscillator(); o.frequency.value = 440; o.connect(g); o.start(); nodes.push(o); }
      else {
        // nguyên âm tổng hợp: cao độ 140 Hz rung nhẹ, 12 hài, qua 2 bộ lọc formant (~700 / 1200 Hz)
        const o = ctx.createOscillator(); o.type = "sawtooth"; o.frequency.value = 140;
        const lfo = ctx.createOscillator(); lfo.frequency.value = 5; const lg = ctx.createGain(); lg.gain.value = 6; lfo.connect(lg).connect(o.frequency); lfo.start();
        const f1 = ctx.createBiquadFilter(); f1.type = "bandpass"; f1.frequency.value = 700; f1.Q.value = 4;
        const f2 = ctx.createBiquadFilter(); f2.type = "bandpass"; f2.frequency.value = 1200; f2.Q.value = 5;
        o.connect(f1).connect(g); o.connect(f2).connect(g); o.start(); nodes.push(o, lfo);
      }
      // nói từng tiếng: 0,35 s có tiếng / 0,25 s nghỉ
      const t0 = ctx.currentTime;
      for (let k = 0; k < 2000; k++) {
        const a = t0 + k * 0.6;
        g.gain.setValueAtTime(0, a); g.gain.linearRampToValueAtTime(kind === "tone" ? 0.3 : 0.9, a + 0.03);
        g.gain.setValueAtTime(kind === "tone" ? 0.3 : 0.9, a + 0.32); g.gain.linearRampToValueAtTime(0, a + 0.35);
      }
    }
    VC.fakeStop = () => nodes.forEach((n) => { try { n.stop(); } catch { } });
    return dst.stream;
  }

  async function micOn() {
    if (VC.mic || VC.busy) return VC.mic;
    VC.busy = true;
    try {
      const ctx = VC.ctx = new AudioContext({ sampleRate: 48000 });
      if (VC.fake) VC.raw = fakeMicStream(ctx, VC.fake);
      else {
        VC.raw = await navigator.mediaDevices.getUserMedia({
          audio: {
            deviceId: VC.micId ? { ideal: VC.micId } : undefined,
            echoCancellation: true, autoGainControl: true, noiseSuppression: VC.noise === "basic", channelCount: 1,
          },
        });
        fillMics();
      }
      const src = ctx.createMediaStreamSource(VC.raw);
      const dest = ctx.createMediaStreamDestination();
      dest.channelCount = 1;
      let tail = src;
      VC.node = null; VC.last = null;
      if (VC.noise === "strong") {
        try {
          if (VC.worklet !== ctx) { await ctx.audioWorklet.addModule("voice-worklet.js"); VC.worklet = ctx; }
          VC.node = new AudioWorkletNode(ctx, "rnnoise", { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1], processorOptions: { wasm: await wasmBytes(), mode: "strong" } });
          VC.node.port.onmessage = (e) => { if (e.data.error) { toast("Không chạy được lọc ồn mạnh: " + e.data.error, 5000); } else VC.last = e.data; };
          src.connect(VC.node); tail = VC.node;
        } catch (e) { toast("Không chạy được lọc ồn mạnh (" + (e?.message || e) + ") — dùng lọc của trình duyệt.", 5000); }
      }
      VC.an = ctx.createAnalyser(); VC.an.fftSize = 1024;
      tail.connect(dest); tail.connect(VC.an);
      VC.track = dest.stream.getAudioTracks()[0];
      VC.mic = true;
      await setSenders(VC.track);
      return true;
    } catch (e) {
      stopMic();
      const n = e?.name;
      toast(n === "NotAllowedError" ? "Windows chặn micro: Settings → Privacy & security → Microphone → bật cho ứng dụng desktop. Bạn vẫn nghe được mọi người."
        : n === "NotFoundError" ? "Máy không có micro — bạn vẫn nghe được mọi người."
          : n === "NotReadableError" ? "Micro đang bị ứng dụng khác giữ (Zoom / Teams…) — tắt bên đó rồi bật lại mic."
            : "Không mở được micro: " + (e?.message || e), 7000);
      return false;
    } finally { VC.busy = false; }
  }

  function stopMic() {
    VC.mic = false;
    if (VC.raw) { VC.raw.getTracks().forEach((t) => t.stop()); VC.raw = null; }
    if (VC.fakeStop) { VC.fakeStop(); VC.fakeStop = null; }
    if (VC.ctx) { try { VC.ctx.close(); } catch { } VC.ctx = null; }
    VC.node = null; VC.an = null; VC.track = null; VC.worklet = null; VC.last = null;
  }

  async function micOff() {
    stopMic();
    await setSenders(null);
  }

  async function setSenders(track) {
    for (const pc of VC.pcs.values()) {
      const tr = pc.getTransceivers()[0];
      if (tr && tr.sender && pc.signalingState !== "closed") try { await tr.sender.replaceTrack(track); } catch { }
    }
  }

  // ───────────── vào / rời voice ─────────────

  async function join() {
    if (VC.on) return;
    VC.on = true;
    render();
    const ok = await micOn();
    if (!VC.on) { stopMic(); return; }   // bấm rời trong lúc chờ quyền micro
    send({ t: "voice", v: ok ? 2 : 1 });
    render();
  }

  function leave(silent) {
    if (!VC.on && !silent) return;
    VC.on = false;
    stopMic();
    for (const id of [...VC.pcs.keys()]) drop(id);
    for (const id of [...VC.peers.keys()]) dropAudio(id);
    VC.speaking.clear(); Theater.setSpeaking(VC.speaking);
    if (VC.duck) { VC.duck = false; applyVolume(); }
    if (!silent) send({ t: "voice", v: 0 });
    render();
  }

  async function toggleMic() {
    if (!VC.on) { join(); return; }
    if (VC.busy) return;
    if (VC.mic) { await micOff(); send({ t: "voice", v: 1 }); }
    else if (await micOn()) send({ t: "voice", v: 2 });
    render();
  }

  // ───────────── kết nối máy-máy ─────────────

  /** Gọi mỗi khi danh sách người xem đổi: nối tới người mới vào voice, bỏ người đã rời. */
  function sync() {
    const want = VC.on && (meMsg()?.voice || 0) > 0 ? S.viewers.filter((v) => v.id !== S.me && v.voice > 0) : [];
    const ids = new Set(want.map((v) => v.id));
    for (const id of [...VC.pcs.keys()]) if (!ids.has(id)) drop(id);
    for (const id of [...VC.peers.keys()]) if (!ids.has(id)) dropAudio(id);
    for (const v of want) if (!VC.pcs.has(v.id) && S.me < v.id) offer(v.id, 0);
    render();
  }

  function makePc(id, sid) {
    const pc = new RTCPeerConnection({ iceServers: [] });
    pc.sid = sid; pc.q = [];
    VC.pcs.set(id, pc);
    pc.onicecandidate = (e) => { if (e.candidate) send({ t: "vc", to: id, data: { k: "ice", sid: pc.sid, c: e.candidate.toJSON() } }); };
    pc.ontrack = (e) => { if (VC.pcs.get(id) === pc) attachAudio(id, e.streams[0] || new MediaStream([e.track])); };
    pc.onconnectionstatechange = () => {
      if (VC.pcs.get(id) !== pc) return;
      // nối hỏng (tường lửa / mạng chập chờn): bên gửi offer thử lại tối đa 3 lần
      if (pc.connectionState === "failed" && S.me < id && (pc.tries || 0) < 3) setTimeout(() => { if (VC.pcs.get(id) === pc) offer(id, (pc.tries || 0) + 1); }, 1500);
      renderPeople();
    };
    return pc;
  }

  async function offer(id, tries) {
    drop(id);
    const pc = makePc(id, `${S.me}.${++VC.seq}`);
    pc.tries = tries;
    try {
      const tr = pc.addTransceiver("audio", { direction: "sendrecv" });
      if (VC.track) await tr.sender.replaceTrack(VC.track);
      await pc.setLocalDescription(await pc.createOffer());
      if (VC.pcs.get(id) !== pc) return;
      send({ t: "vc", to: id, data: { k: "offer", sid: pc.sid, sdp: pc.localDescription.sdp } });
    } catch (e) { console.error("vc offer", e); }
  }

  async function flush(pc) { for (const c of pc.q.splice(0)) try { await pc.addIceCandidate(c); } catch { } }

  async function onVc(from, d) {
    if (!VC.on || !d) return;
    try {
      if (d.k === "offer") {
        drop(from);
        const pc = makePc(from, d.sid);
        await pc.setRemoteDescription({ type: "offer", sdp: d.sdp });
        const tr = pc.getTransceivers()[0];
        if (tr) { tr.direction = "sendrecv"; if (VC.track) await tr.sender.replaceTrack(VC.track); }
        await flush(pc);
        await pc.setLocalDescription(await pc.createAnswer());
        if (VC.pcs.get(from) === pc) send({ t: "vc", to: from, data: { k: "answer", sid: pc.sid, sdp: pc.localDescription.sdp } });
      } else if (d.k === "answer") {
        const pc = VC.pcs.get(from);
        if (!pc || pc.sid !== d.sid || pc.signalingState !== "have-local-offer") return;
        await pc.setRemoteDescription({ type: "answer", sdp: d.sdp });
        await flush(pc);
      } else if (d.k === "ice") {
        const pc = VC.pcs.get(from);
        if (!pc || pc.sid !== d.sid || !d.c) return;
        if (!pc.remoteDescription) pc.q.push(d.c); else try { await pc.addIceCandidate(d.c); } catch { }
      }
    } catch (e) { console.error("vc", d.k, e); }
  }

  function drop(id) {
    const pc = VC.pcs.get(id);
    if (!pc) return;
    VC.pcs.delete(id);
    try { pc.close(); } catch { }
  }

  // ───────────── nghe người khác ─────────────

  function meterCtx() {
    if (!VC.meterCtx || VC.meterCtx.state === "closed") VC.meterCtx = new AudioContext();
    return VC.meterCtx;
  }

  function attachAudio(id, stream) {
    let p = VC.peers.get(id);
    if (!p) {
      const audio = document.createElement("audio");
      audio.autoplay = true; audio.className = "vc-audio";
      document.body.appendChild(audio);
      p = { audio, src: null, an: null, loudAt: 0 };
      VC.peers.set(id, p);
    }
    p.audio.srcObject = stream;
    applyPeerVolume(id);
    p.audio.play().catch(() => { });
    // đo mức tiếng (luồng WebRTC phải đang gắn vào thẻ audio thì WebAudio mới đọc được — đã gắn ở trên)
    try {
      const ctx = meterCtx();
      try { p.src?.disconnect(); } catch { }
      p.src = ctx.createMediaStreamSource(stream);
      p.an = ctx.createAnalyser(); p.an.fftSize = 1024;
      p.src.connect(p.an);
    } catch { }
  }

  function dropAudio(id) {
    const p = VC.peers.get(id);
    if (!p) return;
    VC.peers.delete(id);
    try { p.src?.disconnect(); } catch { }
    p.audio.srcObject = null; p.audio.remove();
  }

  function applyPeerVolume(id) {
    const p = VC.peers.get(id);
    if (p) p.audio.volume = VC.vols.has(id) ? VC.vols.get(id) : 1;
  }

  // ───────────── đang nói / giảm tiếng phim ─────────────

  const buf = new Float32Array(1024);
  function rms(an) {
    if (!an) return 0;
    an.getFloatTimeDomainData(buf);
    let s = 0; for (let i = 0; i < buf.length; i++) s += buf[i] * buf[i];
    return Math.sqrt(s / buf.length);
  }

  setInterval(() => {
    if (!VC.on) return;
    const now = performance.now();
    const set = new Set();
    if (VC.mic) {
      const lv = rms(VC.an);
      VC.levelMe = lv;
      if (lv > SPEAK) VC.loudMe = now;
      if (now - VC.loudMe < HOLD) set.add(S.me);
      const bar = $("micLevel").firstElementChild;
      if (bar) bar.style.width = Math.min(100, Math.round(lv * 600)) + "%";
    }
    for (const [id, p] of VC.peers) {
      p.level = rms(p.an);
      if (p.level > SPEAK) p.loudAt = now;
      if (now - p.loudAt < HOLD) set.add(id);
    }
    const changed = set.size !== VC.speaking.size || [...set].some((id) => !VC.speaking.has(id));
    VC.speaking = set;
    Theater.setSpeaking(set);
    const duck = [...set].some((id) => id !== S.me);
    if (duck !== VC.duck) { VC.duck = duck; applyVolume(); }
    if (changed) renderPeople();
  }, 100);

  // ───────────── giao diện ─────────────

  function render() {
    const me = meMsg();
    const inVc = VC.on;
    const b = $("btnVoice");
    b.classList.toggle("on", inVc);
    b.textContent = inVc ? "🎧 Rời voice" : "🎧 Voice";
    b.title = inVc ? "Rời voice — thôi nghe / nói với mọi người" : "Vào voice: nói chuyện với cả phòng qua mạng LAN (như Google Meet). Nên dùng tai nghe để tiếng phim không lọt vào mic.";
    $("btnMic").classList.toggle("hidden", !inVc);
    $("btnMic").textContent = VC.mic ? "🎙" : "🔇";
    $("btnMic").classList.toggle("off", !VC.mic);
    $("btnMic").title = VC.mic ? "Đang bật mic — bấm để tắt (V)" : "Đang tắt mic — bấm để bật (V)";
    $("selNoise").classList.toggle("hidden", !inVc);
    $("selNoise").value = VC.noise;
    $("micLevel").classList.toggle("hidden", !inVc || !VC.mic);
    $("selMic").classList.toggle("hidden", !inVc || $("selMic").options.length < 2);
    if (me && !inVc && me.voice > 0) send({ t: "voice", v: 0 });   // trang nạp lại / lệch trạng thái
  }

  async function fillMics() {
    try {
      const list = (await navigator.mediaDevices.enumerateDevices()).filter((d) => d.kind === "audioinput");
      const sel = $("selMic");
      sel.innerHTML = list.map((d, i) => `<option value="${esc(d.deviceId)}">🎤 ${esc(d.label || "Micro " + (i + 1))}</option>`).join("");
      const cur = VC.raw?.getAudioTracks()[0]?.getSettings().deviceId;
      sel.value = cur || VC.micId || "default";
      render();
    } catch { }
  }

  $("btnVoice").onclick = () => { if (VC.on) leave(false); else join(); };
  $("btnMic").onclick = () => toggleMic();
  $("selNoise").onchange = async () => {
    VC.noise = $("selNoise").value;
    post({ t: "ui.prefs", noise: VC.noise });
    if (VC.mic) { stopMic(); await micOn(); }
    toast({ strong: "Lọc ồn mạnh (AI — RNNoise): lọc tiếng gõ phím, quạt, người nói xa.", basic: "Lọc ồn cơ bản của trình duyệt.", off: "Tắt lọc ồn." }[VC.noise]);
  };
  $("selMic").onchange = async () => {
    VC.micId = $("selMic").value;
    post({ t: "ui.prefs", micId: VC.micId });
    if (VC.mic) { stopMic(); await micOn(); }
  };

  return {
    get on() { return VC.on; },
    get speaking() { return VC.speaking; },
    duck: () => VC.duck,
    init(m) { VC.noise = m.noise || "strong"; VC.micId = m.micId || ""; VC.fake = m.fakeMic && m.fakeMic !== "1" ? m.fakeMic : m.fakeMic ? "voice" : null; render(); },
    sync, onVc, toggleMic, join,
    /** Chủ phòng tắt mic mình (tin "vcmute") — máy chủ phòng đã ghi voice = 1; mình dừng micro, bật lại bằng 🔇 / V khi cần. */
    async forceMute(by) {
      if (!VC.on) return;
      if (VC.mic) await micOff();
      send({ t: "voice", v: 1 });
      render();
      toast(`Chủ phòng ${by || ""} đã tắt mic của bạn — bấm 🔇 (hoặc phím V) để bật lại khi cần nói.`, 6000);
    },
    leave: () => leave(false),
    reset: () => leave(true),   // rời phòng / mất kết nối: dọn hết, không gửi gì
    peerVolume(id, v) { VC.vols.set(id, v); applyPeerVolume(id); },
    peerVolumeOf: (id) => VC.vols.has(id) ? VC.vols.get(id) : 1,
    /** Cho tab 👥: 🎙 / 🔇 + chấm xanh khi đang nói. */
    badge(v) {
      if (!v.voice) return "";
      const talk = VC.speaking.has(v.id);
      return `<span class="vc-badge${talk ? " talk" : ""}" title="${v.voice === 2 ? "Trong voice, đang bật mic" : "Trong voice, tắt mic"}">${v.voice === 2 ? "🎙" : "🔇"}</span>`;
    },
    /** Thử tự động: trạng thái kết nối + byte nhận + mức tiếng. */
    async stats() {
      const out = { on: VC.on, mic: VC.mic, noise: VC.noise, worklet: !!VC.node, last: VC.last, levelMe: VC.levelMe || 0, speaking: [...VC.speaking], duck: VC.duck, peers: {} };
      for (const [id, pc] of VC.pcs) {
        let rx = 0, tx = 0, lvl = 0;
        try { (await pc.getStats()).forEach((r) => { if (r.type === "inbound-rtp" && r.kind === "audio") { rx = r.bytesReceived; lvl = r.audioLevel ?? 0; } if (r.type === "outbound-rtp" && r.kind === "audio") tx = r.bytesSent; }); } catch { }
        out.peers[id] = { state: pc.connectionState, rx, tx, lvl, level: VC.peers.get(id)?.level || 0 };
      }
      return out;
    },
  };
})();
