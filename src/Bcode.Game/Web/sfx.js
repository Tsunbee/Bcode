// Âm thanh tổng hợp bằng WebAudio (không có file âm thanh nào) — tự viết, ngắn gọn.
(function () {
  let ctx = null;
  let muted = false;
  try { muted = localStorage.getItem("bcgame.mute") === "1"; } catch { }

  function ac() {
    if (!ctx) { try { ctx = new AudioContext(); } catch { return null; } }
    if (ctx.state === "suspended") ctx.resume();
    return ctx;
  }

  function tone(freq, dur, type = "sine", vol = 0.12, when = 0, slide = 0) {
    if (muted) return;
    const a = ac(); if (!a) return;
    const t0 = a.currentTime + when;
    const o = a.createOscillator(), g = a.createGain();
    o.type = type; o.frequency.setValueAtTime(freq, t0);
    if (slide) o.frequency.exponentialRampToValueAtTime(Math.max(30, freq + slide), t0 + dur);
    g.gain.setValueAtTime(0.0001, t0);
    g.gain.exponentialRampToValueAtTime(vol, t0 + 0.012);
    g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
    o.connect(g).connect(a.destination);
    o.start(t0); o.stop(t0 + dur + 0.02);
  }

  const S = {
    get muted() { return muted; },
    toggle() { muted = !muted; try { localStorage.setItem("bcgame.mute", muted ? "1" : "0"); } catch { } return muted; },
    click() { tone(660, 0.05, "square", 0.04); },
    ok() { tone(660, 0.09, "triangle", 0.12); tone(990, 0.16, "triangle", 0.12, 0.08); },
    done() { [523, 659, 784, 1046].forEach((f, i) => tone(f, 0.14, "triangle", 0.11, i * 0.07)); },
    wrong() { tone(180, 0.22, "sawtooth", 0.07); },
    msg() { tone(880, 0.07, "sine", 0.1); tone(1320, 0.1, "sine", 0.08, 0.07); },
    kill() { tone(140, 0.35, "sawtooth", 0.16, 0, -90); tone(70, 0.4, "square", 0.08, 0.05); },
    dead() { tone(300, 0.5, "triangle", 0.12, 0, -200); },
    meeting() { for (let i = 0; i < 3; i++) { tone(740, 0.16, "square", 0.08, i * 0.34); tone(554, 0.16, "square", 0.08, i * 0.34 + 0.17); } },
    vote() { tone(520, 0.06, "square", 0.05); },
    bug() { tone(240, 0.08, "square", 0.05); tone(200, 0.08, "square", 0.05, 0.1); },
    clue() { tone(988, 0.12, "sine", 0.09); tone(1318, 0.2, "sine", 0.07, 0.1); },
    win() { [523, 659, 784, 1046, 1318].forEach((f, i) => tone(f, 0.22, "triangle", 0.12, i * 0.11)); },
    lose() { [392, 349, 311, 262].forEach((f, i) => tone(f, 0.3, "sawtooth", 0.06, i * 0.18)); },
    step() { tone(90 + Math.random() * 30, 0.03, "sine", 0.025); },
  };
  window.Sfx = S;
})();
