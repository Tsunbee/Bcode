rnnoise.wasm — bộ lọc tạp âm RNNoise (mạng nơ-ron nhỏ, chạy ngay trên máy, không gửi âm thanh đi đâu) cho chat voice của Rạp Bcode.

Nguồn: gói npm @jitsi/rnnoise-wasm 0.2.1 (dist/rnnoise.wasm, 112 141 byte) — https://github.com/jitsi/rnnoise-wasm
  Giấy phép bản dựng: Apache-2.0 (LICENSE-jitsi-rnnoise-wasm.txt).
  RNNoise gốc: https://github.com/xiph/rnnoise — BSD-3-Clause, Copyright (c) 2017 Mozilla, 2007-2017 Jean-Marc Valin, Xiph.Org Foundation.

Chỉ dùng file .wasm (KHÔNG dùng lớp keo emscripten rnnoise.js): Web/voice-worklet.js tự nạp wasm trong AudioWorklet.
Wasm cần 2 hàm nhập: a.a = emscripten_resize_heap(size) (nới bộ nhớ), a.b = emscripten_memcpy_big(dest, src, n).
Hàm xuất: c = memory, d = __wasm_call_ctors, f = rnnoise_create(model=0), g = malloc, h = rnnoise_destroy, i = free,
j = rnnoise_process_frame(state, out*, in*) → xác suất có tiếng nói (0..1). Khung 480 mẫu float (48 kHz, 10 ms), biên độ thang int16.
