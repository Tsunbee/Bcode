/* Gợi ý code SQL cho editor (Monaco): mẫu gõ-tắt (snippet), tên procedure/function kèm tham số điền sẵn, trợ giúp chữ ký khi gõ tham số, cột sau alias,
   tên trong bảng options, và cảnh báo "dùng biến chuẩn mà chưa khai báo" kèm sửa nhanh.
   Dữ liệu do C# đẩy sang: setCatalog (mẫu tĩnh — SqlHintCatalog), setRoutines (chữ ký + options của database đang chọn — SqlHintService),
   setColumns (trả lời yêu cầu cột). Mọi thứ chạy trong trang, không chặn việc gõ. Trợ giúp cho người dùng: Settings → "Hướng dẫn gợi ý code SQL". */
(function () {
  'use strict';
  var DOLLAR = '¤';                                  // ký hiệu $ thật trong catalog (xem SqlHintCatalog)
  var H = window.BcodeHints = { cat: { snippets: [], idioms: [], popular: [], stdParams: [] }, routines: [], byName: {}, options: [], rank: {}, idiom: {}, cols: {}, waiting: {}, reqId: 0, send: function () {} };

  function snip(s) { return String(s).split(DOLLAR).join('\\$'); }
  function bare(name) { var n = String(name).toLowerCase(); return n.indexOf('dbo.') === 0 ? n.slice(4) : n; }
  function escSnippetText(s) { return String(s).replace(/[\\$}]/g, '\\$&'); }

  H.setCatalog = function (c) {
    H.cat = c || H.cat;
    H.rank = {}; (H.cat.popular || []).forEach(function (n, i) { H.rank[String(n).toLowerCase()] = i; });
    H.idiom = {}; (H.cat.idioms || []).forEach(function (i) { H.idiom[String(i.r).toLowerCase()] = i; });
  };

  H.setRoutines = function (payload) {
    var r = (payload && payload.r) || [];
    H.routines = r.map(function (x) { return { name: x[0], type: x[1], ps: (x[2] || []).map(function (p) { return { n: p[0], t: p[1], out: (p[2] & 1) === 1, def: (p[2] & 2) === 2 }; }) }; });
    H.byName = {}; H.routines.forEach(function (x) { H.byName[bare(x.name)] = x; });
    H.options = (payload && payload.o) || [];
  };

  H.setColumns = function (reqId, table, cols) {
    H.cols[table.toLowerCase()] = cols || [];
    var w = H.waiting[reqId]; if (w) { delete H.waiting[reqId]; w(H.cols[table.toLowerCase()]); }
  };

  function isFunc(r) { return r.type !== 'P'; }
  function sigText(r) {
    return r.name + (isFunc(r) ? '(' : ' ') + r.ps.map(function (p) { return p.n + ' ' + p.t + (p.out ? ' OUTPUT' : '') + (p.def ? ' = …' : ''); }).join(', ') + (isFunc(r) ? ')' : '');
  }

  /* Chèn mặc định theo chữ ký (khi chưa có cách gọi quen thuộc): tham số bắt buộc điền sẵn bằng chính tên tham số, vd  dbo.ff_PadL(@cpItem, @nLen) */
  function buildInsert(r, hasDbo) {
    var idi = H.idiom[bare(r.name)];
    if (idi) {
      var t = snip(idi.t);
      return hasDbo ? t.replace(/^dbo\./, '') : t;
    }
    var nameEsc = r.name.replace(/\$/g, '\\$');
    var req = r.ps.filter(function (p) { return !p.def; });
    var args = req.map(function (p, i) { return '${' + (i + 1) + ':' + escSnippetText(p.n) + (p.out ? '' : '') + '}' + (p.out ? ' OUTPUT' : ''); });
    if (isFunc(r)) return (hasDbo || r.name.indexOf('.') > 0 ? '' : 'dbo.') + nameEsc + '(' + args.join(', ') + ')';
    return nameEsc + (args.length ? ' ' + args.join(', ') : '');
  }

  function inStringOrComment(text) {
    var q = 0, i;
    for (i = 0; i < text.length; i++) {
      var c = text[i];
      if (c === "'") q ^= 1;
      else if (!q && c === '-' && text[i + 1] === '-') return true;
    }
    return q === 1;
  }

  var SQL_ALIAS_STOP = { where: 1, on: 1, join: 1, inner: 1, left: 1, right: 1, full: 1, cross: 1, outer: 1, group: 1, order: 1, union: 1, set: 1, having: 1, with: 1, values: 1, select: 1, using: 1, when: 1, then: 1, and: 1, or: 1 };

  function resolveAlias(text, alias) {
    var re = /\b(?:from|join|update|into)\s+((?:\[?\w+\]?\.)?\[?[\w$#]+\]?)(?:\s+(?:as\s+)?([A-Za-z_]\w*))?/gi, m, a = alias.toLowerCase();
    while ((m = re.exec(text)) !== null) {
      var t = m[1].replace(/[\[\]]/g, ''), al = m[2] && !SQL_ALIAS_STOP[m[2].toLowerCase()] ? m[2] : null;
      var last = t.split('.').pop();
      if (al && al.toLowerCase() === a) return t;
      if (!al && last.toLowerCase() === a) return t;
    }
    return null;
  }

  function requestColumns(table) {
    var key = table.toLowerCase();
    if (H.cols[key]) return Promise.resolve(H.cols[key]);
    return new Promise(function (resolve) {
      var id = ++H.reqId, done = false;
      H.waiting[id] = function (c) { if (!done) { done = true; resolve(c); } };
      H.send({ action: 'hint-columns', table: table, reqId: id });
      setTimeout(function () { if (!done) { done = true; delete H.waiting[id]; resolve([]); } }, 4000);
    });
  }

  /* ---------- cảnh báo: dùng biến chuẩn mà chưa khai báo ---------- */
  function stripSql(text) {
    var out = [], i = 0, n = text.length;
    while (i < n) {
      var c = text[i], d = text[i + 1];
      if (c === '-' && d === '-') { while (i < n && text[i] !== '\n') { out.push(' '); i++; } continue; }
      if (c === '/' && d === '*') { out.push(' ', ' '); i += 2; while (i < n && !(text[i] === '*' && text[i + 1] === '/')) { out.push(text[i] === '\n' ? '\n' : ' '); i++; } if (i < n) { out.push(' ', ' '); i += 2; } continue; }
      if (c === "'") {
        out.push(' '); i++;
        while (i < n) { if (text[i] === "'") { if (text[i + 1] === "'") { out.push(' ', ' '); i += 2; continue; } break; } out.push(text[i] === '\n' ? '\n' : ' '); i++; }
        if (i < n) { out.push(' '); i++; }
        continue;
      }
      out.push(c); i++;
    }
    return out.join('');
  }

  function parseHeader(clean) {
    var m = /\b(?:create|alter)\s+(?:or\s+alter\s+)?(proc|procedure|function)\s+[^\s(]+/i.exec(clean);
    if (!m) return null;
    var from = m.index + m[0].length, re = /\bas\b/ig, a;
    re.lastIndex = from;
    while ((a = re.exec(clean)) !== null) {
      if (/@\w+\s*$/.test(clean.substring(from, a.index))) continue;          // "@dateto as smalldatetime": AS kiểu dữ liệu, không phải AS bắt đầu thân
      return { kind: m[1].toLowerCase() === 'function' ? 'function' : 'proc', from: from, asIndex: a.index };
    }
    return null;
  }

  function declaredNames(clean, hdr) {
    var set = {}, seg = clean.substring(hdr.from, hdr.asIndex), m;
    var rx = /@\w+/g;
    while ((m = rx.exec(seg)) !== null) set[m[0].toLowerCase()] = true;
    var dre = /\bdeclare\b/ig, d;
    while ((d = dre.exec(clean)) !== null) {
      var tail = clean.substring(d.index + 7, d.index + 700);
      var stop = /\b(?:select|set|insert|update|delete|exec|execute|if|while|begin|end|from|with|open|fetch|return|print|declare)\b/i.exec(tail);
      var chunk = stop ? tail.substring(0, stop.index) : tail;
      while ((m = rx.exec(chunk)) !== null) set[m[0].toLowerCase()] = true;
      rx.lastIndex = 0;
    }
    return set;
  }

  H.analyze = function (text) {
    var clean = stripSql(text), hdr = parseHeader(clean);
    if (!hdr) return [];
    var declared = declaredNames(clean, hdr), body = clean.substring(hdr.asIndex + 2), issues = [];
    (H.cat.stdParams || []).forEach(function (sp) {
      var low = sp.n.toLowerCase();
      if (declared[low]) return;
      var re = new RegExp('(^|[^@\\w])(' + sp.n.replace(/[$]/g, '\\$') + ')(?![\\w$#])', 'i'), m = re.exec(body);
      if (!m) return;
      var off = hdr.asIndex + 2 + m.index + m[1].length;
      issues.push({ name: sp.n, type: sp.t, offset: off, length: sp.n.length, hdr: hdr, text: clean });
    });
    return issues;
  };

  /* ---------- Mẫu thông minh: gõ  tên mẫu + tên bảng  → điền sẵn cột của bảng ---------- */
  var SMART_KW = ['part', 'sel', 'ins', 'cur', 'sysrep', 'bil'];
  H.smartKeywords = SMART_KW;

  function splitTop(s) {
    var segs = [], cur = '', d = 0, q = false;
    for (var i = 0; i < s.length; i++) {
      var c = s[i];
      if (c === "'") q = !q;
      else if (!q) {
        if (c === '(') d++; else if (c === ')') d--;
        else if (c === ',' && d === 0) { segs.push(cur); cur = ''; continue; }
      }
      cur += c;
    }
    if (cur.trim()) segs.push(cur);
    return segs;
  }

  /* Tên cột của một mục trong danh sách SELECT:  a.col  |  expr AS alias  |  CAST(..) alias  |  alias = expr  |  *  */
  function colName(seg) {
    seg = seg.trim().replace(/\s+/g, ' ');
    if (!seg) return null;
    if (seg === '*' || /\.\*$/.test(seg)) return '*';
    var m = /\bas\s+\[?([\w$#]+)\]?$/i.exec(seg); if (m) return m[1];
    if ((m = /^\[?([\w$#]+)\]?\s*=\s*.+$/.exec(seg))) return m[1];
    if ((m = /^(?:[\w$#]+\.)?\[?([\w$#]+)\]?$/.exec(seg))) return m[1];
    if ((m = /\)\s*\[?([\w$#]+)\]?$/.exec(seg))) return m[1];
    if ((m = /^[^\s(]+\s+\[?([\w$#]+)\]?$/.exec(seg))) return m[1];
    return null;
  }

  /* Các bảng tạm / biến bảng được khai báo trong script: SELECT … INTO #t FROM …, CREATE TABLE #t (…), DECLARE @t TABLE (…) → { 'tênthường': {name, cols} } */
  H.scriptTables = function (text) {
    var clean = stripSql(text), tabs = {}, m, re;
    re = /\binto\s+(#[\w$]+)/ig;
    while ((m = re.exec(clean)) !== null) {
      var before = clean.substring(0, m.index), selRe = /\bselect\b/ig, last = -1, s;
      while ((s = selRe.exec(before)) !== null) last = s.index;
      if (last < 0) continue;
      var list = before.substring(last + 6).replace(/^\s*(?:distinct\b|all\b)?\s*(?:top\s*\(?\s*\d+\s*\)?(?:\s+percent)?)?/i, '');
      var cols = splitTop(list).map(colName).filter(function (c) { return c; });
      var ref = /^\s*from\s+([#\w$.\[\]]+)/i.exec(clean.substring(m.index + m[0].length));
      tabs[m[1].toLowerCase()] = { name: m[1], cols: cols, ref: ref ? ref[1].replace(/[\[\]]/g, '').toLowerCase() : null };
    }
    re = /\b(?:create\s+table\s+(#[\w$]+)|declare\s+(@\w+)\s+table)\s*\(/ig;
    while ((m = re.exec(clean)) !== null) {
      var name = m[1] || m[2], i = m.index + m[0].length, d = 1;
      while (i < clean.length && d) { if (clean[i] === '(') d++; else if (clean[i] === ')') d--; i++; }
      var body = clean.substring(m.index + m[0].length, i - 1), cs = [];
      splitTop(body).forEach(function (seg) {
        var t = /^\s*\[?([\w$#]+)\]?/.exec(seg);
        if (t && !/^(?:constraint|primary|unique|foreign|check|index)$/i.test(t[1])) cs.push(t[1]);
      });
      tabs[name.toLowerCase()] = { name: name, cols: cs, ref: null };
    }
    // SELECT * INTO #a FROM #b → lấy cột của #b (lần theo tối đa vài cấp)
    Object.keys(tabs).forEach(function (k) {
      var t = tabs[k], hops = 0;
      while (t.cols.indexOf('*') >= 0 && hops++ < 4) {
        var src = t.ref && tabs[t.ref];
        var at = t.cols.indexOf('*');
        if (!src || src === t) { t.cols.splice(at, 1); continue; }
        t.cols = t.cols.slice(0, at).concat(src.cols.filter(function (c) { return c !== '*'; }), t.cols.slice(at + 1));
      }
      t.cols = t.cols.filter(function (c, i, a) { return c !== '*' && a.indexOf(c) === i; });
    });
    return tabs;
  };

  /* Nội dung mẫu thông minh. ¤ = $ thật của mẫu (như catalog); tên cột/bảng đã được escape riêng. */
  function smartBody(kw, table, cols, isScript) {
    var e = escSnippetText, list = cols.map(e).join(', '), t = e(table), d = DOLLAR;
    switch (kw) {
      case 'part':
        return isScript
          ? "SET @q = 'insert into " + t + " select " + list + " from ${1:r00}" + d + "%Partition with(nolock) where %[' + @Key + ']%'\nEXEC FastBusiness" + d + "Partition" + d + "Execute @q, NULL, '${2:ngay_ct}', @DateFrom, @DateTo, @UserID, @Admin"
          : "SET @q = 'insert into ${1:#tmp} select " + list + " from " + t + d + "%Partition with(nolock) where %[' + @Key + ']%'\nEXEC FastBusiness" + d + "Partition" + d + "Execute @q, NULL, '${2:ngay_ct}', @DateFrom, @DateTo, @UserID, @Admin";
      case 'sel': return 'SELECT ' + list + '\n\tFROM ' + t;
      case 'ins': return 'INSERT INTO ' + t + ' (' + list + ')\nSELECT ' + list + '\n\tFROM ${1:nguon}';
      case 'sysrep': return 'SELECT ${1:5} AS sysorder, 1 AS sysprint, 1 AS systotal, ' + list + '\n\tFROM ' + t;
      case 'cur': {
        var decl = cols.map(function (c, i) { return '@' + e(c) + ' ${' + (i + 1) + ':VARCHAR(32)}'; }).join(', '), into = cols.map(function (c) { return '@' + e(c); }).join(', ');
        return 'DECLARE ' + decl + '\nDECLARE ${' + (cols.length + 1) + ':cr} CURSOR FOR\n\tSELECT ' + list + ' FROM ' + t + '\nOPEN ${' + (cols.length + 1) + ':cr}\nFETCH NEXT FROM ${' + (cols.length + 1) + ':cr} INTO ' + into +
          '\nWHILE @@FETCH_STATUS = 0\nBEGIN\n\t$0\n\tFETCH NEXT FROM ${' + (cols.length + 1) + ':cr} INTO ' + into + '\nEND\nCLOSE ${' + (cols.length + 1) + ':cr}\nDEALLOCATE ${' + (cols.length + 1) + ':cr}';
      }
      case 'bil': {
        var lc = cols.map(function (c) { return c.toLowerCase(); });
        var pairs = cols.filter(function (c) { return /^(?:ten|chi_tieu|dien_giai|name|comment|descript|form_name)/i.test(c) && !/2$/.test(c) && lc.indexOf(c.toLowerCase() + '2') >= 0; });
        if (!pairs.length) return null;
        return pairs.map(function (c) { return "CASE WHEN @Language = 'v' THEN " + e(c) + ' ELSE ' + e(c) + '2 END AS ' + e(c); }).join(',\n');
      }
    }
    return null;
  }

  /* ---------- cài vào Monaco ---------- */
  H.install = function (monaco, editor, send) {
    H.send = send || H.send;
    var K = monaco.languages.CompletionItemKind, SNIP = monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet;

    function rangeOf(pos, len) { return { startLineNumber: pos.lineNumber, endLineNumber: pos.lineNumber, startColumn: pos.column - len, endColumn: pos.column }; }
    function before(model, pos) { return model.getLineContent(pos.lineNumber).substring(0, pos.column - 1); }
    var TABLE_CTX = /\b(?:from|join|into|update|table)\s+[\w#$.\[\]]*$/i;

    /* 1) Mẫu gõ-tắt: gõ ≥ 2 chữ đầu của mã (rsrep, bil, unit...) rồi Tab/Enter */
    monaco.languages.registerCompletionItemProvider('sql', {
      triggerCharacters: [],
      provideCompletionItems: function (model, pos) {
        var tb = before(model, pos), w = /[\p{L}\p{N}_]*$/u.exec(tb)[0];     // chữ có dấu tiếng Việt cũng là chữ
        if (w.length < 2 || /[.@#$]\w*$/.test(tb) || inStringOrComment(tb)) return { suggestions: [] };
        var lw = w.toLowerCase();
        var items = (H.cat.snippets || []).filter(function (s) { return s.p.toLowerCase().indexOf(lw) === 0; }).map(function (s) {
          return {
            label: s.p, kind: K.Snippet, detail: s.n + '  ·  ' + s.c, documentation: { value: s.d + '\n\n```sql\n' + s.b.split(DOLLAR).join('$').replace(/\$\{\d+:([^}|]*)\}/g, '$1').replace(/\$\{\d+\|([^,|]*)[^}]*\}/g, '$1').replace(/\$\d|\$\{\d\}/g, '') + '\n```' },
            insertText: snip(s.b), insertTextRules: SNIP, range: rangeOf(pos, w.length), sortText: '00_' + s.p, filterText: s.p
          };
        });
        // Snippet của Library (do người dùng tự lưu, đã lọc theo dự án): gõ ≥ 2 chữ đầu của tên → chèn nguyên nội dung (giữ nguyên chữ, không hiểu $ là ký hiệu snippet)
        (H.user || []).forEach(function (u) {
          var ln = String(u.n).toLowerCase();
          if (ln.indexOf(lw) !== 0 && !(lw.length >= 3 && ln.indexOf(lw) >= 0)) return;
          items.push({ label: u.n, kind: K.Snippet, detail: 'Library  ·  ' + (u.c || 'General') + (u.proj ? '  ·  ★ ' + u.proj : ''), documentation: { value: '```sql\n' + String(u.b).slice(0, 600) + '\n```' },
            insertText: u.b, range: rangeOf(pos, w.length), sortText: '01_' + u.n, filterText: u.n });
        });
        return { suggestions: items };
      }
    });

    /* 2+3) Procedure / function: sau exec / dbo. hoặc gõ ≥ 3 chữ của hàm hay dùng — chèn sẵn tham số (cách gọi quen thuộc nếu có) */
    monaco.languages.registerCompletionItemProvider('sql', {
      triggerCharacters: ['.', ' '],
      provideCompletionItems: function (model, pos) {
        if (!H.routines.length) return { suggestions: [] };
        var tb = before(model, pos);
        if (inStringOrComment(tb) || TABLE_CTX.test(tb)) return { suggestions: [] };
        var mode = null, w = '', hasDbo = false, m;
        if ((m = /\b(?:exec|execute)\s+(?:dbo\.)?([\w$#]*)$/i.exec(tb))) { mode = 'exec'; w = m[1]; hasDbo = /dbo\.[\w$#]*$/i.test(tb); }
        else if ((m = /\bdbo\.([\w$#]*)$/i.exec(tb))) { mode = 'dbo'; w = m[1]; hasDbo = true; }
        else { w = /[\w$#]*$/.exec(tb)[0]; if (w.length >= 3 && !/[.@]\w*$/.test(tb)) mode = 'popular'; }
        if (!mode) return { suggestions: [] };
        var lw = w.toLowerCase(), out = [];
        for (var i = 0; i < H.routines.length && out.length < 400; i++) {
          var r = H.routines[i], b = bare(r.name);
          if (mode === 'exec' && isFunc(r) && !H.idiom[b]) continue;
          if (mode === 'popular') { if (H.rank[b] === undefined) continue; if (b.indexOf(lw) !== 0 && !(lw.length >= 4 && b.indexOf(lw) >= 0)) continue; }
          else if (lw && b.indexOf(lw) < 0) continue;
          var rk = H.rank[b], idi = H.idiom[b];
          out.push({
            label: r.name, kind: isFunc(r) ? K.Function : K.Method,
            detail: (isFunc(r) ? 'function' : 'procedure') + (rk !== undefined ? '  ★ hay dùng' : ''),
            documentation: { value: '`' + sigText(r) + '`' + (idi ? '\n\nCách gọi quen thuộc: ' + idi.n : '') },
            insertText: buildInsert(r, hasDbo), insertTextRules: SNIP, range: rangeOf(pos, w.length),
            sortText: (rk !== undefined ? '0' + ('000' + rk).slice(-3) : '1') + r.name.toLowerCase(), filterText: r.name
          });
        }
        return { suggestions: out };
      }
    });

    /* 4) Cột sau alias:  a.  →  cột của bảng đứng sau alias a (khoá chính lên đầu) */
    monaco.languages.registerCompletionItemProvider('sql', {
      triggerCharacters: ['.'],
      provideCompletionItems: function (model, pos) {
        var tb = before(model, pos), m = /([A-Za-z_][\w$#]*)\.([\w$#]*)$/.exec(tb);
        if (!m || inStringOrComment(tb) || m[1].toLowerCase() === 'dbo') return { suggestions: [] };
        var table = resolveAlias(model.getValue(), m[1]);
        if (!table || table.charAt(0) === '#') return { suggestions: [] };
        var parts = table.split('.'), name = parts.pop(), schema = parts.pop() || 'dbo', w = m[2].length;
        return requestColumns(schema + '.' + name).then(function (cols) {
          return { suggestions: cols.map(function (c, i) {
            return { label: c[0], kind: K.Field, detail: (c[1] ? '🔑 khoá chính  ·  ' : '') + table, insertText: c[0], range: rangeOf(pos, w), sortText: (c[1] ? '0' : '1') + ('0000' + i).slice(-4) };
          }) };
        });
      }
    });

    /* 4b) Mở rộng tất cả cột có tiền tố:  FROM dmkh a … SELECT a.dmkh  →  a.ma_kh, a.ten_kh, …  (bảng trong database hoặc bảng tạm / biến bảng trong script).
          Gõ  alias.tênbảng  (từ 2 chữ đầu của tên bảng); không alias thì  tênbảng.tênbảng. */
    monaco.languages.registerCompletionItemProvider('sql', {
      triggerCharacters: ['.'],
      provideCompletionItems: function (model, pos) {
        var tb = before(model, pos), m = /([A-Za-z_][\w$#]*)\.([\w$#]{2,})$/.exec(tb);
        if (!m || inStringOrComment(tb) || m[1].toLowerCase() === 'dbo') return { suggestions: [] };
        var text = model.getValue(), table = resolveAlias(text, m[1]);
        if (!table) return { suggestions: [] };
        var parts = table.split('.'), name = parts.pop(), schema = parts.pop() || 'dbo', lw = m[2].toLowerCase();
        if (name.toLowerCase().indexOf(lw) !== 0) return { suggestions: [] };       // chỉ khi phần sau dấu chấm đang gõ dở / đủ tên bảng
        var range = { startLineNumber: pos.lineNumber, endLineNumber: pos.lineNumber, startColumn: pos.column - m[0].length, endColumn: pos.column };
        function make(cols, pk) {
          if (!cols.length) return { suggestions: [] };
          var body = cols.map(function (c) { return m[1] + '.' + c; }).join(', ');
          return { suggestions: [{ label: m[1] + '.' + name, kind: K.Snippet, detail: 'tất cả ' + cols.length + ' cột của ' + table + ', có tiền tố ' + m[1] + '.',
            documentation: { value: '```sql\n' + body + '\n```' }, insertText: body, range: range, filterText: m[0], sortText: '000' }] };
        }
        if (name.charAt(0) === '#' || name.charAt(0) === '@') {
          var st = H.scriptTables(text)[name.toLowerCase()];
          return make(st ? st.cols : []);
        }
        return requestColumns(schema + '.' + name).then(function (cs) { return make(cs.map(function (c) { return c[0]; })); });
      }
    });

    /* 6) Tên trong bảng options:  FROM options WHERE name = '…' */
    monaco.languages.registerCompletionItemProvider('sql', {
      triggerCharacters: ["'"],
      provideCompletionItems: function (model, pos) {
        var tb = before(model, pos), m = /\bname\s*=\s*'([\w]*)$/i.exec(tb);
        if (!m || !H.options.length) return { suggestions: [] };
        var ctx = '';
        for (var l = Math.max(1, pos.lineNumber - 3); l <= pos.lineNumber; l++) ctx += model.getLineContent(l) + '\n';
        if (!/\boptions\b/i.test(ctx)) return { suggestions: [] };
        return { suggestions: H.options.map(function (o) {
          return { label: o[0], kind: K.Constant, detail: o[1] === '' ? '(trống)' : 'giá trị hiện tại: ' + o[1], insertText: o[0], range: rangeOf(pos, m[1].length), sortText: (o[0].indexOf('m_round') === 0 ? '0' : '1') + o[0] };
        }) };
      }
    });

    /* 7) Mẫu thông minh:  part #tmp  /  sel dmvt  /  cur #t …  → điền sẵn cột của bảng (bảng tạm / biến bảng trong script, hoặc bảng trong database) */
    var SMART_RE = new RegExp('(?:^|[\\s;(])(' + SMART_KW.join('|') + ')\\s+([#@\\w$.\\[\\]]+)$', 'i');
    var SMART_BARE = new RegExp('(?:^|[\\s;(])(' + SMART_KW.join('|') + ')\\s+$', 'i');
    var SMART_DESC = { part: 'Truy vấn phân kỳ', sel: 'SELECT', ins: 'INSERT … SELECT', cur: 'Cursor', sysrep: 'Cột báo cáo', bil: 'Tên song ngữ' };

    function dbTableKnown(name) {
      var n = String(name).toLowerCase().replace(/^dbo\./, ''), list = window._dbTables || [];
      for (var i = 0; i < list.length; i++) if (String(list[i].name).toLowerCase() === n) return true;
      return false;
    }

    function smartItem(kw, table, cols, isScript, range, typed) {
      var body = smartBody(kw, table, cols, isScript);
      if (!body) return null;
      return { label: kw + ' ' + table, kind: K.Snippet, detail: SMART_DESC[kw] + ' — điền sẵn ' + cols.length + ' cột của ' + table, documentation: { value: '```sql\n' + body.split(DOLLAR).join('$').replace(/\$\{\d+:([^}|]*)\}/g, '$1').replace(/\$0/g, '') + '\n```' },
        insertText: snip(body), insertTextRules: SNIP, range: range, filterText: typed, sortText: '000' };
    }

    monaco.languages.registerCompletionItemProvider('sql', {
      triggerCharacters: [' '],
      provideCompletionItems: function (model, pos) {
        var tb = before(model, pos);
        if (inStringOrComment(tb)) return { suggestions: [] };
        var bare = SMART_BARE.exec(tb), m = SMART_RE.exec(tb), tabs, out = [];
        if (!bare && !m) return { suggestions: [] };
        var hit = bare || m, kw = hit[1].toLowerCase(), kwAt = hit.index + hit[0].toLowerCase().indexOf(kw);
        var range = { startLineNumber: pos.lineNumber, endLineNumber: pos.lineNumber, startColumn: kwAt + 1, endColumn: pos.column };
        var typed = tb.substring(kwAt);
        tabs = H.scriptTables(model.getValue());

        if (bare) {                                           // vừa gõ "part " → liệt kê các bảng tạm có trong script để chọn
          Object.keys(tabs).forEach(function (k) {
            var t = tabs[k]; if (t.cols.length && t.name.charAt(0) === '#') { var it = smartItem(kw, t.name, t.cols, true, range, typed + t.name); if (it) out.push(it); }
          });
          return { suggestions: out };
        }
        var arg = m[2].replace(/[\[\]]/g, ''), la = arg.toLowerCase();
        if (la.charAt(0) === '#' || la.charAt(0) === '@') {      // bảng tạm / biến bảng: khớp theo tiền tố ("part #b" → #b2, #bg…)
          Object.keys(tabs).forEach(function (k) {
            var t = tabs[k];
            if (t.cols.length && k.indexOf(la) === 0) { var i0 = smartItem(kw, t.name, t.cols, true, range, typed); if (i0) out.push(i0); }
          });
          return { suggestions: out };
        }
        var st = tabs[la];
        if (st && st.cols.length) { var it1 = smartItem(kw, st.name, st.cols, true, range, typed); return { suggestions: it1 ? [it1] : [] }; }
        if (kw === 'bil' && !dbTableKnown(arg)) {              // bil ten_kh → CASE song ngữ cho đúng một cột
          if (!/^[\w$]+$/.test(arg)) return { suggestions: [] };
          var e = escSnippetText(arg);
          return { suggestions: [{ label: 'bil ' + arg, kind: K.Snippet, detail: 'Tên song ngữ cho cột ' + arg, insertText: "CASE WHEN @Language = 'v' THEN " + e + ' ELSE ' + e + "2 END AS " + e, insertTextRules: SNIP, range: range, filterText: typed, sortText: '000' }] };
        }
        if (!dbTableKnown(arg)) return { suggestions: [] };    // bảng trong database: chỉ hỏi khi tên có trong danh sách bảng (tránh hỏi mỗi phím gõ)
        var parts = arg.split('.'), name = parts.pop(), schema = parts.pop() || 'dbo';
        return requestColumns(schema + '.' + name).then(function (cs) {
          var cols = cs.map(function (c) { return c[0]; });
          if (!cols.length) return { suggestions: [] };
          var it2 = smartItem(kw, arg, cols, false, range, typed);
          return { suggestions: it2 ? [it2] : [] };
        });
      }
    });

    /* 5b) Trợ giúp chữ ký khi gõ tham số:  ff_PadL(  hoặc  exec proc a,  */
    function commasAtDepth0(s) {
      var depth = 0, q = false, n = 0;
      for (var i = 0; i < s.length; i++) { var c = s[i]; if (c === "'") q = !q; else if (!q) { if (c === '(') depth++; else if (c === ')') depth--; else if (c === ',' && depth === 0) n++; } }
      return n;
    }
    monaco.languages.registerSignatureHelpProvider('sql', {
      signatureHelpTriggerCharacters: ['(', ',', ' '],
      provideSignatureHelp: function (model, pos) {
        var tb = before(model, pos), r = null, idx = 0, m;
        if (inStringOrComment(tb)) return null;
        var depth = 0, open = -1;
        for (var i = tb.length - 1; i >= 0; i--) { var c = tb[i]; if (c === ')') depth++; else if (c === '(') { if (depth === 0) { open = i; break; } depth--; } }
        if (open >= 0 && (m = /([\w$#.]+)\s*$/.exec(tb.substring(0, open)))) { r = H.byName[bare(m[1])]; idx = commasAtDepth0(tb.substring(open + 1)); }
        if (!r && (m = /\b(?:exec|execute)\s+(?:dbo\.)?([\w$#]+)\s+(.*)$/i.exec(tb))) { r = H.byName[bare(m[1])]; idx = commasAtDepth0(m[2]); }
        if (!r) return null;
        var label = sigText(r), params = [], from = label.indexOf(isFunc(r) ? '(' : ' ') + 1;
        r.ps.forEach(function (p) {
          var t = p.n + ' ' + p.t + (p.out ? ' OUTPUT' : '') + (p.def ? ' = …' : ''), s = label.indexOf(t, from);
          params.push({ label: [s, s + t.length] }); from = s + t.length;
        });
        return { value: { signatures: [{ label: label, parameters: params, documentation: isFunc(r) ? 'function' : 'procedure' }], activeSignature: 0, activeParameter: Math.min(idx, Math.max(0, params.length - 1)) }, dispose: function () {} };
      }
    });

    /* 5a) Cảnh báo dùng biến chuẩn chưa khai báo + sửa nhanh */
    var timer = null;
    function lint() {
      var model = editor.getModel(); if (!model) return;
      var issues = [];
      try { issues = H.analyze(model.getValue()); } catch (e) { issues = []; }
      monaco.editor.setModelMarkers(model, 'bcode-hints', issues.map(function (it) {
        var s = model.getPositionAt(it.offset), e = model.getPositionAt(it.offset + it.length);
        return { severity: monaco.MarkerSeverity.Warning, message: it.name + ' được dùng nhưng chưa khai báo (không có trong tham số, cũng không DECLARE). Bấm bóng đèn / Ctrl+. để thêm tham số.',
          code: 'bc-missing:' + it.name + ':' + it.type, source: 'Bcode', startLineNumber: s.lineNumber, startColumn: s.column, endLineNumber: e.lineNumber, endColumn: e.column };
      }));
    }
    function schedule() { clearTimeout(timer); timer = setTimeout(lint, 700); }
    editor.onDidChangeModelContent(schedule);
    H.lintNow = lint;

    monaco.languages.registerCodeActionProvider('sql', {
      provideCodeActions: function (model, range, context) {
        var actions = [];
        (context.markers || []).forEach(function (mk) {
          var code = typeof mk.code === 'object' && mk.code ? mk.code.value : mk.code;
          var m = /^bc-missing:([^:]+):(.+)$/.exec(code || ''); if (!m) return;
          var text = model.getValue(), clean = stripSql(text), hdr = parseHeader(clean);
          if (!hdr || hdr.kind !== 'proc') return;                         // function có ngoặc ( ) quanh tham số — chỉ cảnh báo, không tự sửa
          var seg = clean.substring(hdr.from, hdr.asIndex), trimmed = seg.replace(/\s+$/, '');
          var lastLine = trimmed.split('\n').pop();
          if (/--|\/\*/.test(text.substring(hdr.from, hdr.from + trimmed.length).split('\n').pop())) return;   // dòng tham số cuối có chú thích — không đoán chỗ chèn
          var hasParams = /@\w+/.test(seg), at = model.getPositionAt(hdr.from + trimmed.length);
          var ins = (hasParams ? ',\n\t' : '\n\t') + m[1] + ' ' + m[2] + (hasParams ? '' : '\n');
          actions.push({
            title: 'Thêm tham số ' + m[1] + ' ' + m[2] + ' vào procedure', kind: 'quickfix', isPreferred: true, diagnostics: [mk],
            edit: { edits: [{ resource: model.uri, versionId: undefined, textEdit: { range: { startLineNumber: at.lineNumber, startColumn: at.column, endLineNumber: at.lineNumber, endColumn: at.column }, text: ins } }] }
          });
        });
        return { actions: actions, dispose: function () {} };
      }
    }, { providedCodeActionKinds: ['quickfix'] });
  };
})();
