// Entity resolution: what does `&Name;` actually contain, and where was it declared?
//
// An FCode controller carries most of its real code inside DOCTYPE entities — whole T-SQL
// routines and JavaScript blocks declared as `<!ENTITY Name "…">` — and a controller's own
// DOCTYPE usually declares almost none of them itself. It pulls in a chain of files through
// `<!ENTITY X SYSTEM "…">`, and the value entity you are looking at was declared two or
// three files up that chain.
//
// F12 used to handle only the SYSTEM half: if the word under the caret named a file entity
// declared in THIS document, it opened that file. On a value entity — which is what most
// `&Name;` references in a controller are — it did nothing at all, silently, which left no
// way to find out what a reference expands to short of grepping the folder by hand.
//
// This resolves a name against the current document *and* everything it includes, and shows
// the declared code in a peek window rather than only jumping: an entity value is often a
// few hundred lines, and landing in the middle of someone else's DOCTYPE subset with no way
// back is not the same as reading what the reference means.

/// How many levels of `SYSTEM` includes to follow. ĐÃ SỬA: từng để 4, nhưng cái thật sự
/// chặn vòng lặp vô hạn (1 file include chính nó, trực tiếp hoặc gián tiếp) là tập `seen`
/// bên dưới — mỗi đường dẫn chỉ được thăm 1 lần, nên vòng lặp bị chặn dù depth có lớn tới
/// đâu. Giới hạn 4 hoá ra lại chặn nhầm các chuỗi include THẲNG (không lặp) sâu hơn 4 lớp —
/// đúng kiểu cấu trúc thư mục nhiều tầng Include\XML\... của FastBusiness, khiến F12/hover
/// im lặng với entity khai báo ở tầng include thứ 5 trở đi dù không hề có vòng lặp gì. Nâng
/// lên một số rất lớn để depth chỉ còn là lưới an toàn cho đồ thị include bệnh lý (rất rộng
/// VÀ rất sâu cùng lúc), không còn là giới hạn thực tế cho chuỗi include bình thường nữa.
const ENTITY_INCLUDE_DEPTH = 64;

function escapeRegExp(s) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

class BcodeEntity {
  constructor(bcode) {
    this.bcode = bcode;

    this.fileCache = new Map();

    // Khai báo đã phân tích theo từng file: path -> { text, decls, byName }. resolve() được gọi cho TỪNG
    // &Entity; (kiểm tra lỗi chạy lại sau mỗi lần gõ) × từng file include; trước đây mỗi lần như vậy là 1 lượt
    // regex quét lại toàn bộ file include → vài trăm × vài chục file × hàng trăm KB mỗi lần dừng gõ = giật.
    this.declCache = new Map();

    // Kết quả tìm entity theo chuỗi include của file đang mở: name -> kết quả | null. Việc tìm 1 tên là đi qua
    // MỌI file include (hàng chục file, mỗi file hàng trăm khai báo); kiểm tra lỗi lại hỏi cả trăm tên sau mỗi
    // lần gõ nên cộng lại là hàng giây trên luồng giao diện. Chuỗi include chỉ đổi khi phần <!DOCTYPE ...]>
    // của chính file đổi (hoặc file include đổi trên đĩa → invalidate()), nên khoá theo 2 thứ đó.
    this.chainMemos = new Map(); // khoá (đường dẫn|DOCTYPE) -> Map(name -> kết quả); giữ vài file gần nhất

    this.includeIndex = new Map();

    this.workspaceEntityCache = new Map();
    this.peekEditor = null;
    this.overlay = null;
  }

  /// File này đã từng được đọc làm file include của 1 file khác (đang nằm trong cache)? Chỉ khi đó lưu nó mới làm
  /// cache entity cũ đi — lưu 1 controller bình thường thì không cần bỏ cache và dựng lại chỉ mục include.
  isCachedFile(path) {
    return !!path && this.fileCache.has(String(path).toLowerCase());
  }

  invalidate() {
    this.fileCache.clear();
    this.declCache.clear();
    this.chainMemos.clear();
    this.includeIndex.clear();
    // Worker kiểm tra lỗi có bộ nhớ đệm riêng — báo cho nó bỏ luôn (file include có thể vừa đổi).
    if (typeof window !== 'undefined' && window.bcodeProblems && window.bcodeProblems.invalidateWorker) window.bcodeProblems.invalidateWorker();
    this.workspaceEntityCache.clear();
  }


  /// Conditional section của DTD: <![%Conditional.Revert;[ ...khai báo... ]]> (hoặc <![INCLUDE[ ]]> / <![IGNORE[ ]]>).
  /// Tham số %Conditional.Revert; thường trỏ tới 1 file nhỏ chỉ chứa chữ INCLUDE hoặc IGNORE
  /// (vd. Include\Revert.txt). IGNORE thì cả khối bị bỏ qua — nên khai báo đứng sau nó (cùng tên entity)
  /// mới là cái có hiệu lực. Thiếu bước này thì luôn lấy khai báo đầu tiên (trong khối) và sai
  /// ngược với FastBusiness. Khối bị bỏ được thay bằng khoảng trắng GIỮ NGUYÊN độ dài và xuống dòng,
  /// để offset của các khai báo còn lại (dùng cho F12/hover) không lệch.
  async applyConditionals(text, path) {
    if (!text || text.indexOf('<![') < 0) return text;
    const startRe = /<!\[\s*(?:%([A-Za-z_][\w.:$-]*);|(INCLUDE|IGNORE))\s*\[/g;
    const params = this.parseDeclarations(text).filter((d) => d.isParam);
    const dir = dirNameOf(path || '');
    let out = text;
    let m;
    while ((m = startRe.exec(text))) {
      // Tìm ]]> khớp, tính cả khối lồng nhau.
      let depth = 1;
      let i = m.index + m[0].length;
      while (i < text.length && depth > 0) {
        const open = text.indexOf('<![', i);
        const close = text.indexOf(']]>', i);
        if (close < 0) { i = text.length; break; }
        if (open >= 0 && open < close) { depth++; i = open + 3; } else { depth--; i = close + 3; }
      }
      const end = i;

      let mode = m[2] ? m[2].toUpperCase() : null;
      if (!mode) {
        const decl = params.find((d) => d.name === m[1]);
        let value = '';
        if (decl && decl.kind === 'value') value = decl.value || '';
        else if (decl && decl.kind === 'system') {
          const file = await this.readFile(resolvePath(dir, decl.systemPath.replace(/\//g, '\\')));
          value = file || '';
        }
        mode = /^\s*\uFEFF?\s*INCLUDE\s*$/i.test(value) ? 'INCLUDE' : 'IGNORE';
      }

      if (mode === 'IGNORE') {
        const blank = text.slice(m.index, end).replace(/[^\n]/g, ' ');
        out = out.slice(0, m.index) + blank + out.slice(end);
        startRe.lastIndex = end;
      }
      // INCLUDE: giữ nguyên, và tiếp tục quét BÊN TRONG (đã đặt lastIndex sau phần mở đầu).
    }
    return out;
  }

  async declInfo(text, path) {
    const key = (path || '').toLowerCase();
    const hit = this.declCache.get(key);
    // text === text: cùng tham chiếu chuỗi (file include lấy từ fileCache) thì so sánh O(1).
    if (hit && hit.text === text) return hit;
    const decls = this.parseDeclarations(await this.applyConditionals(text, path));
    const byName = new Map();
    for (const d of decls) if (!byName.has(d.name)) byName.set(d.name, d); // trùng tên: khai báo đầu thắng
    const info = { text, decls, byName };
    this.declCache.set(key, info);
    return info;
  }

  async declsOf(text, path) {
    return (await this.declInfo(text, path)).decls;
  }

  parseDeclarations(text) {
    const decls = [];
    const re = /<!ENTITY\s+/g;
    let m;
    while ((m = re.exec(text))) {
      let i = m.index + m[0].length;
      let isParam = false;
      if (text[i] === '%') { isParam = true; i++; while (/\s/.test(text[i])) i++; }

      const nameMatch = /^[A-Za-z_][\w.:$-]*/.exec(text.slice(i, i + 200));
      if (!nameMatch) continue;
      const name = nameMatch[0];
      i += name.length;
      while (/\s/.test(text[i])) i++;

      let kind = 'value';
      if (/^SYSTEM\b/i.test(text.slice(i, i + 7))) {
        kind = 'system';
        i += 6;
        while (/\s/.test(text[i])) i++;
      } else if (/^PUBLIC\b/i.test(text.slice(i, i + 7))) {

        i += 6;
        while (/\s/.test(text[i])) i++;
        const skipped = this.readQuoted(text, i);
        if (!skipped) continue;
        i = skipped.end;
        while (/\s/.test(text[i])) i++;
        kind = 'system';
      }

      const quoted = this.readQuoted(text, i);
      if (!quoted) continue;

      decls.push({
        name,
        isParam,
        kind,
        value: kind === 'value' ? quoted.text : null,
        systemPath: kind === 'system' ? quoted.text : null,
        offset: m.index,
        valueOffset: quoted.start,
      });

      re.lastIndex = quoted.end;
    }
    return decls;
  }


  readQuoted(text, i) {
    const quote = text[i];
    if (quote !== '"' && quote !== "'") return null;
    const end = text.indexOf(quote, i + 1);
    if (end < 0) return null;
    return { text: text.slice(i + 1, end), start: i + 1, end: end + 1 };
  }


  async readFile(path) {
    const key = path.toLowerCase();
    if (this.fileCache.has(key)) return this.fileCache.get(key);
    let text = null;
    try { text = await window.bcodeHost.call('BeginReadFile', path); }
    catch { text = null; } // missing include — the Problems panel already reports that
    this.fileCache.set(key, text);
    return text;
  }

  async resolve(name, path, text, seen, depth) {
    seen = seen || new Set();
    depth = depth == null ? ENTITY_INCLUDE_DEPTH : depth;
    if (!text || seen.has(path.toLowerCase())) return null;
    seen.add(path.toLowerCase());

    const info = await this.declInfo(text, path);
    const decls = info.decls;
    const hit = info.byName.get(name);
    if (hit) return { decl: hit, path, text };
    if (depth <= 0) return null;

    const dir = dirNameOf(path);
    for (const include of decls.filter((d) => d.kind === 'system')) {
      const resolved = resolvePath(dir, include.systemPath.replace(/\//g, '\\'));
      if (seen.has(resolved.toLowerCase())) continue;
      const included = await this.readFile(resolved);
      if (!included) continue;
      const found = await this.resolve(name, resolved, included, seen, depth - 1);
      if (found) return found;
    }
    return null;
  }


  /// resolve() cho file đang mở, có nhớ kết quả theo (đường dẫn, phần DOCTYPE) — xem chainMemo.
  async resolveTop(name, path, text) {
    const head = (/<!DOCTYPE[\s\S]*?\]>/i.exec(text) || [''])[0];
    const key = path.toLowerCase() + '|' + head;
    let memo = this.chainMemos.get(key);
    if (!memo) {
      memo = new Map();
      this.chainMemos.set(key, memo);
      if (this.chainMemos.size > 6) this.chainMemos.delete(this.chainMemos.keys().next().value);
    }
    if (memo.has(name)) return memo.get(name);
    const result = await this.resolve(name, path, text);
    memo.set(name, result);
    return result;
  }

  async resolveActive(name) {
    const bcode = this.bcode;
    if (!bcode.activePath || !bcode.currentModel) return null;
    const direct = await this.resolveTop(name, bcode.activePath, bcode.currentModel.getValue());
    if (direct) return direct;
    return this.resolveViaWorkspaceSearch(name);
  }


  /// Chỉ đi theo chuỗi include của file đang mở — KHÔNG fallback sang tìm cả project. Dùng cho
  /// kiểm tra lỗi: entity có khai báo ở file nào đó trong project nhưng file này không include
  /// tới thì lúc chạy vẫn là "không tìm thấy", nên Problems phải báo.
  async resolveChainOnly(name) {
    const bcode = this.bcode;
    if (!bcode.activePath || !bcode.currentModel) return null;
    return this.resolveTop(name, bcode.activePath, bcode.currentModel.getValue());
  }

  async resolveViaWorkspaceSearch(name) {
    if (this.workspaceEntityCache.has(name)) return this.workspaceEntityCache.get(name);
    const found = await this._searchWorkspaceForEntity(name);
    this.workspaceEntityCache.set(name, found);
    return found;
  }

  async _searchWorkspaceForEntity(name) {
    let root;
    try { root = await window.chrome.webview.hostObjects.host.GetWorkspaceRoot(this.bcode.activePath); }
    catch { root = null; }
    if (!root) return null;

    const pattern = '<!ENTITY\\s+%?\\s*' + escapeRegExp(name) + '(?=[\\s"\'])';
    let raw;
    try {
      raw = await window.bcodeHost.call('BeginSearchWorkspace', root, pattern, true, false, false, '', 50);
    } catch { return null; }

    let result;
    try { result = JSON.parse(raw); } catch { return null; }
    if (result.error || !result.matches || result.matches.length === 0) return null;


    const seenPaths = new Set();
    for (const match of result.matches) {
      const key = match.path.toLowerCase();
      if (seenPaths.has(key)) continue;
      seenPaths.add(key);
      const text = await this.readFile(match.path);
      if (!text) continue;
      const decls = await this.declsOf(text, match.path);
      const hit = decls.find((d) => d.name === name);
      if (hit) return { decl: hit, path: match.path, text };
    }
    return null;
  }


  async buildIncludeIndex(path, text, seen, depth) {
    seen = seen || new Set([path.toLowerCase()]);
    depth = depth == null ? ENTITY_INCLUDE_DEPTH : depth;
    const index = new Map();
    if (depth <= 0) return index;

    const dir = dirNameOf(path);
    for (const include of (await this.declsOf(text, path)).filter((d) => d.kind === 'system')) {
      const resolved = resolvePath(dir, include.systemPath.replace(/\//g, '\\'));
      if (seen.has(resolved.toLowerCase())) continue;
      seen.add(resolved.toLowerCase());
      const included = await this.readFile(resolved);
      if (!included) continue;

      for (const decl of await this.declsOf(included, resolved)) {

        if (!index.has(decl.name)) index.set(decl.name, { decl, path: resolved });
      }
      for (const [name, entry] of await this.buildIncludeIndex(resolved, included, seen, depth - 1)) {
        if (!index.has(name)) index.set(name, entry);
      }
    }
    return index;
  }

  async refreshIncludeIndex(path, text) {
    if (!path || !text) return;
    try {
      this.includeIndex.set(path.toLowerCase(), await this.buildIncludeIndex(path, text));
    } catch {
    }
  }

  includeIndexFor(path) {
    return path ? this.includeIndex.get(path.toLowerCase()) || null : null;
  }

  async expand(text, maxPasses) {
    maxPasses = maxPasses || 5;
    const expanded = new Set();
    const unresolved = new Set();
    const resolvedValues = new Map(); // name -> value | null, so one name costs one walk

    let result = text;
    for (let pass = 0; pass < maxPasses; pass++) {
      const names = [...new Set(
        [...result.matchAll(/&([A-Za-z_][\w.:$-]*);/g)].map((m) => m[1])
      )].filter((n) => !XML_BUILTIN_ENTITIES.has(n) && !unresolved.has(n));
      if (names.length === 0) break;

      for (const name of names) {
        if (resolvedValues.has(name)) continue;
        const found = await this.resolveActive(name);
        resolvedValues.set(name, found && found.decl.kind === 'value' ? found.decl.value : null);
      }

      let changed = false;
      for (const name of names) {
        const value = resolvedValues.get(name);
        if (value == null) { unresolved.add(name); continue; }
        const ref = '&' + name + ';';
        if (result.includes(ref)) {
          result = result.split(ref).join(value);
          expanded.add(name);
          changed = true;
        }
      }
      if (!changed) break;
    }

    return { text: result, expanded: [...expanded], unresolved: [...unresolved] };
  }

  /// Như expand(), nhưng entity kiểu SYSTEM ("include\Revert.xml") cũng được thay bằng NỘI DUNG file đó
  /// (bỏ dòng <?xml ...?>). Dùng cho kiểm tra lỗi: view/field/action nằm trong file include vẫn là có thật.
  async expandFull(text, maxPasses) {
    maxPasses = maxPasses || 6;
    const unresolved = new Set();
    const values = new Map(); // name -> text | null
    let result = text;
    for (let pass = 0; pass < maxPasses; pass++) {
      const names = [...new Set(
        [...result.matchAll(/&([A-Za-z_][\w.:$-]*);/g)].map((m) => m[1])
      )].filter((n) => !XML_BUILTIN_ENTITIES.has(n) && !unresolved.has(n));
      if (names.length === 0) break;

      for (const name of names) {
        if (values.has(name)) continue;
        const found = await this.resolveActive(name);
        let value = null;
        if (found && found.decl.kind === 'value') value = found.decl.value;
        else if (found && found.decl.kind === 'system') {
          const target = resolvePath(dirNameOf(found.path), found.decl.systemPath.replace(/\//g, '\\'));
          const content = await this.readFile(target);
          if (content != null) value = content.replace(/^﻿?\s*<\?xml[^>]*\?>/i, '');
        }
        values.set(name, value);
      }

      let changed = false;
      for (const name of names) {
        const value = values.get(name);
        if (value == null) { unresolved.add(name); continue; }
        const ref = '&' + name + ';';
        if (result.includes(ref)) { result = result.split(ref).join(value); changed = true; }
      }
      if (!changed) break;
    }
    return { text: result, unresolved: [...unresolved] };
  }


  /// Tự trích xuất từ dưới con trỏ, cho phép chứa dấu chấm (.), gạch ngang (-), hai chấm (:), đô la ($)
  getEntityWordAtPosition(model, position) {
    const line = model.getLineContent(position.lineNumber);
    const col = position.column - 1; // Monaco column bắt đầu từ 1
    const re = /[A-Za-z_][\w.:$-]*/g;
    let m;
    while ((m = re.exec(line))) {
      const start = m.index;
      const end = start + m[0].length;
      if (col >= start && col <= end) {
        return {
          word: m[0],
          startColumn: start + 1,
          endColumn: end + 1
        };
      }
    }
    return null;
  }

  nameAtCaret() {
    const model = this.bcode.currentModel;
    const pos = this.bcode.editor.getPosition();
    if (!model || !pos) return null;
    
    // Đã sửa: Dùng hàm custom để lấy được cả dấu chấm
    const word = this.getEntityWordAtPosition(model, pos); 
    
    return word ? word.word : null;
  }

  quotedPathAtCaret() {
    return this.quotedPathAt(this.bcode.currentModel, this.bcode.editor.getPosition());
  }

  quotedPathAt(model, pos) {
    if (!model || !pos) return null;
    const line = model.getLineContent(pos.lineNumber);
    const caret = pos.column - 1; // chỉ số (0-based) của ký tự ngay sau con trỏ
    const re = /(["'])([^"'\r\n]*)\1/g;
    let m;
    while ((m = re.exec(line))) {
      const start = m.index + 1;
      const end = start + m[2].length;
      if (caret < start || caret > end) continue;
      const value = m[2].trim();
      return /[\\/]/.test(value) && /\.[A-Za-z0-9]{1,6}$/.test(value) ? value : null;
    }
    return null;
  }


  /// F12 = XEM TRƯỚC (cửa sổ peek, không rời file đang sửa) — kể cả khi đích là 1 file;
  /// Ctrl+F12 = ĐI TỚI: mở file đó (entity SYSTEM / đường dẫn trong dấu nháy) hoặc nhảy tới nơi
  /// khai báo (entity có giá trị). Trước đây F12 mở thẳng file với 2 loại đầu, chỉ entity có giá trị
  /// mới có peek, nên không có cách nào "xem lướt" 1 file include mà không bị chuyển tab.
  async goToOrPeek(mode = 'peek') {
    const go = mode === 'go';
    // Bôi đen NHIỀU entity rồi F12 → xem trước lần lượt nội dung của từng entity (1 trang hoặc từng trang).
    if (!go) {
      const names = this.entityNamesInSelection();
      if (names.length >= 2) { await this.showMultiPeek(names); return true; }
    }
    const quotedPath = this.quotedPathAtCaret();
    if (quotedPath && this.bcode.activePath) {
      const normalized = quotedPath.replace(/\//g, '\\');
      const isAbsolute = /^([A-Za-z]:\\|\\\\)/.test(normalized);
      const target = isAbsolute ? normalized : resolvePath(dirNameOf(this.bcode.activePath), normalized);
      let exists = false;
      try { exists = JSON.parse(await window.bcodeHost.call('BeginPathsExist', JSON.stringify([target])))[0]; }
      catch { exists = false; }
      if (exists) {
        if (go) await this.bcode.openFile(target);
        else await this.showPeekFile(fileNameOf(target), target);
        return true;
      }
    }

    const name = this.nameAtCaret();
    if (!name) return false;

    const found = await this.resolveActive(name);
    if (!found) return false;

    if (found.decl.kind === 'system') {
      const dir = dirNameOf(found.path);
      const target = resolvePath(dir, found.decl.systemPath.replace(/\//g, '\\'));
      let exists = false;
      try { exists = JSON.parse(await window.bcodeHost.call('BeginPathsExist', JSON.stringify([target])))[0]; }
      catch { exists = false; }
      if (!exists) return false;
      if (go) await this.bcode.openFile(target);
      else await this.showPeekFile(`&${name};`, target);
      return true;
    }

    if (go) await this.openDeclaration(found);
    else this.showPeek(name, found);
    return true;
  }

  /// Tên các entity nằm trong vùng đang bôi đen, THEO THỨ TỰ xuất hiện (không trùng): khai báo <!ENTITY Name ...>, tham chiếu &Name; và %Name;.
  entityNamesInSelection() {
    const model = this.bcode.currentModel;
    const sel = this.bcode.editor.getSelection();
    if (!model || !sel || sel.isEmpty()) return [];
    const text = model.getValueInRange(sel);
    const re = /<!ENTITY\s+%?\s*([A-Za-z_][\w.:$-]*)|&([A-Za-z_][\w.:$-]*);|%([A-Za-z_][\w.:$-]*);/g;
    const out = [];
    const seen = new Set();
    let m;
    while ((m = re.exec(text))) {
      const name = m[1] || m[2] || m[3];
      if (!name || XML_BUILTIN_ENTITIES.has(name) || seen.has(name)) continue;
      seen.add(name);
      out.push(name);
    }
    return out;
  }

  /// Nội dung của 1 entity để xem trước: entity giá trị → chính giá trị; entity SYSTEM → nội dung file (đọc mới từ đĩa).
  async entityPreviewItem(name) {
    const found = await this.resolveActive(name);
    if (!found) return { name, title: '&' + name + ';', subtitle: 'Không tìm thấy khai báo entity này', text: '(không tìm thấy entity "' + name + '")', language: 'plaintext', missing: true };
    if (found.decl.kind === 'system') {
      const target = resolvePath(dirNameOf(found.path), found.decl.systemPath.replace(/\//g, '\\'));
      let text;
      try { text = await window.bcodeHost.call('BeginReadFile', target); }
      catch (e) { text = '(Không đọc được file: ' + (e && e.message ? e.message : e) + ')'; }
      text = text == null ? '' : String(text);
      const ext = target.slice(target.lastIndexOf('.')).toLowerCase();
      const language = ['.xml', '.f', '.ent'].includes(ext) ? (window.bcodeFcodeLanguageReady ? window.FCODE_LANGUAGE_ID : 'xml') : this.languageOf(text);
      return { name, title: '&' + name + ';', subtitle: target, text, language, ctxPath: target, ctxText: text, onOpen: async () => { this.closePeek(); await this.bcode.openFile(target); } };
    }
    const lines = found.decl.value.split('\n').length;
    return {
      name, title: '&' + name + ';', subtitle: 'Khai báo tại ' + fileNameOf(found.path) + ' · dòng ' + offsetToPosition(found.text, found.decl.offset).line + ' · ' + lines + ' dòng',
      text: found.decl.value, language: this.languageOf(found.decl.value), ctxPath: found.path, ctxText: found.text, onOpen: () => this.openDeclaration(found),
    };
  }

  /// Xem trước nhiều entity, 2 cách (nhớ lựa chọn lần trước): "Một trang" = nối toàn bộ nội dung theo thứ tự vào 1 khung (có dòng phân cách);
  /// "Từng entity" = mỗi entity 1 trang, chuyển bằng ◀ ▶ / Alt+← Alt+→ / bấm tên.
  async showMultiPeek(names, startIdx = 0) {
    const items = [];
    for (const n of names) items.push(await this.entityPreviewItem(n));

    // Giữ lịch sử khi quay lại từ 1 khung F12 con (xem peekBack) — mở mới từ editor chính thì xoá.
    const keep = this._keepHistory; this._keepHistory = null;
    this._disposePeek();
    this.peekHistory = keep || [];
    let mode = 'all';
    try { mode = localStorage.getItem('bcodePeekMode') === 'each' ? 'each' : 'all'; } catch { /* không có localStorage */ }
    let idx = Math.min(Math.max(0, startIdx), Math.max(0, items.length - 1));

    const overlay = document.createElement('div');
    overlay.className = 'peekOverlay';
    overlay.onmousedown = (e) => { if (e.target === overlay) this.closePeek(); };
    const box = document.createElement('div');
    box.className = 'peekBox';

    const header = document.createElement('div');
    header.className = 'peekHeader';
    const titleWrap = document.createElement('div');
    const title = document.createElement('div'); title.className = 'peekTitle';
    const subtitle = document.createElement('div'); subtitle.className = 'peekSubtitle';
    titleWrap.append(title, subtitle);
    const modes = document.createElement('div'); modes.className = 'peekModes';
    const btnAll = document.createElement('button'); btnAll.textContent = 'Một trang'; btnAll.title = 'Xem toàn bộ nội dung các entity trong 1 trang';
    const btnEach = document.createElement('button'); btnEach.textContent = 'Từng entity'; btnEach.title = 'Mỗi entity một trang (Alt+←  Alt+→)';
    modes.append(btnAll, btnEach);
    const closeBtn = document.createElement('span'); closeBtn.className = 'peekClose'; closeBtn.textContent = '✕'; closeBtn.title = 'Đóng (Esc)';
    closeBtn.onclick = () => this.closePeek();
    header.append(titleWrap, modes, closeBtn);

    const nav = document.createElement('div'); nav.className = 'peekNav';
    const prev = document.createElement('button'); prev.className = 'dlgButton'; prev.textContent = '◀'; prev.title = 'Entity trước (Alt+←)';
    const next = document.createElement('button'); next.className = 'dlgButton'; next.textContent = '▶'; next.title = 'Entity sau (Alt+→)';
    const counter = document.createElement('span'); counter.className = 'peekHint'; counter.style.flex = '0 0 auto';
    const chips = document.createElement('div'); chips.className = 'peekChips';
    nav.append(prev, counter, next, chips);

    const body = document.createElement('div'); body.className = 'peekBody';

    const footer = document.createElement('div'); footer.className = 'peekFooter';
    const hint = document.createElement('span'); hint.className = 'peekHint';
    this.peekHint = hint;
    const copyBtn = document.createElement('button'); copyBtn.className = 'dlgButton';
    const openBtn = document.createElement('button'); openBtn.className = 'dlgButton primary';
    footer.append(hint, copyBtn, openBtn);

    box.append(header, nav, body, footer);
    overlay.appendChild(box);
    document.body.appendChild(overlay);

    // Dòng phân cách giữa các entity ở chế độ "Một trang": đúng kiểu chú thích của ngôn ngữ đang dùng.
    const languages = [...new Set(items.map((i) => i.language))];
    const allLanguage = languages.length === 1 ? languages[0] : (window.bcodeFcodeLanguageReady ? window.FCODE_LANGUAGE_ID : 'xml');
    const sep = (it) => {
      const label = '===== ' + it.title + '  (' + it.subtitle + ') =====';
      if (allLanguage === 'sql') return '-- ' + label;
      if (allLanguage === 'javascript') return '// ' + label;
      if (allLanguage === 'plaintext') return label;
      return '<!-- ' + label.replace(/--/g, '- -') + ' -->';
    };
    const allText = () => items.map((it) => sep(it) + '\n' + it.text).join('\n\n');

    this.peekEditor = monaco.editor.create(body, {
      value: '', language: 'plaintext', theme: window.bcodeTheme ? window.bcodeTheme.monacoThemeName : 'vs-dark',
      readOnly: true, automaticLayout: true, fontFamily: window.bcodeTheme ? window.bcodeTheme.fontFamily : "'Roboto', Consolas, monospace",
      fontSize: 15, minimap: { enabled: false }, scrollBeyondLastLine: false,
    });

    // F12 / Ctrl+F12 ngay trong khung nhiều entity: tìm theo entity đang chứa con trỏ (chế độ "Một trang": đổi dòng → entity qua các đoạn nối).
    const itemAtCaret = () => {
      if (mode === 'each') return items[idx];
      const line = this.peekEditor.getPosition().lineNumber;
      let start = 1;
      for (const it of items) {
        const count = 1 + it.text.split('\n').length;   // dòng phân cách + nội dung
        if (line < start + count) return it;
        start += count + 1;                               // + 1 dòng trống giữa các entity
      }
      return items[items.length - 1];
    };
    const multiNavigate = (m) => {
      const it = itemAtCaret();
      if (!it || !it.ctxPath) return;
      this.peekCurrent = { ctxPath: it.ctxPath, ctxText: it.ctxText, restore: () => this.showMultiPeek(names, items.indexOf(it)) };
      this.peekNavigate(m);
    };
    this.peekEditor.addCommand(monaco.KeyCode.F12, () => multiNavigate('peek'));
    this.peekEditor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.F12, () => multiNavigate('go'));

    const render = () => {
      const each = mode === 'each';
      btnAll.classList.toggle('on', !each); btnEach.classList.toggle('on', each);
      nav.style.display = each ? 'flex' : 'none';
      const it = items[idx];
      let text, language;
      if (each) {
        text = it.text; language = it.language;
        title.textContent = it.title + '   (' + (idx + 1) + '/' + items.length + ')';
        subtitle.textContent = it.subtitle; subtitle.title = it.subtitle;
        counter.textContent = (idx + 1) + ' / ' + items.length;
        prev.disabled = idx === 0; next.disabled = idx === items.length - 1;
        chips.innerHTML = '';
        items.forEach((x, i) => {
          const c = document.createElement('button');
          c.className = 'peekChip' + (i === idx ? ' on' : '') + (x.missing ? ' missing' : ''); c.textContent = x.name; c.title = x.subtitle;
          c.onclick = () => { idx = i; render(); };
          chips.appendChild(c);
        });
        hint.textContent = 'Chỉ xem — Alt+←/→ chuyển entity, F12 xem tiếp entity dưới con trỏ, Esc đóng.';
        copyBtn.textContent = 'Copy entity này'; copyBtn.dataset.label = 'Copy entity này';
        openBtn.style.display = it.onOpen ? '' : 'none'; openBtn.textContent = 'Mở entity này';
      } else {
        text = allText(); language = allLanguage;
        title.textContent = items.length + ' entity đã chọn';
        subtitle.textContent = items.map((x) => x.title).join('  ');
        subtitle.title = subtitle.textContent;
        hint.textContent = 'Chỉ xem — nội dung các entity nối theo thứ tự đã chọn. F12 xem tiếp entity dưới con trỏ.';
        copyBtn.textContent = 'Copy tất cả'; copyBtn.dataset.label = 'Copy tất cả';
        openBtn.style.display = 'none';
      }
      const model = this.peekEditor.getModel();
      monaco.editor.setModelLanguage(model, language);
      model.setValue(text);
      this.fitPeekBox(box, body, each ? items.map((x) => x.text) : [text]);
      this.peekEditor.setScrollTop(0);
      this.peekEditor.setPosition({ lineNumber: 1, column: 1 });
    };
    const setMode = (m) => { mode = m; try { localStorage.setItem('bcodePeekMode', m); } catch { /* bỏ qua */ } render(); };
    btnAll.onclick = () => setMode('all');
    btnEach.onclick = () => setMode('each');
    prev.onclick = () => { if (idx > 0) { idx--; render(); } };
    next.onclick = () => { if (idx < items.length - 1) { idx++; render(); } };
    copyBtn.onclick = () => {
      navigator.clipboard.writeText(mode === 'each' ? items[idx].text : allText());
      const label = copyBtn.dataset.label; copyBtn.textContent = 'Đã copy'; setTimeout(() => { copyBtn.textContent = label; }, 1200);
    };
    openBtn.onclick = () => { const it = items[idx]; if (it && it.onOpen) it.onOpen(); };

    this.escHandler = (e) => {
      if (e.key === 'Escape') { this.closePeek(); return; }
      if (mode === 'each' && e.altKey && e.key === 'ArrowLeft') { e.preventDefault(); prev.click(); }
      if (mode === 'each' && e.altKey && e.key === 'ArrowRight') { e.preventDefault(); next.click(); }
    };
    document.addEventListener('keydown', this.escHandler, true);
    this.overlay = overlay;
    render();
    this.peekEditor.focus();
  }

  /// Peek của 1 FILE (đọc mới từ đĩa, không dùng cache include — người dùng có thể vừa sửa nó).
  async showPeekFile(title, path) {
    let text;
    try { text = await window.bcodeHost.call('BeginReadFile', path); }
    catch (e) { text = '(Không đọc được file: ' + (e && e.message ? e.message : e) + ')'; }
    text = text == null ? '' : String(text);

    const ext = path.slice(path.lastIndexOf('.')).toLowerCase();
    const language = ['.xml', '.f', '.ent'].includes(ext)
      ? (window.bcodeFcodeLanguageReady ? window.FCODE_LANGUAGE_ID : 'xml')
      : this.languageOf(text);

    this.showPeekContent({
      title,
      subtitle: path,
      subtitleTitle: path,
      text,
      language,
      openLabel: 'Mở file (Ctrl+F12)',
      ctxPath: path, ctxText: text,
      onOpen: async () => { this.closePeek(); await this.bcode.openFile(path); },
      hint: 'Chỉ xem — Ctrl+F12 (hoặc "Mở file") để chuyển tới file này.',
      copyLabel: 'Copy nội dung',
    });
  }


  async openDeclaration(found) {
    const pos = offsetToPosition(found.text, found.decl.offset);
    this.closePeek();
    await this.bcode.openFile(found.path, { line: pos.line, column: pos.col });
  }

  languageOf(value) {
    if (typeof looksLikeSql === 'function' && looksLikeSql(value)) return 'sql';
    if (typeof JS_HINT_RE !== 'undefined' && JS_HINT_RE.test(value)) return 'javascript';
    if (/<[A-Za-z!/]/.test(value)) {
      return window.bcodeFcodeLanguageReady ? window.FCODE_LANGUAGE_ID : 'xml';
    }
    return 'plaintext';
  }

  showPeek(name, found) {
    const sameFile = found.path.toLowerCase() === (this.bcode.activePath || '').toLowerCase();
    const lines = found.decl.value.split('\n').length;
    this.showPeekContent({
      title: `&${name};`,
      subtitle:
        `Khai báo ${sameFile ? 'trong file này' : 'tại ' + fileNameOf(found.path)} · ` +
        `dòng ${offsetToPosition(found.text, found.decl.offset).line} · ${lines} dòng`,
      subtitleTitle: found.path,
      text: found.decl.value,
      language: this.languageOf(found.decl.value),
      openLabel: 'Mở nơi khai báo (Ctrl+F12)',
      ctxPath: found.path, ctxText: found.text,
      onOpen: () => this.openDeclaration(found),
      hint: 'Chỉ xem — Ctrl+F12 (hoặc "Mở nơi khai báo") để chuyển tới đó.',
      copyLabel: 'Copy code',
    });
  }

  /// Khung peek dùng chung: entity có giá trị (showPeek) và file include (showPeekFile).
  showPeekContent(o) {
    // Lịch sử các khung đã xem (F12 tiếp trong khung peek) — giữ khi đang chuyển tiếp / quay lại, xoá khi mở peek mới từ editor chính.
    const keep = this._keepHistory; this._keepHistory = null;
    this._disposePeek();
    this.peekHistory = keep || [];
    this.peekCurrent = o;
    const lines = o.text.split('\n').length;

    const overlay = document.createElement('div');
    overlay.className = 'peekOverlay';
    overlay.onmousedown = (e) => { if (e.target === overlay) this.closePeek(); };

    const box = document.createElement('div');
    box.className = 'peekBox';

    const header = document.createElement('div');
    header.className = 'peekHeader';
    const titleWrap = document.createElement('div');
    const title = document.createElement('div');
    title.className = 'peekTitle';
    title.textContent = o.title;
    const subtitle = document.createElement('div');
    subtitle.className = 'peekSubtitle';
    subtitle.textContent = o.subtitle;
    subtitle.title = o.subtitleTitle || o.subtitle;
    titleWrap.append(title, subtitle);

    const closeBtn = document.createElement('span');
    closeBtn.className = 'peekClose';
    closeBtn.textContent = '✕';
    closeBtn.title = 'Đóng (Esc)';
    closeBtn.onclick = () => this.closePeek();
    header.append(titleWrap, closeBtn);

    const body = document.createElement('div');
    body.className = 'peekBody';

    const footer = document.createElement('div');
    footer.className = 'peekFooter';
    const openBtn = document.createElement('button');
    openBtn.className = 'dlgButton primary';
    openBtn.textContent = o.openLabel;
    openBtn.onclick = () => o.onOpen();
    const copyBtn = document.createElement('button');
    copyBtn.className = 'dlgButton';
    copyBtn.textContent = o.copyLabel;
    copyBtn.onclick = () => {
      navigator.clipboard.writeText(o.text);
      copyBtn.textContent = 'Đã copy';
      setTimeout(() => { copyBtn.textContent = o.copyLabel; }, 1200);
    };
    const hint = document.createElement('span');
    hint.className = 'peekHint';
    hint.textContent = o.hint + (o.ctxPath ? ' F12 trong khung này xem tiếp entity khác, Alt+← quay lại.' : '');
    this.peekHint = hint;
    footer.append(hint);
    if (this.peekHistory.length) {
      const backBtn = document.createElement('button');
      backBtn.className = 'dlgButton';
      backBtn.textContent = '◀ Quay lại';
      backBtn.onclick = () => this.peekBack();
      footer.append(backBtn);
    }
    footer.append(copyBtn, openBtn);

    box.append(header, body, footer);
    overlay.appendChild(box);
    document.body.appendChild(overlay);

    this.peekEditor = monaco.editor.create(body, {
      value: o.text,
      language: o.language,
      theme: window.bcodeTheme ? window.bcodeTheme.monacoThemeName : 'vs-dark',
      readOnly: true,
      automaticLayout: true,
      fontFamily: window.bcodeTheme ? window.bcodeTheme.fontFamily : "'Roboto', Consolas, monospace",
      fontSize: 15,
      minimap: { enabled: lines > 80 },
      scrollBeyondLastLine: false,
    });
    if (o.ctxPath) {
      // Trong khung peek: F12 xem tiếp entity / file dưới con trỏ (tính theo file của khung này), Ctrl+F12 đi tới, Alt+← quay lại khung trước.
      this.peekEditor.addCommand(monaco.KeyCode.F12, () => this.peekNavigate('peek'));
      this.peekEditor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.F12, () => this.peekNavigate('go'));
      this.peekEditor.addCommand(monaco.KeyMod.Alt | monaco.KeyCode.LeftArrow, () => this.peekBack());
    }
    this.fitPeekBox(box, body, [o.text]);
    this.peekEditor.focus();

    this.escHandler = (e) => { if (e.key === 'Escape') this.closePeek(); };
    document.addEventListener('keydown', this.escHandler, true);
    this.overlay = overlay;
  }

  /// Quay lại khung peek trước đó (Alt+← hoặc nút "Quay lại").
  peekBack() {
    if (!this.peekHistory || !this.peekHistory.length) return;
    const hist = this.peekHistory.slice();
    const prev = hist.pop();
    this._keepHistory = hist;
    if (prev.restore) prev.restore();   // khung nhiều entity: dựng lại đúng entity đang xem
    else this.showPeekContent(prev);
  }

  /// F12 / Ctrl+F12 NGAY TRONG khung peek: tìm entity (hoặc đường dẫn trong dấu nháy) dưới con trỏ, tính theo file đang xem trong khung
  /// chứ không theo file đang mở ở editor chính, rồi xem tiếp trong cùng khung (lịch sử để quay lại). Ctrl+F12 = đóng khung, mở nơi đó.
  async peekNavigate(mode) {
    const cur = this.peekCurrent, ed = this.peekEditor;
    if (!cur || !cur.ctxPath || !ed) return;
    const model = ed.getModel(), pos = ed.getPosition();
    const go = mode === 'go';
    const flash = (msg) => { if (this.peekHint) { const old = this.peekHint.textContent; this.peekHint.textContent = msg; setTimeout(() => { if (this.peekHint) this.peekHint.textContent = old; }, 2500); } };
    const showNested = (fn) => { this._keepHistory = [...(this.peekHistory || []), cur]; return fn(); };
    const exists = async (p) => { try { return JSON.parse(await window.bcodeHost.call('BeginPathsExist', JSON.stringify([p])))[0]; } catch { return false; } };

    const quoted = this.quotedPathAt(model, pos);
    if (quoted) {
      const normalized = quoted.replace(/\//g, '\\');
      const target = /^([A-Za-z]:\\|\\\\)/.test(normalized) ? normalized : resolvePath(dirNameOf(cur.ctxPath), normalized);
      if (await exists(target)) {
        if (go) { this.closePeek(); await this.bcode.openFile(target); }
        else await showNested(() => this.showPeekFile(fileNameOf(target), target));
        return;
      }
    }

    const w = this.getEntityWordAtPosition(model, pos);
    if (!w) { flash('Không có entity dưới con trỏ.'); return; }
    const found = (await this.resolve(w.word, cur.ctxPath, cur.ctxText)) || (await this.resolveViaWorkspaceSearch(w.word));
    if (!found) { flash('Không tìm thấy khai báo của "' + w.word + '".'); return; }

    if (found.decl.kind === 'system') {
      const target = resolvePath(dirNameOf(found.path), found.decl.systemPath.replace(/\//g, '\\'));
      if (!(await exists(target))) { flash('Không thấy file include: ' + target); return; }
      if (go) { this.closePeek(); await this.bcode.openFile(target); }
      else await showNested(() => this.showPeekFile(`&${w.word};`, target));
      return;
    }
    if (go) await this.openDeclaration(found);
    else await showNested(() => this.showPeek(w.word, found));
  }

  closePeek() {
    this.peekHistory = [];
    this._keepHistory = null;
    this._disposePeek();
  }

  /// Co dãn khung peek theo nội dung: cao theo số dòng, rộng theo dòng dài nhất (tab tính theo tabSize). Kẹp trong
  /// [640px+ (đủ footer), 92vw] × [3 dòng, 86vh]; vượt thì editor tự cuộn như cũ. `texts` nhiều đoạn (peek nhiều entity, chế độ
  /// "Từng entity") → lấy theo đoạn lớn nhất, để chuyển entity khung không nhảy kích thước.
  fitPeekBox(box, body, texts) {
    const ed = this.peekEditor;
    if (!ed) return;
    const opt = monaco.editor.EditorOption;
    const tab = ed.getModel()?.getOptions().tabSize || 4;
    let lines = 1, cols = 1;
    for (const t of texts) {
      const ls = String(t || '').split('\n');
      lines = Math.max(lines, ls.length);
      for (const l of ls) {
        let w = 0;
        for (const ch of l) w = ch === '\t' ? w + tab - (w % tab) : w + 1;
        if (w > cols) cols = w;
      }
    }
    const L = ed.getLayoutInfo();
    const charW = ed.getOption(opt.fontInfo).typicalHalfwidthCharacterWidth || 8;
    const lineH = ed.getOption(opt.lineHeight) || 20;

    const maxW = Math.floor(window.innerWidth * 0.92);
    const wantW = L.contentLeft + cols * charW + L.verticalScrollbarWidth + ((L.minimap && L.minimap.minimapWidth) || 0) + 32;
    // Tối thiểu đủ cho footer (gợi ý + các nút) nằm gọn — đo nút thật, không đoán.
    const footer = box.querySelector('.peekFooter');
    const buttonsW = footer ? [...footer.querySelectorAll('.dlgButton')]
      .filter((b) => b.style.display !== 'none')
      .reduce((s, b) => s + b.scrollWidth + 8, 0) : 0;
    const minW = Math.min(maxW, Math.max(640, buttonsW + 360));
    const width = Math.max(minW, Math.min(Math.ceil(wantW), maxW));
    box.style.width = width + 'px';

    // Đo phần header/footer SAU khi đặt rộng — hộp hẹp thì dòng gợi ý ở footer có thể xuống hàng.
    const chrome = box.offsetHeight - body.offsetHeight;
    const hScroll = wantW > maxW ? 14 : 0;
    const maxBody = Math.max(lineH * 3, Math.floor(window.innerHeight * 0.86) - chrome);
    const bodyH = Math.max(lineH * 3, Math.min(lines * lineH + 6 + hScroll, maxBody));
    box.style.height = (chrome + bodyH) + 'px';
  }

  _disposePeek() {
    if (this.peekEditor) {

      this.peekEditor.getModel()?.dispose();
      this.peekEditor.dispose();
      this.peekEditor = null;
    }
    if (this.escHandler) {
      document.removeEventListener('keydown', this.escHandler, true);
      this.escHandler = null;
    }
    if (this.overlay) { this.overlay.remove(); this.overlay = null; }
  }

async provideHover(model, position, token) {
    if (model !== this.bcode.currentModel) return null;
    const word = this.getEntityWordAtPosition(model, position);
    if (!word) return null;
    const found = await this.resolveActive(word.word);
    if (!found) return null;
    if ((token && token.isCancellationRequested) || model !== this.bcode.currentModel) return null;

    const range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
    const where = found.path.toLowerCase() === (this.bcode.activePath || '').toLowerCase()
      ? 'file này'
      : fileNameOf(found.path);

    if (found.decl.kind === 'system') {
      return {
        range,
        contents: [
          { value: `**ENTITY ${word.word}** — file (khai ở ${where})` },
          { value: '`' + found.decl.systemPath + '`' },
          { value: '_F12 xem trước · Ctrl+F12 mở file này_' },
        ],
      };
    }

    // A preview, not the whole value: a hover card that covers the editor is worse than no
    // hover card, and the full text is one keystroke away.
    const value = found.decl.value;
    const lines = value.split('\n');
    const preview = lines.slice(0, 12).join('\n');
    const truncated = lines.length > 12 ? `\n… còn ${lines.length - 12} dòng` : '';
    const language = this.languageOf(value);
    return {
      range,
      contents: [
        { value: `**ENTITY ${word.word}** — ${lines.length} dòng, khai ở ${where}` },
        { value: '```' + (language === 'plaintext' ? '' : language) + '\n' + preview + truncated + '\n```' },
        { value: '_F12 xem toàn bộ code · Ctrl+F12 tới nơi khai báo_' },
      ],
    };
  }
}

window.BcodeEntity = BcodeEntity;