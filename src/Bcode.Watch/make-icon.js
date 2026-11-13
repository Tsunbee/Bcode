// Dựng watch.ico cho Rạp Bcode từ logo.svg (40–256 px) + logo-small.svg (16–32 px).
// Edge headless vẽ SVG vào canvas từng cỡ (vector → nét ở mọi cỡ), in data URL PNG ra DOM (--dump-dom), rồi gói thành ICO (mục PNG).
// Chạy: node src/Bcode.Watch/make-icon.js   (cần Microsoft Edge; ảnh PNG từng cỡ để xem thử nằm trong %TEMP%/bcode-watch-icon/png)
const fs = require("fs"), path = require("path"), cp = require("child_process");
const os = require("os");
const web = path.join(__dirname, "Web"), outIco = path.join(__dirname, "watch.ico");
const work = path.join(os.tmpdir(), "bcode-watch-icon"), outDir = path.join(work, "png");
const SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256];
fs.mkdirSync(work, { recursive: true });
fs.mkdirSync(outDir, { recursive: true });
const big = "data:image/svg+xml;base64," + fs.readFileSync(path.join(web, "logo.svg")).toString("base64");
const small = "data:image/svg+xml;base64," + fs.readFileSync(path.join(web, "logo-small.svg")).toString("base64");
const html = `<!doctype html><body><pre id=o></pre><script>
(async () => {
  const load = (src) => new Promise((r, j) => { const i = new Image(); i.onload = () => r(i); i.onerror = j; i.src = src; });
  const B = await load(${JSON.stringify(big)}), S = await load(${JSON.stringify(small)});
  const out = {};
  for (const s of ${JSON.stringify(SIZES)}) {
    const c = document.createElement("canvas"); c.width = c.height = s;
    const g = c.getContext("2d"); g.imageSmoothingQuality = "high";
    g.drawImage(s <= 32 ? S : B, 0, 0, s, s);
    out[s] = c.toDataURL("image/png");
  }
  document.getElementById("o").textContent = "@@" + JSON.stringify(out) + "@@";
})();
</script></body>`;
const page = path.join(work, "render.html");
fs.writeFileSync(page, html);
const edge = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const dom = cp.execFileSync(edge, ["--headless=new", "--disable-gpu", "--no-first-run", "--user-data-dir=" + path.join(work, "edge"),
  "--virtual-time-budget=8000", "--dump-dom", "file:///" + page.replace(/\\/g, "/")], { maxBuffer: 64 << 20 }).toString();
const m = dom.match(/<pre id="o">@@(.*?)@@<\/pre>/s);
if (!m) { console.error("không lấy được ảnh:\n" + dom.slice(0, 500)); process.exit(1); }
const pngs = JSON.parse(m[1].replace(/&quot;/g, '"').replace(/&amp;/g, "&"));
const bufs = SIZES.map((s) => { const b = Buffer.from(pngs[s].split(",")[1], "base64"); fs.writeFileSync(path.join(outDir, `logo-${s}.png`), b); return b; });
// ICO: ICONDIR (6 byte) + ICONDIRENTRY 16 byte / ảnh + dữ liệu PNG
const head = Buffer.alloc(6 + 16 * SIZES.length);
head.writeUInt16LE(0, 0); head.writeUInt16LE(1, 2); head.writeUInt16LE(SIZES.length, 4);
let off = head.length;
SIZES.forEach((s, i) => {
  const e = 6 + 16 * i;
  head.writeUInt8(s >= 256 ? 0 : s, e); head.writeUInt8(s >= 256 ? 0 : s, e + 1);
  head.writeUInt8(0, e + 2); head.writeUInt8(0, e + 3);
  head.writeUInt16LE(1, e + 4); head.writeUInt16LE(32, e + 6);
  head.writeUInt32LE(bufs[i].length, e + 8); head.writeUInt32LE(off, e + 12);
  off += bufs[i].length;
});
fs.writeFileSync(outIco, Buffer.concat([head, ...bufs]));
console.log("ok", outIco, fs.statSync(outIco).size, "byte;", SIZES.map((s, i) => s + ":" + bufs[i].length).join(" "));
