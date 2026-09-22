// IntelliSense for BcodeViewer — the four suggestion layers Monaco can provide, wired to
// what this codebase actually knows:
//
//   1. Snippets      — the Hint Code library (personal + the team's shared folder), turned
//                      into real completion items with VSCode tabstop syntax.
//   2. FCode XML     — structure learned from the open document itself (ENTITY names, tag
//                      names, attributes already used on a tag, <field name> values).
//   3. SQL           — tables/columns from the workspace Bcode.App is connected to.
//   4. Ghost text    — Claude, as an inline suggestion, only when explicitly enabled.
//
// The one rule running through all of it: a provider is called on EVERY keystroke, so it
// must not do I/O. Everything the host owns (snippets, feature flags, the table list) is
// pulled once and kept in page memory here; the only per-use host call is a table's columns
// (once per table, then cached) and the AI call (debounced, cancellable, opt-in).

// Documents that are markup on the outside with other languages embedded inside, and so
// need the region logic below. 'fcode-xml' is BcodeViewer's own language (see
// fcode-language.js) — plain 'xml' stays listed because a controller opens as 'xml' when
// that language failed to register, and because .xml files from elsewhere still use it.
const MARKUP_LANGUAGES = ['xml', 'fcode-xml', 'html'];

function isMarkupLanguage(language) {
  return MARKUP_LANGUAGES.includes(language);
}

const FCODE_LANGUAGES = ['xml', 'fcode-xml', 'sql', 'javascript', 'css', 'html', 'json', 'plaintext'];

// Category (the Hint Code library's own field) -> Monaco language ids it applies to.
//
// 'plaintext' appears in every list on purpose. detectLanguage() falls back to plaintext
// for any extension it doesn't recognise, and this project has plenty (.v, .ent variants,
// files saved without one) — without it those files got the completion provider registered
// but zero snippets through it, which looks exactly like the feature being broken rather
// than like a language it doesn't know. Offering everything there is the better failure.
const CATEGORY_LANGUAGES = {
  JS: ['javascript', 'html', 'xml', 'fcode-xml', 'plaintext'],
  SQL: ['sql', 'plaintext'],
  XML: ['xml', 'fcode-xml', 'html', 'plaintext'],
  CSS: ['css', 'html', 'xml', 'fcode-xml', 'plaintext'],
};

// Attribute names seeded per tag. Deliberately short: this is a fallback for an EMPTY file,
// where there is nothing to learn from yet. In any real file the document-derived list
// (collectTagAttributes) is what carries the suggestions, because it reflects how this
// project actually writes its XML rather than a schema guessed at from outside.
// Which Hint Code categories belong in each embedded region of an XML document.
// CSS keeps XML company in the markup region because FCode puts style fragments in
// attributes there; JS deliberately does not, which is the whole point of this table —
// before regions existed, JS snippets were offered in the middle of a <fields> block.
const REGION_CATEGORIES = {
  js: ['JS'],
  sql: ['SQL'],
  css: ['CSS'],
  xml: ['XML', 'CSS'],
};

const SEED_ATTRIBUTES = {
  field: ['name', 'caption', 'type', 'width', 'visible', 'readonly', 'required', 'format', 'default'],
  view: ['id', 'caption', 'type'],
  command: ['event', 'caption', 'id'],
  action: ['id', 'caption', 'icon', 'type'],
  grid: ['id', 'caption', 'table'],
};

// ---- Embedded regions inside one FCode XML file -------------------------------------
//
// An FCode controller is a single .xml document, so model.getLanguageId() says "xml" for
// all of it — including the JavaScript inside <script> and any SQL it carries. Filtering
// suggestions by the model's language therefore gets both halves wrong: JS snippets were
// offered in the middle of a <fields> block, and SQL snippets could never appear at all.
//
// These functions split the document into regions so a suggestion can be matched to the
// language actually at the caret.
//
// <script> is evidence-based: the codebase already treats it as the JS section (see
// editor.js's createFunctionAtCaret, which inserts functions before its ]]> and knows the
// content is CDATA). SQL is content-based instead, because nothing in this repo says which
// element holds it — guessing a tag name would be inventing a schema. A caller who knows
// the real tags can name them in Settings, and those are used in addition.

const SQL_START_RE = /^\s*(?:--[^\n]*\n\s*)*(?:SELECT|INSERT|UPDATE|DELETE|MERGE|EXEC|EXECUTE|WITH|DECLARE|TRUNCATE|CREATE|ALTER|DROP)\b/i;
const SQL_SHAPE_RE = /\bFROM\b[\s\S]*\b(?:WHERE|JOIN|GROUP\s+BY|ORDER\s+BY)\b/i;

function looksLikeSql(text) {
  if (!text || text.length < 12) return false;
  return SQL_START_RE.test(text) || SQL_SHAPE_RE.test(text);
}

/// FCode marks embedded JavaScript explicitly inside CDATA:
///   <![CDATA[/* <flatten type="Javascript"> */ ... /* </flatten> */]]>
/// That comment is authoritative and worth more than any heuristic — it is how a
/// <command> that holds JavaScript rather than SQL identifies itself.
const JS_FLATTEN_RE = /<flatten\s+type="Javascript"/i;

/// Shapes that only occur in this project's client script, used to tell a JavaScript
/// ENTITY value from a SQL one. Kept narrow on purpose: an entity that matches neither is
/// left unclassified rather than guessed at.
const JS_HINT_RE = /(?:\bfunction\s+[\w$]+\s*\(|document\.getElementById|setTimeout\s*\(|\$find\(|\.parentForm\b|\bf\._[a-z]|\bthis\._controlBehavior\b)/;

/// Sections whose content is one language throughout, taken from the real structure of an
/// FCode Dir controller rather than guessed:
///   <script><text>…</text></script>            JavaScript (mixed with &Entity; refs)
///   <commands><command event="…"><text>…       T-SQL
///   <response><action id="…"><text>…           T-SQL
///   <css><text>…                               CSS
///   <clientScript><![CDATA[onchange="…"]]>     JavaScript fragment on a field
const SECTION_TAGS = [
  ['script', 'js'],
  ['clientScript', 'js'],
  ['command', 'sql'],
  ['action', 'sql'],
  ['css', 'css'],
  ['style', 'css'], // not used by the controllers seen so far; harmless if it appears
];

/// {kind, start, end} spans over the document text. Regions MAY nest — a CDATA block
/// marked as JavaScript sits inside a <command> that is otherwise SQL — and regionKindAt
/// resolves that by taking the innermost one.
function buildRegions(text, sqlTags) {
  const regions = [];

  const pushTagRegions = (tag, kind) => {
    const re = new RegExp('<' + tag + '\\b[^>]*>([\\s\\S]*?)<\\/' + tag + '>', 'gi');
    let m;
    while ((m = re.exec(text))) {
      const start = m.index + m[0].indexOf(m[1]);
      regions.push({ kind, start, end: start + m[1].length });
    }
  };

  for (const [tag, kind] of SECTION_TAGS) pushTagRegions(tag, kind);
  for (const tag of sqlTags) {
    if (/^[A-Za-z][A-Za-z0-9_-]*$/.test(tag)) pushTagRegions(tag, 'sql');
  }

  // DOCTYPE internal-subset entities. In a real controller these carry a large share of
  // the file's actual code — whole SQL routines (&Post;, &Delete;, &AfterUpdate;) and the
  // occasional JavaScript snippet — and they sit outside every section above, so without
  // this the most code-dense part of the document counts as plain markup.
  // SYSTEM entities have no inline value and are skipped by the quote requirement.
  const entityRe = /<!ENTITY\s+%?\s*[A-Za-z0-9_.$]+\s+"([\s\S]*?)"\s*>/g;
  let e;
  while ((e = entityRe.exec(text))) {
    const value = e[1];
    const start = e.index + e[0].indexOf(value);
    // SQL first: several JavaScript-looking entities are really SQL building a JS string,
    // and a FROM…WHERE shape is the stronger signal of the two.
    const kind = looksLikeSql(value) ? 'sql' : JS_HINT_RE.test(value) ? 'js' : null;
    if (kind) regions.push({ kind, start, end: start + value.length });
  }

  // CDATA blocks. The flatten marker overrides whatever section encloses them (that is
  // exactly what it is for); otherwise a SQL-shaped block inside a section not already
  // classified as code is picked up by content.
  const cdataRe = /<!\[CDATA\[([\s\S]*?)\]\]>/g;
  let c;
  while ((c = cdataRe.exec(text))) {
    const inner = c[1];
    const start = c.index + c[0].indexOf(inner);
    if (JS_FLATTEN_RE.test(inner)) {
      regions.push({ kind: 'js', start, end: start + inner.length });
    } else if (looksLikeSql(inner) && !insideKind(regions, start, 'js', 'css')) {
      regions.push({ kind: 'sql', start, end: start + inner.length });
    }
  }

  // Plain text between two tags, for SQL in an element this doesn't know the name of.
  // The 12-character floor in looksLikeSql plus its need for a leading SQL verb (or a
  // FROM…WHERE shape) is what keeps captions and labels out: element text in these files
  // is otherwise short and prose-like.
  const textRe = />([^<]{12,})</g;
  let t;
  while ((t = textRe.exec(text))) {
    const start = t.index + 1;
    if (looksLikeSql(t[1]) && !insideAnyRegion(regions, start)) {
      regions.push({ kind: 'sql', start, end: start + t[1].length });
    }
    // A run of text ends at the '<' that begins the next tag, and lastIndex has consumed
    // it — which would skip a region starting immediately after.
    textRe.lastIndex--;
  }

  return regions.sort((a, b) => a.start - b.start);
}

function insideAnyRegion(regions, offset) {
  return regions.some((r) => offset >= r.start && offset < r.end);
}

function insideKind(regions, offset, ...kinds) {
  return regions.some((r) => offset >= r.start && offset < r.end && kinds.includes(r.kind));
}

/// Which region an offset falls in. Split out from regionAt so it can be exercised without
/// a Monaco model — the app is single-instance machine-wide, so spinning up a second copy
/// to test this just hands the file to the running one.
function regionKindAt(regions, offset) {
  // Innermost wins. Regions legitimately nest — a CDATA block flagged as JavaScript lives
  // inside a <command> that is otherwise SQL — and the narrower span is always the more
  // specific statement about what the text at this offset actually is.
  let best = null;
  for (const r of regions) {
    if (offset < r.start) break; // sorted by start — nothing later can contain this offset
    if (offset > r.end) continue;
    if (!best || r.end - r.start < best.end - best.start) best = r;
  }
  return best ? best.kind : 'xml';
}

/// A model's derived facts, recomputed only when its version changes — buildSymbols-style
/// regex passes are cheap but not free, and a provider can fire several times per keystroke
/// (one per registered provider, plus Monaco's own re-filtering).
const docCache = new WeakMap();

function docFacts(model) {
  const version = model.getVersionId();
  const cached = docCache.get(model);
  if (cached && cached.version === version) return cached.facts;

  const text = model.getValue();
  const facts = {
    entities: unique(matchAll(text, /<!ENTITY\s+%?\s*([A-Za-z0-9_.]+)/g)),
    // The \s before each attribute name matters: without it "[^>]*name=" also matches
    // othername=/fieldname=/colname=, quietly filling the field list with values that are
    // not field names at all. These are broader than editor.js's buildSymbols equivalents
    // on purpose (the attribute needn't come first), but not broader than that.
    fields: unique(matchAll(text, /<field\b[^>]*?\sname="([^"]+)"/g)),
    views: unique(matchAll(text, /<view\b[^>]*?\sid="([^"]+)"/g)),
    actions: unique(matchAll(text, /<action\b[^>]*?\sid="([^"]+)"/g)),
    events: unique(matchAll(text, /<command\b[^>]*?\sevent="([^"]+)"/g)),
    functions: unique(matchAll(text, /function\s+([A-Za-z0-9_$]+)\s*\(/g)),
    tags: unique(matchAll(text, /<([A-Za-z][A-Za-z0-9_-]*)[\s>/]/g)),
    tagAttributes: collectTagAttributes(text),
    sqlAliases: collectSqlAliases(text),
  };

  docCache.set(model, { version, facts });
  return facts;
}

function matchAll(text, re) {
  const out = [];
  let m;
  re.lastIndex = 0;
  while ((m = re.exec(text))) out.push(m[1]);
  return out;
}

function unique(list) {
  return Array.from(new Set(list.filter(Boolean)));
}

/// tag name -> attribute names seen used on it anywhere in this document. This is what
/// makes attribute completion useful without anyone writing down FCode's schema: the file
/// (and the hundreds like it in the project) already demonstrates which attributes go with
/// which tag, so the document teaches the editor rather than the other way round.
function collectTagAttributes(text) {
  const map = {};
  const tagRe = /<([A-Za-z][A-Za-z0-9_-]*)((?:\s+[A-Za-z0-9_:-]+\s*=\s*"[^"]*")+)/g;
  let m;
  while ((m = tagRe.exec(text))) {
    const tag = m[1].toLowerCase();
    const attrs = map[tag] || (map[tag] = new Set());
    const attrRe = /([A-Za-z0-9_:-]+)\s*=\s*"/g;
    let a;
    while ((a = attrRe.exec(m[2]))) attrs.add(a[1]);
  }
  const plain = {};
  for (const [tag, set] of Object.entries(map)) plain[tag] = Array.from(set);
  return plain;
}

/// alias -> table, from "FROM bang a" / "JOIN bang2 AS b" — so "a." can suggest that
/// table's columns. Bare "FROM bang" also registers under the table's own name, which is
/// how most of this project's ad-hoc queries are written.
function collectSqlAliases(text) {
  const map = {};
  const re = /\b(?:FROM|JOIN|UPDATE|INTO)\s+(\[?[A-Za-z0-9_$#.]+\]?)(?:\s+(?:AS\s+)?(\[?[A-Za-z0-9_]+\]?))?/gi;
  let m;
  while ((m = re.exec(text))) {
    const table = strip(m[1]);
    if (!table) continue;
    const alias = m[2] ? strip(m[2]) : '';
    // Keywords that legitimately follow a table name and are not an alias.
    if (alias && !/^(on|where|inner|left|right|full|cross|join|group|order|set|having|union|select|with|outer|as)$/i.test(alias)) {
      map[alias.toLowerCase()] = table;
    }
    map[table.toLowerCase()] = table;
    const bare = table.includes('.') ? table.split('.').pop() : null;
    if (bare) map[bare.toLowerCase()] = table;
  }
  return map;
}

function strip(s) {
  return (s || '').replace(/[\[\]]/g, '').trim();
}

/// Semicolon-separated globs against the open file's full path — "*\Controllers\*;*\Grid\*".
/// Case-insensitive and slash-agnostic, because these paths are UNC and hand-typed.
function matchesPathScope(scope, path) {
  if (!scope || !scope.trim()) return true;
  if (!path) return false;
  const normalized = path.replace(/\//g, '\\').toLowerCase();
  return scope.split(';').some((pattern) => {
    const p = pattern.trim().replace(/\//g, '\\').toLowerCase();
    if (!p) return false;
    const re = new RegExp('^' + p.split('*').map(escapeRegex).join('.*') + '$');
    return re.test(normalized);
  });
}

function escapeRegex(s) {
  return s.replace(/[.+?^${}()|[\]\\]/g, '\\$&');
}

function wordRange(model, position) {
  const word = model.getWordUntilPosition(position);
  return {
    startLineNumber: position.lineNumber,
    endLineNumber: position.lineNumber,
    startColumn: word.startColumn,
    endColumn: word.endColumn,
  };
}

class BcodeCompletion {
  constructor(editorInstance) {
    this.bcode = editorInstance;          // the BcodeEditor from editor.js
    this.editor = editorInstance.editor;  // the raw Monaco instance
    this.snippets = [];
    this.config = { aiCompletion: false, sqlCompletion: true, sqlRegionTags: [] };
    this._regionCache = null;             // per-model embedded-region map — see regionAt
    this.sqlTables = null;                // null = not fetched yet, [] = unavailable
    this.columnCache = new Map();         // table (lowercase) -> column array
    this.aiCache = new Map();             // prefix tail -> suggestion, so re-triggers are free

    this.registerProviders();
    this.loadFromHost();
  }

  get host() {
    return window.chrome.webview.hostObjects.host;
  }

  /// Which language the caret is actually sitting in. For a real .sql or .js file that is
  /// simply the model's language; for an FCode .xml it is the embedded region (see
  /// buildRegions), because one document holds all three.
  regionAt(model, position) {
    const language = model.getLanguageId();
    if (!isMarkupLanguage(language)) {
      return language === 'sql' ? 'sql' : language === 'javascript' ? 'js' : language === 'css' ? 'css' : 'xml';
    }

    const version = model.getVersionId();
    const cached = this._regionCache;
    // Keyed on the configured tag list too: changing it in Settings has to invalidate the
    // regions, and the model version alone would not.
    const tagKey = (this.config.sqlRegionTags || []).join(',');
    if (!cached || cached.model !== model || cached.version !== version || cached.tagKey !== tagKey) {
      this._regionCache = {
        model,
        version,
        tagKey,
        regions: buildRegions(model.getValue(), this.config.sqlRegionTags || []),
      };
    }

    return regionKindAt(this._regionCache.regions, model.getOffsetAt(position));
  }

  /// Snippets and feature flags come over ONCE. Everything after this point runs against
  /// these arrays in page memory — see the note at the top of the file about why a provider
  /// must never make a host call on the typing path.
  async loadFromHost() {
    // Each result is only assigned on success. This runs again on every reload, and a
    // transient host hiccup must not empty a library that was working a second ago — the
    // previous values stay in place instead.
    try {
      this.snippets = JSON.parse(await this.host.GetSnippets());
    } catch { /* keep whatever we had (initially: none, which is a valid state) */ }
    try {
      this.config = JSON.parse(await this.host.GetEditorConfig());
    } catch { /* keep the previous flags */ }
    this.editor.updateOptions({
      inlineSuggest: { enabled: !!this.config.aiCompletion },
      suggestOnTriggerCharacters: true,
      quickSuggestions: { other: true, comments: false, strings: true }, // strings: true — FCode's content lives inside attribute values
    });
  }

  /// Called from the host after the Hint Code dialog closes, and after Settings changes the
  /// shared folder or the AI/SQL toggles — so both the library AND the feature flags are
  /// live without restarting. Same path as the initial load on purpose: there's no second
  /// code path to keep in step with it.
  async reloadSnippets() {
    // The page's own caches go too, not just the library. Settings may have just pointed
    // Bcode at a different workspace or turned SQL completion back on after it failed — the
    // host drops its schema cache at the same moment (EditorBridge.InvalidateSqlSchema), and
    // leaving the page holding the previous empty table list would make that look like it
    // hadn't worked.
    this.sqlTables = null;
    this.columnCache.clear();
    this.aiCache.clear();
    await this.loadFromHost();
  }

  registerProviders() {
    monaco.languages.registerCompletionItemProvider(FCODE_LANGUAGES, {
      provideCompletionItems: (model, position) => this.provideSnippets(model, position),
    });

    monaco.languages.registerCompletionItemProvider(MARKUP_LANGUAGES, {
      triggerCharacters: ['<', '&', ' ', '"'],
      provideCompletionItems: (model, position) => this.provideXml(model, position),
    });

    // Registered for xml/html as well as sql: most of this project's SQL does not live in
    // a .sql file at all, it sits inside a controller's XML. provideSql bails out unless
    // the caret is genuinely in a SQL region, so this costs nothing elsewhere.
    monaco.languages.registerCompletionItemProvider(['sql', ...MARKUP_LANGUAGES], {
      triggerCharacters: ['.', ' '],
      provideCompletionItems: (model, position) => this.provideSql(model, position),
    });

    monaco.languages.registerInlineCompletionsProvider(FCODE_LANGUAGES, {
      provideInlineCompletions: (model, position, context, token) =>
        this.provideInline(model, position, context, token),
      freeInlineCompletions: () => {},
    });

    // Hovering a &Entity; shows what file it pulls in — the same fact F12 already jumps to
    // (see editor.js's jumpToEntityAtCaret), just without leaving the line.
    monaco.languages.registerHoverProvider(MARKUP_LANGUAGES, {
      provideHover: (model, position) => this.provideEntityHover(model, position),
    });
  }

  // ---- Layer 1: the Hint Code library ------------------------------------------------

  provideSnippets(model, position) {
    const language = model.getLanguageId();
    const path = this.bcode.activePath;
    const range = wordRange(model, position);

    // Inside an .xml/.html document the region at the caret decides, not the file's
    // language: a <script> block gets JS snippets and nothing else, a SQL block gets SQL
    // snippets, and the markup itself gets the XML ones. Elsewhere (a real .sql or .js
    // file) the file's own language is the right answer and the category map applies.
    const region = this.regionAt(model, position);
    const byRegion = isMarkupLanguage(language)
      ? (REGION_CATEGORIES[region] || REGION_CATEGORIES.xml)
      : null;

    const suggestions = this.snippets
      .filter((s) => (byRegion
        ? byRegion.includes(s.category)
        : (CATEGORY_LANGUAGES[s.category] || ['plaintext']).includes(language)))
      .filter((s) => matchesPathScope(s.pathScope, path))
      .map((s) => ({
        label: { label: s.prefix, description: s.shared ? `Hint Code · ${s.source}` : 'Hint Code' },
        kind: monaco.languages.CompletionItemKind.Snippet,
        detail: s.description || s.type,
        documentation: { value: '```\n' + s.code + '\n```' },
        insertText: s.code,
        // The library stores VSCode tabstop syntax verbatim (${1:name}, ${2|a,b|}, $0), and
        // Monaco IS VSCode's editor — so it needs no translation, only this flag to be
        // interpreted instead of inserted literally.
        insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
        range,
        // Personal snippets sort above the team's when both match — yours is the one you
        // just wrote for this specific job.
        sortText: (s.shared ? '1' : '0') + s.prefix,
      }));

    return { suggestions };
  }

  // ---- Layer 2: FCode XML structure --------------------------------------------------

  provideXml(model, position) {
    // Tag/attribute/entity completion is about markup, so it stays out of the embedded
    // blocks. The one thing still worth offering inside <script> is the list of field
    // names and functions declared in the file, which is what FCode's JavaScript refers
    // to — that's the fallback branch at the end of this method.
    const region = this.regionAt(model, position);
    if (region === 'sql' || region === 'css') return { suggestions: [] };

    const facts = docFacts(model);
    if (region === 'js') return this.documentIdentifiers(model, position, facts);

    const lineToCaret = model.getValueInRange({
      startLineNumber: position.lineNumber, startColumn: 1,
      endLineNumber: position.lineNumber, endColumn: position.column,
    });

    // &Entity; — the include references, which is the one thing in these files that is
    // genuinely impossible to remember and painful to get wrong (a typo'd entity name
    // fails at runtime, not at edit time).
    const entityMatch = /&([A-Za-z0-9_.]*)$/.exec(lineToCaret);
    if (entityMatch) {
      const range = {
        startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
        startColumn: position.column - entityMatch[1].length, endColumn: position.column,
      };
      return {
        suggestions: facts.entities.map((name) => ({
          label: name,
          kind: monaco.languages.CompletionItemKind.Reference,
          detail: 'ENTITY đã khai trong file',
          insertText: name + ';',
          range,
        })),
      };
    }

    // An attribute VALUE: <field type="|" — offer what this document already uses for that
    // same attribute, which for type/event/id is exactly the closed set that's legal.
    const valueMatch = /<([A-Za-z][A-Za-z0-9_-]*)[^<>]*\s([A-Za-z0-9_:-]+)="([^"]*)$/.exec(lineToCaret);
    if (valueMatch) {
      const [, tag, attribute, typed] = valueMatch;
      const values = this.attributeValues(model, tag.toLowerCase(), attribute.toLowerCase(), facts);
      if (!values.length) return { suggestions: [] };
      const range = {
        startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
        startColumn: position.column - typed.length, endColumn: position.column,
      };
      return {
        suggestions: values.map((v) => ({
          label: v,
          kind: monaco.languages.CompletionItemKind.Value,
          detail: `${tag}.${attribute} đã dùng trong file`,
          insertText: v,
          range,
        })),
      };
    }

    // An attribute NAME: inside an open tag, after whitespace, not inside a value.
    const openTagMatch = /<([A-Za-z][A-Za-z0-9_-]*)((?:[^<>"]|"[^"]*")*)$/.exec(lineToCaret);
    if (openTagMatch && /\s[A-Za-z0-9_:-]*$/.test(openTagMatch[2])) {
      const tag = openTagMatch[1].toLowerCase();
      const already = new Set((openTagMatch[2].match(/([A-Za-z0-9_:-]+)\s*=/g) || [])
        .map((a) => a.replace(/\s*=$/, '').toLowerCase()));
      const candidates = unique([...(facts.tagAttributes[tag] || []), ...(SEED_ATTRIBUTES[tag] || [])])
        .filter((a) => !already.has(a.toLowerCase()));
      const range = wordRange(model, position);
      return {
        suggestions: candidates.map((a) => ({
          label: a,
          kind: monaco.languages.CompletionItemKind.Property,
          detail: `thuộc tính của <${tag}>`,
          insertText: `${a}="$1"`,
          insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
          range,
        })),
      };
    }

    // A tag name right after "<".
    const tagMatch = /<([A-Za-z0-9_-]*)$/.exec(lineToCaret);
    if (tagMatch) {
      const range = {
        startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
        startColumn: position.column - tagMatch[1].length, endColumn: position.column,
      };
      return {
        suggestions: facts.tags.map((t) => ({
          label: t,
          kind: monaco.languages.CompletionItemKind.Class,
          detail: 'tag đã dùng trong file',
          insertText: t,
          range,
        })),
      };
    }

    // Anywhere else in the markup (a caption, free text): the names declared in this file.
    return this.documentIdentifiers(model, position, facts);
  }

  /// Field names and function names declared anywhere in this document. Useful in two
  /// places for the same reason: FCode's JavaScript refers to fields by the exact string
  /// in `<field name="...">`, and a mistyped one is silent until runtime.
  documentIdentifiers(model, position, facts) {
    const range = wordRange(model, position);
    return {
      suggestions: [
        ...facts.fields.map((f) => ({
          label: f,
          kind: monaco.languages.CompletionItemKind.Field,
          detail: '<field name> trong file',
          insertText: f,
          range,
        })),
        ...facts.functions.map((f) => ({
          label: f,
          kind: monaco.languages.CompletionItemKind.Function,
          detail: 'function trong file',
          insertText: f,
          range,
        })),
      ],
    };
  }

  attributeValues(model, tag, attribute, facts) {
    // Values this document already uses for the same tag+attribute pair — the file is its
    // own best reference for what's legal here.
    const text = model.getValue();
    const re = new RegExp('<' + tag + '\\b[^<>]*\\s' + attribute + '="([^"]+)"', 'gi');
    const used = unique(matchAll(text, re));

    // Cross-references that live under a DIFFERENT tag than the one being typed, so the
    // scan above can't find them: a command's event, a view's id, and so on.
    if (tag === 'command' && attribute === 'event') return unique([...used, ...facts.events]);
    if (attribute === 'field' || attribute === 'fieldname') return unique([...used, ...facts.fields]);
    if (attribute === 'view' || attribute === 'viewid') return unique([...used, ...facts.views]);
    return used;
  }

  provideEntityHover(model, position) {
    const word = model.getWordAtPosition(position);
    if (!word) return null;
    const text = model.getValue();
    const re = new RegExp('<!ENTITY\\s+%?\\s*' + escapeRegex(word.word) + '\\s+SYSTEM\\s+"([^"]+)"');
    const m = re.exec(text);
    if (!m) return null;
    return {
      range: new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn),
      contents: [{ value: `**ENTITY ${word.word}**` }, { value: '`' + m[1] + '`' }, { value: '_F12 để mở file này_' }],
    };
  }

  // ---- Layer 3: SQL schema -----------------------------------------------------------

  async provideSql(model, position) {
    if (!this.config.sqlCompletion) return { suggestions: [] };
    if (this.regionAt(model, position) !== 'sql') return { suggestions: [] };

    const facts = docFacts(model);
    const lineToCaret = model.getValueInRange({
      startLineNumber: position.lineNumber, startColumn: 1,
      endLineNumber: position.lineNumber, endColumn: position.column,
    });

    // "alias." — the one case worth a host round trip, because it happens once per table
    // and the answer is cached for the rest of the session.
    const dotMatch = /([A-Za-z0-9_$#]+)\.([A-Za-z0-9_]*)$/.exec(lineToCaret);
    if (dotMatch) {
      const table = facts.sqlAliases[dotMatch[1].toLowerCase()] || dotMatch[1];
      const columns = await this.columnsFor(table);
      const range = {
        startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
        startColumn: position.column - dotMatch[2].length, endColumn: position.column,
      };
      return {
        suggestions: columns.map((c) => ({
          label: c.pk ? `${c.name} (PK)` : c.name,
          kind: c.pk ? monaco.languages.CompletionItemKind.Constant : monaco.languages.CompletionItemKind.Field, // Constant, not Key: Key exists on SymbolKind but NOT CompletionItemKind, and an undefined kind renders as a blank icon
          detail: `${c.type}${c.nullable ? ' null' : ' not null'} — ${table}`,
          insertText: c.name,
          // PK columns first: in this schema they're the join keys, which is what anyone
          // typing "a." right after a JOIN is reaching for.
          sortText: (c.pk ? '0' : '1') + c.name,
          range,
        })),
      };
    }

    // After FROM/JOIN/UPDATE/INTO — table names.
    if (/\b(FROM|JOIN|UPDATE|INTO)\s+[A-Za-z0-9_$#]*$/i.test(lineToCaret)) {
      const tables = await this.tables();
      const range = wordRange(model, position);
      return {
        suggestions: tables.map((t) => ({
          label: t.name,
          kind: t.kind === 'view'
            ? monaco.languages.CompletionItemKind.Interface
            : monaco.languages.CompletionItemKind.Struct,
          detail: `${t.schema} · ${t.kind}`,
          insertText: t.name,
          range,
        })),
      };
    }

    // Elsewhere in a query: columns of whatever tables this statement already mentions —
    // no alias needed, which is how most of these queries are actually written.
    const mentioned = unique(Object.values(facts.sqlAliases));
    if (!mentioned.length) return { suggestions: [] };
    const range = wordRange(model, position);
    const suggestions = [];
    for (const table of mentioned.slice(0, 4)) { // 4 tables is a wide join already; more is noise
      for (const c of await this.columnsFor(table)) {
        suggestions.push({
          label: c.name,
          kind: c.pk ? monaco.languages.CompletionItemKind.Constant : monaco.languages.CompletionItemKind.Field, // Constant, not Key: Key exists on SymbolKind but NOT CompletionItemKind, and an undefined kind renders as a blank icon
          detail: `${c.type} — ${table}`,
          insertText: c.name,
          sortText: (c.pk ? '0' : '1') + c.name,
          range,
        });
      }
    }
    return { suggestions };
  }

  async tables() {
    if (this.sqlTables !== null) return this.sqlTables;
    this.sqlTables = []; // set first: a second keystroke landing mid-fetch must not fire a second query
    try {
      this.sqlTables = JSON.parse(await this.host.GetSqlTables());
    } catch {
      this.sqlTables = [];
    }
    return this.sqlTables;
  }

  async columnsFor(table) {
    const key = (table || '').toLowerCase();
    if (!key) return [];
    if (this.columnCache.has(key)) return this.columnCache.get(key);
    this.columnCache.set(key, []); // same guard as tables() — one query per table, ever
    try {
      const columns = JSON.parse(await this.host.GetSqlColumns(table));
      this.columnCache.set(key, columns);
      return columns;
    } catch {
      return [];
    }
  }

  // ---- Layer 4: AI ghost text --------------------------------------------------------

  async provideInline(model, position, context, token) {
    if (!this.config.aiCompletion) return { items: [] };
    if (!this.bcode.activePath) return { items: [] };

    const prefix = model.getValueInRange({
      startLineNumber: 1, startColumn: 1,
      endLineNumber: position.lineNumber, endColumn: position.column,
    });

    // Don't suggest in the middle of a word — the user is still typing an identifier, and
    // ghost text there fights with the normal suggestion widget for the same keystrokes.
    if (/[A-Za-z0-9_$]{2}$/.test(prefix)) return { items: [] };

    const cacheKey = prefix.slice(-400);
    if (this.aiCache.has(cacheKey)) {
      return this.wrapInline(this.aiCache.get(cacheKey), position);
    }

    // Debounce inside the provider: Monaco calls this on every content change, and a request
    // per character would be both useless (superseded before it lands) and billed. 400ms of
    // no typing is the signal that a suggestion is actually wanted. The host cancels any
    // still-running previous call on its side — see EditorBridge.GetInlineCompletion.
    const versionAtRequest = model.getVersionId();
    await new Promise((resolve) => setTimeout(resolve, 400));
    if (token.isCancellationRequested) return { items: [] };
    // Typing during the debounce means Monaco has already asked again for the newer
    // position; this older call has nothing left to contribute.
    if (model.getVersionId() !== versionAtRequest) return { items: [] };

    const suffix = model.getValueInRange({
      startLineNumber: position.lineNumber, startColumn: position.column,
      endLineNumber: model.getLineCount(), endColumn: model.getLineMaxColumn(model.getLineCount()),
    });

    let text = '';
    try {
      // The region goes with it: inside a controller's <script> block the model is being
      // asked to continue JavaScript, not XML, and the surrounding text alone is a weak
      // signal when the caret sits a few lines into a function.
      text = await this.host.GetInlineCompletion(
        prefix, suffix, this.bcode.activePath, this.regionAt(model, position));
    } catch {
      return { items: [] };
    }
    if (token.isCancellationRequested) return { items: [] };

    // Cache even an empty answer: "nothing useful goes here" is worth remembering so
    // pausing at the same spot twice doesn't pay for the same call twice. Bounded because
    // this grows with every pause in a long editing session.
    if (this.aiCache.size > 200) this.aiCache.clear();
    this.aiCache.set(cacheKey, text || '');

    return this.wrapInline(text, position);
  }

  wrapInline(text, position) {
    if (!text) return { items: [] };
    return {
      items: [{
        insertText: text,
        range: new monaco.Range(position.lineNumber, position.column, position.lineNumber, position.column),
      }],
    };
  }
}

window.BcodeCompletion = BcodeCompletion;
