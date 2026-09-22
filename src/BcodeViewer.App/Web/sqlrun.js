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

class BcodeSqlRun {
  constructor(bcode) {
    this.bcode = bcode;
    this.dock = window.bcodeDock || (window.bcodeDock = new BcodeDock());
    this.pane = this.dock.register('sqlPanel', 'SQL');
    /// Parameter values, remembered for the session: the same @ma_dvcs gets asked for by
    /// every command in the file, and retyping it each run is how a useful tool becomes an
    /// annoying one.
    this.paramValues = {};
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

  async run() {
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
    const needsDialog = info.parameters.length > 0 || info.writes.length > 0;
    let rollback = info.writes.length > 0;
    if (needsDialog) {
      const answer = await this.askBeforeRunning(sql, info, notes);
      if (!answer) return;
      rollback = answer.rollback;
    }

    await this.execute(sql, rollback, notes, picked.source);
  }

  /// One dialog covering both questions: what are the parameter values, and — for a script
  /// that writes — commit or roll back. They are asked together because they are answered
  /// together, right before the same button.
  askBeforeRunning(sql, info, notes) {
    return new Promise((resolve) => {
      const { overlay, body } = window.bcodeDialogs.makeDialog('Chạy SQL');
      let settled = false;
      const finish = (value) => { if (!settled) { settled = true; overlay.remove(); resolve(value); } };
      overlay.addEventListener('click', (e) => { if (e.target === overlay) finish(null); });

      if (info.workspace) {
        const ws = window.bcodeDialogs.makeLabel('Chạy trên: ' + info.workspace);
        body.appendChild(ws);
      }
      if (notes.length) body.appendChild(window.bcodeDialogs.makeLabel(notes.join(' · ')));

      const inputs = new Map();
      if (info.parameters.length) {
        body.appendChild(window.bcodeDialogs.makeLabel(
          `Tham số (${info.parameters.length}) — để trống nghĩa là NULL:`));
        const grid = document.createElement('div');
        grid.className = 'sqlParamGrid';
        for (const name of info.parameters) {
          const label = document.createElement('label');
          label.textContent = '@' + name;
          const input = document.createElement('input');
          input.type = 'text';
          input.value = this.paramValues[name] || '';
          input.spellcheck = false;
          inputs.set(name, input);
          grid.append(label, input);
        }
        body.appendChild(grid);
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

      const preview = document.createElement('textarea');
      preview.className = 'dlgTextarea';
      preview.rows = 8;
      preview.readOnly = true;
      preview.value = sql;
      body.appendChild(window.bcodeDialogs.makeLabel('Câu lệnh sẽ chạy (đã gộp entity):'));
      body.appendChild(preview);

      const runBtn = window.bcodeDialogs.makeButton('Chạy', 'primary');
      const cancelBtn = window.bcodeDialogs.makeButton('Hủy');
      runBtn.onclick = () => {
        for (const [name, input] of inputs) this.paramValues[name] = input.value;
        finish({ rollback: rollbackBox.checked });
      };
      cancelBtn.onclick = () => finish(null);
      body.appendChild(window.bcodeDialogs.makeButtonRow([cancelBtn, runBtn]));

      document.body.appendChild(overlay);
      const first = inputs.values().next().value;
      (first || runBtn).focus();
    });
  }

  async execute(sql, rollback, notes, source) {
    this.running = true;
    this.cancelled = false;
    this.stopBtn.style.display = '';
    const label = `Đang chạy ${source}${rollback ? ' (chạy thử)' : ''}`;
    this.setStatus(label + '...');
    this.results.innerHTML = '';

    let started;
    try {
      started = JSON.parse(await window.chrome.webview.hostObjects.host.StartSql(
        sql, JSON.stringify(this.paramValues), rollback));
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
