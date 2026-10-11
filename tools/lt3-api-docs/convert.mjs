// Chuyển tài liệu API chuẩn LT3 (đã tải bằng fetch.mjs) → lt3-standard.json cho Bcode (Templates/Api).
import fs from 'node:fs';
const dir = new URL('./pages/', import.meta.url);   // văn bản tạm do fetch.mjs tải về
const read = (n) => fs.readFileSync(new URL(n + '.txt', dir), 'utf8');
const codeBlocks = (t) => t.split('===== CODE BLOCKS =====')[1]?.split(/^--- code \d+ ---$/m).slice(1).map((c) => c.replace(/\n\s*\n/g, '\n').trim()) || [];
const textPart = (t) => t.split('===== CODE BLOCKS =====')[0];

function sections(text) {
  const lines = textPart(text).split('\n');
  const out = []; let cur = null; let prev = '';
  for (const raw of lines) {
    const line = raw.replace(/\u200b/g, '');
    if (/^ \|\s+\|\s*Attribute\s*\|/.test(line)) {
      const label = prev.trim();
      const name = /detail/i.test(label) ? 'detail' : /\btax\b/i.test(label) ? 'tax' : /header|thông tin chung/i.test(label) ? 'header' : 'data';
      cur = { name, label, fields: [] }; out.push(cur); continue;
    }
    if (cur && /^ \| /.test(line)) {
      const cells = line.split('|').map((c) => c.trim()).slice(1);
      if (cells.length >= 4 && cells[0]) cur.fields.push({ name: cells[0], type: cells[1], required: /✔/.test(cells[2]), description: cells.slice(3).join(' | ').trim() });
      continue;
    }
    if (line.trim()) { cur = cur && /^ \| /.test(line) ? cur : null; prev = line; }
  }
  return out;
}
const firstJson = (t) => { for (const c of codeBlocks(t)) { if (!c.startsWith('{')) continue; try { JSON.parse(c); return c; } catch { /* có chú thích */ } } return null; };
const summary = (t) => (textPart(t).split('\n').find((l) => /^Form \w+ được sử dụng/.test(l)) || '').trim();

const pages = fs.readdirSync(dir).filter((f) => /^api_sync-(data|voucher)_.+\.txt$/.test(f)).map((f) => f.replace('.txt', ''));
const forms = [];
for (const p of pages) {
  const t = read(p);
  const sample = firstJson(t);
  const form = sample ? JSON.parse(sample).form : (/Form (\w+)/.exec(summary(t)) || [])[1];
  const kind = p.includes('sync-voucher') ? 'SyncVoucher' : 'SyncData';
  const tl = textPart(t).split('\n').map((l) => l.replace(/​/g, '').trim()).filter(Boolean);
  const title = tl[tl.indexOf('Trên trang này') - 1] || form;   // dòng ngay trước "Trên trang này" = tên trang
  forms.push({ form, kind, endpoint: 'api/' + kind, title, description: summary(t), sections: sections(t), sample: sample ? JSON.stringify(JSON.parse(sample), null, 4) : null, doc: 'http://172.168.5.14/developers/docs/' + p.replace(/^api_/, 'api/').replace(/_/g, '/') + '/' });
}
// GetData: danh sách form trong trang get-data
const gd = textPart(read('api_get-data'));
for (const m of gd.matchAll(/^(.+?) \((get\w+)\)\s*$/gm)) {
  const form = m[2];
  forms.push({ form, kind: 'GetData', endpoint: 'api/GetData', title: m[1].trim(), description: 'Lấy dữ liệu từ Fast: Id = mã khoá chính đối tác đã đồng bộ (CustomerCode / JobCode / VoucherId…), hoặc khoảng ngày cập nhật (dateFrom–dateTo, yyyy-MM-dd).',
    sections: [{ name: 'data', label: 'Tham số', fields: [
      { name: 'form', type: 'String', required: true, description: 'Tên form' },
      { name: 'Id', type: 'String', required: false, description: 'Mã khoá chính của danh mục / chứng từ đã đồng bộ sang Fast' },
      { name: 'dateFrom', type: 'Date', required: false, description: 'Từ ngày cập nhật (Modified) trên Fast' },
      { name: 'dateTo', type: 'Date', required: false, description: 'Đến ngày cập nhật (Modified) trên Fast' }] }],
    sample: JSON.stringify({ form, Id: '', dateFrom: '{{monthStart}}', dateTo: '{{today}}' }, null, 4), doc: 'http://172.168.5.14/developers/docs/api/get-data/' });
}
const order = { SyncData: 0, SyncVoucher: 1, GetData: 2 };
forms.sort((a, b) => order[a.kind] - order[b.kind]);
const catalog = {
  source: 'http://172.168.5.14/developers/docs/intro/',
  generated: new Date().toISOString().slice(0, 10),
  auth: { tokenPath: 'api/getToken', body: { username: '{{username}}', password: '{{password}}' }, tokenField: 'token.accesstoken', expiresField: 'token.expires', header: 'Authorization: <token> (không "Bearer")' },
  errorCodes: { 200: 'Thành công', 201: 'Form không tồn tại', 202: 'Dữ liệu trống', 400: 'Request không hợp lệ', 401: 'Lỗi xác thực (token sai / hết hạn)', 403: 'Không có quyền truy cập', 500: 'Lỗi server', 601: 'Lỗi cấu trúc dữ liệu' },
  forms,
};
const outDir = new URL('../../src/Bcode.App/Templates/Api/', import.meta.url);
fs.mkdirSync(outDir, { recursive: true });
fs.writeFileSync(new URL('lt3-standard.json', outDir), JSON.stringify(catalog, null, 2));
console.log(forms.map((f) => `${f.kind}:${f.form} sections=${f.sections.map((s) => s.name + '(' + s.fields.length + ')').join(',')} sample=${!!f.sample}`).join('\n'));
