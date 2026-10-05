// "Chạy SQL": runs the T-SQL under the caret against the workspace Bcode is pointed at and
// shows the rows in the bottom dock.
//
// The reason this belongs in BcodeViewer rather than "just use SSMS" is everything that has
// to happen between the text on screen and something a server will accept:
//
//   • The statement is inside a <command>/<action> block, often wrapped in CDATA, so the
//     markup has to come off first (see sqlAtCaret).
//   • It references &Entity; values that hold the real body of the routine — entity.js
//     resolves those through the include chain and expands them (see prepare).
//   • Outside CDATA the document's own escaping applies, so `a &gt; b` has to become
//     `a > b` — and inside CDATA it must NOT, because there it is literal text.
//   • It uses @parameters that FCode supplies at runtime and nobody here has values for,
//     so they are asked for once and bound as real SqlParameters.
//
// Doing those four by hand every time is exactly why people stop checking their queries.
//
// Safety lives on the host side (SqlRunnerService): writes are refused unless enabled in
// Settings, and "Chạy thử" wraps everything in a transaction that is always rolled back.
// This file's job is to make the choice visible before the query runs, not to enforce it.

/// FCode's language suffix on a column name: `b.ten_tk%l` → ten_tk ('v') / ten_tk2 (other).
/// Needs a name character right before it, so LIKE '%loai%' and '%s' placeholders don't match.
const LANGUAGE_SUFFIX_RE = /(\w)%l(?![\w$#])/i;

class BcodeSqlRun {
  constructor(bcode) {
    this.bcode = bcode;
    this.dock = window.bcodeDock || (window.bcodeDock = new BcodeDock());
    this.pane = this.dock.register('sqlPanel', 'SQL');
    /// Parameter values, remembered for the session: the same @ma_dvcs gets asked for by
    /// every command in the file, and retyping it each run is how a useful tool becomes an
    /// annoying one.
    this.paramValues = {};
    /// Values typed for FCode macros (@@language, @@userID, @$mode...) — raw SQL text, kept
    /// for the session like paramValues.
    this.macroValues = {};
    this.running = false;
    this.lastResult = null;

    this.buildChrome();
  }

  buildChrome() {
    const bar = document.createElement('div');
    bar.id = 'sqlBar';

    this.status = document.createElement('span');
    this.status.id = 'sqlStatus';
    this.status.textContent = 'Ctrl+Enter để chạy câu SQL tại con trỏ.';

    this.stopBtn = document.createElement('button');
    this.stopBtn.className = 'dlgButton';
    this.stopBtn.textContent = 'Dừng';
    this.stopBtn.style.display = 'none';
    this.stopBtn.onclick = () => this.cancel();

    this.copyBtn = document.createElement('button');
    this.copyBtn.className = 'dlgButton';
    this.copyBtn.textContent = 'Copy kết quả';
    this.copyBtn.onclick = () => this.copyResult();

    bar.append(this.status, this.stopBtn, this.copyBtn);

    this.results = document.createElement('div');
    this.results.id = 'sqlResults';

    this.pane.append(bar, this.results);
  }

  // ---- Working out what to run --------------------------------------------------------

  /// The selection if there is one, otherwise the whole SQL region the caret sits in — the
  /// <command>/<action> body, or an <!ENTITY> value classified as SQL. Reuses completion.js's
  /// region scanner so "where SQL is" means one thing across the whole app.
  sqlAtCaret() {
    const model = this.bcode.currentModel;
    if (!model) return null;

    const selection = this.bcode.editor.getSelection();
    if (selection && !selection.isEmpty()) {
      return { text: model.getValueInRange(selection), source: 'vùng bôi đen' };
    }

    const text = model.getValue();
    const offset = model.getOffsetAt(this.bcode.editor.getPosition());
    const tags = (window.bcodeCompletion && window.bcodeCompletion.config.sqlRegionTags) || [];
    const containing = buildRegions(text, tags)
      .filter((r) => r.kind === 'sql' && r.start <= offset && offset <= r.end);
    if (containing.length === 0) return null;

    // Innermost wins: a CDATA block marked as SQL inside a <command> is the tighter answer.
    containing.sort((a, b) => (a.end - a.start) - (b.end - b.start));
    const region = containing[0];
    return { text: text.slice(region.start, region.end), source: 'vùng SQL tại con trỏ' };
  }

  /// Markup off, entities expanded, escaping undone where it applies. Returns
  /// {sql, notes:[]} — notes are what the status line reports, so an expansion that only
  /// half worked is visible rather than silently producing a broken query.
  async prepare(raw) {
    const notes = [];

    // CDATA is literal text: inside it `&Entity;` was never expanded by any parser and
    // `&gt;` is four characters, not one. Strip the wrapper, and remember which side of
    // that line the text came from.
    const hadCdata = /<!\[CDATA\[/.test(raw);
    let sql = raw.replace(/<!\[CDATA\[/g, '').replace(/\]\]>/g, '');

    // FCode's own marker comment for a JavaScript CDATA block — meaningless to a server.
    sql = sql.replace(/\/\*\s*<\/?flatten[^>]*>\s*\*\//g, '');

    if (window.bcodeEntity) {
      const result = await window.bcodeEntity.expand(sql);
      sql = result.text;
      if (result.expanded.length) notes.push(`đã thay ${result.expanded.length} entity`);
      if (result.unresolved.length) {
        notes.push(`KHÔNG tìm thấy: ${result.unresolved.map((n) => '&' + n + ';').join(', ')}`);
      }
    }

    if (!hadCdata) {
      // Outside CDATA the document is XML, so this text reached FCode already unescaped.
      sql = sql
        .replace(/&lt;/g, '<').replace(/&gt;/g, '>')
        .replace(/&quot;/g, '"').replace(/&apos;/g, "'")
        .replace(/&amp;/g, '&'); // last: undoing it first would re-expand the others
    }

    return { sql: sql.trim(), notes };
  }

  // ---- Running --------------------------------------------------------------------------

  /// opts.debug: "Debug trong Bcode" — open the prepared script in a Bcode SQL Query tab instead of running it here.
  async run(opts = {}) {
    if (this.running) return;
    this.dock.show('sqlPanel');

    const picked = this.sqlAtCaret();
    if (!picked || !picked.text.trim()) {
      this.setStatus('Đặt con trỏ trong <command>/<action> hoặc bôi đen câu lệnh rồi bấm Ctrl+Enter.', true);
      return;
    }

    const { sql, notes } = await this.prepare(picked.text);
    if (!sql) { this.setStatus('Không còn câu lệnh nào sau khi bỏ markup.', true); return; }

    let info;
    try {
      info = JSON.parse(await window.chrome.webview.hostObjects.host.InspectSql(sql));
    } catch (e) {
      this.setStatus('Lỗi khi phân tích câu lệnh: ' + e, true);
      return;
    }

    // The common case — a plain SELECT with no parameters — runs on the keystroke, with no
    // dialog in the way. Anything that needs a value or can change data asks first.
    const macros = info.macros || [];
    // %l (ten_tk%l) theo ngôn ngữ — cần @@language để biết thay bằng gì, kể cả khi câu lệnh không dùng @@language.
    if (LANGUAGE_SUFFIX_RE.test(sql) && !macros.some((m) => m.name.toLowerCase() === '@@language')) {
      macros.push({ name: '@@language', value: "'v'" });
    }
    info.macros = macros;
    const unsupported = info.unsupportedMacros || [];
    const needsDialog = info.parameters.length > 0 || info.writes.length > 0 || macros.length > 0 || unsupported.length > 0;
    let rollback = info.writes.length > 0;
    let debug = !!opts.debug;
    if (needsDialog) {
      const answer = await this.askBeforeRunning(sql, info, notes);
      if (!answer) return;
      rollback = answer.rollback;
      debug = !!answer.debug;
    }

    // Only this script's parameters: paramValues remembers every run of the session, and a name
    // another script asked for may be one this script declares itself.
    const params = {};
    for (const name of info.parameters) params[name] = this.paramValues[name] || '';
    if (debug) { await this.sendToBcode(this.applyMacros(sql, macros), info, params, picked.source); return; }
    await this.execute(this.applyMacros(sql, macros), rollback, notes, picked.source, params);
  }

  /// "Debug trong Bcode": the same script this panel would run, made standalone — every parameter
  /// DECLAREd with the value typed in the dialog (empty = NULL), "Infinite" ones as the one-column
  /// table FCode builds — and opened in a new SQL Query tab of Bcode, where it can be edited, run
  /// in parts or stepped through.
  async sendToBcode(sql, info, params, source) {
    const lit = (v) => (v === '' || v === null || v === undefined ? 'NULL' : "N'" + String(v).replace(/'/g, "''") + "'");
    const tables = new Set(info.tableParameters || []);
    const lines = [];
    const file = this.bcode.activePath ? fileNameOf(this.bcode.activePath) : '';
    lines.push(`-- Từ BcodeViewer: ${file}${source ? ' — ' + source : ''}`);
    if (info.workspace) lines.push(`-- Workspace lúc chuẩn bị: ${info.workspace}`);
    const scalars = info.parameters.filter((n) => !tables.has(n));
    if (scalars.length) {
      lines.push('declare ' + scalars.map((n) => `@${n} nvarchar(max) = ${lit(params[n])}`).join(',\n        '));
    }
    for (const n of info.parameters.filter((x) => tables.has(x))) {
      lines.push(`declare @${n} table (data nvarchar(max))` + (params[n] ? `; insert into @${n} values (${lit(params[n])})` : ''));
    }
    if ((info.unsupportedMacros || []).length) {
      lines.push(`-- CHÚ Ý: macro FCode ${info.unsupportedMacros.join(', ')} không thay được — sửa tay trước khi chạy.`);
    }
    const script = lines.join('\n') + '\n\n' + sql + '\n';
    const title = (file ? file.replace(/\.[^.]+$/, '') : 'BcodeViewer') + ' (SQL)';

    let error;
    try { error = await window.chrome.webview.hostObjects.host.SendSqlToBcode(script, title); }
    catch (e) { error = 'Không gửi được sang Bcode: ' + e; }
    if (error) this.setStatus(error, true);
    else this.setStatus('Đã mở câu lệnh trong tab SQL Query của Bcode.');
  }

  /// FCode replaces its macros (@@language, @@userID, @$mode...) in the text before the server
  /// sees it; they are not valid T-SQL variables, so they are substituted here too — with the
  /// raw SQL the user typed ('v', 1...). Empty means NULL.
  applyMacros(sql, macros) {
    let out = sql;
    // %l: hậu tố tên cột theo ngôn ngữ — ten_tk%l là ten_tk khi @@language = 'v', ten_tk2 khi khác.
    const language = macros.find((m) => m.name.toLowerCase() === '@@language');
    if (language) {
      const lang = (this.macroValues[language.name] || '').trim().replace(/^N?'(.*)'$/i, '$1').trim().toLowerCase();
      const suffix = lang === 'v' ? '' : '2';
      out = out.replace(new RegExp(LANGUAGE_SUFFIX_RE.source, 'gi'), (_, before) => before + suffix);
    }
    for (const { name } of macros) {
      const value = (this.macroValues[name] || '').trim() || 'NULL';
      const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      out = out.replace(new RegExp('(^|[^\\w@$#])' + escaped + '(?![\\w$#])', 'gi'), (_, before) => before + value);
    }
    return out;
  }

  /// One dialog covering both questions: what are the parameter values, and — for a script
  /// that writes — commit or roll back. They are asked together because they are answered
  /// together, right before the same button.
  askBeforeRunning(sql, info, notes) {
    return new Promise((resolve) => {
      const { overlay, body } = window.bcodeDialogs.makeDialog('Chạy SQL');
      // Responsive: hộp luôn nằm gọn trong cửa sổ (tiêu đề + nút cố định, phần giữa tự cuộn), tham số
      // xếp nhiều cột khi cửa sổ rộng — trước đây 25 tham số đẩy cả hộp tràn ra ngoài, mất tiêu đề và nút Chạy.
      const box = overlay.firstChild;
      box.classList.add('sqlRunBox');
      body.classList.add('sqlRunBody');
      let settled = false;
      const onKey = (e) => {
        if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); finish(null); }
      };
      const finish = (value) => {
        if (settled) return;
        settled = true;
        document.removeEventListener('keydown', onKey, true);
        if (previewEditor) { const m = previewEditor.getModel(); previewEditor.dispose(); if (m) m.dispose(); }
        overlay.remove();
        resolve(value);
      };
      document.addEventListener('keydown', onKey, true);
      overlay.addEventListener('click', (e) => { if (e.target === overlay) finish(null); });
      // Cùng khung với Hint Code / Template / Settings: nút ✕ trên tiêu đề.
      const header = box.querySelector('.dlgHeader');
      if (header) {
        header.classList.add('tplHeader');
        const x = document.createElement('span');
        x.className = 'tplClose';
        x.textContent = '✕';
        x.title = 'Đóng (Esc)';
        x.onclick = () => finish(null);
        header.appendChild(x);
      }

      if (info.workspace) {
        const ws = window.bcodeDialogs.makeLabel('Chạy trên: ' + info.workspace);
        body.appendChild(ws);
      }
      if (notes.length) body.appendChild(window.bcodeDialogs.makeLabel(notes.join(' · ')));

      const inputs = new Map();
      if (info.parameters.length) {
        const head = document.createElement('div');
        head.className = 'sqlParamHead';
        head.appendChild(window.bcodeDialogs.makeLabel(
          `Tham số (${info.parameters.length}) — để trống nghĩa là NULL:`));
        const grid = document.createElement('div');
        grid.className = 'sqlParamGrid';
        const cells = [];
        for (const name of info.parameters) {
          const cell = document.createElement('div');
          cell.className = 'sqlParam';
          const label = document.createElement('label');
          const isTable = (info.tableParameters || []).includes(name);
          label.textContent = '@' + name + (isTable ? ' (bảng)' : '');
          // tên dài bị cắt "…" — rê chuột để xem đủ
          label.title = '@' + name + (isTable
            ? ' — tham số "Infinite" của FCode: dùng như bảng 1 cột (data) chứa giá trị nhập, vd 1,2,3; để trống = bảng rỗng'
            : '');
          const input = document.createElement('input');
          input.type = 'text';
          input.value = this.paramValues[name] || '';
          input.spellcheck = false;
          input.id = 'sqlParam_' + name;
          label.htmlFor = input.id;
          inputs.set(name, input);
          cell.append(label, input);
          cells.push({ name: name.toLowerCase(), cell });
          grid.appendChild(cell);
        }
        // Nhiều tham số thì có ô lọc theo tên để khỏi cuộn tìm.
        if (info.parameters.length > 10) {
          const filter = document.createElement('input');
          filter.type = 'text';
          filter.className = 'sqlParamFilter';
          filter.placeholder = 'Lọc tham số...';
          filter.spellcheck = false;
          filter.oninput = () => {
            const q = filter.value.trim().toLowerCase().replace(/^@/, '');
            for (const c of cells) c.cell.style.display = !q || c.name.includes(q) ? '' : 'none';
          };
          head.appendChild(filter);
        }
        body.appendChild(head);
        body.appendChild(grid);
      }

      // Macro FCode: không phải biến T-SQL nên thay thẳng vào câu lệnh — nhập đúng cú pháp SQL ('v', 1...).
      const macroInputs = new Map();
      const macros = info.macros || [];
      if (macros.length) {
        body.appendChild(window.bcodeDialogs.makeLabel(
          `Macro của FCode (${macros.length}) — thay thẳng vào câu lệnh, nhập đúng cú pháp SQL (vd 'v', 1); để trống = NULL:`));
        const grid = document.createElement('div');
        grid.className = 'sqlParamGrid';
        for (const { name, value } of macros) {
          const cell = document.createElement('div');
          cell.className = 'sqlParam';
          const label = document.createElement('label');
          label.textContent = name;
          label.title = name;
          const input = document.createElement('input');
          input.type = 'text';
          input.value = name in this.macroValues ? this.macroValues[name] : value;
          input.spellcheck = false;
          input.id = 'sqlMacro_' + name.replace(/[^\w]/g, '_');
          label.htmlFor = input.id;
          macroInputs.set(name, input);
          cell.append(label, input);
          grid.appendChild(cell);
        }
        body.appendChild(grid);
      }
      const unsupported = info.unsupportedMacros || [];
      if (unsupported.length) {
        const warn = document.createElement('div');
        warn.className = 'sqlWriteWarning';
        warn.textContent = `Có macro FCode dạng hàm (${unsupported.join(', ')}) — FCode tự sinh SQL cho chúng lúc chạy, ` +
          'BcodeViewer không làm lại được nên phần đó sẽ báo lỗi. Bôi đen đoạn không dùng macro này để chạy riêng.';
        body.appendChild(warn);
      }

      const rollbackWrap = document.createElement('label');
      rollbackWrap.className = 'sqlRollbackRow';
      const rollbackBox = document.createElement('input');
      rollbackBox.type = 'checkbox';
      rollbackBox.checked = info.writes.length > 0;
      rollbackWrap.append(rollbackBox, document.createTextNode(
        ' Chạy thử (bọc transaction rồi luôn rollback — không đổi dữ liệu)'));

      if (info.writes.length > 0) {
        const warn = document.createElement('div');
        warn.className = 'sqlWriteWarning';
        warn.textContent = info.writesAllowed
          ? `Câu lệnh có ${info.writes.join(', ')}. Bỏ dấu tick bên dưới là GHI THẬT.`
          : `Câu lệnh có ${info.writes.join(', ')}. Ghi thật đang bị chặn trong Settings, ` +
            `nên chỉ chạy thử được.`;
        body.appendChild(warn);
        if (!info.writesAllowed) { rollbackBox.checked = true; rollbackBox.disabled = true; }
      }
      body.appendChild(rollbackWrap);

      // Khung xem trước là 1 editor Monaco chỉ đọc, tô màu như editor chính (cùng theme): theme Fcode → tô theo
      // <KeywordStart> của theme (fcode-cdata), theme khác → SQL của Monaco. Không có Monaco thì về textarea.
      const preview = document.createElement('div');
      preview.className = 'sqlRunPreview sqlRunPreviewEditor';
      body.appendChild(window.bcodeDialogs.makeLabel('Câu lệnh sẽ chạy (đã gộp entity):'));
      body.appendChild(preview);
      let previewEditor = null;
      if (window.monaco && monaco.editor) {
        const fcode = !!(window.bcodeTheme && window.bcodeTheme.theme && window.bcodeTheme.theme.fcodeLexer);
        const language = fcode && window.FCODE_LANGUAGE_ID ? 'fcode-cdata' : 'sql';
        previewEditor = monaco.editor.create(preview, {
          value: sql,
          language,
          readOnly: true,
          domReadOnly: true,
          automaticLayout: true,
          minimap: { enabled: false },
          lineNumbers: 'on',
          scrollBeyondLastLine: false,
          wordWrap: 'off',
          fontFamily: window.bcodeTheme ? window.bcodeTheme.fontFamily : "'Roboto', Consolas, monospace",
          fontSize: 13,
          renderLineHighlight: 'none',
          contextmenu: false,
        });
      } else {
        const area = document.createElement('textarea');
        area.className = 'dlgTextarea';
        area.style.cssText = 'width:100%;height:100%;box-sizing:border-box';
        area.readOnly = true;
        area.value = sql;
        preview.appendChild(area);
      }

      const runBtn = window.bcodeDialogs.makeButton('Chạy', 'primary');
      const cancelBtn = window.bcodeDialogs.makeButton('Hủy');
      runBtn.onclick = () => {
        for (const [name, input] of inputs) this.paramValues[name] = input.value;
        for (const [name, input] of macroInputs) this.macroValues[name] = input.value;
        finish({ rollback: rollbackBox.checked });
      };
      cancelBtn.onclick = () => finish(null);
      // Mở cùng câu lệnh (tham số đã nhập → DECLARE sẵn) trong tab SQL Query của Bcode để chạy/sửa/chạy từng bước.
      const debugBtn = window.bcodeDialogs.makeButton('Debug trong Bcode');
      debugBtn.title = 'Mở câu lệnh trong tab SQL Query của Bcode, tham số đã nhập được khai báo sẵn bằng DECLARE';
      debugBtn.onclick = () => {
        for (const [name, input] of inputs) this.paramValues[name] = input.value;
        for (const [name, input] of macroInputs) this.macroValues[name] = input.value;
        finish({ rollback: rollbackBox.checked, debug: true });
      };
      // Enter trong ô tham số = Chạy (như bấm nút).
      for (const input of [...inputs.values(), ...macroInputs.values()])
        input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); runBtn.click(); } });
      // Hàng nút nằm NGOÀI phần cuộn, luôn thấy ở đáy hộp.
      const buttons = window.bcodeDialogs.makeButtonRow([debugBtn, cancelBtn, runBtn]);
      buttons.classList.add('sqlRunButtons');
      box.appendChild(buttons);

      document.body.appendChild(overlay);
      const first = inputs.values().next().value || macroInputs.values().next().value;
      (first || runBtn).focus();
    });
  }

  async execute(sql, rollback, notes, source, params) {
    this.running = true;
    this.cancelled = false;
    this.stopBtn.style.display = '';
    const label = `Đang chạy ${source}${rollback ? ' (chạy thử)' : ''}`;
    this.setStatus(label + '...');
    this.results.innerHTML = '';

    let started;
    try {
      started = JSON.parse(await window.chrome.webview.hostObjects.host.StartSql(
        sql, JSON.stringify(params || {}), rollback));
    } catch (e) {
      this.finish('Lỗi khi chạy: ' + e, true);
      return;
    }
    if (started.error) { this.finish(started.error, true); return; }

    const result = await this.pollUntilDone(started.runId, label);
    if (!result) return; // cancelled — finish() already said so
    if (result.error) { this.finish(result.error, true); return; }

    this.lastResult = result;
    this.stopBtn.style.display = 'none';
    this.running = false;
    this.render(result, notes, rollback);
  }

  /// Asks the host every so often whether the script has finished.
  ///
  /// Polling rather than one call that returns the rows, because a host object call holds
  /// the thread that runs the page for as long as it takes: a slow query would freeze the
  /// editor, and — the part that actually bit — nothing else could get through afterwards,
  /// so "Dừng" was undeliverable by construction. Each poll is a call that returns at once.
  async pollUntilDone(runId, label) {
    const startedAt = Date.now();
    for (;;) {
      if (this.cancelled) return null;
      await new Promise((resolve) => setTimeout(resolve, 250));
      if (this.cancelled) return null;

      let raw;
      try { raw = await window.chrome.webview.hostObjects.host.PollSql(runId); }
      catch (e) { return { error: 'Mất liên lạc với tiến trình chạy: ' + e }; }

      const parsed = JSON.parse(raw);
      if (parsed.status !== 'running') return parsed;

      // A running seconds counter, not an animation: on a server that is not answering the
      // useful information is how long it has been trying, which is what tells you it is
      // the connection rather than the query.
      this.setStatus(`${label} — ${Math.round((Date.now() - startedAt) / 1000)}s (bấm Dừng để thôi)`);
    }
  }

  /// "Dừng". Stops waiting immediately and asks the host to cancel — in that order,
  /// because a connection attempt to an unreachable server can sit in name resolution long
  /// past any timeout, and the panel must not stay stuck to it.
  cancel() {
    if (!this.running) return;
    this.cancelled = true;
    try { window.chrome.webview.hostObjects.host.CancelSql(); } catch { /* nothing left to cancel */ }
    this.finish('Đã dừng.', false);
  }

  finish(message, isError) {
    this.running = false;
    this.stopBtn.style.display = 'none';
    this.setStatus(message, isError);
  }

  // ---- Results ----------------------------------------------------------------------------

  render(result, notes, rollback) {
    this.results.innerHTML = '';

    let totalRows = 0;
    let failed = 0;
    for (const batch of result.batches) {
      if (batch.error) {
        failed++;
        const err = document.createElement('div');
        err.className = 'sqlError';
        err.textContent = (result.batches.length > 1 ? `Batch ${batch.index + 1}: ` : '') + batch.error;
        this.results.appendChild(err);
        continue;
      }

      for (const table of batch.tables) {
        totalRows += table.rows.length;
        this.results.appendChild(this.buildTable(table));
      }

      if (batch.tables.length === 0) {
        const msg = document.createElement('div');
        msg.className = 'sqlMessage';
        msg.textContent = batch.rowsAffected >= 0
          ? `${batch.rowsAffected} dòng bị ảnh hưởng.`
          : 'Thực hiện xong, không có dữ liệu trả về.';
        this.results.appendChild(msg);
      }
    }

    const parts = [`${result.elapsedMs} ms`];
    if (totalRows) parts.push(`${totalRows} dòng`);
    if (failed) parts.push(`${failed} lỗi`);
    if (rollback) parts.push('ĐÃ ROLLBACK — dữ liệu không đổi');
    this.setStatus(parts.concat(notes).join(' · '), failed > 0);
  }

  buildTable(table) {
    const wrap = document.createElement('div');
    wrap.className = 'sqlTableWrap';

    const el = document.createElement('table');
    el.className = 'sqlTable';

    const thead = document.createElement('thead');
    const headRow = document.createElement('tr');
    const rowNumHead = document.createElement('th');
    rowNumHead.className = 'rowNum';
    headRow.appendChild(rowNumHead);
    for (const col of table.columns) {
      const th = document.createElement('th');
      th.textContent = col.name;
      th.title = `${col.name} — ${col.type}`;
      headRow.appendChild(th);
    }
    thead.appendChild(headRow);

    const tbody = document.createElement('tbody');
    table.rows.forEach((row, i) => {
      const tr = document.createElement('tr');
      const num = document.createElement('td');
      num.className = 'rowNum';
      num.textContent = i + 1;
      tr.appendChild(num);
      for (const cell of row) {
        const td = document.createElement('td');
        if (cell === null) {
          // A dimmed NULL, so it can't be confused with an empty string — the difference
          // decides whether a WHERE clause matches.
          td.className = 'nullCell';
          td.textContent = 'NULL';
        } else {
          td.textContent = cell;
          td.title = cell;
        }
        tr.appendChild(td);
      }
      tbody.appendChild(tr);
    });

    el.append(thead, tbody);
    wrap.appendChild(el);

    if (table.truncated) {
      const note = document.createElement('div');
      note.className = 'sqlMessage';
      note.textContent = `Chỉ hiển thị ${table.rows.length} dòng đầu — thêm TOP/WHERE nếu cần xem tiếp.`;
      wrap.appendChild(note);
    }
    return wrap;
  }

  /// Tab-separated, which is what Excel and SSMS both paste cleanly.
  copyResult() {
    if (!this.lastResult) return;
    const lines = [];
    for (const batch of this.lastResult.batches) {
      for (const table of batch.tables || []) {
        lines.push(table.columns.map((c) => c.name).join('\t'));
        for (const row of table.rows) lines.push(row.map((c) => (c === null ? 'NULL' : c)).join('\t'));
        lines.push('');
      }
    }
    navigator.clipboard.writeText(lines.join('\n'));
    this.copyBtn.textContent = 'Đã copy';
    setTimeout(() => { this.copyBtn.textContent = 'Copy kết quả'; }, 1200);
  }

  setStatus(text, isError) {
    this.status.textContent = text;
    this.status.classList.toggle('error', !!isError);
  }
}

window.BcodeSqlRun = BcodeSqlRun;
