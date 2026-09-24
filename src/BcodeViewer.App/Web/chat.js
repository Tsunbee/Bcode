// AI chat panel: sends the user's question plus the active tab's full content as context
// to EditorBridge.AskAI (C# side calls the Anthropic Messages API — see ClaudeChatService).
// Không auto-apply code model đề xuất; một code block chỉ render như text để người dùng tự
// copy. Câu trả lời hiện dần theo từng đoạn — xem bcodeHost.callStreaming.

class BcodeChat {
  constructor(messagesId, inputId, sendBtnId, viewer) {
    this.messagesEl = document.getElementById(messagesId);
    this.inputEl = document.getElementById(inputId);
    this.sendBtn = document.getElementById(sendBtnId);
    this.panelEl = document.getElementById('chatPanel');
    this.reopenBtn = document.getElementById('chatReopenBtn');
    this.viewer = viewer;

    this.sendBtn.onclick = () => this.send();
    this.inputEl.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        this.send();
      }
    });

    // ✕ hides the panel (like VSCode's side panels) — a small floating "AI" tab appears
    // in its place to bring it back, rather than losing the toggle entirely.
    document.getElementById('chatCloseBtn').onclick = () => this.setVisible(false);
    this.reopenBtn.onclick = () => this.setVisible(true);
  }

  setVisible(visible) {
    this.panelEl.style.display = visible ? 'flex' : 'none';
    this.reopenBtn.style.display = visible ? 'none' : 'flex';
    if (this.viewer) this.viewer.editor.layout(); // editor width changed — Monaco needs a nudge
  }

  /// Người dùng có đang ở đáy khung chat không. Ngưỡng vài pixel vì scrollTop là số thực khi
  /// màn hình có tỉ lệ phóng to khác 100%, nên so bằng tuyệt đối sẽ gần như luôn sai.
  isScrolledToBottom() {
    const el = this.messagesEl;
    return el.scrollHeight - el.scrollTop - el.clientHeight < 8;
  }

  appendMessage(role, text) {
    const el = document.createElement('div');
    el.className = 'chatMsg ' + role;
    el.textContent = text;
    this.messagesEl.appendChild(el);
    this.messagesEl.scrollTop = this.messagesEl.scrollHeight;
    return el;
  }

  async send() {
    const prompt = this.inputEl.value.trim();
    if (!prompt) return;
    this.inputEl.value = '';
    this.appendMessage('user', prompt);
    this.sendBtn.disabled = true;

    const thinking = this.appendMessage('assistant', '...');
    try {
      const context = this.viewer ? this.viewer.getActiveContent() : null;
      const filePath = this.viewer ? this.viewer.activePath : null;

      // Trả lời hiện dần theo từng đoạn. Chỉ cuộn khi người dùng đang ở đáy — đang đọc lại
      // đoạn trên mà bị kéo xuống theo từng chữ thì không đọc được gì.
      let streamed = '';
      const reply = await window.bcodeHost.callStreaming('BeginAskAI', (chunk) => {
        if (!streamed) thinking.textContent = ''; // bỏ "..." ở đoạn đầu tiên
        streamed += chunk;
        thinking.textContent = streamed;
        if (this.isScrolledToBottom()) this.messagesEl.scrollTop = this.messagesEl.scrollHeight;
      }, prompt, context, filePath);

      // Luôn lấy kết quả cuối làm chuẩn, không dùng chuỗi đã ghép: đó mới là bản đầy đủ, và
      // là chỗ duy nhất báo lỗi (lỗi không được stream — xem AskAsync).
      thinking.textContent = reply;
    } catch (e) {
      thinking.className = 'chatMsg error';
      thinking.textContent = 'Lỗi: ' + e;
    } finally {
      this.sendBtn.disabled = false;
    }
  }
}
