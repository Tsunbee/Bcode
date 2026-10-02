// A Monaco language for FCode controller XML: markup everywhere, with real JavaScript,
// T-SQL and CSS colouring inside the blocks that actually hold them.
//
// Until now these files were opened as plain 'xml', so a 400-line <script> block and a
// 60-line SQL command were both painted as XML text — every keyword, string and comment
// in the two languages people spend most of their time editing came out the same colour.
//
// This is the same technique Monaco's own HTML language uses for <script>/<style>:
// a Monarch tokenizer that hands control to another registered tokenizer via
// `nextEmbedded`, then takes it back at the closing delimiter. It is built on Monaco's
// stock XML tokenizer (vs/basic-languages/xml) with the embedding rules added, so ordinary
// markup keeps colouring exactly as it did before.
//
// Where the boundary is drawn, and why:
//
//   Embedding happens at the CDATA block, NOT at the section. An FCode <script> contains
//   a <text> element, entity references (&ScriptVoucherInit;) and several CDATA blocks
//   interleaved; handing the whole section to the JavaScript tokenizer would colour the
//   XML scaffolding and the entity refs as broken JavaScript. Embedding per CDATA keeps
//   `<text>` a tag and `&Entity;` an entity reference, which is what they are.
//
//   Which language a CDATA block gets is decided by the section it sits in —
//   <script>/<clientScript> JavaScript, <command>/<action> T-SQL, <css> CSS — except that
//   a block opening with FCode's own `/* <flatten type="Javascript"> */` marker is
//   JavaScript regardless. That marker is how <command event="Checking"> declares that it
//   holds script rather than SQL, and it is authoritative.
//
// <!ENTITY> values in the DOCTYPE: Monarch can't look ahead over a multi-line value, so the language is
// decided from the value's FIRST word (SQL keyword → sql, JS shape → javascript, anything else stays a
// string) — see ENTITY_SQL_START / entityStates below.

const FCODE_LANGUAGE_ID = 'fcode-xml';
/// Biến thể cho các file "mảnh" (Include\*.txt) bọc nguyên 1 khối CDATA ở cấp NGOÀI CÙNG, không nằm trong
/// <script>/<command>: ngôn ngữ gốc để CDATA kiểu đó là chữ trơn (xem ghi chú đầu file — không đoán được nó là
/// JS hay SQL), nên mở file thật thì không có màu dù peek (đoán theo nội dung) có màu. Hai biến thể này là
/// CÙNG tokenizer, chỉ khác: CDATA cấp ngoài cùng được giao cho JavaScript / SQL. Vẫn là ngôn ngữ tự đăng
/// ký (không phải 'javascript' của Monaco) nên không bị kiểm lỗi JS gạch đỏ lên <![CDATA[ và ]]>.
const FCODE_JS_LANGUAGE_ID = 'fcode-js';
const FCODE_SQL_LANGUAGE_ID = 'fcode-sql';

/// Same configuration Monaco ships for XML — comment toggling, brackets, auto-closing and
/// the tag-aware Enter behaviour. Copied rather than inherited because a registered
/// language gets no configuration by default, and without it Ctrl+/ stops commenting.
const FCODE_LANGUAGE_CONF = {
  comments: { blockComment: ['<!--', '-->'] },
  brackets: [['<', '>']],
  // Bracket pair colorization: never on the XML's own < >, which would paint every tag in
  // rotating colours. ( ) [ ] { } inside the embedded JS/SQL are still coloured — those
  // regions use the javascript/sql language configuration.
  colorizedBracketPairs: [],
  autoClosingPairs: [
    { open: '<', close: '>' },
    { open: "'", close: "'" },
    { open: '"', close: '"' },
  ],
  surroundingPairs: [
    { open: '<', close: '>' },
    { open: "'", close: "'" },
    { open: '"', close: '"' },
  ],
};

// <!ENTITY Name "value"> — giá trị thường là cả 1 đoạn T-SQL hoặc JavaScript (vd GetDataDefault "declare ...").
// Trước đây phần này rơi vào trạng thái @tag nên mỗi từ SQL bị tô như TÊN THUỘC TÍNH XML (1 màu cho cả đoạn).
// Monarch không nhìn trước được cả giá trị nhiều dòng, nên đoán theo TỪ ĐẦU TIÊN của giá trị (bỏ qua khoảng trắng /
// xuống dòng ngay sau dấu "): từ khoá SQL → nhúng tokenizer sql, dấu hiệu JS → javascript, còn lại (vd "Item",
// "ma_vt") vẫn là chuỗi như cũ. Kết thúc ở dấu " đóng — trong XML, " bên trong giá trị phải viết &quot; nên an toàn.
const ENTITY_SQL_START = /(?:--|\/\*(?!\s*<flatten)|(?:declare|select|insert|update|delete|exec|execute|set|with|begin|create|alter|drop|truncate|merge|print|while|union|goto|raiserror|if\s+(?:not\s+)?exists|if\s*\(\s*select|if\s*@)\b)/;
const ENTITY_JS_START = /(?:\/\/|\/\*\s*<flatten|(?:function|var|let|const|return|this|document|window|typeof|new|if|for|switch|try)\b|\$find\b|[A-Za-z_$][\w$]*\s*\(|[A-Za-z_$][\w$]*\.[A-Za-z_$])/;
const entityDeclRule = [/(<!)(ENTITY)(\s+%?\s*)(@qualifiedName)/,
  ['delimiter', 'metatag', '', { token: 'attribute.name', next: '@entityDecl' }]];
const entityStates = {
  entityDecl: [
    [/[ \t\r\n]+/, ''],
    [/SYSTEM|PUBLIC/, 'keyword'],
    [/"/, { token: 'attribute.value', switchTo: '@entityValue' }],
    [/'[^']*'/, 'attribute.value'],
    [/>/, { token: 'delimiter', next: '@pop' }],
    [/[^\s"'>]+/, ''],
  ],
  entityValue: [
    [/"/, { token: 'attribute.value', switchTo: '@entityTail' }],
    [/[ \t\r\n]+/, ''],
    [ENTITY_SQL_START, { token: '@rematch', next: '@entitySql', nextEmbedded: 'sql' }],
    [ENTITY_JS_START, { token: '@rematch', next: '@entityJs', nextEmbedded: 'javascript' }],
    [/./, { token: '@rematch', switchTo: '@entityText' }],
  ],
  // Rời vùng nhúng ở dấu " đóng: @rematch trả " lại cho entityValue (rule đầu của nó ăn dấu này).
  entitySql: [[/"/, { token: '@rematch', next: '@pop', nextEmbedded: '@pop' }], [/[^"]+/, '']],
  entityJs: [[/"/, { token: '@rematch', next: '@pop', nextEmbedded: '@pop' }], [/[^"]+/, '']],
  entityText: [
    [/[^"&]+/, 'attribute.value'],
    [/&[A-Za-z_][\w.:$-]*;/, 'string.escape'],
    [/&/, 'attribute.value'],
    [/"/, { token: 'attribute.value', switchTo: '@entityTail' }],
  ],
  entityTail: [
    [/[ \t\r\n]+/, ''],
    [/>/, { token: 'delimiter', next: '@pop' }],
    [/[^>\s]+/, ''],
  ],
};

function buildFcodeTokenizer(rootCdataLanguage = null) {
  // Rules shared by the document root and by the inside of an embedding section: both
  // contain ordinary markup, and the section bodies really do hold child elements
  // (<text>), comments and entity references.
  const markup = [
    entityDeclRule,
    [/(<)(@qualifiedName)/, [{ token: 'delimiter' }, { token: 'tag', next: '@tag' }]],
    [/(<\/)(@qualifiedName)(\s*)(>)/, [{ token: 'delimiter' }, { token: 'tag' }, '', { token: 'delimiter' }]],
    [/(<\?)(@qualifiedName)/, [{ token: 'delimiter' }, { token: 'metatag', next: '@tag' }]],
    [/(<\!)(@qualifiedName)/, [{ token: 'delimiter' }, { token: 'metatag', next: '@tag' }]],
    // Tên entity gồm chữ/số/. : $ - và dừng ở dấu ; ĐẦU TIÊN. Không dùng .+ (tham lam): trên dòng
    // `&Entity;<![CDATA[ = 0;` nó nuốt cả <![CDATA[ tới dấu ; cuối dòng nên đoạn JS/SQL phía sau mất màu.
    [/&[A-Za-z_][\w.:$-]*;/, 'string.escape'],
  ];

  // Opening a section: match the tag name, then let @tag consume its attributes. `next`
  // on the tag token is the state @tag pops back into, which is how "which language do
  // this section's CDATA blocks speak" is remembered without Monarch parameters.
  const openSection = (names, bodyState) => [
    new RegExp('(<)(' + names + ')(?=[\\s>/])'),
    [{ token: 'delimiter' }, { token: 'tag', next: '@tagOf' + bodyState }],
  ];

  // Inside a section: its own closing tag ends it; everything else is markup, with CDATA
  // routed to the embedded tokenizer for that section's language.
  const sectionBody = (names, cdataRules) => [
    [new RegExp('(</)(' + names + ')(\\s*)(>)'),
      [{ token: 'delimiter' }, { token: 'tag', next: '@pop' }, '', { token: 'delimiter' }]],
    { include: '@whitespace' },
    ...cdataRules,
    [/\]\]>/, 'delimiter.cdata'],
    ...markup,
    [/[^<&\]]+/, ''],
    [/[<&\]]/, ''],
  ];

  // The flatten marker must be tried before the plain CDATA rule, or a JavaScript command
  // would be tokenized as SQL.
  const cdataInto = (language, state) => [
    [/(<!\[CDATA\[)(\s*\/\*\s*<flatten\s+type="Javascript"\s*>\s*\*\/)/,
      [{ token: 'delimiter.cdata' },
       { token: 'comment', next: '@cdataJs', nextEmbedded: 'javascript' }]],
    [/<!\[CDATA\[/, { token: 'delimiter.cdata', next: '@' + state, nextEmbedded: language }],
  ];

  // Leaving an embedded block: @rematch hands ']]>' back to the enclosing state (which has
  // a rule to consume it) after both the state stack and the embedded tokenizer are popped.
  const embedded = [
    [/\]\]>/, { token: '@rematch', next: '@pop', nextEmbedded: '@pop' }],
    [/[^\]]+/, ''],
    [/\]/, ''],
  ];

  return {
    defaultToken: '',
    tokenPostfix: '.fcode',
    ignoreCase: true,
    qualifiedName: /(?:[\w\.\-]+:)?[\w\.\-]+/,

    tokenizer: {
      root: [
        openSection('script|clientScript', 'Js'),
        openSection('command|action', 'Sql'),
        openSection('css|style', 'Css'),
        [/[^<&]+/, ''],
        { include: '@whitespace' },
        ...markup,
        // CDATA outside any known section stays plain, as it did before — trừ biến thể fcode-js/fcode-sql,
        // nơi file được xác định (theo nội dung, lúc mở) là 1 mảnh JS/SQL bọc CDATA.
        rootCdataLanguage
          ? [/<!\[CDATA\[/, { token: 'delimiter.cdata', next: rootCdataLanguage === 'sql' ? '@cdataSql' : '@cdataJs', nextEmbedded: rootCdataLanguage }]
          : [/<!\[CDATA\[/, { token: 'delimiter.cdata', next: '@cdata' }],
      ],

      // One @tag state per section kind, each becoming that section's body at '>'.
      //
      // switchTo, not next. Monarch's `next` PUSHES a state; using it here left the stack
      // as root → tagOfX → sectionX, so the section's closing tag popped back into tagOfX
      // instead of root. Everything after the first embedding section was then read as tag
      // attributes until the next '>' pushed a second sectionX — and since the first such
      // section in a controller is <clientScript>, every later CDATA in the document,
      // including all the SQL, was being handed to the JavaScript tokenizer.
      // switchTo REPLACES tagOfX, so the stack is root → sectionX and '@pop' lands home.
      tagOfJs: [{ include: '@tagCommon' }, [/>/, { token: 'delimiter', switchTo: '@sectionJs' }]],
      tagOfSql: [{ include: '@tagCommon' }, [/>/, { token: 'delimiter', switchTo: '@sectionSql' }]],
      tagOfCss: [{ include: '@tagCommon' }, [/>/, { token: 'delimiter', switchTo: '@sectionCss' }]],
      tag: [{ include: '@tagCommon' }, [/>/, { token: 'delimiter', next: '@pop' }]],

      tagCommon: [
        [/[ \t\r\n]+/, ''],
        // <!ENTITY ...> nằm trong <!DOCTYPE x [ ... ]>, tức là đang ở trạng thái @tag của DOCTYPE.
        entityDeclRule,
        [/(@qualifiedName)(\s*=\s*)("[^"]*"|'[^']*')/, ['attribute.name', '', 'attribute.value']],
        [/(@qualifiedName)(\s*=\s*)("[^">?\/]*|'[^'>?\/]*)(?=[\?\/]\>)/, ['attribute.name', '', 'attribute.value']],
        [/(@qualifiedName)(\s*=\s*)("[^">]*|'[^'>]*)/, ['attribute.name', '', 'attribute.value']],
        [/@qualifiedName/, 'attribute.name'],
        [/\?>/, { token: 'delimiter', next: '@pop' }],
        // A self-closing section tag never opens a body, so it pops like any other tag.
        [/(\/)(>)/, [{ token: 'tag' }, { token: 'delimiter', next: '@pop' }]],
      ],

      sectionJs: sectionBody('script|clientScript', cdataInto('javascript', 'cdataJs')),
      sectionSql: sectionBody('command|action', cdataInto('sql', 'cdataSql')),
      sectionCss: sectionBody('css|style', cdataInto('css', 'cdataCss')),

      ...entityStates,

      cdataJs: embedded,
      cdataSql: embedded,
      cdataCss: embedded,

      cdata: [
        [/[^\]]+/, ''],
        [/\]\]>/, { token: 'delimiter.cdata', next: '@pop' }],
        [/\]/, ''],
      ],

      whitespace: [
        [/[ \t\r\n]+/, ''],
        [/<!--/, { token: 'comment', next: '@comment' }],
      ],

      comment: [
        [/[^<\-]+/, 'comment.content'],
        [/-->/, { token: 'comment', next: '@pop' }],
        [/<!--/, 'comment.content.invalid'],
        [/[<\-]/, 'comment.content'],
      ],
    },
  };
}

/// The languages this one embeds. They have to be LOADED, not merely registered, before
/// the first tokenization that reaches a `nextEmbedded` for them.
///
/// Monaco loads vs/basic-languages/* lazily, on first use of a model in that language.
/// Nothing here ever creates a sql or css model, so without this they were still absent
/// when the tokenizer asked for them — and an unavailable embedded language does not fall
/// back to the outer one, it leaves whichever embedded language was entered last in
/// effect. The observable result was every SQL and CSS block coming out tokenized as
/// JavaScript, because the first CDATA in the document happened to be a JS one.
///
/// monaco.editor.colorize resolves only once the language's tokenization support exists,
/// which makes it the simplest reliable way to force the load.
const EMBEDDED_LANGUAGES = ['javascript', 'sql', 'css'];

async function preloadEmbeddedLanguages() {
  await Promise.all(EMBEDDED_LANGUAGES.map(async (id) => {
    try { await monaco.editor.colorize('', id, {}); } catch { /* one missing language just loses its colour */ }
  }));
}

/// Called once, from index.html, before the first model is created — a model created with
/// an unregistered language id silently falls back to plaintext.
async function registerFcodeLanguage() {
  if (!window.monaco || !monaco.languages) return false;
  if (monaco.languages.getLanguages().some((l) => l.id === FCODE_LANGUAGE_ID)) return true;

  try {
    await preloadEmbeddedLanguages();
    for (const [id, alias, rootLanguage] of [
      [FCODE_LANGUAGE_ID, 'FCode XML', null],
      [FCODE_JS_LANGUAGE_ID, 'FCode XML (JS include)', 'javascript'],
      [FCODE_SQL_LANGUAGE_ID, 'FCode XML (SQL include)', 'sql'],
    ]) {
      monaco.languages.register({ id, aliases: [alias] });
      monaco.languages.setLanguageConfiguration(id, FCODE_LANGUAGE_CONF);
      monaco.languages.setMonarchTokensProvider(id, buildFcodeTokenizer(rootLanguage));
    }
    return true;
  } catch (e) {
    // A broken tokenizer must not take the editor down with it: falling back to plain
    // 'xml' loses the embedded colouring and nothing else (detectLanguage checks that the
    // language actually registered).
    if (window.bcodeShowPageError) {
      window.bcodeShowPageError('Không đăng ký được ngôn ngữ FCode XML, dùng tạm XML thường: ' + e);
    }
    return false;
  }
}

/// Chọn biến thể cho 1 file .txt theo NỘI DUNG: bắt đầu bằng <![CDATA[ rồi là JavaScript/SQL → biến thể tương
/// ứng; còn lại (XML thường, mảnh <fields>...) giữ ngôn ngữ FCode gốc. Chỉ đọc ~4000 ký tự đầu.
function detectFcodeVariantLanguage(content) {
  if (!content) return FCODE_LANGUAGE_ID;
  const open = /^\uFEFF?\s*<!\[CDATA\[/.exec(content);
  if (!open) return FCODE_LANGUAGE_ID;
  const body = content.slice(open[0].length, open[0].length + 4000);
  if (/<flatten\s+type="Javascript"/i.test(body)) return FCODE_JS_LANGUAGE_ID;
  if (typeof looksLikeSql === 'function' && looksLikeSql(body)) return FCODE_SQL_LANGUAGE_ID;
  if (typeof JS_HINT_RE !== 'undefined' && JS_HINT_RE.test(body)) return FCODE_JS_LANGUAGE_ID;
  if (/\bfunction\b|\bvar\s+[\w$]+\s*=|\bif\s*\(|=>/.test(body)) return FCODE_JS_LANGUAGE_ID;
  return FCODE_LANGUAGE_ID;
}

window.FCODE_LANGUAGE_ID = FCODE_LANGUAGE_ID;
window.FCODE_JS_LANGUAGE_ID = FCODE_JS_LANGUAGE_ID;
window.FCODE_SQL_LANGUAGE_ID = FCODE_SQL_LANGUAGE_ID;
window.detectFcodeVariantLanguage = detectFcodeVariantLanguage;
window.registerFcodeLanguage = registerFcodeLanguage;
