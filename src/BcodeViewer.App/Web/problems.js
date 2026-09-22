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
  async validate() {
    const bcode = this.bcode;
    if (!bcode.activePath || !bcode.currentModel) { this.setItems([]); return; }

    const path = bcode.activePath;
    const model = bcode.currentModel;
    const text = model.getValue();
    const items = [];

    // Rules that need no I/O run first, so the list is already usable while the entity
    // file probes (one host round trip each, on a UNC share) are still in flight.
    const declared = this.collectEntityNames(text);
    items.push(...this.checkDuplicateFields(text));
    items.push(...this.checkDuplicateIds(text));
    items.push(...this.checkTagBalance(text));
    items.push(...this.checkMissingHandlers(text));
    this.setItems(items);

    const external = [
      ...await this.checkMissingEntityFiles(text, path),
      ...await this.checkUndeclaredEntities(text, declared),
    ];

    // The document may have been edited or closed while those probes ran; publishing now
    // would resurrect problems for text that no longer exists.
    if (bcode.activePath !== path || bcode.currentModel !== model) return;
    if (external.length) this.setItems([...external, ...items]);
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
    const items = [];
    const seen = new Set();
    ENTITY_DECL_RE.lastIndex = 0;
    let m;
    while ((m = ENTITY_DECL_RE.exec(text))) {
      const relPath = m[2];
      if (seen.has(relPath)) continue;
      seen.add(relPath);
      const at = offsetToPosition(text, m.index);
      const resolved = resolvePath(dir, relPath.replace(/\//g, '\\'));
      let exists = false;
      try { exists = await window.chrome.webview.hostObjects.host.PathExists(resolved); }
      catch { exists = false; }
      if (!exists) {
        items.push({
          severity: 'error',
          text: `Không mở được file ENTITY: '${resolved}'`,
          // The location to jump to is the declaration line in THIS file; `path` is what
          // the row offers to open instead once the file does exist.
          line: at.line,
          column: at.col,
          openPath: resolved,
        });
      }
    }
    return items;
  }

  /// A `<field name="X">` repeated inside ONE `<fields>` block. Counting across the whole
  /// document is wrong: a master grid and a nested detail grid each get their own block and
  /// commonly reuse the same hidden-PK name, which is normal FCode structure.
  checkDuplicateFields(text) {
    const items = [];
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

      const gt = text.indexOf('>', lt);
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
    const re = /&([A-Za-z_][\w.:-]*);/g;
    const reported = new Set();
    let m;
    while ((m = re.exec(text))) {
      const name = m[1];
      if (XML_BUILTIN_ENTITIES.has(name) || declared.has(name) || reported.has(name)) continue;
      // Inside a CDATA block an ampersand is literal text (jQuery's `&amp;&amp;`, a URL
      // query string), so nothing there is an entity reference at all.
      if (this.isInsideCdata(text, m.index)) continue;
      reported.add(name);
      if (window.bcodeEntity && await window.bcodeEntity.resolveActive(name)) continue;
      const pos = offsetToPosition(text, m.index);
      items.push({
        severity: 'error',
        text: `Dùng &${name}; nhưng không thấy khai báo <!ENTITY ${name} ...> ở file này hay các file được include.`,
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
