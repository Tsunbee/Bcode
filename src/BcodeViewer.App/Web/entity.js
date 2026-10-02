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
    const model = this.bcode.currentModel;
    const pos = this.bcode.editor.getPosition();
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
      onOpen: () => this.openDeclaration(found),
      hint: 'Chỉ xem — Ctrl+F12 (hoặc "Mở nơi khai báo") để chuyển tới đó.',
      copyLabel: 'Copy code',
    });
  }

  /// Khung peek dùng chung: entity có giá trị (showPeek) và file include (showPeekFile).
  showPeekContent(o) {
    this.closePeek();
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
    hint.textContent = o.hint;
    footer.append(hint, copyBtn, openBtn);

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
    this.peekEditor.focus();

    this.escHandler = (e) => { if (e.key === 'Escape') this.closePeek(); };
    document.addEventListener('keydown', this.escHandler, true);
    this.overlay = overlay;
  }

  closePeek() {
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