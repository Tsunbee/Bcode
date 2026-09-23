// IntelliSense for BcodeViewer — the four suggestion layers Monaco can provide, wired to
// what this codebase actually knows:
//
//   1. Snippets      — the Hint Code library (personal + the team's shared folder), turned
//                      into real completion items with VSCode tabstop syntax.
//   2. FCode XML     — structure learned from the open document (tag names, attributes
//                      already used on a tag, <field name> values, <category index>), from
//                      the files it INCLUDES (every ENTITY the document can resolve, see
//                      entity.js), and from the FastBusiness vocabulary below for the
//                      closed sets a file can only teach you once it already has them.
//   3. SQL           — the @@macro/$partition$/@field placeholders, plus tables/columns
//                      from the workspace Bcode.App is connected to.
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

// Attribute names seeded per tag, for when the document cannot teach them: a new file, or
// a tag used once with only two of its attributes. The document-derived list
// (collectTagAttributes) still comes first, because it reflects how this project actually
// writes its XML. Read off the real Dir/Grid/Filter/Report/Lookup controllers.
const SEED_ATTRIBUTES = {
  field: ['name', 'type', 'width', 'hidden', 'readOnly', 'allowNulls', 'external', 'defaultValue',
          'clientDefault', 'dataFormatString', 'align', 'categoryIndex', 'aliasName', 'inactivate',
          'allowContain', 'allowFilter', 'allowSorting', 'isPrimaryKey', 'maxLength', 'filterSource'],
  view: ['id', 'height', 'anchor', 'split'],
  command: ['event'],
  action: ['id'],
  query: ['event'],
  items: ['style', 'controller', 'reference', 'key', 'check', 'information', 'new', 'row', 'normal'],
  category: ['index', 'columns', 'anchor', 'split', 'length'],
  button: ['command'],
  menuItem: ['commandArgument', 'urlImage'],
  form: ['id', 'reportFile', 'templateFile', 'commandArgument', 'urlImage', 'controller', 'externalID', 'languageType'],
  partition: ['table', 'prime', 'inquiry', 'field', 'expression', 'increase', 'default'],
  handle: ['key', 'field', 'source', 'foreward'],
  header: ['v', 'e'],
  footer: ['v', 'e'],
  label: ['v', 'e'],
  title: ['v', 'e'],
  item: ['value'],
  grid: ['table', 'code', 'order', 'type', 'id', 'uniKey', 'freezeColumns', 'xmlns'],
  dir: ['table', 'code', 'order', 'id', 'type', 'uniKey', 'navigation', 'name', 'check', 'replication', 'xmlns'],
  lookup: ['table', 'code', 'name', 'order', 'xmlns'],
};

// ---- FastBusiness vocabulary --------------------------------------------------------
//
// Closed sets that the document itself can only teach you once it already contains them —
// which is exactly backwards when you are adding the one that is missing. Every list below
// was read off this project's own controllers (Dir/Grid/Filter/Report/Lookup), not from a
// published schema, so treat them as "what this codebase uses" rather than "what FCode
// accepts". Values the open file already uses are still merged in, and listed first.

/// <command event="..."> — the voucher lifecycle, in roughly the order it runs.
const FCODE_COMMAND_EVENTS = [
  'Init', 'Showing', 'Loading', 'Scattering', 'Navigating', 'Copying', 'Closing',
  'Declare', 'InitExternalFields', 'Checking',
  'Inserting', 'Inserted', 'Updating', 'Updated', 'Deleting', 'Deleted',
];

/// <query event="..."> — grid/lookup data loading.
const FCODE_QUERY_EVENTS = ['Loading', 'Declare', 'Finding'];

const FCODE_ITEM_STYLES = ['AutoComplete', 'Numeric', 'Mask', 'Grid', 'DropDownList'];

const FCODE_FIELD_TYPES = ['String', 'Decimal', 'DateTime', 'Boolean', 'Int32', 'Int16', 'Byte'];

/// dataFormatString="@..." — named formats the app resolves per workspace, which is why
/// they are used instead of literal masks.
const FCODE_FORMATS = [
  '@datetimeFormat', '@upperCaseFormat',
  '@quantityInputFormat', '@quantityViewFormat',
  '@foreignCurrencyAmountInputFormat', '@foreignCurrencyAmountViewFormat',
  '@baseCurrencyAmountInputFormat', '@baseCurrencyAmountViewFormat',
  '@foreignCurrencyPriceInputFormat', '@baseCurrencyPriceInputFormat',
  '@exchangeRateInputFormat',
];

/// <button command="..."> — toolbar commands handled by the framework itself. A project
/// command (GuiHang, CallPrice, TaoHDThayThe...) needs its own `case` in the script and its
/// own div.<name> in <css>, and comes from the document's own list rather than this one.
const FCODE_TOOLBAR_COMMANDS = [
  'New', 'Edit', 'Delete', 'Clone', 'Search', 'View', 'Print', 'Export', 'Freeze', 'Separate',
  'Insert', 'Grow', 'Down', 'Remove', 'Lookup', 'Retrieve', 'ImportData', 'Download',
];

/// `@@name` macros the server substitutes before the SQL runs. The descriptions are read
/// off how each one is used in this project's own commands, not from documentation.
const FCODE_SQL_MACROS = [
  ['@@id', 'Ma chung tu - id cua <dir>/<grid>, vd HDA'],
  ['@@master', 'Bang tong hop moi ky - table= o the goc'],
  ['@@prime', 'Tien to bang master theo ky - <partition prime=>'],
  ['@@inquiry', 'Tien to bang inquiry theo ky - <partition inquiry=>'],
  ['@@partition', 'Bang phan ky - <partition table=>'],
  ['@@expression', '<partition expression=>'],
  ['@@increase', '<partition increase=>'],
  ['@@extension', 'Phan mo rong cau truy van do framework ghep vao'],
  ['@@unit', 'Ma don vi co so dang dang nhap'],
  ['@@userID', 'Nguoi dung dang dang nhap'],
  ['@@admin', '1 neu la admin'],
  ['@@language', 'v hoac e'],
  ['@@action', 'New / Edit / View'],
  ['@@view', '1 neu dang o che do xem'],
  ['@@operation', 'Thao tac dang chay, truyen cho cac thu tuc Update*'],
  ['@@form', 'Ma mau in - khop voi <form id=> trong Report'],
  ['@@sysDatabaseName', 'CSDL he thong'],
  ['@@appDatabaseName', 'CSDL ung dung'],
  ['@@refresh', 'Co nap lai, dung trong <query event="Finding">'],
  ['@@pageIndex', 'Trang hien tai (query Finding)'],
  ['@@pageCount', 'So dong moi trang (query Finding)'],
  ['@@lastPage', 'Trang cuoi (query Finding)'],
  ['@@lastCount', 'So dong trang cuoi (query Finding)'],
  ['@@firstItem', 'Khoa dong dau (query Finding)'],
  ['@@lastItem', 'Khoa dong cuoi (query Finding)'],
  ['@@keyMaster', 'Khoa master (query Finding)'],
  ['@@keyDetail', 'Khoa detail (query Finding)'],
  ['@@textList', 'Danh sach cot do framework dung'],
  ['@@textExternal', 'Danh sach cot external do framework dung'],
  ['@@textOrderBy', 'Menh de ORDER BY do framework dung'],
  ['@@viewAccessMode', 'Quyen xem du lieu'],
  ['@@queryString', 'Tham so truyen tu URL'],
];

/// Period suffixes. `m81$$partition$current` becomes `m81$202609`; `$previous` is the period
/// the record was in before an edit moved it to another one.
const FCODE_PARTITION_SUFFIXES = [
  ['$partition$current', 'Ky hien tai cua ban ghi'],
  ['$partition$previous', 'Ky truoc khi sua (khi ngay chung tu doi ky)'],
];

/// Whole elements, not bare tag names. Typing "field" and getting back `<field` leaves you
/// to remember that a field is useless without its `<header v= e=>` — and a field declared
/// without one renders with an empty caption, which looks like a layout bug rather than a
/// missing line. These insert the shape the project actually writes, with the caption and
/// the pieces that differ per file type already in place.
///
/// Keyed by root element, because the same tag is written differently in each: a Dir field
/// carries categoryIndex (which tab it lands on), a Grid field carries width (its column),
/// a Lookup field carries allowFilter, and a Report field is a print label with a type.
const FCODE_ELEMENT_SNIPPETS = {
  dir: {
    field: '<field name="${1:ten_field}" categoryIndex="${2:-1}">\n\t<header v="${3:Nhãn}" e="${4:Label}"></header>\n</field>$0',
    items: '<items style="${1|AutoComplete,Numeric,Mask,Grid,DropDownList|}" controller="${2:Customer}" reference="${3:ten_kh%l}" key="status = \'1\'" check="1 = 1" information="${4:ma_kh$dmkh.ten_kh%l}"/>$0',
    command: '<command event="${1|Loading,Showing,Declare,Checking,Inserting,Inserted,Updating,Updated,Deleting,Deleted|}">\n\t<text>\n\t\t<![CDATA[\n\t\t$0\n\t\t]]>\n\t</text>\n</command>',
    action: '<action id="${1:TenAction}">\n\t<text>\n\t\t<![CDATA[\n\t\t$0\n\t\treturn\n\t\t]]>\n\t</text>\n</action>',
    category: '<category index="${1:20}" columns="${2:100, 30, 70, 35, 65}" anchor="${3:6}">\n\t<header v="${4:Nhãn}" e="${5:Label}"/>\n</category>$0',
    item: '<item value="${1:110}: [${2:ten_field}].Label, [${2:ten_field}]"/>$0',
  },
  grid: {
    field: '<field name="${1:ten_field}" width="${2:80}">\n\t<header v="${3:Nhãn}" e="${4:Label}"></header>\n</field>$0',
    items: '<items style="${1|AutoComplete,Numeric,Mask,DropDownList|}" controller="${2:Item}" reference="${3:ten_vt%l}" key="status = \'1\'" check="1 = 1" information="${4:ma_vt$dmvt.ten_vt%l}"/>$0',
    handle: '<handle key="[${1:co_hien}]" field="${2:ma_vt}"/>$0',
    button: '<button command="${1:TenLenh}">\n\t<title v="${2:Nhãn}$$90" e="${3:Label}$$120"></title>\n</button>$0',
    command: '<command event="${1|Showing,Loading,Scattering,Closing|}">\n\t<text>\n\t\t<![CDATA[\n\t\t$0\n\t\t]]>\n\t</text>\n</command>',
    action: '<action id="${1:TenAction}">\n\t<text>\n\t\t<![CDATA[\n\t\t$0\n\t\treturn\n\t\t]]>\n\t</text>\n</action>',
    query: '<query event="${1|Loading,Declare,Finding|}">\n\t<text>$0</text>\n</query>',
  },
  lookup: {
    field: '<field name="${1:ten_field}" allowFilter="true" allowSorting="&GridLookupAllowSorting;">\n\t<header v="${2:Nhãn}" e="${3:Label}"></header>\n\t<query>&InsertCommandFilter;</query>\n</field>$0',
  },
  report: {
    field: '<field name="${1:h_ten}" type="String">\n\t<header v="${2:Nhãn}" e="${3:Label}"/>\n</field>$0',
    form: '<form id="${1:010}" reportFile="${2:SVTran_02}" templateFile="" commandArgument="${3|Pdf,Excel|}" urlImage="&p;">\n\t<header v="${4:Nhãn}" e="${5:Label}"></header>\n\t<download>\n\t\t<header v="${4:Nhãn}" e="${5:Label}"/>\n\t</download>\n</form>$0',
    category: '<category index="${1:20}" length="${2:4}">\n\t<header v="${3:Nhãn}" e="${4:Label}"/>\n</category>$0',
  },
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

/// How long a whole-document scan may be reused before it is rebuilt.
///
/// These caches used to be keyed on model.getVersionId() alone. That version changes on
/// every single character typed, so the key guaranteed a miss per keystroke: each one
/// re-serialised the entire document with getValue() and re-ran ~15 full-document regex
/// passes (docFacts) plus the embedded-region scan (buildRegions), on the same thread that
/// draws the suggestion list. Measured: 2.1ms per keystroke on a 1,100-line controller and
/// 145ms on a 45,000-line one.
///
/// Reusing a scan for a fraction of a second is safe for the NAME LISTS — field names,
/// view/action ids, function names. Those change when someone finishes writing a
/// declaration, not between two characters of the same word, and the worst a stale copy
/// can do is leave a name you just typed out of its own file's suggestion list for a
/// moment.
///
/// It is NOT safe for anything holding a text OFFSET. Offsets are compared against the
/// live caret position, and typing moves the caret without moving a cached boundary: type
/// a few characters near the end of a <script> block and the caret runs past where the
/// cache still thinks the block ends, so the editor decides you have left it. Everything
/// positional is therefore recomputed every time — see viewInfo below, and regionAt, which
/// keeps exact version keying for the same reason (its scan measured 0.23ms on a
/// 1,100-line controller, far too cheap to be worth risking a wrong answer for).
const DOC_SCAN_MAX_AGE_MS = 400;

/// performance.now() where available (it is, in WebView2) — monotonic, so it cannot jump
/// backwards over a clock change the way Date.now() can and strand a cache as permanently
/// "fresh".
function scanClock() {
  return (typeof performance !== 'undefined' && performance.now) ? performance.now() : Date.now();
}

function docFacts(model) {
  const version = model.getVersionId();
  const cached = docCache.get(model);
  if (cached && cached.version === version) return cached.facts; // document unchanged

  const text = model.getValue();

  // Recomputed on every change without exception: viewInfo carries offsets, which its
  // callers compare against the live caret (see "inViews"). A stale boundary there is a
  // wrong answer, not a slightly old one. It is also cheap — one regex and an indexOf,
  // against the ~15 full-document passes below.
  const viewInfo = collectViewInfo(text);

  // The expensive half is name lists only, and those carry no positions, so a copy from a
  // moment ago is safe to keep — see DOC_SCAN_MAX_AGE_MS.
  if (cached && scanClock() - cached.builtAt < DOC_SCAN_MAX_AGE_MS) {
    const reused = { ...cached.facts, viewInfo };
    // builtAt stays at the original scan's time, so reuse expires on a fixed schedule
    // rather than being extended indefinitely by continuous typing.
    docCache.set(model, { version, builtAt: cached.builtAt, facts: reused });
    return reused;
  }
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
    // <category index="18"><header v="Khác"> — the index alone means nothing to a reader,
    // so the header travels with it and becomes the detail line on the suggestion.
    categories: collectCategories(text),
    // <button command="X"> and the report's <form id="X">: both are ids that have to match
    // something else in the same file (a `case 'X':` in the script, an `@@form = 'X'` test
    // in the query), which is exactly when you want the existing list in front of you.
    buttons: unique(matchAll(text, /<button\b[^>]*?\scommand="([^"]+)"/g)),
    forms: unique(matchAll(text, /<form\b[^>]*?\sid="([^"]+)"/g)),
    // g.$a.<name> — the expression aliases the grid scripts are written in. They are
    // declared by an entity (&FormulaExpression;/ValidFormula.ent), never in this file, so
    // the only way to know the names is to collect the ones in use.
    aliases: unique(matchAll(text, /\$a\.([A-Za-z0-9_$]+)/g)),
    // The controller names this document hands to showForm(...) / controller="...".
    controllers: unique([
      ...matchAll(text, /\bshowForm\(\s*'([^']+)'/g),
      ...matchAll(text, /<items\b[^>]*?\scontroller="([^"]+)"/g),
    ]),
    // dir | grid | report | lookup — the same tag is written differently in each, so the
    // snippets and several of the rules below need to know which kind of file this is.
    rootKind: rootKindOf(text),
    // Where <views> is and which fields it already mentions. A field that is declared but
    // never placed in a view simply does not appear on the form, with nothing to see in
    // the file itself — so "not in the view yet" is the most useful thing the editor can
    // say while you are standing inside one. Computed above, because it is positional.
    viewInfo,
    tags: unique(matchAll(text, /<([A-Za-z][A-Za-z0-9_-]*)[\s>/]/g)),
    tagAttributes: collectTagAttributes(text),
    sqlAliases: collectSqlAliases(text),
  };

  docCache.set(model, { version, builtAt: scanClock(), facts });
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

/// [{index, header}] for every <category index="..."> in the document, header taken from
/// the Vietnamese caption inside it. A category is declared far away from the fields that
/// point at it, so `categoryIndex="18"` is otherwise a number you have to go and look up.
function collectCategories(text) {
  const out = [];
  const re = /<category\b[^>]*?\sindex="([^"]+)"[^>]*>([\s\S]{0,400}?)<\/category>/g;
  let m;
  while ((m = re.exec(text))) {
    const header = /<header\b[^>]*?\sv="([^"]*)"/.exec(m[2]);
    out.push({ index: m[1], header: header ? header[1] : '' });
  }
  // Self-closing categories (<category index="1" .../>) carry no header but still exist.
  const selfClosing = /<category\b[^>]*?\sindex="([^"]+)"[^>]*\/>/g;
  while ((m = selfClosing.exec(text))) {
    if (!out.some((c) => c.index === m[1])) out.push({ index: m[1], header: '' });
  }
  return out;
}

/// Entity names the open document can resolve: the ones its own DOCTYPE declares (read live
/// from the text, because that is what changes while you type) plus the ones its included
/// files declare (see entity.js's include index, built off the typing path).
///
/// Returned as [{name, detail, documentation}] ready to become suggestions, ordered with
/// the document's own first — those are the ones being worked on.
function availableEntities(localNames, activePath) {
  const items = localNames.map((name) => ({ name, detail: 'ENTITY khai trong file này' }));
  const seen = new Set(localNames);

  const index = window.bcodeEntity ? window.bcodeEntity.includeIndexFor(activePath) : null;
  if (!index) return items;

  for (const [name, entry] of index) {
    if (seen.has(name)) continue;
    seen.add(name);
    const isFile = entry.decl.kind === 'system';
    items.push({
      name,
      detail: (isFile ? 'file: ' + entry.decl.systemPath + ' — qua ' : 'qua ') + fileNameOf(entry.path),
      // A one-line taste of the value, so a name like &DeclareStock; is recognisable
      // without opening anything. F12 shows the whole thing.
      documentation: isFile ? null : previewOf(entry.decl.value),
    });
  }
  return items;
}

/// 'dir' | 'grid' | 'report' | 'lookup', from the document's own root element. A Filter
/// file has a <dir> root too, which is correct here: it is laid out the same way.
function rootKindOf(text) {
  const m = /<(dir|grid|report|lookup)\b/i.exec(text);
  return m ? m[1].toLowerCase() : 'dir';
}

/// {start, end, referenced:Set} for the document's <views> block.
///
/// `referenced` covers both layouts, because a controller family uses both: a Grid/Lookup
/// view lists `<field name="x"/>`, a Dir/Filter view writes `[x]` inside an
/// `<item value="mask: [x].Label, [x]">`. Either way, a field the block never mentions is
/// a field that will not be on the screen.
function collectViewInfo(text) {
  const open = /<views\b[^>]*>/.exec(text);
  if (!open) return { start: -1, end: -1, referenced: new Set() };
  const start = open.index;
  const closeAt = text.indexOf('</views>', start);
  const end = closeAt < 0 ? text.length : closeAt + '</views>'.length;
  const block = text.slice(start, end);

  const referenced = new Set([
    ...matchAll(block, /<field\b[^>]*?\sname="([^"]+)"/g),
    ...matchAll(block, /\[([A-Za-z0-9_%$.]+)\]/g),
  ]);
  return { start, end, referenced };
}

function previewOf(value) {
  if (!value) return null;
  const lines = value.split('\n').map((l) => l.trim()).filter(Boolean);
  const head = lines.slice(0, 6).join('\n');
  return lines.length > 6 ? head + '\n… còn ' + (lines.length - 6) + ' dòng' : head;
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

  /// The raw bridge, for the two calls here that answer straight out of host memory
  /// (GetSnippets, GetEditorConfig) and so never needed queueing. Everything else on this
  /// class's path goes through window.bcodeHost.call — see hostcall.js.
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
    // Exact version keying, deliberately — no age-based reuse here. These regions are
    // offset ranges tested against the live caret offset, and typing moves the caret
    // without moving a cached boundary: a few characters near the end of a <script> block
    // and the caret sits past the stale end, so the editor concludes you have left the
    // block and offers XML snippets inside JavaScript. The scan is 0.23ms on a 1,100-line
    // controller, which is not a price worth paying a wrong answer for.
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
      // '[' opens the Dir/Filter view syntax, "'" opens a JS call argument that names a
      // field/action — both are points where the name has to be exact.
      triggerCharacters: ['<', '&', ' ', '"', '[', "'", '.'],
      provideCompletionItems: (model, position) => this.provideXml(model, position),
    });

    // Registered for xml/html as well as sql: most of this project's SQL does not live in
    // a .sql file at all, it sits inside a controller's XML. provideSql bails out unless
    // the caret is genuinely in a SQL region, so this costs nothing elsewhere.
    monaco.languages.registerCompletionItemProvider(['sql', ...MARKUP_LANGUAGES], {
      triggerCharacters: ['.', ' ', '@', '$'],
      provideCompletionItems: (model, position) => this.provideSql(model, position),
    });

    monaco.languages.registerInlineCompletionsProvider(FCODE_LANGUAGES, {
      provideInlineCompletions: (model, position, context, token) =>
        this.provideInline(model, position, context, token),
      freeInlineCompletions: () => {},
    });

    // Hovering a &Entity; shows what it expands to without leaving the line: the file for
    // a SYSTEM entity, a preview of the code for a value one. Resolution (including the
    // included-file chain the declaration usually lives in) belongs to entity.js, which
    // also owns F12 — one answer, two ways of asking for it.
    monaco.languages.registerHoverProvider(MARKUP_LANGUAGES, {
      provideHover: (model, position, token) =>
        window.bcodeEntity ? window.bcodeEntity.provideHover(model, position, token) : null,
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
    //
    // The name pattern allows '-' and '$' as well as dots: real names look like
    // &DiscountPromotion.Include.f; and &Tiny.External.Form.ReleaseLaterStatus;, and a
    // narrower pattern silently stopped offering anything once the caret was past the
    // first dot.
    const entityMatch = /&(%?[A-Za-z0-9_.$-]*)$/.exec(lineToCaret);
    if (entityMatch) {
      const typed = entityMatch[1];
      const range = {
        startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
        startColumn: position.column - typed.length, endColumn: position.column,
      };
      return {
        suggestions: availableEntities(facts.entities, this.bcode.activePath).map((e, i) => ({
          label: e.name,
          kind: monaco.languages.CompletionItemKind.Reference,
          detail: e.detail,
          documentation: e.documentation ? { value: '```sql\n' + e.documentation + '\n```' } : undefined,
          insertText: e.name + ';',
          // Monaco sorts alphabetically by default, which would bury the file's own
          // declarations among several hundred inherited ones. The index keeps the order
          // availableEntities chose (local first).
          sortText: String(i).padStart(5, '0'),
          range,
        })),
      };
    }

    // [field] inside a <view>'s <item value="...">. This is the Dir/Filter layout syntax —
    // "1101000000-1101: [tk].Label, [tk], [ten_tk%l], [status].Label, [status]" — where
    // every name has to match a <field name> exactly, including the %l suffix, and where a
    // typo just makes the control silently not appear on the form.
    const bracketMatch = /\[([A-Za-z0-9_%$.]*)$/.exec(lineToCaret);
    if (bracketMatch && /<item\b|value="/.test(lineToCaret)) {
      const typed = bracketMatch[1];
      const range = {
        startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
        startColumn: position.column - typed.length, endColumn: position.column,
      };
      return {
        suggestions: facts.fields.map((f) => ({
          label: f,
          kind: monaco.languages.CompletionItemKind.Field,
          detail: '<field name> trong file',
          insertText: f + ']',
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

      // Typing the name inside `<field name="|"/>` in a Grid/Lookup view: same list, but
      // the fields not on the form yet come first and say so. That is the only reason to
      // be typing here.
      const offsetNow = model.getOffsetAt(position);
      if (tag.toLowerCase() === 'field' && attribute.toLowerCase() === 'name' &&
          facts.viewInfo.start >= 0 && offsetNow > facts.viewInfo.start && offsetNow < facts.viewInfo.end) {
        values.sort((a, b) => {
          const am = facts.viewInfo.referenced.has(a.value) ? 1 : 0;
          const bm = facts.viewInfo.referenced.has(b.value) ? 1 : 0;
          return am - bm;
        });
        for (const v of values) {
          if (!facts.viewInfo.referenced.has(v.value)) v.detail = 'chưa thấy trong <view>';
        }
      }
      const range = {
        startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
        startColumn: position.column - typed.length, endColumn: position.column,
      };
      return {
        suggestions: values.map((v, i) => ({
          label: v.value,
          kind: monaco.languages.CompletionItemKind.Value,
          detail: v.detail,
          insertText: v.value,
          // Keeps attributeValues' ordering (what the file uses first, vocabulary after).
          sortText: String(i).padStart(4, '0'),
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
      // The range has to swallow the "<" as well: every snippet below writes its own
      // opening bracket, and leaving the typed one in place produced "<<field ...".
      const range = {
        startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
        startColumn: position.column - tagMatch[1].length - 1, endColumn: position.column,
      };
      const offset = model.getOffsetAt(position);
      const inViews = facts.viewInfo.start >= 0 &&
        offset > facts.viewInfo.start && offset < facts.viewInfo.end;

      const suggestions = inViews ? this.viewPlacements(facts, range) : [];

      // Whole-element snippets before bare tag names: a <field> without its <header> is a
      // control with no caption, and typing the tag alone is the step that leads there.
      const snippets = FCODE_ELEMENT_SNIPPETS[facts.rootKind] || {};
      for (const [tag, body] of Object.entries(snippets)) {
        if (inViews && tag === 'field') continue; // handled by viewPlacements, with the real names
        suggestions.push({
          label: tag,
          kind: monaco.languages.CompletionItemKind.Snippet,
          detail: `khối <${tag}> đầy đủ`,
          documentation: { value: '```xml\n' + body.replace(/\$\{\d+\|?|\|\}|\}|\$0/g, '').replace(/\d+:/g, '') + '\n```' },
          insertText: '<' + body,
          insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
          sortText: '1' + tag,
          range,
        });
      }

      for (const t of facts.tags) {
        suggestions.push({
          label: t,
          kind: monaco.languages.CompletionItemKind.Class,
          detail: 'tag đã dùng trong file',
          insertText: '<' + t,
          sortText: '2' + t,
          range,
        });
      }
      return { suggestions };
    }

    // Anywhere else in the markup: the names declared in this file, plus the same whole-
    // element snippets. They are offered without a leading "<" as well because that is how
    // people reach for them — you think "I need a field", not "I need a less-than sign".
    const fallback = this.documentIdentifiers(model, position, facts);
    const wordAt = wordRange(model, position);
    const snippets = FCODE_ELEMENT_SNIPPETS[facts.rootKind] || {};
    for (const [tag, body] of Object.entries(snippets)) {
      fallback.suggestions.push({
        label: tag,
        kind: monaco.languages.CompletionItemKind.Snippet,
        detail: `khối <${tag}> đầy đủ`,
        insertText: '<' + body,
        insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
        sortText: '0' + tag, // ahead of the field/function names, which are far more numerous
        range: wordAt,
      });
    }
    return fallback;
  }

  /// Inside `<views>`: one ready-made placement per field, with the ones not on the form
  /// yet at the top.
  ///
  /// This is the other half of declaring a field. `<field name="nguoi_de_nghi">` on its own
  /// changes nothing a user can see — the control appears only once the view places it, and
  /// nothing in the file marks the gap. Standing in the view and being shown exactly the
  /// fields that are still missing is the moment that gap is cheapest to close.
  ///
  /// The two layouts are written differently and both are produced here: a Grid/Lookup view
  /// is an ordered column list, a Dir/Filter view is a visibility mask followed by the
  /// controls on that row.
  viewPlacements(facts, range) {
    const isColumnList = facts.rootKind === 'grid' || facts.rootKind === 'lookup';
    return facts.fields.map((name) => {
      const placed = facts.viewInfo.referenced.has(name);
      return {
        label: name,
        kind: monaco.languages.CompletionItemKind.Field,
        // "chưa thấy" chứ không phải "thiếu": một view cũng được bồi thêm bằng
        // entity (&EIViews;, &ListView;), mà quét văn bản thì không nhìn thấy.
        detail: placed ? 'đã có trong <view>' : 'chưa thấy trong <view>',
        insertText: isColumnList
          ? `<field name="${name}"/>`
          : `<item value="\${1:110}: [${name}].Label, [${name}]"/>`,
        insertTextRules: isColumnList
          ? undefined
          : monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
        // Missing ones first, and ahead of the element snippets and bare tags below.
        sortText: (placed ? '0b' : '0a') + name,
        range,
      };
    });
  }

  /// The `@@macro`, `$partition$…` and `@field` placeholders inside a SQL region, or null
  /// when the caret is not on one (so the caller can carry on to tables/columns).
  ///
  /// `@field` is worth as much as the macros: inside a command, `@ma_kh` is the master
  /// record's value and `@$ten_khthue` is the value of an external form field — both are
  /// spelled exactly like the `<field name>` they come from, and both fail silently when
  /// mistyped, because SQL Server happily treats an undeclared variable as an error only
  /// at the moment FCode runs it.
  sqlPlaceholders(model, position, lineToCaret, facts) {
    const rangeFor = (typedLength) => ({
      startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
      startColumn: position.column - typedLength, endColumn: position.column,
    });

    // "$" — the period suffix, typed right after a table prefix like m81$ or d81$.
    const partitionMatch = /\$([A-Za-z$]*)$/.exec(lineToCaret);
    if (partitionMatch && !/@@?[A-Za-z0-9_$#]*$/.test(lineToCaret)) {
      const typed = '$' + partitionMatch[1];
      return {
        suggestions: FCODE_PARTITION_SUFFIXES.map(([value, doc]) => ({
          label: value,
          kind: monaco.languages.CompletionItemKind.Constant,
          detail: 'phân kỳ FastBusiness',
          documentation: doc,
          insertText: value,
          range: rangeFor(typed.length),
        })),
      };
    }

    const atMatch = /(@@?)([A-Za-z0-9_$#]*)$/.exec(lineToCaret);
    if (!atMatch) return null;
    const [, sigil, typed] = atMatch;
    const range = rangeFor(sigil.length + typed.length);

    if (sigil === '@@') {
      return {
        suggestions: FCODE_SQL_MACROS.map(([name, doc], i) => ({
          label: name,
          kind: monaco.languages.CompletionItemKind.Constant,
          detail: 'macro FastBusiness',
          documentation: doc,
          insertText: name,
          sortText: String(i).padStart(3, '0'),
          range,
        })),
      };
    }

    // A single "@": the document's own fields, both forms, plus the macros so typing the
    // second "@" is never needed to see them.
    const suggestions = [];
    for (const f of facts.fields) {
      suggestions.push({
        label: '@' + f,
        kind: monaco.languages.CompletionItemKind.Variable,
        detail: 'giá trị field trên bản ghi',
        insertText: '@' + f,
        sortText: '0' + f,
        range,
      });
      suggestions.push({
        label: '@$' + f,
        kind: monaco.languages.CompletionItemKind.Variable,
        detail: 'giá trị field external trên form',
        insertText: '@$' + f,
        sortText: '1' + f,
        range,
      });
    }
    for (const [name, doc] of FCODE_SQL_MACROS) {
      suggestions.push({
        label: name,
        kind: monaco.languages.CompletionItemKind.Constant,
        detail: 'macro FastBusiness',
        documentation: doc,
        insertText: name,
        sortText: '2' + name,
        range,
      });
    }
    return { suggestions };
  }

  /// Field names and function names declared anywhere in this document. Useful in two
  /// places for the same reason: FCode's JavaScript refers to fields by the exact string
  /// in `<field name="...">`, and a mistyped one is silent until runtime.
  documentIdentifiers(model, position, facts) {
    const lineToCaret = model.getValueInRange({
      startLineNumber: position.lineNumber, startColumn: 1,
      endLineNumber: position.lineNumber, endColumn: position.column,
    });

    const quoted = (typedLength) => ({
      startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
      startColumn: position.column - typedLength, endColumn: position.column,
    });
    const items = (list, kind, detail, range, insert) => ({
      suggestions: list.map((v) => ({
        label: v,
        kind: monaco.languages.CompletionItemKind[kind],
        detail,
        insertText: insert ? insert(v) : v,
        range,
      })),
    });

    // g.$a.<alias> — the expression aliases every grid script is written in. They come from
    // an entity, so there is no declaration in this file to look at; the names already in
    // use are the only reference there is.
    const aliasMatch = /\$a\.([A-Za-z0-9_$]*)$/.exec(lineToCaret);
    if (aliasMatch) {
      return items(facts.aliases, 'Constant', 'biểu thức $a đang dùng trong file', quoted(aliasMatch[1].length));
    }

    // The string argument of a call that names something declared elsewhere in the file.
    // Each of these is a name that must match exactly and fails silently when it doesn't:
    // a wrong action id just never answers, a wrong field name returns undefined.
    const call = /([A-Za-z0-9_$]+)\s*\(\s*(?:[^()]*,\s*)?'([A-Za-z0-9_$%.]*)$/.exec(lineToCaret);
    if (call) {
      const [, fn, typed] = call;
      const range = quoted(typed.length);
      if (fn === 'request') {
        // f.request('Ctx', 'ActionId', [...]) / g.request(o, 'Ctx', 'ActionId', [...]) —
        // both the context and the action id are offered from <response>, since the two are
        // spelled the same in every controller here.
        return items(facts.actions, 'Method', '<action id> trong <response>', range);
      }
      if (fn === 'showForm') {
        return items(facts.controllers, 'Module', 'controller đang dùng trong file', range);
      }
      if (/^(_getColumnOrder|getItem|getItemValue|setItemValue|getItemValues|setItemValues|validFields|_getItemValue|_setItemValue|setItemControlBehavior|focus)$/.test(fn)) {
        return items(facts.fields, 'Field', '<field name> trong file', range);
      }
    }

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

  /// Candidate values for one tag+attribute pair, as [{value, detail}].
  ///
  /// Three sources, in this order of trust: what this document already uses for the same
  /// pair (the file is its own best reference), what a DIFFERENT tag in the file declares
  /// when the attribute is a cross-reference, and finally the FastBusiness vocabulary at
  /// the top of this file — which matters precisely when the value you need is the one the
  /// document does not contain yet.
  attributeValues(model, tag, attribute, facts) {
    const text = model.getValue();
    const re = new RegExp('<' + tag + '\\b[^<>]*\\s' + attribute + '="([^"]+)"', 'gi');
    const used = unique(matchAll(text, re)).map((v) => ({ value: v, detail: `${tag}.${attribute} đã dùng trong file` }));

    const add = (values, detail) => {
      const seen = new Set(used.map((u) => u.value));
      for (const v of values) if (!seen.has(v)) { used.push({ value: v, detail }); seen.add(v); }
      return used;
    };

    if (tag === 'command' && attribute === 'event') return add(FCODE_COMMAND_EVENTS, 'sự kiện FCode');
    if (tag === 'query' && attribute === 'event') return add(FCODE_QUERY_EVENTS, 'sự kiện query');
    if (tag === 'items' && attribute === 'style') return add(FCODE_ITEM_STYLES, 'kiểu điều khiển');
    if (tag === 'field' && attribute === 'type') return add(FCODE_FIELD_TYPES, 'kiểu dữ liệu');
    if (attribute === 'dataformatstring') return add(FCODE_FORMATS, 'định dạng chuẩn');
    if (tag === 'button' && attribute === 'command') return add(FCODE_TOOLBAR_COMMANDS, 'lệnh toolbar có sẵn');

    // categoryIndex -> the categories declared in this file, each labelled with its own
    // header so the number means something.
    if (attribute === 'categoryindex') {
      const seen = new Set(used.map((u) => u.value));
      for (const c of facts.categories) {
        if (seen.has(c.index)) continue;
        used.push({ value: c.index, detail: c.header ? `<category> — ${c.header}` : '<category> đã khai' });
        seen.add(c.index);
      }
      return used;
    }

    // Cross-references that live under a different tag than the one being typed.
    if (attribute === 'field' || attribute === 'fieldname') return add(facts.fields, '<field name> trong file');
    if (attribute === 'name' && tag === 'field') return add(facts.fields, '<field name> trong file');
    if (attribute === 'view' || attribute === 'viewid') return add(facts.views, '<view id> trong file');
    if (attribute === 'controller') return add(facts.controllers, 'controller đang dùng trong file');
    return used;
  }


  // ---- Layer 3: SQL schema -----------------------------------------------------------

  /// Deliberately NOT async, though the schema lookups below it are.
  ///
  /// Monaco waits on whatever a provider hands back before it can show or refresh the
  /// suggestion list, and `async` makes even an instant answer a promise — so an async
  /// provider puts every keystroke behind at least one turn of the microtask queue, whether
  /// or not it had anything to contribute. This one is registered for xml/html as well as
  /// sql (most of this project's SQL lives inside a controller), which means the caret is
  /// outside a SQL region for the overwhelming majority of keystrokes. That case now
  /// returns a plain value, so the three providers for a markup document can all resolve
  /// synchronously and the list refreshes in place instead of being torn down and rebuilt.
  ///
  /// Only the branches that genuinely need the database — "alias." and table names after
  /// FROM/JOIN — return a promise, from provideSqlFromSchema.
  provideSql(model, position) {
    if (this.regionAt(model, position) !== 'sql') return { suggestions: [] };

    const facts = docFacts(model);
    const lineToCaret = model.getValueInRange({
      startLineNumber: position.lineNumber, startColumn: 1,
      endLineNumber: position.lineNumber, endColumn: position.column,
    });

    // The FastBusiness placeholders come first and are offered regardless of the SQL
    // completion setting: they need no database connection, and they are the part of a
    // <command> that is impossible to type from memory — @@prime, $partition$current,
    // @@sysDatabaseName. Turning off table suggestions (or being off the VPN) should not
    // take these with it.
    const macros = this.sqlPlaceholders(model, position, lineToCaret, facts);
    if (macros) return macros;

    if (!this.config.sqlCompletion) return { suggestions: [] };

    return this.provideSqlFromSchema(model, position, facts, lineToCaret);
  }

  /// The half of SQL completion that may have to ask the host for schema. Split from
  /// provideSql so that only these branches cost Monaco a promise — see that method.
  async provideSqlFromSchema(model, position, facts, lineToCaret) {
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
      this.sqlTables = JSON.parse(await window.bcodeHost.call('BeginGetSqlTables'));
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
      const columns = JSON.parse(await window.bcodeHost.call('BeginGetSqlColumns', table));
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
    // still-running previous call on its side — see EditorBridge.BeginInlineCompletion.
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
      text = await window.bcodeHost.call('BeginInlineCompletion',
        prefix, suffix, this.bcode.activePath, this.regionAt(model, position));
    } catch {
      // Includes the ordinary "a newer keystroke superseded this one" rejection, which is
      // not worth distinguishing here: either way there is no ghost text to show.
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
