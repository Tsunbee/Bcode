// Theming for the WebView2 half of BcodeViewer.
//
// The palette is NOT defined here — it comes from the host (see UI/ThemeCatalog.cs, served
// by EditorBridge.GetTheme). That direction matters: the WinForms chrome and this page have
// to agree exactly, and the only way to guarantee that is one definition with one consumer
// on each side. A second copy of the hex values in CSS is how the tree ends up #252526
// while the editor next to it is Monokai.
//
// Two things happen on apply:
//   1. every colour lands as a CSS custom property on :root, which style.css is written
//      entirely in terms of (no literal hex outside the fallbacks there);
//   2. Monaco gets a defineTheme() built from the same palette plus the theme's token rules.

const CSS_VARS = {
  background: '--bc-bg',
  panel: '--bc-panel',
  panelAlt: '--bc-panel-alt',
  border: '--bc-border',
  text: '--bc-text',
  textMuted: '--bc-text-muted',
  accent: '--bc-accent',
  accentHover: '--bc-accent-hover',
  accentText: '--bc-accent-text',
  selection: '--bc-selection',
  input: '--bc-input',
  buttonBack: '--bc-button',
  dirtyMarker: '--bc-dirty',
};

const FALLBACK = {
  id: 'dark-plus',
  isDark: true,
  monacoBase: 'vs-dark',
  colors: {
    background: '#1e1e1e', panel: '#252526', panelAlt: '#2d2d30', border: '#3f3f46',
    text: '#d4d4d4', textMuted: '#969696', accent: '#0e639c', accentHover: '#1177bb',
    accentText: '#ffffff', selection: '#094771', input: '#3c3c3c', buttonBack: '#3e3e42',
  },
  editor: {},
  rules: [],
};

/// Tỉ lệ tương phản WCAG giữa 2 màu #rgb / #rrggbb / #rrggbbaa (bỏ qua alpha); màu không đọc được → 0.
function contrastRatio(a, b) {
  const lum = (hex) => {
    let h = String(hex || '').trim().replace('#', '');
    if (h.length === 3 || h.length === 4) h = h.slice(0, 3).split('').map((c) => c + c).join('');
    if (!/^[0-9a-f]{6}/i.test(h)) return null;
    const ch = [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16) / 255).map((c) => (c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4)));
    return 0.2126 * ch[0] + 0.7152 * ch[1] + 0.0722 * ch[2];
  };
  const x = lum(a), y = lum(b);
  if (x === null || y === null) return 0;
  return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
}

class BcodeTheme {
  constructor() {
    this.theme = FALLBACK;
  }

  get host() {
    return window.chrome && window.chrome.webview
      ? window.chrome.webview.hostObjects.host
      : null;
  }

  /// Name of the Monaco theme this page defines. One name reused across switches rather
  /// than one per theme id: defineTheme() overwrites by name, so re-defining the same name
  /// and calling setTheme is what makes a live switch actually repaint the editor.
  get monacoThemeName() {
    return 'bcode-theme';
  }

  /// Editor font from Settings ("Phông chữ editor"), carried in GetTheme so it is known before
  /// the first editor is created. Every Monaco instance in the page reads it from here.
  get fontFamily() {
    return (this.theme && this.theme.editorFont) || "'Roboto', Consolas, monospace";
  }

  /// Indent guides (the vertical lines at each indentation level) — off for an Fcode theme in
  /// "fcode" style, since FcodeViewer draws none.
  get guideOptions() {
    // Chế độ nhẹ (editor.js applyLiteMode) tắt luôn đường gióng thụt lề.
    return { guides: { indentation: !window.bcodeLiteActive && !(this.theme && this.theme.indentGuides === false) } };
  }

  /// Fetched before the editor is constructed (see index.html) so the first paint is
  /// already in the right colours — defining the theme afterwards works too, but shows a
  /// dark flash when the chosen theme is Light+.
  /// Bắt đầu hỏi host theme NGAY (index.html gọi trước khi Monaco tải xong) để vòng WebView2 chạy song song với việc tải Monaco 3,6MB;
  /// init() dùng lại kết quả này.
  prefetch() {
    if (!this._rawPromise && this.host) this._rawPromise = Promise.resolve().then(() => this.host.GetTheme()).catch(() => null);
  }

  async init() {
    let loaded = null;
    try {
      // Kết quả prefetch chỉ dùng cho lần init() đầu tiên lúc khởi động; mọi lần sau (đổi theme → reloadTheme(), Settings) phải hỏi host lại,
      // nếu không sẽ áp lại theme cũ.
      const early = this._rawPromise;
      this._rawPromise = null;
      const raw = early ? await early : (this.host ? await this.host.GetTheme() : null);
      if (raw) loaded = JSON.parse(raw);
    } catch {
      loaded = null; // host unreachable (or the page opened outside WebView2) — use the default
    }
    this.apply(loaded || FALLBACK);
  }

  apply(theme) {
    this.theme = theme;
    const colors = theme.colors || FALLBACK.colors;

    const root = document.documentElement;
    for (const [key, cssVar] of Object.entries(CSS_VARS)) {
      if (colors[key]) root.style.setProperty(cssVar, colors[key]);
    }
    // Lets CSS branch on light vs dark for the few things a palette can't express —
    // shadow strength, for instance, which has to be near-invisible on dark and pronounced
    // on light to read as the same depth.
    root.setAttribute('data-theme', theme.isDark ? 'dark' : 'light');
    root.setAttribute('data-theme-id', theme.id || 'dark-plus');

    // Fcode XML theme: CDATA / <!ENTITY> values are colored by its <KeywordStart> lists (fcode-language.js).
    if (window.setFcodeLexer) window.setFcodeLexer(theme.fcodeLexer || null);

    if (window.monaco && monaco.editor) {
      monaco.editor.defineTheme(this.monacoThemeName, this.buildMonacoTheme(theme));
      monaco.editor.setTheme(this.monacoThemeName);
      // Font may have changed in Settings — push it to every open editor (main, split, dialogs).
      const font = this.fontFamily;
      for (const ed of (monaco.editor.getEditors ? monaco.editor.getEditors() : [])) {
        if (ed.getOption(monaco.editor.EditorOption.fontFamily) !== font) ed.updateOptions({ fontFamily: font });
        ed.updateOptions(this.guideOptions);
      }
      // Editors created later (split view, dialogs) follow the same indent-guide setting.
      if (!this._createHooked && monaco.editor.onDidCreateEditor) {
        this._createHooked = true;
        monaco.editor.onDidCreateEditor((ed) => ed.updateOptions(this.guideOptions));
      }
      if (document.fonts && document.fonts.ready) document.fonts.ready.then(() => monaco.editor.remeasureFonts());
    }
  }

  buildMonacoTheme(theme) {
    const colors = theme.colors || FALLBACK.colors;
    const editor = theme.editor || {};

    // Only assign the keys the theme actually specified. A null here is meaningfully
    // different from a colour: it means "whatever the base theme does", and writing an
    // explicit value would override a base that already had a better answer (Monaco's
    // hc-black, in particular, tunes a lot of these for contrast).
    const editorColors = {};
    const set = (key, value) => { if (value) editorColors[key] = value; };

    set('editor.background', colors.background);
    set('editor.foreground', colors.text);
    set('editorLineNumber.foreground', editor.lineNumber || colors.textMuted);
    set('editorLineNumber.activeForeground', colors.text);
    set('editor.lineHighlightBackground', editor.lineHighlight);
    set('editor.selectionBackground', editor.selection);
    set('editorCursor.foreground', colors.text);
    set('editorIndentGuide.background1', colors.border);
    set('editorWhitespace.foreground', colors.border);

    // The floating widgets — suggestion list, hover, find box. Left at the base theme's
    // values these stay vs-dark grey while the editor behind them is Solarized, which is
    // exactly where a half-applied theme is most visible, because the suggest widget is
    // the thing this editor now puts on screen constantly.
    set('editorWidget.background', colors.panel);
    set('editorWidget.border', colors.border);
    set('editorWidget.foreground', colors.text);
    set('editorSuggestWidget.background', colors.panel);
    set('editorSuggestWidget.border', colors.border);
    set('editorSuggestWidget.foreground', colors.text);
    set('editorSuggestWidget.selectedBackground', colors.selection);
    set('editorSuggestWidget.highlightForeground', colors.accentHover);
    set('editorHoverWidget.background', colors.panel);
    set('editorHoverWidget.border', colors.border);
    set('input.background', colors.input);
    set('input.foreground', colors.text);
    set('input.border', colors.border);
    set('focusBorder', colors.accent);
    set('minimap.background', colors.background);
    set('scrollbarSlider.background', colors.border);
    set('scrollbarSlider.hoverBackground', colors.textMuted);
    set('editorGhostText.foreground', colors.textMuted);

    // A theme imported from VSCode carries its own workbench colors (suggest/hover widgets,
    // scrollbars, indent guides, bracket pair colors...) — they win over the approximations
    // above, which is what makes it look like the same theme as in VSCode.
    for (const [key, value] of Object.entries(theme.monacoColors || {})) set(key, value);

    // Đánh dấu chỗ trùng tên (double-click / đứng trên một tên → mọi chỗ cùng tên): đè lên cả theme nhập từ VS Code.
    this.applyMatchHighlight(theme, set);
    // Ngoặc tương ứng (đứng cạnh / bấm vào ( [ { → tô cặp của nó): màu gốc của Monaco và của đa số theme nhập quá nhạt.
    this.applyBracketMatch(theme, editorColors);

    // Dấu mở/đóng CDATA (<![CDATA[ và ]]>): theme Fcode tô cùng màu với chuỗi; các theme khác không có luật riêng nên để trắng như chữ thường.
    // Gán theo màu "string" của chính theme (hoặc màu chuỗi mặc định của Dark+/Light+) trừ khi theme tự khai báo delimiter.cdata.
    const rules = (theme.rules || []).map((r) => {
      const rule = { token: r.token, foreground: r.foreground };
      if (r.fontStyle) rule.fontStyle = r.fontStyle;
      return rule;
    });
    this.applyFcodeAliasRules(rules);
    if (!rules.some((r) => r.token === 'delimiter.cdata')) {
      const strRule = rules.find((r) => r.token === 'string' || r.token === 'attribute.value');
      rules.push({ token: 'delimiter.cdata', foreground: ((strRule && strRule.foreground) || (theme.isDark ? 'CE9178' : 'A31515')).replace('#', '') });
    }

    return {
      base: theme.monacoBase || 'vs-dark',
      // inherit: true — the rules below cover the token types this codebase's files
      // actually produce (XML tags/attributes, SQL keywords, JS), not the hundreds Monaco
      // knows about. Without inheriting, everything unlisted would render as plain
      // foreground and the editor would look flatter, not differently themed.
      inherit: true,
      rules,
      colors: editorColors,
    };
  }

  /// Token mà fcode-language.js đặt tên khác đi chỉ để Monaco biết đó là chú thích / chuỗi / regex (bỏ qua ngoặc bên trong khi ghép cặp),
  /// màu phải y như trước: chú thích "//" khớp mục KeywordStart kwN (comment.kwN.fcdata = màu kwN.fcdata), regex JS trong theme Fcode
  /// (regex.fcdata = chữ thường), cụm ]]>&Entity;<![CDATA[ đọc xuyên trong vùng nhúng (delimiter.cdata.string / string.escape.entity =
  /// màu delimiter.cdata / string.escape mà phần XML vẫn dùng). Theme không khai báo các màu gốc thì không thêm gì.
  applyFcodeAliasRules(rules) {
    // Cùng 1 token có thể khai 2 lần (phần theme VS Code + phần lexer Fcode): Monaco dùng luật SAU CÙNG, nên lấy luật cuối.
    const find = (token) => { for (let i = rules.length - 1; i >= 0; i--) if (rules[i].token === token) return rules[i]; return null; };
    const alias = (from, to) => {
      const r = find(from);
      if (r && !find(to)) rules.push(Object.assign({}, r, { token: to }));
    };
    for (const r of rules.slice()) {
      const m = /^(kw\d+)\.fcdata$/.exec(r.token || '');
      if (m) alias(r.token, `comment.${m[1]}.fcdata`);
    }
    alias('text.fcdata', 'regex.fcdata');
    alias('text.fcdata', 'comment.text.fcdata');   // begin tran, @end... (SQL không mở/đóng khối, xem fcode-language.js SQL_WORD_BRACKETS)
    alias('delimiter.cdata.fcode', 'delimiter.cdata.string');
    alias('string.escape.fcode', 'string.escape.entity');
  }

  /// Ngoặc tương ứng (matchBrackets): nền + viền rõ trên mọi theme. Nền luôn là màu riêng (xanh lá, khác màu cam của "Đánh dấu từ trùng"
  /// và màu xanh dương của vùng chọn) vì nền mặc định của Monaco / VS Code (#0064001a) gần như không thấy. Viền: giữ màu theme khai báo
  /// (vd màu Brace của theme Fcode) nếu đủ tương phản với nền editor, không thì dùng màu riêng — theme Fcode sáng có Brace trắng trên nền trắng.
  applyBracketMatch(theme, editorColors) {
    const isDark = theme.isDark !== false;
    const bg = editorColors['editor.background'] || (theme.colors && theme.colors.background) || (isDark ? '#1e1e1e' : '#ffffff');
    const own = isDark ? '#3fb950' : '#1a7f37';
    const themed = editorColors['editorBracketMatch.border'];
    editorColors['editorBracketMatch.background'] = own + (isDark ? '59' : '40');   // ~35% tối / 25% sáng: chữ vẫn đọc được
    editorColors['editorBracketMatch.border'] = themed && contrastRatio(themed, bg) >= 3 ? themed : own;
  }

  /// Màu nền + viền của các chỗ trùng tên với từ đang chọn (selectionHighlight, khi double-click) hoặc từ con trỏ đang đứng
  /// (wordHighlight), kèm vạch trên thanh cuộn để biết các chỗ trùng nằm đâu trong file. Màu gốc của Monaco (#add6ff26…) quá nhạt.
  /// Nền chỉ phủ ~30% (Monaco vẽ dưới chữ) nên chữ vẫn đọc được; viền đậm làm chỗ trùng nổi rõ ở mọi theme.
  /// Settings "Đánh dấu từ trùng": "" = vàng cam (mặc định), "theme" = để theme tự tô như cũ, "#rrggbb" = màu tự chọn.
  applyMatchHighlight(theme, set) {
    const pref = String(theme.matchHighlight || '').trim().toLowerCase();
    if (pref === 'theme') return;
    const base = /^#[0-9a-f]{6}$/.test(pref) ? pref : (theme.isDark ? '#ffb000' : '#e08e00');
    const fill = base + (theme.isDark ? '4d' : '5c'); // ~30% tối / ~36% sáng
    const line = base + 'e6';
    for (const k of ['selectionHighlight', 'wordHighlight', 'wordHighlightStrong', 'wordHighlightText']) {
      set(`editor.${k}Background`, fill);
      set(`editor.${k}Border`, line);
      set(`editorOverviewRuler.${k}Foreground`, line);
    }
  }
}

window.bcodeTheme = new BcodeTheme();
