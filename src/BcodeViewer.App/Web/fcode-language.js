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
// Not covered: the SQL and JavaScript held in `<!ENTITY>` values in the DOCTYPE. Monarch
// is a line-at-a-time state machine with no way to look ahead over a multi-line entity
// value and decide which of the two it is, and guessing wrong there would be worse than
// leaving it as a string. The completion side does classify them correctly (it can read
// the whole document), so suggestions are right in entity values even though the colours
// are not.

const FCODE_LANGUAGE_ID = 'fcode-xml';

/// Same configuration Monaco ships for XML — comment toggling, brackets, auto-closing and
/// the tag-aware Enter behaviour. Copied rather than inherited because a registered
/// language gets no configuration by default, and without it Ctrl+/ stops commenting.
const FCODE_LANGUAGE_CONF = {
  comments: { blockComment: ['<!--', '-->'] },
  brackets: [['<', '>']],
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

function buildFcodeTokenizer() {
  // Rules shared by the document root and by the inside of an embedding section: both
  // contain ordinary markup, and the section bodies really do hold child elements
  // (<text>), comments and entity references.
  const markup = [
    [/(<)(@qualifiedName)/, [{ token: 'delimiter' }, { token: 'tag', next: '@tag' }]],
    [/(<\/)(@qualifiedName)(\s*)(>)/, [{ token: 'delimiter' }, { token: 'tag' }, '', { token: 'delimiter' }]],
    [/(<\?)(@qualifiedName)/, [{ token: 'delimiter' }, { token: 'metatag', next: '@tag' }]],
    [/(<\!)(@qualifiedName)/, [{ token: 'delimiter' }, { token: 'metatag', next: '@tag' }]],
    [/&\w+;/, 'string.escape'],
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
        // CDATA outside any known section stays plain, as it did before.
        [/<!\[CDATA\[/, { token: 'delimiter.cdata', next: '@cdata' }],
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
    monaco.languages.register({ id: FCODE_LANGUAGE_ID, aliases: ['FCode XML'] });
    monaco.languages.setLanguageConfiguration(FCODE_LANGUAGE_ID, FCODE_LANGUAGE_CONF);
    monaco.languages.setMonarchTokensProvider(FCODE_LANGUAGE_ID, buildFcodeTokenizer());
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

window.FCODE_LANGUAGE_ID = FCODE_LANGUAGE_ID;
window.registerFcodeLanguage = registerFcodeLanguage;
