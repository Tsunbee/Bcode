// The Problems panel and the checks that fill it.
//
// Two of these rules (a <!ENTITY ... SYSTEM "path"> whose target is missing, and a
// <field name="X"> declared twice in one <fields> block) came from FCodeViewer's own red
// banners and used to live in editor.js. The rest are the same kind of static, text-level
// check on what the file itself says — nothing here reverse-engineers FCode's runtime
// behaviour, and nothing invents a schema. Each one exists because getting it wrong shows
// up as a blank screen or a silent no-op at runtime rather than as an error anyone sees.
//
// Everything is deliberately regex/scanner-based rather than a real XML parse: these files
// are frequently *not* well-formed while being typed (that is what rule 3 reports), and a
// parser that throws on the first bad character can't report anything about the rest.

const XML_BUILTIN_ENTITIES = new Set(['amp', 'lt', 'gt', 'quot', 'apos']);

/// Elements whose content is code, not markup — their bodies are skipped by the tag-balance
/// check, where "if (a < b)" and "</div>" in a string are normal and mean nothing structural.
const CODE_ELEMENTS = new Set(['script', 'clientscript', 'style', 'css', 'command', 'action']);

// ---- FCode request ↔ action ↔ field analysis -----------------------------------------------
// Dùng chung cho Problems (cả trong worker) và "Tạo <action> cho f.request" (contextmenu.js).
// Tất cả là quét text, không parse JS/XML thật: file đang gõ dở vẫn phải phân tích được.

/// Blanks out -- and /* */ comments and '...' literals (same length, line breaks kept), so @names inside
/// them are not taken for parameters.
function fcStripSql(sql) {
  let out = '';
  for (let i = 0; i < sql.length;) {
    const c = sql[i];
    if (c === '-' && sql[i + 1] === '-') { while (i < sql.length && sql[i] !== '\n') { out += ' '; i++; } continue; }
    if (c === '/' && sql[i + 1] === '*') {
      const end = sql.indexOf('*/', i + 2);
      const stop = end < 0 ? sql.length : end + 2;
      for (; i < stop; i++) out += sql[i] === '\n' ? '\n' : ' ';
      continue;
    }
    if (c === "'") {
      out += ' '; i++;
      while (i < sql.length) {
        if (sql[i] === "'" && sql[i + 1] === "'") { out += '  '; i += 2; continue; }
        if (sql[i] === "'") { out += ' '; i++; break; }
        out += sql[i] === '\n' ? '\n' : ' '; i++;
      }
      continue;
    }
    out += c; i++;
  }
  return out;
}

/// Every variable DECLAREd in the (stripped) SQL — all of a comma list, types with (24, 12)
/// included. Same rule as SqlRunnerService.DeclaredVariables.
function fcDeclaredVars(stripped) {
  const declared = new Set();
  const re = /\bdeclare\b/gi;
  let m;
  while ((m = re.exec(stripped))) {
    let i = m.index + m[0].length;
    for (;;) {
      while (i < stripped.length && /\s/.test(stripped[i])) i++;
      if (stripped[i] !== '@') break;
      const start = ++i;
      while (i < stripped.length && /[\w$#@]/.test(stripped[i])) i++;
      if (i === start) break;
      declared.add(stripped.slice(start, i).toLowerCase());
      let depth = 0, more = false;
      for (; i < stripped.length; i++) {
        const c = stripped[i];
        if (c === '(') depth++;
        else if (c === ')') { if (depth > 0) depth--; }
        else if (depth === 0 && c === ',') { more = true; i++; break; }
        else if (depth === 0 && (c === ';' || c === '\n')) break;
      }
      if (!more) break;
    }
  }
  return declared;
}

/// The @parameters an action's SQL expects from outside: every @name that isn't declared in it
/// (@@macros and @$macros excluded). Map lowercased name → name as written.
function fcSqlParams(sql) {
  const stripped = fcStripSql(sql);
  const declared = fcDeclaredVars(stripped);
  const found = new Map();
  const re = /(^|[^@\w$#])@([A-Za-z_][\w$#]*)/g;
  let m;
  while ((m = re.exec(stripped))) {
    const key = m[2].toLowerCase();
    if (!declared.has(key) && !found.has(key)) found.set(key, m[2]);
  }
  return found;
}

/// Index just past the bracket matching text[open] ('[' or '('), skipping quoted strings; -1 if none.
function fcMatchBracket(text, open) {
  const close = text[open] === '[' ? ']' : ')';
  let depth = 0;
  for (let i = open; i < text.length; i++) {
    const c = text[i];
    if (c === "'" || c === '"') {
      for (i++; i < text.length && text[i] !== c; i++) if (text[i] === '\\') i++;
      continue;
    }
    if (c === '[' || c === '(') depth++;
    else if (c === ']' || c === ')') { depth--; if (depth === 0) return c === close ? i + 1 : -1; }
  }
  return -1;
}

/// One element of an f.request field list: 'ma_kh' (a form field) or ['sLine', 'Infinite', v]
/// (a named value of that type). Returns {name, type} or null for anything else.
function fcRequestElement(src) {
  src = src.trim();
  let m = /^'([^']*)'$|^"([^"]*)"$/.exec(src);
  if (m) return { name: m[1] !== undefined ? m[1] : m[2], type: null };
  m = /^\[\s*['"]([^'"]+)['"]\s*(?:,\s*['"]([^'"]*)['"])?/.exec(src);
  if (m) return { name: m[1], type: m[2] || '' };
  return null;
}

/// Top-level elements of an array literal "[a, [b, c], 'd']".
function fcArrayElements(arrayText) {
  const inner = arrayText.trim().replace(/^\[/, '').replace(/\]$/, '');
  const parts = [];
  let depth = 0, start = 0;
  for (let i = 0; i < inner.length; i++) {
    const c = inner[i];
    if (c === "'" || c === '"') { for (i++; i < inner.length && inner[i] !== c; i++) if (inner[i] === '\\') i++; continue; }
    if (c === '[' || c === '(') depth++;
    else if (c === ']' || c === ')') depth--;
    else if (c === ',' && depth === 0) { parts.push(inner.slice(start, i)); start = i + 1; }
  }
  if (inner.slice(start).trim()) parts.push(inner.slice(start));
  return parts.map(fcRequestElement);
}

/// Every `x.request('Action', 'Context', fields)` in the text, with its field list resolved when it
/// can be: an inline array, or a variable built in the same function from `v = [...]` plus
/// `Array.add(v, ...)`. {index, actionId, context, fields:[{name,type}], resolved}.
function fcFindRequests(text) {
  const out = [];
  const re = /\.request\(\s*'([^']*)'\s*,\s*'([^']*)'\s*,?\s*/g;
  let m;
  while ((m = re.exec(text))) {
    const req = { index: m.index + 1, actionId: m[1], context: m[2], fields: [], resolved: false };
    const at = m.index + m[0].length;
    if (text[at] === '[') {
      const end = fcMatchBracket(text, at);
      if (end > 0) { req.fields = fcArrayElements(text.slice(at, end)); req.resolved = req.fields.every(Boolean); }
    } else {
      const v = /^[A-Za-z_$][\w$]*/.exec(text.slice(at));
      if (v) Object.assign(req, fcResolveFieldVariable(text, v[0], m.index));
    }
    req.fields = req.fields.filter(Boolean);
    out.push(req);
  }
  return out;
}

function fcResolveFieldVariable(text, name, before) {
  const fnStart = Math.max(0, text.lastIndexOf('function', before));
  const body = text.slice(fnStart, before);
  const esc = name.replace(/\$/g, '\\$');
  const assign = new RegExp(`(?:^|[^\\w$.])${esc}\\s*=\\s*(?!=)`, 'g');
  let last = null, m;
  while ((m = assign.exec(body))) last = m;
  if (!last) return { fields: [], resolved: false };
  const rhsAt = last.index + last[0].length;
  if (body[rhsAt] !== '[') return { fields: [], resolved: false }; // a = GetDataTable(g)... — can't know
  const end = fcMatchBracket(body, rhsAt);
  if (end < 0) return { fields: [], resolved: false };
  const fields = fcArrayElements(body.slice(rhsAt, end));
  let resolved = fields.every(Boolean);
  const add = new RegExp(`Array\\.add\\(\\s*${esc}\\s*,\\s*`, 'g');
  add.lastIndex = end;
  while ((m = add.exec(body))) {
    const argAt = m.index + m[0].length;
    const close = fcMatchBracket(body, body.lastIndexOf('(', argAt));
    if (close < 0) { resolved = false; break; }
    const el = fcRequestElement(body.slice(argAt, close - 1));
    if (el) fields.push(el); else resolved = false;
  }
  return { fields, resolved };
}

/// The <action id> blocks of a (possibly entity-expanded) text: Map lowercased id → {id, body}.
function fcActions(text) {
  const map = new Map();
  const re = /<action\s+id\s*=\s*"([^"]+)"[^>]*>([\s\S]*?)<\/action>/gi;
  let m;
  while ((m = re.exec(text))) map.set(m[1].toLowerCase(), { id: m[1], body: m[2] });
  return map;
}

class BcodeProblems {
  constructor(bcode) {
    this.bcode = bcode;
    this.items = [];
    this.dock = window.bcodeDock || (window.bcodeDock = new BcodeDock());
    this.pane = this.dock.register('problemsPanel', 'Problems');
    this.summary = document.getElementById('problemsSummary');
    this.summary.onclick = () => this.dock.show('problemsPanel');

    this.list = document.createElement('div');
    this.list.className = 'resultList';
    this.pane.appendChild(this.list);

    this.render();
  }

  toggle() { this.dock.toggle('problemsPanel'); }

  /// Runs every check against the active document and publishes the result. Called on a
  /// debounce from the editor's content change handler and on every tab switch.
  ///
  /// Phần nặng (tìm entity qua cả chuỗi include) chạy trong Web Worker (problems-worker.js) để gõ phím
  /// không bị khựng; worker gửi về danh sách vấn đề từng đợt. Không tạo được worker (vd. trang nạp bằng
  /// file://) thì chạy ngay trên luồng này như trước.
  async validate() {
    const bcode = this.bcode;
    if (!bcode.activePath || !bcode.currentModel) { this.setItems([]); return; }

    const path = bcode.activePath;
    const model = bcode.currentModel;
    const text = model.getValue();
    const runId = (this._runId = (this._runId || 0) + 1);

    const worker = this.ensureWorker();
    if (worker) {
      this._workerRun = { runId, path, model };
      worker.postMessage({ t: 'validate', run: runId, path, text });
      return;
    }

    // Phương án dự phòng: cùng 1 đoạn kiểm tra, chạy tại chỗ.
    await this.runChecks(
      path, text,
      (items) => this.setItems(items),
      // The document may have been edited or closed while those probes ran (or a newer run
      // started); publishing now would resurrect problems for text that no longer exists.
      () => runId !== this._runId || bcode.activePath !== path || bcode.currentModel !== model);
  }

  /// Toàn bộ các kiểm tra, KHÔNG đụng tới giao diện/Monaco — chạy được cả trong worker. publish(items) được
  /// gọi nhiều lần (mỗi kiểm tra xong là hiện ngay); isStale() báo lượt này đã bị lượt mới hơn thay.
  async runChecks(path, text, publish, isStale) {
    const items = [];

    // Rules that need no I/O run first, so the list is already usable while the entity
    // file probes (one host round trip each, on a UNC share) are still in flight.
    const declared = this.collectEntityNames(text);
    items.push(...this.checkDuplicateFields(text));
    items.push(...this.checkDuplicateIds(text));
    items.push(...this.checkTagBalance(text));
    items.push(...this.checkMissingHandlers(text));
    publish(items);

    // Các kiểm tra cần I/O chạy lần lượt và MỖI KIỂM TRA XONG LÀ HIỆN NGAY. Trước đây cả nhóm
    // phải xong hết mới publish, mà "Thiếu entity &X;" còn tìm cả project qua UNC cho từng tên
    // chưa khai báo — một lượt tìm chậm/treo là lỗi file include thiếu (xong từ lâu) cũng không
    // bao giờ hiện, Problems cứ trống. Mỗi check cũng tự bọc try/catch: 1 check ném lỗi không
    // được làm mất kết quả của các check còn lại.
    // Thứ tự: rẻ và quan trọng nhất trước (file include thiếu = form trắng), tìm project sau cùng.
    //
    // Hai kiểm tra chỉ phụ thuộc phần <!DOCTYPE ...]> (file include thiếu, %entity; chưa khai báo) nên nhớ kết
    // quả theo chính phần đó: sửa code ở thân file (script/command/field...) không làm đổi gì thì khỏi kiểm
    // tra lại. Vị trí dòng của DOCTYPE nằm trong khoá để kết quả không bao giờ lệch dòng.
    const head = (/<!DOCTYPE[\s\S]*?\]>/i.exec(text) || [''])[0];
    const headAt = head ? text.indexOf(head) : 0;
    const doctypeKey = path + '|' + offsetToPosition(text, headAt).line + '|' + head;
    const memo = (name, fn) => async () => {
      this._memo = this._memo || new Map();
      const k = name + '|' + doctypeKey;
      if (this._memo.has(k)) return this._memo.get(k);
      const r = await fn();
      this._memo.set(k, r);
      if (this._memo.size > 8) this._memo.delete(this._memo.keys().next().value);
      return r;
    };
    let all = items;
    const steps = [
      // Cần khai triển &Entity; (I/O qua entity.js) nên thuộc nhóm này, không phải nhóm sync trên.
      memo('files', () => this.checkMissingEntityFiles(text, path)),
      memo('params', () => this.checkUndeclaredParamEntities(text)),
      () => this.checkItemVariableCount(text),
      () => this.checkRequestsAndFields(text),
      () => this.checkUndeclaredEntities(text, declared),
    ];
    for (const step of steps) {
      let found = [];
      try { found = await step(); } catch (e) { console.warn('[problems]', e); }
      if (isStale()) return;
      if (found.length) { all = [...found, ...all]; publish(all); }
    }
  }

  // ---- Web Worker ---------------------------------------------------------------------

  ensureWorker() {
    if (this._worker) return this._worker;
    if (this._workerFailed || typeof Worker === 'undefined') return null;
    try {
      const w = new Worker('problems-worker.js');
      w.onmessage = (e) => this.onWorkerMessage(e.data);
      // Worker chết/không nạp được → lần sau (và ngay bây giờ) chạy tại chỗ, không để Problems im lặng.
      w.onerror = () => { this._workerFailed = true; this._worker = null; try { w.terminate(); } catch { /* đã chết */ } this.validate(); };
      this._worker = w;
    } catch {
      this._workerFailed = true;
      this._worker = null;
    }
    return this._worker;
  }

  invalidateWorker() {
    if (this._worker) this._worker.postMessage({ t: 'invalidate' });
    this._memo = null;
  }

  async onWorkerMessage(msg) {
    if (!msg) return;
    if (msg.t === 'items') {
      const cur = this._workerRun;
      if (!cur || msg.run !== this._runId) return; // đã có lượt mới hơn
      if (this.bcode.activePath !== cur.path || this.bcode.currentModel !== cur.model) return;
      this.setItems(msg.items);
      return;
    }
    if (msg.t === 'rpc') {
      // Worker không có cầu nối host → nhờ trang này gọi hộ rồi trả kết quả.
      let reply;
      try {
        const host = window.bcodeHost;
        const result = msg.kind === 'raw'
          ? await window.chrome.webview.hostObjects.host[msg.method](...msg.args)
          : await host.call(msg.method, ...msg.args);
        reply = { t: 'rpc-result', id: msg.id, ok: true, result };
      } catch (e) {
        reply = { t: 'rpc-result', id: msg.id, ok: false, error: String(e && e.message ? e.message : e) };
      }
      if (this._worker) this._worker.postMessage(reply);
    }
  }

  // ---- Rules --------------------------------------------------------------------------

  collectEntityNames(text) {
    const names = new Set();
    const re = /<!ENTITY\s+%?\s*([A-Za-z0-9_.:-]+)/g;
    let m;
    while ((m = re.exec(text))) names.add(m[1]);
    return names;
  }

  /// `<!ENTITY X SYSTEM "sub/file.xml">` pointing at a file that isn't there. This is the
  /// error FCode itself reports at load time, and the reason a controller opens empty.
  async checkMissingEntityFiles(text, path) {
    const dir = dirNameOf(path);
    const seen = new Set();
    const candidates = [];
    ENTITY_DECL_RE.lastIndex = 0;
    let m;
    while ((m = ENTITY_DECL_RE.exec(text))) {
      const relPath = m[2];
      if (seen.has(relPath)) continue;
      seen.add(relPath);
      candidates.push({
        at: offsetToPosition(text, m.index),
        resolved: resolvePath(dir, relPath.replace(/\//g, '\\')),
      });
    }
    if (!candidates.length) return [];

    // One call for the whole document, not one per declaration. This runs 400ms after every
    // keystroke, and a controller with twenty entities used to mean twenty round trips and
    // twenty sequential stat calls across the share, each one awaited before the next
    // started — on a slow share that was seconds of work per pause.
    let found;
    try {
      found = JSON.parse(await window.bcodeHost.call(
        'BeginPathsExist', JSON.stringify(candidates.map((c) => c.resolved))));
    } catch {
      return []; // can't tell right now; saying nothing beats a wall of false errors
    }

    const items = [];
    candidates.forEach((c, i) => {
      if (found[i]) return;
      items.push({
        severity: 'error',
        text: `Không mở được file ENTITY: '${c.resolved}'`,
        // The location to jump to is the declaration line in THIS file; `path` is what
        // the row offers to open instead once the file does exist.
        line: c.at.line,
        column: c.at.col,
        openPath: c.resolved,
      });
    });
    return items;
  }

  /// A `<field name="X">` repeated inside ONE `<fields>` block. Counting across the whole
  /// document is wrong: a master grid and a nested detail grid each get their own block and
  /// commonly reuse the same hidden-PK name, which is normal FCode structure.
  checkDuplicateFields(text) {
    const items = [];
    // Field nằm trong <!-- ... --> (đã rào lại) không tính là khai báo: thay chữ trong chú thích bằng khoảng trắng (giữ nguyên độ dài và
    // xuống dòng để vị trí báo lỗi vẫn đúng với văn bản gốc).
    text = text.replace(/<!--[\s\S]*?-->/g, (m) => m.replace(/[^\n]/g, ' '));
    const blockRe = /<fields\b[^>]*>([\s\S]*?)<\/fields>/g;
    const fieldRe = /<field\s+name="([^"]+)"/g;
    let fb;
    while ((fb = blockRe.exec(text))) {
      const blockText = fb[1];
      const blockStart = fb.index + fb[0].indexOf(blockText);
      const seen = new Map();
      fieldRe.lastIndex = 0;
      let fm;
      while ((fm = fieldRe.exec(blockText))) {
        const name = fm[1];
        if (seen.has(name)) {
          const pos = offsetToPosition(text, blockStart + fm.index);
          items.push({
            severity: 'warning',
            text: `Field "${name}" được khai báo trùng trong cùng một <fields>.`,
            line: pos.line,
            column: pos.col,
            length: fm[0].length,
          });
        } else {
          seen.set(name, fm.index);
        }
      }
    }
    return items;
  }

  /// Repeated `<view id>` / `<action id>`. Unlike field names these are document-wide
  /// handles, so the second one silently shadows the first wherever it's referenced.
  checkDuplicateIds(text) {
    const items = [];
    for (const tag of ['view', 'action']) {
      const re = new RegExp(`<${tag}\\s+[^>]*\\bid="([^"]+)"`, 'g');
      const seen = new Set();
      let m;
      while ((m = re.exec(text))) {
        if (seen.has(m[1])) {
          const pos = offsetToPosition(text, m.index);
          items.push({
            severity: 'warning',
            text: `<${tag}> có id="${m[1]}" bị trùng.`,
            line: pos.line,
            column: pos.col,
            length: m[0].length,
          });
        } else {
          seen.add(m[1]);
        }
      }
    }
    return items;
  }

  /// Index of the `>` that ends the tag starting at `lt`, skipping any `>` inside a quoted
  /// attribute value — FCode attributes hold SQL (`check="... charindex(x) > 0"`), and the
  /// first raw `>` there is not the end of the tag. -1 if the tag never ends.
  findTagEnd(text, lt) {
    let quote = null;
    for (let j = lt + 1; j < text.length; j++) {
      const c = text[j];
      if (quote) { if (c === quote) quote = null; }
      else if (c === '"' || c === "'") quote = c;
      else if (c === '>') return j;
    }
    return -1;
  }

  /// Unbalanced markup: a close tag with no open one, or an element left open at EOF.
  ///
  /// Scanned with a stack rather than parsed, and everything that is not markup is skipped
  /// explicitly: comments, CDATA sections, the DOCTYPE's internal subset, processing
  /// instructions, and the bodies of code-bearing elements (see CODE_ELEMENTS) where a
  /// `<` is arithmetic, not a tag.
  checkTagBalance(text) {
    const items = [];
    const stack = [];
    let i = 0;

    while (i < text.length) {
      const lt = text.indexOf('<', i);
      if (lt < 0) break;

      if (text.startsWith('<!--', lt)) { i = this.skipTo(text, lt, '-->'); continue; }
      if (text.startsWith('<![CDATA[', lt)) { i = this.skipTo(text, lt, ']]>'); continue; }
      if (text.startsWith('<?', lt)) { i = this.skipTo(text, lt, '?>'); continue; }
      if (text.startsWith('<!', lt)) { i = this.skipDeclaration(text, lt); continue; }

      const gt = this.findTagEnd(text, lt);
      if (gt < 0) break; // a tag still being typed at EOF — not worth reporting mid-keystroke
      const inner = text.slice(lt + 1, gt);

      if (inner.startsWith('/')) {
        const name = inner.slice(1).trim().toLowerCase();
        const top = stack[stack.length - 1];
        if (!top) {
          const pos = offsetToPosition(text, lt);
          items.push({ severity: 'error', text: `Thẻ đóng </${name}> không có thẻ mở tương ứng.`, line: pos.line, column: pos.col, length: gt - lt + 1 });
        } else if (top.name !== name) {
          const pos = offsetToPosition(text, lt);
          items.push({ severity: 'error', text: `Thẻ đóng </${name}> không khớp với <${top.name}> đang mở ở dòng ${top.line}.`, line: pos.line, column: pos.col, length: gt - lt + 1 });
          // Pop anyway: keeping the unmatched element on the stack turns one real mistake
          // into an error on every close tag after it.
          stack.pop();
        } else {
          stack.pop();
        }
        i = gt + 1;
        continue;
      }

      const nameMatch = /^([A-Za-z_][\w.:-]*)/.exec(inner);
      if (!nameMatch) { i = gt + 1; continue; }
      const name = nameMatch[1].toLowerCase();
      const selfClosing = inner.trimEnd().endsWith('/');
      if (!selfClosing) {
        stack.push({ name, line: offsetToPosition(text, lt).line, offset: lt });
        if (CODE_ELEMENTS.has(name)) {
          // Jump past the body to this element's own close tag; nothing inside is markup.
          const close = text.toLowerCase().indexOf(`</${name}`, gt);
          if (close >= 0) { i = close; continue; }
        }
      }
      i = gt + 1;
    }

    for (const open of stack) {
      const pos = offsetToPosition(text, open.offset);
      items.push({ severity: 'error', text: `Thẻ <${open.name}> chưa được đóng.`, line: pos.line, column: pos.col });
    }
    return items;
  }

  skipTo(text, from, terminator) {
    const end = text.indexOf(terminator, from);
    return end < 0 ? text.length : end + terminator.length;
  }

  /// `<!DOCTYPE ... [ ... ]>` — the internal subset holds every <!ENTITY> declaration and
  /// contains '>' characters of its own, so the first '>' is not the end of it.
  skipDeclaration(text, from) {
    const bracket = text.indexOf('[', from);
    const gt = text.indexOf('>', from);
    if (bracket >= 0 && (gt < 0 || bracket < gt)) {
      const close = text.indexOf(']', bracket);
      if (close >= 0) {
        const after = text.indexOf('>', close);
        return after < 0 ? text.length : after + 1;
      }
    }
    return gt < 0 ? text.length : gt + 1;
  }

  /// `&Name;` used without a matching `<!ENTITY Name ...>` anywhere the document can see.
  /// In an FCode controller this is the single most common way to get a file that simply
  /// refuses to load.
  ///
  /// "Anywhere it can see" is the whole difficulty: a controller declares almost none of
  /// the entities it uses itself, it pulls them in through `<!ENTITY X SYSTEM "…">`. An
  /// earlier version only looked at this file's own DOCTYPE and so reported nearly every
  /// reference in a real controller as undeclared — a panel that is wrong about the common
  /// case teaches people to ignore it. Names not declared here are checked against the
  /// include chain (entity.js), which reads each file once and caches it.
  async checkUndeclaredEntities(text, declared) {
    const items = [];
    const re = /&([A-Za-z_][\w.:$-]*);/g;
    const reported = new Set();
    // File gốc (có <!DOCTYPE>) phải tự include được mọi entity nó dùng: entity chỉ có ở 1 file
    // khác trong project mà file này không include tới thì lúc chạy vẫn là "không tìm thấy".
    // File lẻ không có DOCTYPE (Include/fragment — được file khác include vào) thì vẫn tìm cả
    // project, vì nó không thể tự khai báo gì cả.
    const strict = /<!DOCTYPE\b/i.test(text);
    // Mỗi lần tìm cả project là 1 lượt quét UNC — chỉ làm cho vài tên đầu (để gợi ý "có ở file
    // nào"), phần còn lại vẫn báo thiếu bình thường, tránh treo khi 1 file include thiếu kéo
    // theo hàng chục tên.
    let projectSearches = 0;
    let m;
    while ((m = re.exec(text))) {
      const name = m[1];
      if (XML_BUILTIN_ENTITIES.has(name) || declared.has(name) || reported.has(name)) continue;
      // Inside a CDATA block an ampersand is literal text (jQuery's `&amp;&amp;`, a URL
      // query string), so nothing there is an entity reference at all.
      if (this.isInsideCdata(text, m.index)) continue;
      reported.add(name);
      let elsewhere = null;
      if (window.bcodeEntity) {
        if (strict) {
          if (await window.bcodeEntity.resolveChainOnly(name)) continue;
          if (projectSearches++ < 5) elsewhere = await window.bcodeEntity.resolveViaWorkspaceSearch(name);
        } else if (await window.bcodeEntity.resolveActive(name)) continue;
      }
      const pos = offsetToPosition(text, m.index);
      items.push({
        severity: 'error',
        text: elsewhere
          ? `Thiếu entity &${name}; — chỉ có khai báo ở '${elsewhere.path}' nhưng file này không include tới nó (thiếu <!ENTITY ... SYSTEM> hoặc include sai).`
          : `Thiếu entity &${name}; — không thấy <!ENTITY ${name} ...> ở file này, các file được include, hay bất kỳ file nào trong project.`,
        openPath: elsewhere ? elsewhere.path : undefined,
        line: pos.line,
        column: pos.col,
        length: m[0].length,
      });
    }
    return items;
  }

  /// `%Name;` (parameter entity) dùng trong phần DOCTYPE mà chưa khai báo — vd
  /// `%Control.Unit.Include.Customer;` khi file Unit.ent chưa được include. Chỉ xét trong khối
  /// <!DOCTYPE ... [ ... ]>; chuỗi trong dấu nháy bị che đi để "100%;" trong giá trị không bị khớp.
  async checkUndeclaredParamEntities(text) {
    const items = [];
    const start = text.search(/<!DOCTYPE\b/i);
    if (start < 0 || !window.bcodeEntity) return items;
    const end = this.skipDeclaration(text, start);
    const subset = text.slice(start, end).replace(/"[^"]*"|'[^']*'/g, (q) => ' '.repeat(q.length));

    const re = /%([A-Za-z_][\w.:$-]*);/g;
    const reported = new Set();
    let m;
    while ((m = re.exec(subset))) {
      const name = m[1];
      if (reported.has(name)) continue;
      reported.add(name);
      if (await window.bcodeEntity.resolveChainOnly(name)) continue;
      const pos = offsetToPosition(text, start + m.index);
      items.push({
        severity: 'error',
        text: `Thiếu entity %${name}; — không thấy <!ENTITY % ${name} ...> ở file này hay các file được include.`,
        line: pos.line,
        column: pos.col,
        length: m[0].length,
      });
    }
    return items;
  }

  isInsideCdata(text, offset) {
    const open = text.lastIndexOf('<![CDATA[', offset);
    if (open < 0) return false;
    const close = text.indexOf(']]>', open);
    return close < 0 || close > offset;
  }

  /// An attribute like onchange="doThing(this)" whose function is nowhere in this file.
  ///
  /// Reported as a warning, never an error, and only when the file has a <script> block of
  /// its own: the handler may legitimately live in an included entity file or in a shared
  /// .js, which this check cannot see. It still earns its place — a typo'd handler name is
  /// silent at runtime, the control just stops responding.
  checkMissingHandlers(text) {
    if (!/<script\b/i.test(text)) return [];
    const items = [];
    const re = /\son[a-z]+\s*=\s*"([A-Za-z_$][\w$]*)\s*\(/g;
    const reported = new Set();
    let m;
    while ((m = re.exec(text))) {
      const name = m[1];
      if (reported.has(name)) continue;
      const defined =
        text.includes(`function ${name}(`) ||
        new RegExp(`\\b${name}\\s*[:=]\\s*function\\b`).test(text) ||
        new RegExp(`\\b(?:var|let|const)\\s+${name}\\s*=\\s*(?:async\\s*)?\\(`).test(text);
      if (defined) continue;
      reported.add(name);
      const pos = offsetToPosition(text, m.index + 1);
      items.push({
        severity: 'warning',
        text: `Không tìm thấy hàm "${name}" trong file này (có thể nằm ở file ENTITY/JS khác).`,
        line: pos.line,
        column: pos.col,
        handler: name,
      });
    }
    return items;
  }

  /// `<item value="1100011000: [name].Label, [name], ...">` — dòng khai báo view của 1 dir:
  /// chuỗi mã ở đầu (chỉ gồm '0'/'1'/'-') định vị trí, mỗi số '1' ứng với đúng 1 biến liệt
  /// kê sau dấu ':'. Số biến liệt kê lệch với số vị trí '1' (thừa hoặc thiếu) gần như luôn
  /// là gõ/dán nhầm — thiếu 1 field khi thêm cột mới, hoặc thừa 1 field copy nhầm từ dòng
  /// khác — và lỗi này im lặng lúc chạy (FCode không báo gì, chỉ lệch/thiếu dữ liệu hiển
  /// thị). Không đụng tới dòng khai báo độ rộng cột (<item value="120, 30, ...">) vì dòng
  /// đó toàn số & không có dấu ':'.
  ///
  /// ĐÃ SỬA: 1 mục trong danh sách có thể là 1 "&TenEntity;" thay vì [ten_bien] viết thẳng —
  /// bản thân entity đó lại khai báo NHIỀU biến cách nhau bằng dấu phẩy (vd
  /// &DetailTaxFormViewAccountLine; = "[tk_thue_no].Footer, [tk_thue_no], [tk_thue_co]", tức
  /// 3 biến chứ không phải 1). Đếm thẳng số dấu phẩy trên text gốc coi "&Ten;" là 1 token duy
  /// nhất nên báo "thiếu biến" sai — phải khai triển (expand) từng &Ten; ra đúng nội dung nó
  /// khai báo (đệ quy, kể cả entity lồng entity) rồi mới đếm dấu phẩy trên kết quả đã khai
  /// triển. Dùng lại window.bcodeEntity.expand() — đúng cơ chế phân giải include chain mà F12/
  /// hover entity đang dùng — nên đếm ra đúng những gì file này thực sự resolve tới. Cần đọc
  /// file entity qua UNC share nên hàm này chạy bất đồng bộ, cùng nhóm I/O với 2 check entity
  /// khác trong validate() thay vì nhóm sync chạy ngay tức thì.
  async checkItemVariableCount(text) {
    const items = [];
    const re = /<item\s+value="([01-]+):\s*([^"]*)"\s*\/?>/g;
    const matches = [];
    let m;
    while ((m = re.exec(text))) matches.push(m);

    for (const match of matches) {
      const code = match[1];
      const list = match[2];
      // Chỉ xét khi có ít nhất 1 biến kiểu [ten] hoặc 1 tham chiếu &Entity; — tránh khớp
      // nhầm 1 value dạng "10:xx" không liên quan gì đến kiểu khai báo này.
      if (!/\[/.test(list) && !/&[A-Za-z_]/.test(list)) continue;

      let expandedList = list;
      if (window.bcodeEntity && /&[A-Za-z_][\w.:$-]*;/.test(list)) {
        try {
          expandedList = (await window.bcodeEntity.expand(list)).text;
        } catch {
          // Không đọc được file entity (share rớt...) — đếm tạm theo bản gốc, thà bỏ sót
          // còn hơn báo sai vì 1 entity chưa kịp khai triển.
        }
      }

      const onesCount = (code.match(/1/g) || []).length;
      const varCount = expandedList.split(',').map((s) => s.trim()).filter((s) => s.length > 0).length;
      if (onesCount === varCount) continue;

      const pos = offsetToPosition(text, match.index);
      const diff = varCount - onesCount;
      const detail = diff > 0
        ? `thừa ${diff} biến so với ${onesCount} vị trí '1' trong mã "${code}"`
        : `thiếu ${-diff} biến so với ${onesCount} vị trí '1' trong mã "${code}"`;
      items.push({
        severity: 'warning',
        text: `<item> khai báo ${varCount} biến nhưng ${detail}.`,
        line: pos.line,
        column: pos.col,
        length: match[0].length,
      });
    }
    return items;
  }

  /// The parts of a controller that live in included entities more often than not (<response>,
  /// <commands>, <fields>), expanded through the include chain. Cached per block text: these
  /// rarely change while typing in <script>, and expanding means reading files over the share.
  async expandBlocks(text, tag) {
    const re = new RegExp(`<${tag}\\b[^>]*>[\\s\\S]*?</${tag}>`, 'gi');
    const blocks = text.match(re) || [];
    if (!blocks.length) return '';
    const joined = blocks.join('\n');
    if (!window.bcodeEntity || !/&[A-Za-z_][\w.:$-]*;/.test(joined)) return joined;
    this._expandCache = this._expandCache || new Map();
    if (this._expandCache.has(joined)) return this._expandCache.get(joined);
    let expanded = joined;
    try { expanded = (await window.bcodeEntity.expand(joined)).text; } catch { /* share unreachable — use as is */ }
    this._expandCache.set(joined, expanded);
    if (this._expandCache.size > 12) this._expandCache.delete(this._expandCache.keys().next().value);
    return expanded;
  }

  /// Nội dung của MỌI &Entity; mà file dùng (kể cả entity SYSTEM = file include, và entity lồng entity), đã khai triển.
  /// Field / action / command / view nằm trong đó là có thật dù không viết trực tiếp trong file — nếu chỉ quét phần
  /// nằm trong <fields>/<response>/<commands> của file thì entity đặt ngoài các khối đó (hay cả khối nằm trong entity) bị coi là thiếu.
  async expandEntityContents(text) {
    if (!window.bcodeEntity) return '';
    const body = text.replace(/<!DOCTYPE[\s\S]*?\]>/i, '');
    const names = [...new Set([...body.matchAll(/&([A-Za-z_][\w.:$-]*);/g)].map((m) => m[1]))]
      .filter((n) => !/^(amp|lt|gt|quot|apos)$/.test(n))
      .slice(0, 300);
    if (!names.length) return '';
    const key = names.join(',');
    this._entityContentCache = this._entityContentCache || new Map();
    if (this._entityContentCache.has(key)) return this._entityContentCache.get(key);
    let out = '';
    try { out = (await window.bcodeEntity.expandFull(names.map((n) => '&' + n + ';').join('\n'))).text; }
    catch { /* share unreachable — thà bỏ sót còn hơn báo sai */ }
    this._entityContentCache.set(key, out);
    if (this._entityContentCache.size > 6) this._entityContentCache.delete(this._entityContentCache.keys().next().value);
    return out;
  }

  /// "[&Revert.Field.0;]" trong view: khai triển entity ra danh sách tên field thật.
  async expandNameList(raw) {
    if (!/&[A-Za-z_]/.test(raw) || !window.bcodeEntity) return [raw];
    try {
      const t = (await window.bcodeEntity.expandFull(raw)).text;
      return t.split(',').map((x) => x.trim()).filter(Boolean);
    } catch { return []; }
  }

  /// FCode wiring that only fails at runtime on FBO:
  ///   • f.request('X', ...) with no <action id="X"> (in this file or its entities);
  ///   • the action's SQL using @p the request doesn't send, or the request sending names the
  ///     action never uses — when the field list can be read (inline array, or a variable built
  ///     with Array.add in the same function);
  ///   • a plain name in the request ('ma_kh') that is not a field of the form;
  ///   • `case 'X':` in a ...ResponseComplete handler that no request / action / command produces;
  ///   • [name] in a <view> <item>, or f.getItem/getItemValue/setItemValue('name'...), naming a
  ///     field <fields> doesn't declare.
  /// Only positions in THIS file are reported; what comes from entities is used to know what exists.
  async checkRequestsAndFields(text) {
    const items = [];
    const at = (offset, length) => { const p = offsetToPosition(text, offset); return { line: p.line, column: p.col, length }; };
    const warn = (offset, length, msg) => items.push({ severity: 'warning', text: msg, ...at(offset, length) });

    const requests = fcFindRequests(text);
    const hasFields = /<fields\b/i.test(text);
    if (!requests.length && !hasFields) return items;

    const entityText = await this.expandEntityContents(text);
    const actions = fcActions((await this.expandBlocks(text, 'response')) + '\n' + entityText);
    const commandsText = (await this.expandBlocks(text, 'commands')) + '\n' + entityText;
    const fieldsText = hasFields ? (await this.expandBlocks(text, 'fields')) + '\n' + entityText : '';
    const fields = new Set();
    for (const m of fieldsText.matchAll(/<field\s+name\s*=\s*"([^"]+)"/gi)) fields.add(m[1].toLowerCase());
    // Only the top-level form's <fields>; a file without one (a Grid/Filter fragment) skips field checks.
    const checkFields = fields.size > 0;
    const isField = (n) => !checkFields || fields.has(n.toLowerCase());

    // ---- requests ↔ actions ----
    for (const r of requests) {
      const len = 'request'.length;
      const action = actions.get(r.actionId.toLowerCase()) || actions.get(r.context.toLowerCase());
      if (!action) {
        warn(r.index, len, `f.request('${r.actionId}') nhưng không thấy <action id="${r.actionId}"> trong <response> (kể cả trong các ENTITY).`);
        continue;
      }
      if (!r.resolved) continue;
      const used = fcSqlParams(action.body);
      const sent = new Map(r.fields.map((f) => [f.name.toLowerCase(), f]));
      const missing = [...used.entries()].filter(([k]) => !sent.has(k)).map(([, v]) => '@' + v);
      const unused = [...sent.values()].filter((f) => !used.has(f.name.toLowerCase())).map((f) => f.name);
      if (missing.length) {
        warn(r.index, len, `<action id="${action.id}"> dùng ${missing.join(', ')} nhưng f.request('${r.actionId}') không gửi (sẽ là NULL).`);
      }
      if (unused.length) {
        warn(r.index, len, `f.request('${r.actionId}') gửi ${unused.join(', ')} nhưng <action id="${action.id}"> không dùng.`);
      }
      if (checkFields) {
        const notFields = r.fields.filter((f) => f.type === null && !isField(f.name)).map((f) => f.name);
        if (notFields.length) {
          warn(r.index, len, `f.request('${r.actionId}') gửi ${notFields.map((n) => `'${n}'`).join(', ')} nhưng form không có field này — giá trị tự đặt phải viết dạng ['ten', 'Kiểu', giá_trị].`);
        }
      }
    }

    // ---- case 'X': in ResponseComplete ↔ what produces it ----
    // Context do chính framework FCode phát ra (vòng đời voucher + gợi ý AutoComplete) — không cần f.request/<action> nào.
    const produced = new Set(['init', 'showing', 'loading', 'scattering', 'navigating', 'copying', 'closing', 'declare',
      'initexternalfields', 'checking', 'inserting', 'inserted', 'updating', 'updated', 'deleting', 'deleted', 'suggestion', 'rendering', 'rendered']);
    for (const k of actions.keys()) produced.add(k);
    for (const r of requests) { produced.add(r.actionId.toLowerCase()); produced.add(r.context.toLowerCase()); }
    for (const m of commandsText.matchAll(/<command\s+event\s*=\s*"([^"]+)"/gi)) produced.add(m[1].toLowerCase());
    for (const fn of text.matchAll(/function\s+([\w$]*ResponseComplete[\w$]*)\s*\(/g)) {
      const start = fn.index;
      const next = text.indexOf('\nfunction ', start + 10);
      const body = text.slice(start, next < 0 ? text.length : next);
      for (const c of body.matchAll(/\bcase\s+'([^']+)'\s*:/g)) {
        if (produced.has(c[1].toLowerCase())) continue;
        warn(start + c.index, c[0].length, `case '${c[1]}' trong ${fn[1]} nhưng không có f.request / <action> / <command event> nào tên '${c[1]}'.`);
      }
    }

    if (!checkFields) return items;

    // ---- [name] in <view> items ----
    const reported = new Set();
    for (const m of text.matchAll(/<item\s+value\s*=\s*"[01-]+\s*:\s*([^"]*)"/gi)) {
      const listAt = m.index + m[0].indexOf(m[1]);
      for (const ref of m[1].matchAll(/\[([^\]]+)\]/g)) {
        for (const name of await this.expandNameList(ref[1].trim())) {
          if (isField(name) || reported.has('v:' + name.toLowerCase())) continue;
          reported.add('v:' + name.toLowerCase());
          warn(listAt + ref.index, ref[0].length, `View dùng [${name}] nhưng <fields> không khai báo field "${name}".`);
        }
      }
    }

    // ---- f.getItem('x') and friends in JS ----
    const jsRef = /([\w$\])]+)\.(getItem|getItemValue|setItemValue|setReferenceKeyFilter|setItemValues|validFields)\(\s*'([^']+)'/g;
    for (const m of text.matchAll(jsRef)) {
      if (/_controlBehavior$|\$a$/.test(m[1])) continue; // grid behaviours have their own columns
      const names = /^(setItemValues|validFields)$/.test(m[2]) ? m[3].split(',') : [m[3]];
      for (const raw of names) {
        const name = raw.trim();
        if (!name || /[{$%&<>\[\]]/.test(name) || isField(name) || reported.has('j:' + name.toLowerCase())) continue;
        reported.add('j:' + name.toLowerCase());
        warn(m.index + m[0].indexOf("'"), m[3].length + 2, `${m[2]}('${name}') nhưng <fields> không khai báo field "${name}".`);
      }
    }
    return items;
  }

  // ---- Presentation --------------------------------------------------------------------

  setItems(items) {
    // Sorted by position so reading the list top to bottom is reading the file top to
    // bottom; errors before warnings on the same line.
    this.items = (items || []).slice().sort((a, b) =>
      (a.line || 0) - (b.line || 0) ||
      (a.column || 0) - (b.column || 0) ||
      (a.severity === b.severity ? 0 : a.severity === 'error' ? -1 : 1));
    this.render();
    this.applyMarkers();
  }

  /// Squiggles in the text itself, which is where the problem actually is — the panel only
  /// makes them enumerable. Monaco owns the minimap/overview-ruler marks that come with them.
  applyMarkers() {
    const model = this.bcode.currentModel;
    if (!model) return;
    const markers = this.items
      .filter((i) => i.line != null)
      .map((i) => ({
        severity: i.severity === 'error' ? monaco.MarkerSeverity.Error : monaco.MarkerSeverity.Warning,
        message: i.text,
        startLineNumber: i.line,
        startColumn: i.column || 1,
        endLineNumber: i.line,
        // No length given: underline to the end of the line rather than a zero-width
        // squiggle nobody can see or hover.
        endColumn: i.length ? (i.column || 1) + i.length : model.getLineMaxColumn(i.line),
      }));
    monaco.editor.setModelMarkers(model, 'bcode', markers);
  }

  render() {
    const errors = this.items.filter((i) => i.severity === 'error').length;
    const warnings = this.items.length - errors;

    this.dock.setBadge('problemsPanel', this.items.length);

    // The summary line is the only always-visible surface, so it carries the counts and
    // the first message — enough to decide whether to open the panel at all.
    if (this.items.length === 0) {
      this.summary.style.display = 'none';
    } else {
      this.summary.style.display = 'flex';
      this.summary.innerHTML = '';
      if (errors) {
        const el = document.createElement('span');
        el.className = 'sev error';
        el.textContent = `✖ ${errors} lỗi`;
        this.summary.appendChild(el);
      }
      if (warnings) {
        const el = document.createElement('span');
        el.className = 'sev warn';
        el.textContent = `⚠ ${warnings} cảnh báo`;
        this.summary.appendChild(el);
      }
      const first = document.createElement('span');
      first.textContent = this.items[0].text;
      first.style.cssText = 'overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
      this.summary.appendChild(first);
    }

    this.list.innerHTML = '';
    if (this.items.length === 0) {
      const empty = document.createElement('div');
      empty.className = 'dockEmpty';
      empty.textContent = 'Không có vấn đề nào trong file đang mở.';
      this.list.appendChild(empty);
      return;
    }

    for (const item of this.items) {
      const row = buildResultRow({
        icon: item.severity === 'error' ? '✖' : '⚠',
        iconClass: item.severity === 'error' ? 'error' : 'warn',
        location: item.line != null ? `${item.line}:${item.column || 1}` : '',
        text: item.text,
        title: item.openPath ? `Ctrl+click để mở: ${item.openPath}` : item.text,
        onClick: (e) => {
          // Ctrl+click on a missing-entity row opens the file it names instead of the
          // declaration line — useful the moment someone creates it.
          if (e.ctrlKey && item.openPath) this.bcode.openFile(item.openPath);
          else this.bcode.goToValidationIssue(item);
        },
      });
      this.list.appendChild(row);
    }
  }
}

window.BcodeProblems = BcodeProblems;