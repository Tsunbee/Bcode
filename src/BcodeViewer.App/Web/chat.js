// AI chat panel: sends the user's question plus the active tab's full content as context
// to EditorBridge.AskAI (C# side calls the Anthropic Messages API — see ClaudeChatService).
// v1 has no streaming and no auto-apply of code the model suggests; a code block just
// renders as text the user can copy, keeping the first cut small.

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
      const reply = await window.chrome.webview.hostObjects.host.AskAI(prompt, context, filePath);
      thinking.textContent = reply;
    } catch (e) {
      thinking.className = 'chatMsg error';
      thinking.textContent = 'Lỗi: ' + e;
    } finally {
      this.sendBtn.disabled = false;
    }
  }
}
