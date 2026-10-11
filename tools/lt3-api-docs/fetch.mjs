// Tải các trang tài liệu API chuẩn LT3 (Docusaurus) → văn bản thuần + khối code, lưu vào thư mục này.
import fs from 'node:fs';
const base = 'http://172.168.5.14/developers/docs/';
const pages = ['intro', 'authentication', 'api/sync-data', 'api/sync-data/setcustomer', 'api/sync-data/setitem', 'api/sync-data/setuomconversion', 'api/sync-data/setsite',
  'api/sync-data/setdepartment', 'api/sync-data/setjob', 'api/sync-data/setcontract', 'api/sync-data/setexpense', 'api/sync-voucher', 'api/sync-voucher/setpurchaseinvoice',
  'api/sync-voucher/setsaleinvoice', 'api/sync-voucher/setsalereturn', 'api/sync-voucher/setreceipt', 'api/sync-voucher/setissue', 'api/sync-voucher/setcashreceipt',
  'api/sync-voucher/setcashdisbursement', 'api/get-data'];
const decode = (s) => s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&#39;/g, "'").replace(/&nbsp;/g, ' ').replace(/&amp;/g, '&');
for (const p of pages) {
  const html = await (await fetch(base + p + '/')).text();
  const main = (/<article[\s\S]*?<\/article>/.exec(html) || [html])[0];
  // khối code: giữ nguyên xuống dòng
  const codes = [...main.matchAll(/<pre[^>]*>([\s\S]*?)<\/pre>/g)].map((m) => decode(m[1].replace(/<br\s*\/?>/g, '\n').replace(/<\/span><span class="token-line"[^>]*>/g, '\n').replace(/<[^>]+>/g, '')));
  const text = decode(main.replace(/<pre[\s\S]*?<\/pre>/g, '\n[CODE]\n').replace(/<\/(p|h[1-6]|li|tr|div)>/g, '\n').replace(/<(td|th)[^>]*>/g, ' | ').replace(/<[^>]+>/g, '')).replace(/\n\s*\n+/g, '\n');
  const name = p.replace(/\//g, '_');
  fs.mkdirSync(new URL('./pages/', import.meta.url), { recursive: true });
  fs.writeFileSync(new URL('./pages/' + name + '.txt', import.meta.url), text + '\n\n===== CODE BLOCKS =====\n' + codes.map((c, i) => `--- code ${i + 1} ---\n${c}`).join('\n'));
  console.log(name, text.length, 'chars,', codes.length, 'code blocks');
}
