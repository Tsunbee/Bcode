# Assets/

Bee's app icon — a bee (🐝), matching the app's owner and the "B" in "Bcode".
Generated programmatically (rendered from the Noto Color Emoji font, cropped/padded to a
square, exported at multiple sizes), not hand-drawn or taken from anywhere else.

- `bee.ico` — multi-size icon (16–256px). Used two ways: as the project's
  `<ApplicationIcon>` (the .exe file's own icon, e.g. in Explorer/the taskbar shortcut) and,
  embedded as a resource, as `MainForm.Icon` at runtime (title bar / Alt+Tab / running
  taskbar icon) — see `UI/AppIcons.cs`.
- `bee_16.png` — the same bee at a fixed 16x16, used as the icon in front of every node
  (file and folder) in the File Lookup tree.
- `bee_master.png` — the source square crop everything else was resized from, kept in case
  the icon needs to be re-exported at a different size later.
