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

  /// Fetched before the editor is constructed (see index.html) so the first paint is
  /// already in the right colours — defining the theme afterwards works too, but shows a
  /// dark flash when the chosen theme is Light+.
  async init() {
    let loaded = null;
    try {
      const raw = this.host ? await this.host.GetTheme() : null;
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

    if (window.monaco && monaco.editor) {
      monaco.editor.defineTheme(this.monacoThemeName, this.buildMonacoTheme(theme));
      monaco.editor.setTheme(this.monacoThemeName);
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

    return {
      base: theme.monacoBase || 'vs-dark',
      // inherit: true — the rules below cover the token types this codebase's files
      // actually produce (XML tags/attributes, SQL keywords, JS), not the hundreds Monaco
      // knows about. Without inheriting, everything unlisted would render as plain
      // foreground and the editor would look flatter, not differently themed.
      inherit: true,
      rules: (theme.rules || []).map((r) => {
        const rule = { token: r.token, foreground: r.foreground };
        if (r.fontStyle) rule.fontStyle = r.fontStyle;
        return rule;
      }),
      colors: editorColors,
    };
  }
}

window.bcodeTheme = new BcodeTheme();
