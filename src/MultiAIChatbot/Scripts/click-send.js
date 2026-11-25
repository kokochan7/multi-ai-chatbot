// Presses an AI site's Send button once it becomes clickable.
// Returns "clicked", "waiting" (button not ready yet) or "enter" (fell back to the Enter key).
(() => {
  const config = __CONFIG__;
  const useEnter = __USE_ENTER__;

  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    return r.width > 0 && r.height > 0;
  };

  if (!useEnter) {
    for (const sel of config.sendSelectors) {
      const btn = Array.from(document.querySelectorAll(sel)).find(visible);
      if (btn) {
        if (btn.disabled || btn.getAttribute('aria-disabled') === 'true') return 'waiting';
        btn.click();
        return 'clicked';
      }
    }
    return 'waiting';
  }

  const target = document.activeElement || document.body;
  for (const type of ['keydown', 'keypress', 'keyup']) {
    target.dispatchEvent(new KeyboardEvent(type, {
      key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true
    }));
  }
  return 'enter';
})();
