// File > Settings... — vẽ ngay trong trang thay cho SettingsForm (WinForms) để cùng theme, cùng kiểu
// với mọi hộp thoại khác của trang (.dlgOverlay/.dlgBox, màu lấy từ biến --bc-* của theme đang chọn).
// Đọc/ghi qua EditorBridge.GetSettings / BeginSaveSettings; hộp chọn thư mục/file vẫn là của Windows
// (BeginChooseFolder / BeginChooseFile) vì trang không tự mở được hộp chọn đường dẫn trên máy.

class BcodeSettingsDialog {
  async show() {
    if (document.getElementById('settingsOverlay')) return; // đang mở rồi
    let s;
    try { s = JSON.parse(await window.chrome.webview.hostObjects.host.GetSettings()); }
    catch (e) { alert('Không đọc được Settings:\n' + e); return; }

    const overlay = document.createElement('div');
    overlay.id = 'settingsOverlay';
    overlay.className = 'dlgOverlay';
    const box = document.createElement('div');
    box.className = 'dlgBox setBox';
    const header = document.createElement('div');
    header.className = 'dlgHeader';
    header.textContent = 'BcodeViewer — Settings';
    const body = document.createElement('div');
    body.className = 'setBody';
    box.append(header, body);
    overlay.appendChild(box);

    const grid = document.createElement('div');
    grid.className = 'setGrid';
    body.appendChild(grid);

    const section = (text) => {
      const h = document.createElement('div');
      h.className = 'setSection';
      h.textContent = text;
      grid.appendChild(h);
    };
    const note = (text) => {
      const n = document.createElement('div');
      n.className = 'setNote';
      n.textContent = text;
      grid.appendChild(n);
    };
    // Một dòng: nhãn | ô nhập | nút phụ (tuỳ chọn)
    const row = (label, control, trailing) => {
      const l = document.createElement('label');
      l.className = 'setLabel';
      l.textContent = label;
      grid.appendChild(l);
      control.classList.add('setControl');
      if (!trailing) control.classList.add('setSpan');
      grid.appendChild(control);
      if (trailing) grid.appendChild(trailing);
      return control;
    };
    const input = (value, password) => {
      const i = document.createElement('input');
      i.type = password ? 'password' : 'text';
      i.className = 'setInput';
      i.value = value || '';
      i.spellcheck = false;
      return i;
    };
    const check = (text, checked) => {
      const wrap = document.createElement('label');
      wrap.className = 'setCheck';
      const c = document.createElement('input');
      c.type = 'checkbox';
      c.checked = !!checked;
      wrap.append(c, document.createTextNode(' ' + text));
      wrap.box = c;
      return wrap;
    };
    const button = (text, onClick) => {
      const b = document.createElement('button');
      b.className = 'dlgButton';
      b.textContent = text;
      b.onclick = onClick;
      return b;
    };

    section('Giao diện editor');
    const font = input(s.editorFontFamily);
    font.placeholder = "Để trống = mặc định ('Roboto', Consolas, monospace)";
    font.setAttribute('list', 'settingsFontList');
    const fonts = document.createElement('datalist');
    fonts.id = 'settingsFontList';
    for (const f of ['Consolas', "'Cascadia Code', Consolas, monospace", "'Cascadia Mono', Consolas, monospace",
      "'JetBrains Mono', Consolas, monospace", "'Fira Code', Consolas, monospace", "'Courier New', monospace",
      "'Roboto', Consolas, monospace"]) fonts.appendChild(new Option(f));
    grid.appendChild(fonts);
    row('Phông chữ editor:', font);
    const fcodeStyle = document.createElement('select');
    fcodeStyle.className = 'setInput';
    for (const [v, t] of [['bcode', 'Bcode — chỉ đổi màu editor'], ['fcode', 'Fcode — giống FcodeViewer']]) fcodeStyle.add(new Option(t, v));
    fcodeStyle.value = s.fcodeThemeStyle === 'fcode' ? 'fcode' : 'bcode';
    row('Kiểu theme Fcode (.xml):', fcodeStyle);
    note('Áp dụng cho theme nhập từ file .xml của FcodeViewer; khung app (menu, toolbar, cây thư mục) luôn giữ màu Dark+/Light+. Bcode: chỉ editor đổi màu. Fcode: editor còn dùng font ghi trong theme (<Font name>, vượt mục Phông chữ ở trên) và bỏ các đường kẻ dọc thụt lề như Fcode.');
    note('VS Code mặc định dùng Consolas trên Windows — chọn Consolas để chữ trông giống VS Code nhất. Phông phải có sẵn trên máy (Cascadia có sẵn trên Windows 11; JetBrains Mono / Fira Code phải tự cài).');

    section('AI (chat + gợi ý)');
    const apiKey = row('Anthropic API key:', input(s.anthropicApiKey, true));
    const geminiKey = row('Gemini API key:', input(s.geminiApiKey, true));
    const model = row('Model (chat):', input(s.model));
    const aiCompletion = row('', check('Bật gợi ý AI khi gõ (ghost text)', s.enableAiCompletion));
    note('Mỗi lần ngừng gõ ~0,4 giây sẽ gọi Claude 1 lần và bị tính phí. Ctrl+I (gọi tay) vẫn chạy kể cả khi tắt mục này.');
    const completionModel = row('Model (ghost text):', input(s.completionModel));
    const engine = document.createElement('select');
    engine.className = 'setInput';
    for (const [v, t] of [['claude', 'Claude'], ['gemini', 'Gemini']]) engine.add(new Option(t, v));
    engine.value = s.completionEngine === 'gemini' ? 'gemini' : 'claude';
    row('Engine gợi ý (ghost text):', engine);
    const translateEngine = document.createElement('select');
    translateEngine.className = 'setInput';
    for (const [v, t] of [['google', 'Google (không cần API key)'], ['gemini', 'Gemini (cần Gemini API key)'], ['claude', 'Claude (cần Anthropic API key)']]) translateEngine.add(new Option(t, v));
    translateEngine.value = ['gemini', 'claude'].includes(s.translateEngine) ? s.translateEngine : 'google';
    row('Engine dịch caption:', translateEngine);
    note('Dùng cho "Dịch caption (v → e)". Google gọi endpoint web của Google Translate — không cần key nhưng không chính thức (có thể bị giới hạn tốc độ) và dịch từng caption không có ngữ cảnh; Gemini/Claude dịch tốt hơn nhờ biết tên field và thuật ngữ ERP.');
    note('Chọn Gemini vẫn dùng nguyên prompt như Claude, chỉ đổi nơi gửi request — cần điền Gemini API key ở trên. Chat panel và Ctrl+I không đổi theo mục này, luôn dùng Claude.');

    section('Template dùng chung');
    const sharedPath = input(s.sharedTemplatePath);
    row('Thư mục chung:', sharedPath, button('Browse...', async () => {
      const p = await window.bcodeHost.call('BeginChooseFolder', sharedPath.value);
      if (p) sharedPath.value = p;
    }));
    note('Thư mục UNC hoặc 1 repo git cả nhóm đọc được. Trong đó: *.code-snippets (định dạng VSCode) và *.json là snippet gõ theo prefix; thư mục con files\\ là template cả file cho Ctrl+N. BcodeViewer chỉ ĐỌC thư mục này.');

    section('Gợi ý SQL');
    const sqlStatus = document.createElement('div');
    sqlStatus.className = 'setNote';
    const sqlCompletion = row('', check('Gợi ý tên bảng / cột trong vùng SQL', s.enableSqlCompletion), button('Test', async () => {
      sqlStatus.textContent = 'Đang kiểm tra...';
      try { sqlStatus.textContent = await window.chrome.webview.hostObjects.host.DescribeSqlStatus(); }
      catch (e) { sqlStatus.textContent = 'Lỗi: ' + e; }
    }));
    grid.appendChild(sqlStatus);
    const sqlWrites = row('', check('Cho phép "Chạy SQL" thực thi câu ghi (INSERT/UPDATE/DELETE/EXEC...)', s.enableSqlWrites));
    note('Mặc định TẮT. Khi tắt, câu có lệnh ghi vẫn chạy được ở chế độ "Chạy thử (rollback)" — chạy trong transaction rồi luôn rollback, nên vẫn biết được số dòng bị ảnh hưởng mà không đổi dữ liệu. Chỉ bật mục này khi bạn thực sự muốn ghi thật: BcodeViewer chạy trên đúng WS mà Bcode đang chọn, thường là CSDL thật của khách.');
    const fcodeConfig = input(s.fcodeConfigXmlPath);
    row('Config.xml của FCode:', fcodeConfig, button('Browse...', async () => {
      const p = await window.bcodeHost.call('BeginChooseFile', fcodeConfig.value);
      if (p) fcodeConfig.value = p;
    }));
    const fcodePassword = row('Mật khẩu SQL:', input(s.fcodeSqlPassword, true));
    note('Dự phòng cho "Chạy SQL" / gợi ý SQL khi Bcode CHƯA có workspace nào (chưa có settings.json): tự kết nối theo project trong Config.xml của FCode (khớp theo thư mục của file đang mở, không khớp thì lấy project đầu). Password trong Config.xml bị FCode mã hoá nên phải nhập mật khẩu SQL ở đây.');
    const regionTags = row('Thẻ chứa SQL:', input(s.sqlRegionTags));
    note('Thường để trống. BcodeViewer đã tự nhận cấu trúc controller: command và action là SQL, script và clientScript là JavaScript, css là CSS, CDATA có /* <flatten type="Javascript"> */ là JavaScript, và giá trị <!ENTITY> được phân loại theo nội dung. Chỉ khai thêm ở đây nếu dự án bạn còn thẻ riêng nào khác chứa SQL (cách nhau bằng dấu phẩy).');
    note('Dùng lại đúng workspace (WS) mà Bcode đang chọn — không cần khai báo kết nối riêng. Chỉ kết nối khi mở file .sql, và schema lấy về được nhớ sẵn cho tới khi đóng app.');

    const close = () => { overlay.remove(); document.removeEventListener('keydown', onKey, true); };
    const okBtn = document.createElement('button');
    okBtn.className = 'dlgButton primary';
    okBtn.textContent = 'OK';
    okBtn.onclick = async () => {
      okBtn.disabled = true;
      const data = {
        anthropicApiKey: apiKey.value, geminiApiKey: geminiKey.value, model: model.value,
        enableAiCompletion: aiCompletion.box.checked, completionModel: completionModel.value,
        completionEngine: engine.value, translateEngine: translateEngine.value, sharedTemplatePath: sharedPath.value,
        enableSqlCompletion: sqlCompletion.box.checked, enableSqlWrites: sqlWrites.box.checked,
        fcodeConfigXmlPath: fcodeConfig.value, fcodeSqlPassword: fcodePassword.value,
        sqlRegionTags: regionTags.value, editorFontFamily: font.value, fcodeThemeStyle: fcodeStyle.value,
      };
      try {
        await window.bcodeHost.call('BeginSaveSettings', JSON.stringify(data));
      } catch (e) {
        alert('Không lưu được Settings:\n' + e);
        okBtn.disabled = false;
        return;
      }
      close();
      if (window.bcodeCompletion) window.bcodeCompletion.reloadSnippets();
      if (window.bcodeTheme) window.bcodeTheme.init(); // áp phông chữ mới cho mọi editor đang mở
      if (window.bcodeViewer && window.bcodeViewer.showToast) window.bcodeViewer.showToast('Đã lưu Settings', 2500);
    };
    const cancelBtn = document.createElement('button');
    cancelBtn.className = 'dlgButton';
    cancelBtn.textContent = 'Cancel';
    cancelBtn.onclick = close;
    const buttons = document.createElement('div');
    buttons.className = 'dlgButtonRow setButtons';
    buttons.append(okBtn, cancelBtn);
    box.appendChild(buttons);

    // Esc = Cancel, Enter (ngoài ô nhập nhiều dòng) = OK — như hộp thoại cũ.
    const onKey = (e) => {
      if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(); }
      else if (e.key === 'Enter' && e.target.tagName !== 'BUTTON') { e.preventDefault(); e.stopPropagation(); okBtn.click(); }
    };
    document.addEventListener('keydown', onKey, true);

    document.body.appendChild(overlay);
    apiKey.focus();
  }
}

window.bcodeSettings = new BcodeSettingsDialog();
