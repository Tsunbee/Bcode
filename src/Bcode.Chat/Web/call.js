// Bhoo — gọi thoại / video 1-1 trong LAN bằng WebRTC (không máy chủ STUN / TURN: 2 máy cùng mạng nối thẳng nhau).
// Tín hiệu (đổ chuông, SDP, ICE) đi qua kết nối Bhoo: trang → C# ('rtc') → máy kia → trang bên đó ({t:'rtc', op}).
// Tiếng chuông tự tổng hợp bằng WebAudio. Lọc ồn / vọng tiếng dùng sẵn của trình duyệt (echoCancellation, noiseSuppression).
'use strict';

const Call = (() => {
  let st = null;   // { cid, video, mine, phase: 'ringing'|'incoming'|'connecting'|'live', pc, local, remote, timer, t0, pendingIce }
  let ringCtx = null, ringT = 0;

  const box = () => $('#callBox');
  const newCid = () => Math.random().toString(36).slice(2) + Date.now().toString(36);
  const sig = (op, extra) => send('rtc', Object.assign({ op, cid: st && st.cid }, extra || {}));

  // ── chuông: 2 tiếng "tút" kiểu điện thoại bàn; gọi đi thì chuông chờ nhẹ hơn
  function ring(incoming) {
    stopRing();
    try { ringCtx = new AudioContext(); } catch { return; }
    const beep = () => {
      if (!ringCtx) return;
      const t = ringCtx.currentTime;
      const tone = (f, at, dur) => {
        const o = ringCtx.createOscillator(), g = ringCtx.createGain();
        o.type = 'sine'; o.frequency.value = f; o.connect(g); g.connect(ringCtx.destination);
        g.gain.setValueAtTime(0, t + at); g.gain.linearRampToValueAtTime(incoming ? .18 : .07, t + at + .02);
        g.gain.setValueAtTime(incoming ? .18 : .07, t + at + dur - .03); g.gain.linearRampToValueAtTime(0, t + at + dur);
        o.start(t + at); o.stop(t + at + dur + .05);
      };
      if (incoming) { tone(880, 0, .35); tone(660, 0, .35); tone(880, .5, .35); tone(660, .5, .35); }
      else tone(425, 0, 1.0);
    };
    beep(); ringT = setInterval(beep, incoming ? 2200 : 3500);
  }
  function stopRing() { clearInterval(ringT); ringT = 0; if (ringCtx) { try { ringCtx.close(); } catch { } ringCtx = null; } }

  async function media(video) {
    const audio = { echoCancellation: true, noiseSuppression: true, autoGainControl: true };
    try { return await navigator.mediaDevices.getUserMedia({ audio, video: video ? { width: 640, height: 480 } : false }); }
    catch (e) {
      if (video) { try { toast('Không mở được camera — gọi thoại thôi.', 4000); return await navigator.mediaDevices.getUserMedia({ audio, video: false }); } catch { } }
      throw new Error('Không mở được micro' + (e && e.name === 'NotFoundError' ? ' (máy chưa có micro)' : '') + '.');
    }
  }

  function makePc() {
    const pc = new RTCPeerConnection({ iceServers: [] });
    pc.onicecandidate = e => { if (e.candidate) sig('ice', { cand: e.candidate.toJSON() }); };
    pc.ontrack = e => {
      st.remote = e.streams[0];
      const v = $('#cRemote'); if (v && v.srcObject !== st.remote) { v.srcObject = st.remote; v.play().catch(() => { }); }
      render();
    };
    pc.onconnectionstatechange = () => {
      if (!st) return;
      if (pc.connectionState === 'connected' && st.phase !== 'live') { st.phase = 'live'; st.t0 = Date.now(); stopRing(); render(); }
      if (pc.connectionState === 'failed') { toast('Mất kết nối cuộc gọi (tường lửa hoặc khác mạng?).', 5000); hangup(); }
      if (pc.connectionState === 'disconnected') setTimeout(() => { if (st && st.pc === pc && pc.connectionState === 'disconnected') hangup(); }, 6000);
    };
    st.local.getTracks().forEach(t => pc.addTrack(t, st.local));
    return pc;
  }

  async function start(video) {
    if (st) { toast('Đang có cuộc gọi.'); return; }
    st = { cid: newCid(), video, mine: true, phase: 'ringing', pendingIce: [] };
    render();
    try { st.local = await media(video); }
    catch (e) { toast(e.message, 5000); cleanup(); return; }
    st.video = st.local.getVideoTracks().length > 0;
    sig('ring', { video: st.video });
    ring(false);
    render();
    st.noAnswer = setTimeout(() => { if (st && st.phase === 'ringing') { toast('Không ai nghe máy.'); hangup(); } }, 40000);
  }

  async function accept(withVideo) {
    if (!st || st.phase !== 'incoming') return;
    stopRing();
    st.phase = 'connecting'; render();
    try { st.local = await media(withVideo); }
    catch (e) { toast(e.message, 5000); sig('decline'); cleanup(); return; }
    st.pc = makePc();
    sig('accept', { video: st.local.getVideoTracks().length > 0 });
    render();
  }

  function decline() { if (!st) return; sig('decline'); cleanup(); }
  function hangup() { if (!st) return; sig('hangup'); cleanup(); }

  function cleanup() {
    stopRing();
    if (st) {
      clearTimeout(st.noAnswer); clearInterval(st.timer);
      try { st.pc && st.pc.close(); } catch { }
      st.local && st.local.getTracks().forEach(t => t.stop());
    }
    st = null;
    document.body.classList.remove('incall');
    render();
  }

  async function onSignal(m) {
    switch (m.op) {
      case 'ring':
        if (st) return;   // C# đã báo bận nếu cần
        st = { cid: m.cid, video: !!m.video, mine: false, phase: 'incoming', pendingIce: [] };
        ring(true); render();
        break;
      case 'accept':
        if (!st || !st.mine || m.cid !== st.cid) return;
        clearTimeout(st.noAnswer); stopRing();
        st.phase = 'connecting'; render();
        st.pc = makePc();
        const offer = await st.pc.createOffer();
        await st.pc.setLocalDescription(offer);
        sig('offer', { sdp: offer.sdp });
        break;
      case 'offer':
        if (!st || !st.pc || m.cid !== st.cid) return;
        await st.pc.setRemoteDescription({ type: 'offer', sdp: m.sdp });
        for (const c of st.pendingIce.splice(0)) { try { await st.pc.addIceCandidate(c); } catch { } }
        const ans = await st.pc.createAnswer();
        await st.pc.setLocalDescription(ans);
        sig('answer', { sdp: ans.sdp });
        break;
      case 'answer':
        if (!st || !st.pc || m.cid !== st.cid) return;
        await st.pc.setRemoteDescription({ type: 'answer', sdp: m.sdp });
        for (const c of st.pendingIce.splice(0)) { try { await st.pc.addIceCandidate(c); } catch { } }
        break;
      case 'ice':
        if (!st || m.cid !== st.cid || !m.cand) return;
        if (!st.pc || !st.pc.remoteDescription) st.pendingIce.push(m.cand);
        else { try { await st.pc.addIceCandidate(m.cand); } catch { } }
        break;
      case 'decline': if (st && m.cid === st.cid) { toast(`${title} đã từ chối cuộc gọi.`); cleanup(); } break;
      case 'busy': if (st && m.cid === st.cid) { toast(`${title} đang trong cuộc gọi khác.`); cleanup(); } break;
      case 'hangup': if (st && m.cid === st.cid) cleanup(); break;
      case 'error': toast(m.text, 5000); if (st && st.cid === m.cid && st.phase === 'ringing') cleanup(); break;
    }
  }

  function toggle(kind) {
    if (!st || !st.local) return;
    const tr = kind === 'mic' ? st.local.getAudioTracks() : st.local.getVideoTracks();
    tr.forEach(t => t.enabled = !t.enabled);
    render();
  }

  function fmtDur(ms) { const s = Math.floor(ms / 1000); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; }

  function render() {
    const b = box();
    if (!b) return;
    if (!st) { b.innerHTML = ''; b.hidden = true; document.body.classList.remove('incall'); return; }
    b.hidden = false;
    document.body.classList.add('incall');
    const peer = buddy(peerId()) || { name: title, color: '#3a6ea5' };
    const micOn = st.local ? st.local.getAudioTracks().some(t => t.enabled) : true;
    const camOn = st.local ? st.local.getVideoTracks().some(t => t.enabled) : false;
    const hasCam = st.local ? st.local.getVideoTracks().length > 0 : false;
    const remoteVideo = st.remote && st.remote.getVideoTracks().length > 0;
    const status = { ringing: 'Đang gọi…', incoming: (st.video ? 'Gọi video cho bạn…' : 'Đang gọi cho bạn…'), connecting: 'Đang nối…', live: '' }[st.phase];
    if (!b.firstChild) {
      b.innerHTML = `<div class="cv"><video id="cRemote" autoplay playsinline></video><div class="cpic"></div><video id="cLocal" autoplay playsinline muted></video>
        <div class="cst"></div></div><div class="cbtns"></div>`;
    }
    const rv = $('#cRemote', b), lv = $('#cLocal', b);
    if (st.remote && rv.srcObject !== st.remote) { rv.srcObject = st.remote; rv.play().catch(() => { }); }
    if (st.local && lv.srcObject !== st.local) lv.srcObject = st.local;
    rv.style.display = remoteVideo ? '' : 'none';
    lv.style.display = hasCam && camOn ? '' : 'none';
    const pic = $('.cpic', b);
    pic.style.display = remoteVideo ? 'none' : '';
    pic.setAttribute('style', (remoteVideo ? 'display:none;' : '') + avStyle(peer));
    pic.innerHTML = peer.av ? '' : esc((peer.name || '?')[0].toUpperCase());
    const stEl = $('.cst', b);
    stEl.innerHTML = `<b>${esc(peer.name)}</b><br><span class="ctime">${st.phase === 'live' ? fmtDur(Date.now() - st.t0) : status}</span>`;
    if (st.phase === 'live' && !st.timer) st.timer = setInterval(() => { const t = $('.ctime', box()); if (t && st) t.textContent = fmtDur(Date.now() - st.t0); }, 1000);
    const btns = $('.cbtns', b);
    btns.innerHTML = '';
    const B = (label, cls, on, tip) => { const x = el('button', { class: 'btn ' + (cls || ''), title: tip || '' }, label); x.onclick = on; btns.append(x); };
    if (st.phase === 'incoming') {
      B('📞 Nghe', 'primary ok', () => accept(false), 'Nghe máy (chỉ tiếng)');
      if (st.video) B('📹 Nghe + hình', 'ok', () => accept(true), 'Nghe máy và bật camera của bạn');
      B('Từ chối', 'bad', decline);
    } else {
      B(micOn ? '🎙 Tắt mic' : '🔇 Bật mic', '', () => toggle('mic'));
      if (hasCam) B(camOn ? '📷 Tắt hình' : '📷 Bật hình', '', () => toggle('cam'));
      B('📵 Gác máy', 'bad', hangup);
    }
  }

  return { start, onSignal, hangup, get active() { return !!st; } };
})();
