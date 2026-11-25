// Looks at the page around file attaching. The C# side fills in the site config.
// Returns JSON: { x, y } = middle of the chat box (for a real drop), attached = how many file
// previews are showing, signedOut = a visible "Sign in" button (signed-out sites often refuse files).
(() => {
  const config = __CONFIG__;
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    return r.width > 0 && r.height > 0;
  };

  let editor = null;
  for (const sel of config.editorSelectors) {
    editor = Array.from(document.querySelectorAll(sel)).find(visible);
    if (editor) break;
  }
  let x = -1, y = -1;
  if (editor) {
    editor.scrollIntoView({ block: 'nearest' });
    const r = editor.getBoundingClientRect();
    x = Math.round(r.left + r.width / 2);
    y = Math.round(r.top + r.height / 2);
  }

  const previews = new Set();
  for (const sel of (config.attachmentSelectors || [])) {
    try { document.querySelectorAll(sel).forEach(el => { if (visible(el)) previews.add(el); }); } catch (e) { }
  }
  document.querySelectorAll('img[src^="blob:"], img[src^="data:image"]').forEach(el => {
    if (visible(el)) previews.add(el);
  });

  const signedOut = Array.from(document.querySelectorAll('a, button'))
    .some(el => visible(el) && /^\s*sign in\s*$/i.test(el.textContent || ''));

  return JSON.stringify({ x, y, attached: previews.size, signedOut });
})();
