// Types a prompt into an AI site's chat box.
// The C# side fills in the site config (JSON with editorSelectors) and the prompt text below.
(() => {
  const config = __CONFIG__;
  const text = __TEXT__;

  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    return r.width > 0 && r.height > 0 && getComputedStyle(el).visibility !== 'hidden';
  };

  let editor = null;
  for (const sel of config.editorSelectors) {
    editor = Array.from(document.querySelectorAll(sel)).find(visible);
    if (editor) break;
  }
  if (!editor) {
    // Fallback: the last visible text box on the page.
    const all = Array.from(document.querySelectorAll('[contenteditable="true"], textarea')).filter(visible);
    editor = all[all.length - 1] || null;
  }
  if (!editor) return JSON.stringify({ ok: false, reason: 'no-editor' });

  const currentText = () => (editor.tagName === 'TEXTAREA' ? editor.value : editor.innerText).trim();
  const normalized = s => s.replace(/\s+/g, ' ').trim();

  editor.focus();

  if (editor.tagName === 'TEXTAREA') {
    const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set;
    setter.call(editor, text);
    editor.dispatchEvent(new Event('input', { bubbles: true }));
  } else {
    // Clear whatever is there, then insert like a real user would.
    const range = document.createRange();
    range.selectNodeContents(editor);
    const sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(range);
    document.execCommand('delete', false);
    document.execCommand('insertText', false, text);

    // Fallback for editors that ignore execCommand: simulate a paste.
    if (normalized(currentText()) !== normalized(text)) {
      try {
        const dt = new DataTransfer();
        dt.setData('text/plain', text);
        editor.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
      } catch (e) { /* ignore */ }
    }
  }

  const ok = normalized(currentText()).length > 0;
  return JSON.stringify({ ok, reason: ok ? 'inserted' : 'insert-failed' });
})();
