// Attaches files to an AI site's chat box, as if the user pasted or dropped them.
// The C# side fills in the site config and the files ([{ name, type, data (base64) }]).
// Returns "paste", "input", "drop" or "none".
(() => {
  const config = __CONFIG__;
  const files = __FILES__.map(f => {
    const bin = atob(f.data);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return new File([bytes], f.name, { type: f.type || 'application/octet-stream' });
  });
  const transfer = () => {
    const dt = new DataTransfer();
    files.forEach(f => dt.items.add(f));
    return dt;
  };

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

  // 1. Paste into the chat box: ChatGPT, Claude and Gemini all accept pasted files.
  if (editor) {
    editor.focus();
    const paste = new ClipboardEvent('paste', { clipboardData: transfer(), bubbles: true, cancelable: true });
    editor.dispatchEvent(paste);
    if (paste.defaultPrevented) return 'paste';
  }

  // 2. The page's hidden file picker, if it has one that accepts these files.
  const accepts = (input, file) => {
    const accept = (input.accept || '').trim();
    if (!accept) return true;
    return accept.split(',').map(a => a.trim().toLowerCase()).some(a =>
      a === file.type.toLowerCase() ||
      (a.endsWith('/*') && file.type.toLowerCase().startsWith(a.slice(0, -1))) ||
      (a.startsWith('.') && file.name.toLowerCase().endsWith(a)));
  };
  const input = Array.from(document.querySelectorAll('input[type="file"]'))
    .find(i => (i.multiple || files.length === 1) && files.every(f => accepts(i, f)));
  if (input) {
    try {
      input.files = transfer().files;
      input.dispatchEvent(new Event('input', { bubbles: true }));
      input.dispatchEvent(new Event('change', { bubbles: true }));
      return 'input';
    } catch (e) { /* fall through */ }
  }

  // 3. Drop onto the chat box.
  if (editor) {
    const dt = transfer();
    for (const type of ['dragenter', 'dragover', 'drop']) {
      editor.dispatchEvent(new DragEvent(type, { dataTransfer: dt, bubbles: true, cancelable: true }));
    }
    return 'drop';
  }
  return 'none';
})();
