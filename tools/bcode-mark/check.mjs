#!/usr/bin/env node
// Kiểm tra thuỷ ấn truy nguồn Bcode trong một bản nghi ngờ là clone.
// Dò dấu bí mật "BCWM1:<12 hex>" và GUID dự án trong mọi file (exe/dll/html/js/log...) của một thư mục hoặc 1 file.
// So dấu tìm được với dấu của CHÍNH BẠN: mở Bcode bạn phát hành, xem Trợ giúp hoặc dòng "BcodeMark ..." ở đầu %AppData%\Bcode\error.log.
//
//   node check.mjs <đường-dẫn-thư-mục-hoặc-file> [--mine <dấu-của-bạn>]
//
// KHÔNG cần key gốc: công cụ chỉ TRÍCH dấu rồi so khớp; dấu là băm một chiều nên không suy ngược ra key.
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, extname } from 'node:path';

const PROJECT_GUID = 'b3c0de00-0000-4bee-b3c0-deb3c0deb3c0';
const TOKEN = /BCWM1:([0-9a-f]{12})/g;
const SKIP_EXT = new Set(['.png', '.jpg', '.jpeg', '.gif', '.ico', '.zip', '.7z', '.mp4', '.pdb']);

const args = process.argv.slice(2);
const root = args.find((a) => !a.startsWith('--'));
const mineIdx = args.indexOf('--mine');
const mine = mineIdx >= 0 ? (args[mineIdx + 1] || '').replace(/^BCWM1:/, '').toLowerCase() : null;
if (!root) { console.error('Dùng: node check.mjs <thư-mục|file> [--mine <dấu-của-bạn>]'); process.exit(2); }

const files = [];
(function walk(p) {
  const st = statSync(p);
  if (st.isDirectory()) { for (const n of readdirSync(p)) walk(join(p, n)); return; }
  if (st.size <= 64 * 1024 * 1024 && !SKIP_EXT.has(extname(p).toLowerCase())) files.push(p);
})(root);

const fps = new Map();   // dấu -> [file...]
let guidFiles = 0;
for (const f of files) {
  let raw;
  try { raw = readFileSync(f, 'latin1'); } catch { continue; }
  // Chuỗi trong .NET lưu UTF-16 (xen byte 0) nên dấu không liền; quét cả bản đã bỏ byte 0 để bắt literal trong .exe/.dll, lẫn bản gốc cho html/log (UTF-8).
  const views = [raw, raw.replace(/\u0000/g, '')];
  if (views.some((t) => t.includes(PROJECT_GUID))) guidFiles++;
  for (const t of views)
    for (const m of t.matchAll(TOKEN)) {
      const fp = m[1];
      if (!fps.has(fp)) fps.set(fp, new Set());
      fps.get(fp).add(f);
    }
}

console.log(`Đã quét ${files.length} file dưới: ${root}`);
console.log(`- GUID dự án Bcode (${PROJECT_GUID}): ${guidFiles ? 'CÓ ở ' + guidFiles + ' file → là bản dựng từ source Bcode' : 'không thấy'}`);
if (fps.size === 0) {
  console.log('- Dấu bí mật BCWM1: KHÔNG thấy (bản không key, hoặc không chạy để sinh dấu trên trang/log, hoặc đã bị gỡ).');
} else {
  console.log(`- Dấu bí mật tìm thấy: ${fps.size}`);
  for (const [fp, set] of fps) {
    const mark = mine ? (fp === mine ? '  ✔ TRÙNG dấu của bạn → đúng là bản của bạn' : '  ✗ khác dấu của bạn') : '';
    console.log(`    BCWM1:${fp}  (ở ${set.size} file)${mark}`);
    for (const f of [...set].slice(0, 5)) console.log(`        ${f}`);
  }
  if (mine && ![...fps.keys()].includes(mine)) console.log(`  → Không có file nào mang đúng dấu của bạn (BCWM1:${mine}).`);
}
