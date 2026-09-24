// Navigation: the Outline panel on the right, plus the two lookups that answer "where is
// this defined?" and "where else is it used?".
//
// The symbols themselves come from buildSymbols() in editor.js — the same scanner that
// already backs Monaco's own Ctrl+Shift+O list, so the panel and the quick-open popup can
// never disagree about what is in the file. What this adds is structure: an FCode
// controller is a flat wall of XML where the interesting parts (the ENTITY includes at the
// top, the <field> declarations, the <command> SQL, the JavaScript at the bottom) are
// hundreds of lines apart, and a flat alphabetical list of 300 field names is no more
// navigable than the file. Grouping by what a symbol *is* is the whole point.

/// Group order is reading order: what a controller declares first comes first.
const OUTLINE_GROUPS = [
  { key: 'entity', label: 'ENTITY', icon: '⧉', kind: 'Constant' },
  { key: 'view', label: 'View', icon: '▤', kind: 'Class' },
  { key: 'field', label: 'Field', icon: '▸', kind: 'Field' },
  { key: 'command', label: 'Command', icon: '⚡', kind: 'Event' },
  { key: 'action', label: 'Action', icon: '◆', kind: 'Method' },
  { key: 'function', label: 'Function', icon: 'ƒ', kind: 'Function' },
];

class BcodeOutline {
  constructor(bcode) {
    this.bcode = bcode;
    this.panel = document.getElementById('outlinePanel');
    this.tree = document.getElementById('outlineTree');
    this.filter = document.getElementById('outlineFilter');
    this.collapsed = new Set();
    this.nodes = []; // flattened, in document order — used to highlight the caret's symbol

    document.getElementById('outlineCloseBtn').onclick = () => this.hide();
    this.filter.addEventListener('input', () => this.render());

    this.registerProviders();
    this.refresh();
  }

  // ---- Panel ---------------------------------------------------------------------------

  toggle() { if (this.isVisible()) this.hide(); else this.show(); }
  isVisible() { return this.panel.style.display !== 'none'; }
  show() { this.panel.style.display = 'flex'; this.refresh(); }
  hide() { this.panel.style.display = 'none'; }

  refresh() {
    if (!this.isVisible()) return;
    const model = this.bcode.currentModel;
    this.symbols = model ? buildSymbols(model.getValue()) : [];
    this.render();
  }

  render() {
    const needle = this.filter.value.trim().toLowerCase();
    this.tree.innerHTML = '';
    this.nodes = [];

    if (!this.symbols || this.symbols.length === 0) {
      const empty = document.createElement('div');
      empty.className = 'dockEmpty';
      empty.textContent = this.bcode.activePath ? 'Không nhận diện được cấu trúc trong file này.' : 'Chưa mở file nào.';
      this.tree.appendChild(empty);
      return;
    }

    for (const group of OUTLINE_GROUPS) {
      const kind = monaco.languages.SymbolKind[group.kind];
      let members = this.symbols.filter((s) => s.kind === kind);
      if (needle) members = members.filter((s) => s.name.toLowerCase().includes(needle));
      if (members.length === 0) continue;

      const header = this.buildNode({
        cls: 'outlineGroup',
        twisty: this.collapsed.has(group.key) ? '▸' : '▾',
        icon: group.icon,
        label: group.label,
        detail: String(members.length),
        onClick: () => {
          if (this.collapsed.has(group.key)) this.collapsed.delete(group.key);
          else this.collapsed.add(group.key);
          this.render();
        },
      });
      this.tree.appendChild(header);
      // A filter is a search, and a search that hides its hits behind a folded group is
      // just a slower way of finding nothing — typing in the box expands everything.
      if (this.collapsed.has(group.key) && !needle) continue;

      for (const sym of members) {
        const line = sym.range.startLineNumber;
        const node = this.buildNode({
          cls: 'outlineChild',
          twisty: '',
          icon: '',
          label: sym.name,
          detail: String(line),
          indent: true,
          title: `${sym.name} — dòng ${line}`,
          onClick: () => this.bcode.revealPosition(line, sym.range.startColumn),
        });
        node.dataset.line = String(line);
        this.tree.appendChild(node);
        this.nodes.push({ line, element: node });
      }
    }

    this.highlightCaret();
  }

  buildNode({ cls, twisty, icon, label, detail, onClick, indent, title }) {
    const node = document.createElement('div');
    node.className = 'outlineNode ' + (cls || '');
    if (title) node.title = title;
    if (indent) node.style.paddingLeft = '26px';

    const tw = document.createElement('span');
    tw.className = 'twisty';
    tw.textContent = twisty || '';
    const ic = document.createElement('span');
    ic.className = 'kind';
    ic.textContent = icon || '';
    const lb = document.createElement('span');
    lb.className = 'label';
    lb.textContent = label;
    const dt = document.createElement('span');
    dt.className = 'detail';
    dt.textContent = detail || '';
    dt.style.marginLeft = 'auto';

    node.append(tw, ic, lb, dt);
    if (onClick) node.onclick = onClick;
    return node;
  }

  /// Marks the symbol the caret is currently inside — the last one declared at or above
  /// the caret line, which for a flat declaration list is exactly "the one you are in".
  highlightCaret() {
    if (!this.isVisible() || this.nodes.length === 0) return;
    const pos = this.bcode.editor.getPosition();
    if (!pos) return;

    let best = null;
    for (const node of this.nodes) {
      if (node.line <= pos.lineNumber && (!best || node.line > best.line)) best = node;
    }
    for (const node of this.nodes) node.element.classList.toggle('current', node === best);
    if (best) best.element.scrollIntoView({ block: 'nearest' });
  }

  // ---- Go to definition / find references -------------------------------------------

  /// Ctrl+click and F12-style navigation, through Monaco's own definition API so it gets
  /// the underline-on-hover and the peek window for free.
  ///
  /// Two things are resolvable from inside one controller, and both are things people
  /// actually chase: an `&Entity;` reference (jumps to its `<!ENTITY>` declaration, from
  /// which F12 then opens the file it names) and a JavaScript handler name (jumps to its
  /// `function` in the same document). Anything else returns nothing rather than guessing.
  registerProviders() {
    const languages = ['xml', 'fcode-xml', 'javascript'];
    monaco.languages.registerDefinitionProvider(languages, {
      provideDefinition: (model, position) => {
        const word = model.getWordAtPosition(position);
        if (!word) return null;
        const text = model.getValue();
        const target = this.findDeclaration(text, word.word);
        if (!target) return null;
        return [{
          uri: model.uri,
          range: new monaco.Range(target.line, target.col, target.line, target.col + word.word.length),
        }];
      },
    });
  }

  /// Offset of the declaration of <paramref>name</paramref>, or null. Declaration forms are
  /// checked most-specific first so that a name used as both an entity and a function
  /// resolves to the structural one.
  findDeclaration(text, name) {
    const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const patterns = [
      new RegExp(`<!ENTITY\\s+%?\\s*${escaped}\\b`),
      new RegExp(`function\\s+${escaped}\\s*\\(`),
      new RegExp(`\\b${escaped}\\s*[:=]\\s*function\\b`),
      new RegExp(`<(?:view|action)\\s+[^>]*\\bid="${escaped}"`),
      new RegExp(`<field\\s+name="${escaped}"`),
    ];
    for (const re of patterns) {
      const m = re.exec(text);
      if (m) {
        const pos = offsetToPosition(text, m.index);
        return { line: pos.line, col: pos.col };
      }
    }
    return null;
  }

  /// Shift+F12: every occurrence of the word under the caret, across the whole project,
  /// listed in the search panel.
  ///
  /// This is a whole-word text search, not a resolved symbol table — the honest thing to
  /// build here. A field name in an FCode site is referenced from XML attributes, from
  /// SQL, from JavaScript and from another project's include file, and no single parser
  /// sees all four. A text search sees all four, and the grouped list makes the few
  /// coincidental hits obvious at a glance.
  async findReferencesAtCaret() {
    const model = this.bcode.currentModel;
    const pos = this.bcode.editor.getPosition();
    if (!model || !pos) return;
    const word = model.getWordAtPosition(pos);
    if (!word) return;

    const root = await window.bcodeSearch.ensureRoot();
    if (!root) { window.bcodeSearch.open(word.word); return; }

    window.bcodeSearch.dock.show('searchPanel');
    window.bcodeSearch.setStatus(`Đang tìm "${word.word}" trong ${root} ...`);
    let raw;
    try {
      raw = await window.bcodeHost.call('BeginSearchWorkspace',
        root, word.word, false, false, true, '', 2000);
    } catch (e) {
      window.bcodeSearch.setStatus('Lỗi khi tìm: ' + e, true);
      return;
    }

    const result = JSON.parse(raw);
    if (result.error) { window.bcodeSearch.setStatus(result.error, true); return; }
    const files = new Set(result.matches.map((m) => m.path)).size;
    window.bcodeSearch.showResults(result.matches, word.word, {
      // Seeds the query box with what was searched, so refining it by hand (adding a case
      // or regex option) continues from here instead of starting over.
      query: word.word,
      status: `"${word.word}": ${result.matches.length} nơi sử dụng trong ${files} file` +
        (result.truncated ? ' (đã cắt bớt)' : ''),
    });
  }
}

window.BcodeOutline = BcodeOutline;
