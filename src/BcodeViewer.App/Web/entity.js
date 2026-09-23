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

/// How many levels of `SYSTEM` includes to follow. The chains seen in practice are two or
/// three deep; the limit is there so a file that (directly or indirectly) includes itself
/// can't turn one F12 into an endless walk.
const ENTITY_INCLUDE_DEPTH = 4;

class BcodeEntity {
  constructor(bcode) {
    this.bcode = bcode;
    /// path (lowercased) -> file text. Included entity files are read once per session and
    /// re-read only after a save: they are on a UNC share, F12 must feel instant, and this
    /// same set of files is walked again on every hover.
    this.fileCache = new Map();
    /// path (lowercased) -> Map(name -> {decl, path}) for the files that path includes.
    /// See buildIncludeIndex: built off the typing path, read from it.
    this.includeIndex = new Map();
    this.peekEditor = null;
    this.overlay = null;
  }

  /// Dropped after any save, because the file just written may well be one of the included
  /// entity files these caches are holding. The index is rebuilt lazily on the next tab
  /// activation, or right away for the document that was just saved.
  invalidate() {
    this.fileCache.clear();
    this.includeIndex.clear();
  }

  // ---- Parsing --------------------------------------------------------------------------

  /// Every `<!ENTITY ...>` declaration in one document, as
  /// {name, isParam, kind:'value'|'system', value, systemPath, offset}.
  ///
  /// Hand-scanned rather than matched with one regex: a declaration is `<!ENTITY [%] name
  /// (SYSTEM)? "quoted">`, the quoted part may be single- or double-quoted, and a value can
  /// run for hundreds of lines and contain `>` freely — which is exactly what defeats the
  /// obvious `<!ENTITY[^>]*>` pattern.
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
        // PUBLIC takes two quoted strings; the second is the system id. Not used by any
        // controller seen here, but skipping it cleanly beats mis-reading it as a value.
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
      // Resume past the value: a value holding its own `<!ENTITY` text (SQL that builds a
      // DOCTYPE, which does happen) must not be read as a second declaration.
      re.lastIndex = quoted.end;
    }
    return decls;
  }

  /// The quoted string starting at <paramref>i</paramref>, or null. XML entity values have
  /// no backslash escaping — an embedded quote is written `&quot;` — so the first matching
  /// quote genuinely ends the value.
  readQuoted(text, i) {
    const quote = text[i];
    if (quote !== '"' && quote !== "'") return null;
    const end = text.indexOf(quote, i + 1);
    if (end < 0) return null;
    return { text: text.slice(i + 1, end), start: i + 1, end: end + 1 };
  }

  // ---- Resolution ------------------------------------------------------------------------

  async readFile(path) {
    const key = path.toLowerCase();
    if (this.fileCache.has(key)) return this.fileCache.get(key);
    let text = null;
    try { text = await window.bcodeHost.call('BeginReadFile', path); }
    catch { text = null; } // missing include — the Problems panel already reports that
    this.fileCache.set(key, text);
    return text;
  }

  /// Finds <paramref>name</paramref> in <paramref>path</paramref>'s own DOCTYPE, then in
  /// each file that document includes, breadth-first so the nearest declaration wins — the
  /// same order an XML parser would apply them in, and the one that matches what the file
  /// being edited actually sees.
  ///
  /// Returns {decl, path, text} or null.
  async resolve(name, path, text, seen, depth) {
    seen = seen || new Set();
    depth = depth == null ? ENTITY_INCLUDE_DEPTH : depth;
    if (!text || seen.has(path.toLowerCase())) return null;
    seen.add(path.toLowerCase());

    const decls = this.parseDeclarations(text);
    const hit = decls.find((d) => d.name === name);
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

  /// Resolves the name for the document currently on screen.
  async resolveActive(name) {
    const bcode = this.bcode;
    if (!bcode.activePath || !bcode.currentModel) return null;
    return this.resolve(name, bcode.activePath, bcode.currentModel.getValue());
  }

  // ---- Index of everything the document can see ----------------------------------------------

  /// Every entity declared by the files this document INCLUDES, as name -> {decl, path}.
  /// Deliberately excludes the document's own DOCTYPE: that part changes as you type, and
  /// completion re-reads it from the live model instead (see completion.js). What is here
  /// only changes when a file on disk does.
  ///
  /// Why this exists at all: a controller's own DOCTYPE declares maybe a third of the names
  /// it uses. `&ExportQueryFields;`, `&EIFields;`, `&ListField;`, `&Combo.SVTran.AfterUpdate;`
  /// all arrive through `%Export;`, `%Invoice;`, `%Combo.SVTran;` — so an entity list built
  /// from the open file alone is missing most of the answer, which is the same as being
  /// wrong.
  async buildIncludeIndex(path, text, seen, depth) {
    seen = seen || new Set([path.toLowerCase()]);
    depth = depth == null ? ENTITY_INCLUDE_DEPTH : depth;
    const index = new Map();
    if (depth <= 0) return index;

    const dir = dirNameOf(path);
    for (const include of this.parseDeclarations(text).filter((d) => d.kind === 'system')) {
      const resolved = resolvePath(dir, include.systemPath.replace(/\//g, '\\'));
      if (seen.has(resolved.toLowerCase())) continue;
      seen.add(resolved.toLowerCase());
      const included = await this.readFile(resolved);
      if (!included) continue;

      for (const decl of this.parseDeclarations(included)) {
        // First declaration wins, matching the order an XML parser applies them in — so
        // what the list offers is what the document would actually resolve to.
        if (!index.has(decl.name)) index.set(decl.name, { decl, path: resolved });
      }
      for (const [name, entry] of await this.buildIncludeIndex(resolved, included, seen, depth - 1)) {
        if (!index.has(name)) index.set(name, entry);
      }
    }
    return index;
  }

  /// Builds (or rebuilds) the include index for one document and caches it by path. Called
  /// when a tab is activated and after a save — never from a completion provider, which
  /// must not do I/O.
  async refreshIncludeIndex(path, text) {
    if (!path || !text) return;
    try {
      this.includeIndex.set(path.toLowerCase(), await this.buildIncludeIndex(path, text));
    } catch {
      // A dropped share leaves the previous index in place rather than emptying the list.
    }
  }

  /// Synchronous accessor for the provider. Null until the walk above has finished, which
  /// callers treat as "only the local declarations for now".
  includeIndexFor(path) {
    return path ? this.includeIndex.get(path.toLowerCase()) || null : null;
  }

  // ---- Expansion ----------------------------------------------------------------------------

  /// Replaces every `&Name;` in <paramref>text</paramref> with what it is declared as, using
  /// the active document's include chain. Returns {text, expanded:[names], unresolved:[names]}.
  ///
  /// Repeated in passes because an entity value routinely references other entities — that is
  /// how a controller composes one routine out of several. Capped, so a pair that reference
  /// each other stops after a few rounds with the remainder left as-is rather than hanging.
  ///
  /// The five XML built-ins are left alone here; whether they should be unescaped depends on
  /// where the text came from (inside a CDATA block they are literal), which only the caller
  /// knows — see sqlrun.js.
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
        // A SYSTEM entity pulls in a whole file of declarations, not a value that belongs
        // in the middle of a statement — substituting one would produce nonsense.
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

  // ---- F12 --------------------------------------------------------------------------------

  /// The word under the caret, ignoring a leading `&`/`%` and a trailing `;` so F12 works
  /// wherever in `&Name;` the caret happens to sit.
  nameAtCaret() {
    const model = this.bcode.currentModel;
    const pos = this.bcode.editor.getPosition();
    if (!model || !pos) return null;
    const word = model.getWordAtPosition(pos);
    return word ? word.word : null;
  }

  /// F12. A SYSTEM entity names a file, so its "code" is that file and it opens. A value
  /// entity's code is the value itself, so it opens in the peek window — with a button to
  /// jump to the declaration when you do want to go there and edit it.
  async goToOrPeek() {
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
      await this.bcode.openFile(target);
      return true;
    }

    this.showPeek(name, found);
    return true;
  }

  /// Opens the declaring file at the declaration and closes the peek — the "I actually want
  /// to change this" path.
  async openDeclaration(found) {
    const pos = offsetToPosition(found.text, found.decl.offset);
    this.closePeek();
    await this.bcode.openFile(found.path, { line: pos.line, column: pos.col });
  }

  // ---- Peek window --------------------------------------------------------------------------

  /// Which language to colour an entity value as. Reuses the classifier completion.js
  /// already applies to these same values when deciding what to suggest inside them, so a
  /// value can't be SQL for one feature and JavaScript for the other.
  languageOf(value) {
    if (typeof looksLikeSql === 'function' && looksLikeSql(value)) return 'sql';
    if (typeof JS_HINT_RE !== 'undefined' && JS_HINT_RE.test(value)) return 'javascript';
    if (/<[A-Za-z!/]/.test(value)) {
      return window.bcodeFcodeLanguageReady ? window.FCODE_LANGUAGE_ID : 'xml';
    }
    return 'plaintext';
  }

  showPeek(name, found) {
    this.closePeek();

    const overlay = document.createElement('div');
    overlay.className = 'peekOverlay';
    // Clicking the backdrop dismisses, but a click that started inside the code must not:
    // dragging a selection out past the edge of the box would otherwise close the window
    // on mouseup.
    overlay.onmousedown = (e) => { if (e.target === overlay) this.closePeek(); };

    const box = document.createElement('div');
    box.className = 'peekBox';

    const header = document.createElement('div');
    header.className = 'peekHeader';
    const titleWrap = document.createElement('div');
    const title = document.createElement('div');
    title.className = 'peekTitle';
    title.textContent = `&${name};`;
    const subtitle = document.createElement('div');
    subtitle.className = 'peekSubtitle';
    const sameFile = found.path.toLowerCase() === (this.bcode.activePath || '').toLowerCase();
    const lines = found.decl.value.split('\n').length;
    subtitle.textContent =
      `Khai báo ${sameFile ? 'trong file này' : 'tại ' + fileNameOf(found.path)} · ` +
      `dòng ${offsetToPosition(found.text, found.decl.offset).line} · ${lines} dòng`;
    subtitle.title = found.path;
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
    openBtn.textContent = 'Mở nơi khai báo';
    openBtn.onclick = () => this.openDeclaration(found);
    const copyBtn = document.createElement('button');
    copyBtn.className = 'dlgButton';
    copyBtn.textContent = 'Copy code';
    copyBtn.onclick = () => {
      navigator.clipboard.writeText(found.decl.value);
      copyBtn.textContent = 'Đã copy';
      setTimeout(() => { copyBtn.textContent = 'Copy code'; }, 1200);
    };
    const hint = document.createElement('span');
    hint.className = 'peekHint';
    hint.textContent = 'Chỉ xem — sửa thì bấm "Mở nơi khai báo".';
    footer.append(hint, copyBtn, openBtn);

    box.append(header, body, footer);
    overlay.appendChild(box);
    document.body.appendChild(overlay);

    // Read-only, and a real Monaco instance rather than a <pre>: these values are SQL and
    // JavaScript, and reading 300 lines of either without colouring, folding or Ctrl+F is
    // the problem this is meant to solve, not a smaller version of it.
    this.peekEditor = monaco.editor.create(body, {
      value: found.decl.value,
      language: this.languageOf(found.decl.value),
      theme: window.bcodeTheme ? window.bcodeTheme.monacoThemeName : 'vs-dark',
      readOnly: true,
      automaticLayout: true,
      fontFamily: 'Consolas',
      fontSize: 13,
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
      // Dispose the editor before its container leaves the DOM, or Monaco keeps the
      // resize observer and the model alive for the life of the page.
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

  // ---- Hover ---------------------------------------------------------------------------------

  /// Replaces the SYSTEM-only hover that used to live in completion.js. Async, because the
  /// answer may be in a file that has not been read yet — Monaco accepts a Promise here.
  ///
  /// <paramref>token</paramref> is Monaco's cancellation token and it matters here more than
  /// in most providers: resolving a name can mean reading several include files off a UNC
  /// share, which easily outlives the moment the mouse moves on. Without these checks that
  /// work carried on to produce a hover card for a position the user had already left.
  async provideHover(model, position, token) {
    if (model !== this.bcode.currentModel) return null;
    const word = model.getWordAtPosition(position);
    if (!word) return null;

    const found = await this.resolveActive(word.word);
    if (!found) return null;
    // Cancelled while the files were being read, or the document changed underneath us.
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
          { value: '_F12 để mở file này_' },
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
        { value: '_F12 để xem toàn bộ code_' },
      ],
    };
  }
}

window.BcodeEntity = BcodeEntity;
