"use strict";
// Lọc tạp âm cho chat voice — chạy trong AudioWorklet (luồng âm thanh riêng, không giật giao diện).
// RNNoise (vendor/rnnoise/rnnoise.wasm, xem README ở đó): mạng nơ-ron nhỏ tách tiếng người khỏi tiếng ồn (quạt, gõ phím, xe, người nói xa),
// mỗi khung 480 mẫu = 10 ms ở 48 kHz, trả kèm xác suất "đang có tiếng nói" (VAD).
// Sau RNNoise thêm "cổng tiếng" như Discord: không có tiếng nói (VAD thấp) quá 250 ms thì hạ còn 10% → phần ồn sót lại giữa các câu biến mất.
// Trễ thêm ~10–13 ms (gom đủ khung 480 từ các khối 128 mẫu).
// Chế độ (port.postMessage({mode})): "strong" = RNNoise + cổng; khác = cho qua nguyên vẹn.
// Gửi về trang mỗi 100 ms: { vad, inRms, outRms } — để đo / thử (lọc bớt được bao nhiêu).

const FRAME = 480;
const Q = FRAME * 8;

class RnnoiseProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super();
    const po = options.processorOptions || {};
    this.mode = po.mode || "strong";
    this.ok = false;
    try {
      let mem = null;
      const imports = {
        a: {
          // emscripten_resize_heap: nới bộ nhớ wasm khi cần (thực tế 16 MB ban đầu là đủ)
          a: (size) => { try { mem.grow((((size >>> 0) - mem.buffer.byteLength) + 65535) >>> 16); return 1; } catch { return 0; } },
          // emscripten_memcpy_big
          b: (dest, src, num) => { new Uint8Array(mem.buffer).copyWithin(dest, src, src + num); },
        },
      };
      const inst = new WebAssembly.Instance(new WebAssembly.Module(po.wasm), imports);
      const ex = inst.exports;
      mem = ex.c;
      ex.d();                         // __wasm_call_ctors
      this.ex = ex;
      this.st = ex.f(0);              // rnnoise_create(mô hình mặc định)
      this.pIn = ex.g(FRAME * 4);     // malloc
      this.pOut = ex.g(FRAME * 4);
      this.ok = !!this.st;
    } catch (e) {
      this.port.postMessage({ error: String(e && e.message || e) });
    }
    this.inBuf = new Float32Array(FRAME); this.inN = 0;
    this.outQ = new Float32Array(Q); this.qR = 0; this.qW = 0; this.primed = false;
    this.gain = 1; this.hold = 0;
    this.mVad = 0; this.mIn = 0; this.mOut = 0; this.mN = 0;
    this.port.onmessage = (e) => { if (e.data && e.data.mode) this.mode = e.data.mode; };
  }

  frame() {
    const heap = new Float32Array(this.ex.c.buffer);
    const pi = this.pIn >> 2, po = this.pOut >> 2;
    let sIn = 0;
    for (let i = 0; i < FRAME; i++) { const x = this.inBuf[i]; sIn += x * x; heap[pi + i] = x * 32768; }
    const vad = this.ex.j(this.st, this.pOut, this.pIn);   // rnnoise_process_frame(state, out, in)
    if (vad >= 0.5) this.hold = 25; else if (this.hold > 0) this.hold--;
    const target = this.hold > 0 ? 1 : 0.1;
    let sOut = 0;
    for (let i = 0; i < FRAME; i++) {
      // mở cổng nhanh (~2 ms), đóng chậm (~40 ms) — không cắt đầu chữ, không "phụt" khi tắt
      this.gain += (target - this.gain) * (target > this.gain ? 0.01 : 0.0005);
      const y = heap[po + i] / 32768 * this.gain;
      sOut += y * y;
      this.outQ[this.qW] = y; this.qW = (this.qW + 1) % Q;
    }
    this.meter(vad, sIn / FRAME, sOut / FRAME);
  }

  meter(vad, pin, pout) {
    this.mVad = Math.max(this.mVad, vad); this.mIn += pin; this.mOut += pout; this.mN++;
    if (this.mN >= 10) {
      this.port.postMessage({ vad: this.mVad, inRms: Math.sqrt(this.mIn / this.mN), outRms: Math.sqrt(this.mOut / this.mN) });
      this.mVad = 0; this.mIn = 0; this.mOut = 0; this.mN = 0;
    }
  }

  process(inputs, outputs) {
    const out = outputs[0][0];
    const inp = inputs[0] && inputs[0][0];
    if (!out) return true;
    if (!inp) { out.fill(0); return true; }
    if (this.mode !== "strong" || !this.ok) {
      out.set(inp);
      let s = 0; for (let i = 0; i < inp.length; i++) s += inp[i] * inp[i];
      if (this.mN === 0) this.mVad = 0;
      this.mIn += s / inp.length; this.mOut += s / inp.length; this.mN += 128 / FRAME;
      if (this.mN >= 10) { this.port.postMessage({ vad: -1, inRms: Math.sqrt(this.mIn / this.mN), outRms: Math.sqrt(this.mOut / this.mN) }); this.mIn = 0; this.mOut = 0; this.mN = 0; }
      return true;
    }
    for (let i = 0; i < inp.length; i++) {
      this.inBuf[this.inN++] = inp[i];
      if (this.inN === FRAME) { this.frame(); this.inN = 0; }
    }
    const avail = (this.qW - this.qR + Q) % Q;
    if (!this.primed) { if (avail < FRAME) { out.fill(0); return true; } this.primed = true; }
    for (let i = 0; i < out.length; i++) {
      if (this.qR === this.qW) { out[i] = 0; continue; }
      out[i] = this.outQ[this.qR]; this.qR = (this.qR + 1) % Q;
    }
    return true;
  }
}

registerProcessor("rnnoise", RnnoiseProcessor);
