// Hộp thoại thông báo / xác nhận / nhập chữ theo theme của trang — thay alert/confirm/prompt của trình
// duyệt (hộp xám kiểu Windows, không theo theme, chặn cả trang) và MessageBox bên WinForms (MainForm gọi
// qua bcodeViewer.uiDialog, xem EditorBridge.ResolveUiDialog).
//
//   await bcodeUi.alert('Đã lưu', { title: 'BcodeViewer', kind: 'info' | 'warning' | 'error' })
//   if (await bcodeUi.confirm('Xoá?', { okText: 'Xoá', danger: true })) ...
//   const name = await bcodeUi.prompt('Tên mới:', 'abc')   // null = Huỷ
//
// Nhiều hộp gọi liền nhau thì xếp hàng, hiện lần lượt. window.alert được thay bằng bản không chặn (code cũ
// chỉ "báo rồi đi tiếp" nên không cần chờ); confirm/prompt phải await nên các chỗ gọi đã được sửa.

class BcodeUi {
  constructor() {
    this.queue = Promise.resolve();
  }

  alert(message, opts = {}) {
    return this.enqueue(() => this.show({ ...opts, message, buttons: [{ text: opts.okText || 'OK', value: true, primary: true }] }))
      .then(() => undefined);
  }

  confirm(message, opts = {}) {
    return this.enqueue(() => this.show({
      ...opts, message,
      buttons: [
        { text: opts.cancelText || 'Huỷ', value: false },
        { text: opts.okText || 'Đồng ý', value: true, primary: true, danger: !!opts.danger },
      ],
    })).then((v) => v === true);
  }

  prompt(message, defaultValue = '', opts = {}) {
    return this.enqueue(() => this.show({
      ...opts, message, input: defaultValue == null ? '' : String(defaultValue),
      buttons: [
        { text: opts.cancelText || 'Huỷ', value: null },
        { text: opts.okText || 'OK', value: '__input__', primary: true },
      ],
    }));
  }

  enqueue(fn) {
    const run = this.queue.then(fn, fn);
    this.queue = run.catch(() => {});
    return run;
  }

  show({ title, message, kind, input, buttons }) {
    return new Promise((resolve) => {
      const overlay = document.createElement('div');
      overlay.className = 'dlgOverlay uiOverlay';
      const box = document.createElement('div');
      box.className = 'dlgBox uiBox' + (kind ? ' ui-' + kind : '');
      const header = document.createElement('div');
      header.className = 'dlgHeader tplHeader';
      const t = document.createElement('span');
      t.textContent = title || 'BcodeViewer';
      const x = document.createElement('span');
      x.className = 'tplClose';
      x.textContent = '✕';
      header.append(t, x);
      const body = document.createElement('div');
      body.className = 'uiBody';
      if (kind) {
        const icon = document.createElement('span');
        icon.className = 'uiIcon';
        icon.textContent = kind === 'error' ? '⛔' : kind === 'warning' ? '⚠' : kind === 'question' ? '?' : 'ℹ';
        body.appendChild(icon);
      }
      const content = document.createElement('div');
      content.className = 'uiContent';
      const msg = document.createElement('div');
      msg.className = 'uiMessage';
      msg.textContent = message == null ? '' : String(message);
      content.appendChild(msg);
      let field = null;
      if (input !== undefined) {
        field = document.createElement('input');
        field.type = 'text';
        field.className = 'setInput uiInput';
        field.value = input;
        field.spellcheck = false;
        content.appendChild(field);
      }
      body.appendChild(content);

      const row = document.createElement('div');
      row.className = 'dlgButtonRow uiButtons';
      let primaryBtn = null;
      const cancelValue = buttons.length > 1 ? buttons[0].value : buttons[0].value;
      const done = (value) => {
        document.removeEventListener('keydown', onKey, true);
        overlay.remove();
        resolve(value === '__input__' ? field.value : value);
      };
      for (const b of buttons) {
        const btn = document.createElement('button');
        btn.className = 'dlgButton' + (b.primary ? ' primary' : '') + (b.danger ? ' danger' : '');
        btn.textContent = b.text;
        btn.onclick = () => done(b.value);
        if (b.primary) primaryBtn = btn;
        row.appendChild(btn);
      }
      x.onclick = () => done(cancelValue);
      const onKey = (e) => {
        if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); done(cancelValue); }
        else if (e.key === 'Enter' && e.target.tagName !== 'BUTTON') { e.preventDefault(); e.stopPropagation(); primaryBtn && primaryBtn.click(); }
      };
      document.addEventListener('keydown', onKey, true);

      box.append(header, body, row);
      overlay.appendChild(box);
      document.body.appendChild(overlay);
      if (field) { field.focus(); field.select(); } else if (primaryBtn) primaryBtn.focus();
    });
  }
}

window.bcodeUi = new BcodeUi();

// Bản không chặn của alert: mọi chỗ cũ chỉ báo rồi đi tiếp.
window.alert = (message) => { window.bcodeUi.alert(message); };
