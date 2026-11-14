// Bhoo — bộ biểu tượng tự vẽ (SVG 24×24, bóng kiểu đầu những năm 2000) thay cho emoji của hệ điều hành
// (emoji mỗi máy Windows vẽ một kiểu, trông lộn xộn trên thanh công cụ). Không lấy hình của sản phẩm nào.
'use strict';

const ICONS = (() => {
  let n = 0;
  const g = (c1, c2) => { const id = 'ig' + (++n); return [id, `<linearGradient id="${id}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${c1}"/><stop offset="1" stop-color="${c2}"/></linearGradient>`]; };
  const svg = (defs, body) => `<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><defs>${defs}</defs>${body}</svg>`;
  const shine = '<path d="M5 6c3-3 11-3 14 0" fill="none" stroke="#fff" stroke-opacity=".55" stroke-width="1.2" stroke-linecap="round"/>';
  return {
    buzz() {
      const [a, da] = g('#ffe76a', '#f0a000');
      return svg(da, `<rect x="7.5" y="3" width="9" height="18" rx="2" fill="url(#${a})" stroke="#9a5a00"/><rect x="9.3" y="5.5" width="5.4" height="9" rx=".6" fill="#fff" stroke="#9a5a00" stroke-width=".6"/>
        <circle cx="12" cy="18" r="1.1" fill="#9a5a00"/><path d="M4.5 7.5l-2 2 2 2-2 2 2 2M19.5 7.5l2 2-2 2 2 2-2 2" fill="none" stroke="#d4141e" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"/>`);
    },
    call() {
      const [a, da] = g('#7fe07a', '#1f8a2a');
      return svg(da, `<path d="M6.2 3.2l2.6-.6 2 4.4-1.9 1.4c.9 2.2 2.5 3.9 4.7 4.8l1.4-1.9 4.4 2-.6 2.6c-.3 1.2-1.4 2-2.6 1.9C9.4 17.3 3.7 11.6 3.2 5.6c-.1-1.2.7-2.3 1.9-2.4z" fill="url(#${a})" stroke="#145c1b" stroke-linejoin="round"/>
        <path d="M5.6 4.6c.6-.3 1.5-.4 2.3-.4" stroke="#fff" stroke-opacity=".7" stroke-linecap="round" fill="none"/>`);
    },
    video() {
      const [a, da] = g('#8ec5ff', '#2a62c0');
      return svg(da, `<rect x="2.5" y="6.5" width="13" height="11" rx="2" fill="url(#${a})" stroke="#1a3f80"/><path d="M15.5 10.5l6-3.5v10l-6-3.5z" fill="url(#${a})" stroke="#1a3f80" stroke-linejoin="round"/>
        <circle cx="6.5" cy="10" r="1.3" fill="#fff" fill-opacity=".8"/>`);
    },
    file() {
      const [a, da] = g('#ffffff', '#dfe6f0');
      const [b, db] = g('#9fc2ee', '#3b72c4');
      return svg(da + db, `<path d="M5.5 2.5h9l4 4v15h-13z" fill="url(#${a})" stroke="#5a6b85" stroke-linejoin="round"/><path d="M14.5 2.5v4h4" fill="#dfe6f0" stroke="#5a6b85" stroke-linejoin="round"/>
        <path d="M8 10h8M8 12.5h8M8 15h5" stroke="#9aa8bd"/><path d="M12 22v-6.5a2 2 0 0 1 4 0V20a3.4 3.4 0 0 1-6.8 0v-5" fill="none" stroke="url(#${b})" stroke-width="1.6" stroke-linecap="round"/>`);
    },
    scene() {
      const [a, da] = g('#f6dcae', '#d29a4e');
      return svg(da, `<path d="M12 2.8c5.3 0 9.2 3.6 9.2 7.8 0 2.6-2 3.6-3.8 3.6h-1.8c-1.3 0-2 1.2-1.4 2.3.8 1.4-.1 3.3-2.3 3.3C6.6 19.8 2.8 16 2.8 11.4 2.8 6.6 6.9 2.8 12 2.8z" fill="url(#${a})" stroke="#8a5a1c"/>
        <circle cx="7.5" cy="10.5" r="1.6" fill="#d4141e"/><circle cx="10.5" cy="6.5" r="1.6" fill="#f0b400"/><circle cx="15" cy="6.8" r="1.6" fill="#2e9e3e"/><circle cx="17.6" cy="10.4" r="1.6" fill="#2a62c0"/><circle cx="8.2" cy="15" r="1.7" fill="#fff" stroke="#8a5a1c" stroke-width=".6"/>`);
    },
    fun() {
      const [a, da] = g('#ff8fb8', '#d0245e');
      const [b, db] = g('#ffe36a', '#f0a000');
      return svg(da + db, `<path d="M3 21l5.5-14 9 9z" fill="url(#${b})" stroke="#9a5a00" stroke-linejoin="round"/><path d="M5.6 14.4l4 4M7 10.8l6.2 6.2" stroke="#d4141e" stroke-width="1.3"/>
        <circle cx="16" cy="4.5" r="1.4" fill="url(#${a})"/><circle cx="20" cy="9" r="1.2" fill="#2a8ad8"/><circle cx="19.5" cy="3.5" r=".9" fill="#2e9e3e"/>
        <path d="M12 6c.5-1.6 2-2.4 3.3-2.1M14.5 10c1.6-.4 3.2.3 3.8 1.6M11 3.2l.6-1.4M21.5 13.5l1.2.4" fill="none" stroke="#d0245e" stroke-width="1.2" stroke-linecap="round"/>`);
    },
    invite() {
      const [a, da] = g('#9fc7f5', '#2f68b8');
      const [b, db] = g('#ffd38a', '#e0902a');
      return svg(da + db, `<circle cx="8.5" cy="7.5" r="3.4" fill="url(#${b})" stroke="#8a5212"/><path d="M2 20c0-4 2.9-6.6 6.5-6.6S15 16 15 20z" fill="url(#${a})" stroke="#1a3f80" stroke-linejoin="round"/>
        <circle cx="16.5" cy="8.5" r="2.8" fill="url(#${b})" stroke="#8a5212"/><path d="M13.6 14.3c.9-.6 1.9-.9 2.9-.9 3 0 5.5 2.2 5.5 5.6h-5.6" fill="url(#${a})" stroke="#1a3f80" stroke-linejoin="round"/>`);
    },
    clear() {
      const [a, da] = g('#ffb0b0', '#e05252');
      return svg(da, `<path d="M3.8 15.2l9-9a2 2 0 0 1 2.8 0l3.2 3.2a2 2 0 0 1 0 2.8L12 19H7.6z" fill="url(#${a})" stroke="#8a1c1c" stroke-linejoin="round"/>
        <path d="M3.8 15.2l3.8 3.8H12" fill="#f4f4f4" stroke="#8a1c1c" stroke-linejoin="round"/><path d="M8 11l4.9 4.9" stroke="#8a1c1c"/><path d="M12 20.5h9" stroke="#9a978a" stroke-linecap="round"/>`);
    },
    rooms() {
      const [a, da] = g('#ffd765', '#e08a00');
      return svg(da, `<path d="M4 3.5h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2h-9l-5 4v-4H4a2 2 0 0 1-2-2v-10a2 2 0 0 1 2-2z" fill="url(#${a})" stroke="#8a5212" stroke-linejoin="round"/>
        <path d="M10 6.5l-1.3 8M14.5 6.5l-1.3 8M7.5 9h9M7 12.2h9" stroke="#fff" stroke-width="1.5" stroke-linecap="round"/>`);
    },
    newroom() {
      const [a, da] = g('#8fd0ff', '#2a78c8');
      return svg(da, `<path d="M3 4h12a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H9l-4 3.5V15H3a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z" fill="url(#${a})" stroke="#1a4a85" stroke-linejoin="round"/>
        <circle cx="19" cy="17" r="4.3" fill="#2e9e3e" stroke="#145c1b"/><path d="M19 14.6v4.8M16.6 17h4.8" stroke="#fff" stroke-width="1.6" stroke-linecap="round"/>${shine}`);
    },
    addip() {
      const [a, da] = g('#cfe3fb', '#6b97cf');
      return svg(da, `<rect x="2" y="3.5" width="16" height="11" rx="1.5" fill="url(#${a})" stroke="#1a4a85"/><rect x="4" y="5.5" width="12" height="7" fill="#1f4882"/>
        <path d="M7 17.5h6M10 14.5v3" stroke="#1a4a85" stroke-width="1.4"/><circle cx="18.5" cy="17.5" r="4.3" fill="#2e9e3e" stroke="#145c1b"/><path d="M18.5 15.1v4.8M16.1 17.5h4.8" stroke="#fff" stroke-width="1.6" stroke-linecap="round"/>`);
    },
    history() {
      const [a, da] = g('#fff6d8', '#e6c97e');
      return svg(da, `<path d="M5 3.5h12.5v15a2.5 2.5 0 0 1-2.5 2.5H5z" fill="url(#${a})" stroke="#8a6a1c"/><path d="M8 7.5h7M8 10h7M8 12.5h4" stroke="#a88a3c"/>
        <circle cx="16.5" cy="16.5" r="4.6" fill="#fff" stroke="#2a62c0" stroke-width="1.3"/><path d="M16.5 14v2.7l1.8 1.1" fill="none" stroke="#2a62c0" stroke-width="1.3" stroke-linecap="round"/>`);
    },
    chat() {
      const [a, da] = g('#ffffff', '#cfe0f5');
      return svg(da, `<path d="M4 4h16a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2h-8l-5 4v-4H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z" fill="url(#${a})" stroke="#2a5a9a" stroke-linejoin="round"/>
        <circle cx="8" cy="10.5" r="1.2" fill="#2a5a9a"/><circle cx="12" cy="10.5" r="1.2" fill="#2a5a9a"/><circle cx="16" cy="10.5" r="1.2" fill="#2a5a9a"/>`);
    },
    block() {
      return svg('', `<circle cx="12" cy="12" r="8.5" fill="#fff" stroke="#c0281c" stroke-width="2.4"/><path d="M6 6l12 12" stroke="#c0281c" stroke-width="2.4"/>`);
    },
    picture() {
      const [a, da] = g('#bfe3ff', '#ffffff');
      return svg(da, `<rect x="2.5" y="4.5" width="19" height="15" rx="1.5" fill="url(#${a})" stroke="#4a5a75"/><circle cx="8" cy="9.5" r="2" fill="#f5b400"/>
        <path d="M3 18.5l6-6 4 4 3-3 5 5" fill="#4caf50" stroke="#2e7a32" stroke-linejoin="round"/>`);
    },
    movie() {
      const [a, da] = g('#555', '#111');
      return svg(da, `<rect x="2.5" y="8.5" width="19" height="12" rx="1.5" fill="url(#${a})"/><path d="M2.5 8.5l18-4.2.6 2.5-18 4.2z" fill="#333" stroke="#111"/>
        <path d="M6 7.7l2.2 1.8M10.5 6.6l2.2 1.8M15 5.6l2.2 1.8" stroke="#fff" stroke-width="1.3"/><path d="M10 12l5 2.5-5 2.5z" fill="#ffd200"/>`);
    },
    game() {
      const [a, da] = g('#9a7bd8', '#4a2a96');
      return svg(da, `<path d="M6.5 7.5h11c2.6 0 4.2 4.8 4.4 8.4.1 2-1.9 2.9-3.3 1.5l-2.3-2.4H7.7l-2.3 2.4c-1.4 1.4-3.4.5-3.3-1.5.2-3.6 1.8-8.4 4.4-8.4z" fill="url(#${a})" stroke="#2a1660" stroke-linejoin="round"/>
        <path d="M7.5 9.8v3.4M5.8 11.5h3.4" stroke="#fff" stroke-width="1.4" stroke-linecap="round"/><circle cx="16" cy="10.5" r="1" fill="#ffd200"/><circle cx="18" cy="12.5" r="1" fill="#4cd964"/>`);
    },
    send() {
      const [a, da] = g('#ffffff', '#d9e6f7');
      return svg(da, `<path d="M2.5 11.2L21 3.5l-4.2 17-5.3-4.6-3 3.3v-5.2z" fill="url(#${a})" stroke="#2a4f8a" stroke-linejoin="round"/><path d="M8.5 14l12.3-10.3" stroke="#2a4f8a"/><path d="M8.5 14v5.2l3-3.3" fill="#9fb8dc" stroke="#2a4f8a" stroke-linejoin="round"/>`);
    },
    search() {
      return svg('', `<circle cx="10" cy="10" r="6" fill="#e8f2ff" stroke="#2a5a9a" stroke-width="1.8"/><path d="M14.5 14.5l6 6" stroke="#8a5212" stroke-width="3" stroke-linecap="round"/>`);
    },
  };
})();

/** HTML 1 biểu tượng (dùng trong menu: icon: ico('call')). */
function ico(name, cls) { return `<span class="ico ${cls || 'mic'}">${ICONS[name] ? ICONS[name]() : ''}</span>`; }

/** Điền mọi <span data-ic="tên"> trong trang. */
function fillIcons(root) {
  (root || document).querySelectorAll('[data-ic]').forEach(s => { if (!s.firstChild && ICONS[s.dataset.ic]) s.innerHTML = ICONS[s.dataset.ic](); });
}
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', () => fillIcons()); else fillIcons();
