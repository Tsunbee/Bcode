// Web Worker chạy các kiểm tra của Problems (problems.js) và việc tìm entity qua chuỗi include (entity.js)
// ra khỏi luồng giao diện — gõ phím không còn bị khựng khi file include lớn.
//
// Không có DOM/Monaco/cầu nối host ở đây, nên:
//   - đọc file / tìm project: gửi 'rpc' về trang, trang gọi bcodeHost rồi trả 'rpc-result';
//   - các hàm tiện ích nhỏ mà problems.js/entity.js dựa vào (offsetToPosition, dirNameOf, resolvePath,
//     ENTITY_DECL_RE) được chép lại bên dưới từ editor.js (không nạp được editor.js vì nó dựng Monaco).
//
// Dùng chính problems.js / entity.js (importScripts) nên luật kiểm tra chỉ có MỘT bản.

self.window = self;

const ENTITY_DECL_RE = /<!ENTITY\s+%?\s*([A-Za-z0-9_.:$-]+)\s+SYSTEM\s+"([^"]+)"/g;

function offsetToPosition(text, offset) {
  const upTo = text.slice(0, offset);
  const line = (upTo.match(/\n/g) || []).length + 1;
  const lastNewline = upTo.lastIndexOf('\n');
  const col = offset - lastNewline;
  return { line, col };
}

function dirNameOf(path) {
  if (!path) return '';
  const i = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'));
  return i >= 0 ? path.slice(0, i) : '';
}

function resolvePath(baseDir, relative) {
  const parts = (baseDir + '\\' + relative).split(/[\\/]+/);
  const stack = [];
  for (const part of parts) {
    if (part === '' || part === '.') continue;
    if (part === '..') stack.pop();
    else stack.push(part);
  }
  const prefix = baseDir.startsWith('\\\\') ? '\\\\' : '';
  return prefix + stack.join('\\');
}

// ---- RPC về trang ----------------------------------------------------------------------

let rpcSeq = 0;
const rpcPending = new Map();
function rpc(kind, method, args) {
  return new Promise((resolve, reject) => {
    const id = ++rpcSeq;
    rpcPending.set(id, { resolve, reject });
    self.postMessage({ t: 'rpc', id, kind, method, args });
  });
}

self.bcodeHost = { call: (method, ...args) => rpc('host', method, args) };
self.chrome = { webview: { hostObjects: { host: { GetWorkspaceRoot: (p) => rpc('raw', 'GetWorkspaceRoot', [p]) } } } };

importScripts('problems.js', 'entity.js');

// ---- Vòng đời ---------------------------------------------------------------------------

// "Document" giả cho BcodeEntity: nó chỉ cần đường dẫn và nội dung file đang kiểm tra.
const shim = { activePath: null, text: '', currentModel: { getValue: () => shim.text } };
const entity = new BcodeEntity(shim);
// invalidate() của BcodeEntity gọi window.bcodeProblems — trong worker không có, đã được rào điều kiện.
self.bcodeEntity = entity;

const checker = Object.create(BcodeProblems.prototype);
let latestRun = 0;

self.onmessage = async (e) => {
  const msg = e.data;
  if (!msg) return;

  if (msg.t === 'rpc-result') {
    const p = rpcPending.get(msg.id);
    if (!p) return;
    rpcPending.delete(msg.id);
    if (msg.ok) p.resolve(msg.result); else p.reject(new Error(msg.error || 'rpc failed'));
    return;
  }

  if (msg.t === 'invalidate') {
    entity.invalidate();
    checker._memo = null;
    return;
  }

  if (msg.t === 'validate') {
    latestRun = msg.run;
    shim.activePath = msg.path;
    shim.text = msg.text;
    try {
      await checker.runChecks(
        msg.path, msg.text,
        (items) => { if (msg.run === latestRun) self.postMessage({ t: 'items', run: msg.run, items }); },
        () => msg.run !== latestRun);
    } catch (err) {
      // Lỗi bất ngờ trong 1 lượt không được làm worker chết: lượt kế tiếp vẫn chạy.
      console.warn('[problems-worker]', err);
    }
  }
};
