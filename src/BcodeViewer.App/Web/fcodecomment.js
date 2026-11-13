// Comment / bỏ comment (Ctrl+/, nút Comment) cho file FCode XML — chọn KIỂU chú thích theo vùng con trỏ đứng.
//
// Monaco tự chọn kiểu theo ngôn ngữ ở cột 1 của dòng đầu: trong CDATA SQL ra "--", CDATA JS ra "//", theme Fcode (CDATA là
// ngôn ngữ fcode-cdata) ra "--" cả với JS, còn dòng &Entity; trong <script><text> (bung ra JS) lại ra <!-- -->. Ở đây:
//   • VÙNG CODE → /* ... */ (dùng được cho SQL, JS lẫn CSS):
//       - trong <![CDATA[ ... ]]> (mọi CDATA, kể cả CDATA cấp ngoài cùng của file include .txt);
//       - chữ nằm ngoài CDATA nhưng trong <text> (hoặc thẳng trong thẻ) của <script>/<clientScript>/<command>/<action>/
//         <processing>/<css>/<style> — vd dòng &GridExtraToolbarScript; hay `]]>&Entity;<![CDATA[` giữa 2 khối CDATA:
//         Fast nối hết chữ của <text> thành 1 đoạn code, nên entity ở đó bung ra JS/SQL;
//       - giá trị <!ENTITY Ten "..."> trong DOCTYPE;
//       - chữ ở cấp ngoài cùng của file include KHÔNG có thẻ gốc (mảnh JS/SQL trơn, vd Include\Javascript\*.txt).
//   • CÒN LẠI (thẻ XML, &Entity; nằm giữa các thẻ — vd &XMLGetTaxRate; trong <response> bung ra <action>...</action>)
//     → <!-- ... -->.
// Comment cả dòng (từ ký tự khác trắng đầu tiên của dòng đầu tới ký tự cuối của dòng cuối), như lệnh Toggle Line Comment.
// Vùng chọn vắt qua ranh giới CDATA: bỏ qua phần đầu/cuối chỉ gồm <![CDATA[ / ]]> / khoảng trắng; còn lẫn thẻ XML thì
// không đặt được 1 cặp chú thích hợp lệ → báo, không sửa.

const FCC_CODE_SECTION = /^(?:script|clientscript|command|action|processing|css|style)$/i;
const FCC_SECTION_LANG = { script: 'js', clientscript: 'js', command: 'sql', action: 'sql', processing: 'sql', css: 'css', style: 'css' };

/// Quét cả văn bản → { ranges: [[start, end, lang]] } các vùng code (offset, end không tính), đã sắp theo start.
/// rootLang: ngôn ngữ của CDATA/chữ cấp ngoài cùng (biến thể fcode-js / fcode-sql) hoặc null.
function fcodeScanCodeRanges(text, rootLang) {
  const n = text.length;
  const ranges = [];
  const rootText = [];          // chữ cấp ngoài cùng (ngoài mọi thẻ) — là code chỉ khi file không có thẻ gốc nào
  let hasRootElement = false;
  const stack = [];
  const add = (list, s, e, lang) => {
    if (e <= s) return;
    const last = list[list.length - 1];
    if (last && last[1] === s && last[2] === lang) last[1] = e;
    else list.push([s, e, lang]);
  };
  const sectionLang = () => {
    for (let k = stack.length - 1; k >= 0; k--) if (FCC_CODE_SECTION.test(stack[k])) return FCC_SECTION_LANG[stack[k].toLowerCase()];
    return null;
  };
  // Chữ ở vị trí hiện tại có phải code: trong <text> (hoặc thẳng trong thẻ) của 1 section code.
  const textLang = () => {
    const top = stack[stack.length - 1];
    if (!(top.toLowerCase() === 'text' || FCC_CODE_SECTION.test(top))) return undefined;
    const lang = sectionLang();
    return lang === null ? undefined : lang;
  };
  const skipTo = (from, token) => { const e = text.indexOf(token, from); return e < 0 ? n : e + token.length; };
  const nameAt = (from) => { const m = /^[\w.:-]+/.exec(text.slice(from, from + 200)); return m ? m[0] : ''; };

  // <!DOCTYPE x [ ... ]>: bỏ qua, chỉ lấy phần giữa 2 dấu nháy của <!ENTITY ...> làm vùng code.
  const scanDoctype = (i) => {
    let inEntity = false;
    let condDepth = 0;            // khối điều kiện <![%X;[ ... ]]> lồng trong DOCTYPE
    while (i < n) {
      const c = text[i];
      if (text.startsWith('<!--', i)) { i = skipTo(i + 4, '-->'); continue; }
      if (!inEntity && text.startsWith('<![', i)) { condDepth++; i = skipTo(i + 3, '['); continue; }
      if (!inEntity && condDepth > 0 && text.startsWith(']]>', i)) { condDepth--; i += 3; continue; }
      if (text.startsWith('<!ENTITY', i)) { inEntity = true; i += 8; continue; }
      if (c === '"' || c === "'") {
        const e = text.indexOf(c, i + 1);
        const end = e < 0 ? n : e;
        if (inEntity) add(ranges, i + 1, end, null);
        i = end + 1;
        continue;
      }
      if (c === '>') {
        if (!inEntity) return i + 1;      // hết DOCTYPE (không có / sau phần [ ... ])
        inEntity = false; i++; continue;
      }
      if (c === ']' && !inEntity) {
        // ']' rồi khoảng trắng rồi '>' = hết phần khai báo trong DOCTYPE
        const m = /^\]\s*>/.exec(text.slice(i, i + 50));
        if (m) return i + m[0].length;
      }
      i++;
    }
    return n;
  };

  let i = 0;
  while (i < n) {
    if (text[i] === '<') {
      if (text.startsWith('<![CDATA[', i)) {
        const s = i + 9;
        let e = text.indexOf(']]>', s);
        if (e < 0) e = n;
        let lang = stack.length ? sectionLang() : rootLang;
        if (/^\s*\/\*\s*<flatten\s+type="Javascript"/i.test(text.slice(s, s + 80))) lang = 'js';
        add(ranges, s, e, lang);
        i = e + 3;
        continue;
      }
      if (text.startsWith('<!--', i)) { i = skipTo(i + 4, '-->'); continue; }
      if (/^<!DOCTYPE/i.test(text.slice(i, i + 9))) { i = scanDoctype(i + 9); continue; }
      if (text.startsWith('<![', i)) { i = skipTo(i + 3, '['); continue; }   // mở khối điều kiện <![%X;[
      if (text.startsWith('<?', i)) { i = skipTo(i + 2, '?>'); continue; }
      if (text.startsWith('<!', i)) { i = skipTo(i + 2, '>'); continue; }
      if (text.startsWith('</', i)) {
        const name = nameAt(i + 2).toLowerCase();
        for (let k = stack.length - 1; k >= 0; k--) {
          if (stack[k].toLowerCase() === name) { stack.length = k; break; }
        }
        i = skipTo(i + 2, '>');
        continue;
      }
      const name = nameAt(i + 1);
      if (name) {
        // Thuộc tính có thể chứa '>' trong nháy → đi qua từng nháy.
        let j = i + 1 + name.length;
        let selfClose = false;
        while (j < n) {
          const c = text[j];
          if (c === '"' || c === "'") { const e = text.indexOf(c, j + 1); j = e < 0 ? n : e + 1; continue; }
          if (c === '>') { selfClose = text[j - 1] === '/'; j++; break; }
          j++;
        }
        if (!stack.length) hasRootElement = true;
        if (!selfClose) stack.push(name);
        i = j;
        continue;
      }
      // '<' lẻ (không phải thẻ) — coi như chữ
    }
    if (text.startsWith(']]>', i)) { i += 3; continue; }   // đóng khối điều kiện
    let e = text.indexOf('<', i + 1);
    if (e < 0) e = n;
    const close = text.indexOf(']]>', i);
    if (close >= 0 && close < e) e = close;
    if (!stack.length) add(rootText, i, e, rootLang);
    else {
      const lang = textLang();
      if (lang !== undefined) add(ranges, i, e, lang);
    }
    i = e;
  }

  if (!hasRootElement && rootText.length) {
    for (const r of rootText) ranges.push(r);
    ranges.sort((a, b) => a[0] - b[0]);
  }
  return { ranges };
}

/// Vùng code chứa offset (hoặc null).
function fcodeCodeRangeAt(ranges, offset) {
  let lo = 0, hi = ranges.length - 1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    const r = ranges[mid];
    if (offset < r[0]) hi = mid - 1;
    else if (offset >= r[1]) lo = mid + 1;
    else return r;
  }
  return null;
}

const FCC_STYLES = {
  code: { open: '/*', close: '*/' },
  xml: { open: '<!--', close: '-->' },
};

/// Tính các sửa đổi cho 1 vùng chọn (theo dòng). text = cả nội dung model, lineStarts = offset đầu mỗi dòng (0-based),
/// lines = mảng chuỗi dòng. Trả { edits: [{start, end, text}] } (offset trên text gốc) hoặc { error }.
function fcodeCommentPlan(text, lines, lineStarts, scan, startLine, endLine) {
  // Dòng (0-based) có chữ trong khoảng; bỏ dòng trắng ở 2 đầu.
  const firstNonWs = (ln) => { const m = /\S/.exec(lines[ln]); return m ? m.index : -1; };
  let a = startLine, b = endLine;
  while (a <= b && firstNonWs(a) < 0) a++;
  while (b >= a && firstNonWs(b) < 0) b--;
  if (a > b) return { edits: [] };

  let s = lineStarts[a] + firstNonWs(a);                    // ký tự đầu (tính)
  let e = lineStarts[b] + lines[b].replace(/\s+$/, '').length; // sau ký tự cuối (không tính)

  const isCode = (off) => !!fcodeCodeRangeAt(scan.ranges, off);
  let kind;
  if (isCode(s) && isCode(e - 1)) kind = 'code';
  else if (!isCode(s) && !isCode(e - 1)) kind = 'xml';
  else {
    // Vắt qua ranh giới CDATA: co 2 đầu vào trong vùng code, chỉ được bỏ qua <![CDATA[ / ]]> / khoảng trắng.
    const skipMarkers = /^(?:\s|<!\[CDATA\[|\]\]>)*/;
    if (!isCode(s)) {
      const m = skipMarkers.exec(text.slice(s, e));
      s += m[0].length;
    }
    if (!isCode(e - 1)) {
      const rev = text.slice(s, e);
      const m = /(?:\s|<!\[CDATA\[|\]\]>)*$/.exec(rev);
      e -= m[0].length;
    }
    if (s >= e || !isCode(s) || !isCode(e - 1)) {
      return { error: 'Vùng chọn vắt qua ranh giới CDATA và lẫn thẻ XML — chọn trọn trong CDATA (comment /* */) hoặc chọn trọn cả khối thẻ (comment <!-- -->).' };
    }
    kind = 'code';
  }

  if (kind === 'code') {
    // Giữa 2 đầu chỉ được có code (hoặc dấu <![CDATA[ / ]]> nối các khối của cùng 1 <text>), không được có thẻ XML:
    // /* ở <action> này mà */ ở <action> khác thì cả 2 đoạn SQL đều hỏng.
    let k = s;
    while (k < e) {
      const r = fcodeCodeRangeAt(scan.ranges, k);
      if (r) { k = r[1]; continue; }
      const rest = text.slice(k, Math.min(e, k + 9));
      if (/^\s/.test(rest)) { k++; continue; }
      if (rest.startsWith('<![CDATA[')) { k += 9; continue; }
      if (rest.startsWith(']]>')) { k += 3; continue; }
      return { error: 'Vùng chọn gồm code ở 2 khối khác nhau (giữa có thẻ XML) — comment từng khối một.' };
    }
  }

  const style = FCC_STYLES[kind];
  const body = text.slice(s, e);

  // ---- Bỏ comment ----
  // 1) Cả đoạn là 1 chú thích bọc ngoài: /* ... */ (dấu đóng đầu tiên chính là dấu cuối) hoặc <!-- ... -->.
  //    Không coi dấu flatten của FCode (/* <flatten type="Javascript"> */) là chú thích.
  const unwrap = (open, close) => {
    if (body.length < open.length + close.length || !body.startsWith(open) || !body.endsWith(close)) return null;
    if (body.indexOf(close, open.length) !== body.length - close.length) return null;
    if (open === '/*' && /^\/\*\s*<flatten\b/i.test(body)) return null;
    const edits = [];
    const o = body[open.length] === ' ' ? open.length + 1 : open.length;
    edits.push({ start: s, end: s + o, text: '' });
    const ce = e - close.length;
    const cs = body[body.length - close.length - 1] === ' ' && ce - 1 >= s + o ? ce - 1 : ce;
    edits.push({ start: cs, end: e, text: '' });
    return { edits };
  };
  let res = unwrap(style.open, style.close);
  if (res) return res;
  // Trong vùng code: gỡ cả kiểu cũ <!-- --> do lệnh Comment trước đây (Monaco) đặt vào.
  if (kind === 'code') { res = unwrap('<!--', '-->'); if (res) return res; }

  // 2) Mọi dòng có chữ đều đã comment riêng: /* ... */ từng dòng, hoặc -- / // đầu dòng (kiểu Ctrl+/ cũ trong CDATA).
  const span = [];   // [lineStart offset của phần chữ, phần chữ] cho từng dòng có chữ, giới hạn trong [s, e)
  for (let ln = a; ln <= b; ln++) {
    const ls = Math.max(lineStarts[ln], s);
    const le = Math.min(lineStarts[ln] + lines[ln].length, e);
    const seg = text.slice(ls, le);
    const m = /\S/.exec(seg);
    if (!m) continue;
    const t = seg.replace(/\s+$/, '');
    span.push([ls + m.index, t.slice(m.index)]);
  }
  if (kind === 'code' && span.length > 1) {
    const perLine = span.every(([, t]) => t.startsWith('/*') && t.endsWith('*/') && t.length >= 4
      && t.indexOf('*/', 2) === t.length - 2 && !/^\/\*\s*<flatten\b/i.test(t));
    if (perLine) {
      const edits = [];
      for (const [off, t] of span) {
        const o = t[2] === ' ' ? 3 : 2;
        const c = t.length >= 5 && t[t.length - 3] === ' ' && t.length - 3 >= o ? t.length - 3 : t.length - 2;
        edits.push({ start: off, end: off + o, text: '' });
        edits.push({ start: off + c, end: off + t.length, text: '' });
      }
      return { edits };
    }
  }
  if (kind === 'code') {
    for (const tok of ['--', '//']) {
      if (span.every(([, t]) => t.startsWith(tok) && (t.length === 2 || t[2] === ' ' || tok === '//'))) {
        return { edits: span.map(([off, t]) => ({ start: off, end: off + (t[2] === ' ' ? 3 : 2), text: '' })) };
      }
    }
  }

  // ---- Comment ----
  if (kind === 'code' && body.includes('*/')) {
    const r = fcodeCodeRangeAt(scan.ranges, s);
    // T-SQL cho lồng /* /* */ */, JS/CSS thì không — bọc ngoài sẽ đóng sớm ở dấu */ bên trong.
    if (!(r && r[2] === 'sql')) {
      return { error: 'Đoạn chọn đã có */ bên trong — JS/CSS không lồng được comment /* */. Bỏ comment bên trong trước, hoặc chọn ít dòng hơn.' };
    }
  }
  if (kind === 'xml' && body.includes('--')) {
    // XML không cho "--" trong <!-- -->: Fast (XmlDocument) sẽ báo lỗi đọc file. Giữ như lệnh cũ nhưng nhắc.
    return { edits: [{ start: s, end: s, text: style.open + ' ' }, { start: e, end: e, text: ' ' + style.close }],
      warning: 'Đoạn vừa comment có "--" (comment SQL, hoặc <!-- --> bên trong) — XML không cho "--" trong <!-- -->, Fast có thể báo lỗi đọc file.' };
  }
  return { edits: [{ start: s, end: s, text: style.open + ' ' }, { start: e, end: e, text: ' ' + style.close }] };
}

/// Chạy lệnh trên 1 editor Monaco. Trả false nếu ngôn ngữ không phải FCode (để gọi lệnh comment gốc của Monaco).
function fcodeToggleComment(editor, toast) {
  const model = editor && editor.getModel();
  if (!model) return true;
  const lang = model.getLanguageId();
  const rootLang = lang === window.FCODE_JS_LANGUAGE_ID ? 'js' : lang === window.FCODE_SQL_LANGUAGE_ID ? 'sql' : null;
  if (lang !== window.FCODE_LANGUAGE_ID && !rootLang) return false;

  const text = model.getValue(monaco.editor.EndOfLinePreference.TextDefined);
  const lines = model.getLinesContent();
  const eolLen = model.getEOL().length;
  const lineStarts = new Array(lines.length);
  for (let k = 0, off = 0; k < lines.length; k++) { lineStarts[k] = off; off += lines[k].length + eolLen; }
  const scan = fcodeScanCodeRanges(text, rootLang);

  const all = [];
  const seen = new Set();
  let message = null;
  for (const sel of editor.getSelections() || []) {
    let endLine = sel.endLineNumber;
    if (endLine > sel.startLineNumber && sel.endColumn === 1) endLine--;   // chọn tới đầu dòng sau = không tính dòng đó
    const key = sel.startLineNumber + ':' + endLine;
    if (seen.has(key)) continue;
    seen.add(key);
    const plan = fcodeCommentPlan(text, lines, lineStarts, scan, sel.startLineNumber - 1, endLine - 1);
    if (plan.error) { message = plan.error; continue; }
    if (plan.warning) message = plan.warning;
    all.push(...plan.edits);
  }
  if (all.length) {
    const edits = all.map((x) => {
      const p1 = model.getPositionAt(x.start);
      const p2 = model.getPositionAt(x.end);
      return { range: new monaco.Range(p1.lineNumber, p1.column, p2.lineNumber, p2.column), text: x.text };
    });
    editor.pushUndoStop();
    editor.executeEdits('bcode-comment', edits);
    editor.pushUndoStop();
  }
  if (message && toast) toast(message);
  editor.focus();
  return true;
}

window.fcodeToggleComment = fcodeToggleComment;
window.fcodeScanCodeRanges = fcodeScanCodeRanges;
window.fcodeCommentPlan = fcodeCommentPlan;
