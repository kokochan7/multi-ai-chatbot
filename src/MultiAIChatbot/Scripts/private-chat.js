// Turns on a site's private chat (Claude's Incognito, Gemini's Temporary chat) by clicking its
// button, as a person would. The C# side fills in a pattern for the button's label.
// Returns "clicked", "already" (it's on) or "none" (no such button yet, or signed out).
(() => {
  const want = new RegExp(__WANT__, 'i');
  const turnOff = /exit|leave|turn off|disable|stop|end /i;
  const visible = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
  const candidates = Array.from(document.querySelectorAll(
    'button, a, [role="button"], [role="switch"], [role="menuitem"], [role="menuitemcheckbox"]')).filter(visible);
  for (const el of candidates) {
    const label = (el.getAttribute('aria-label') || el.getAttribute('title') || el.getAttribute('data-tooltip') ||
      el.textContent || '').trim();
    if (!want.test(label)) continue;
    if (turnOff.test(label)) return 'already';
    if (el.getAttribute('aria-pressed') === 'true' || el.getAttribute('aria-checked') === 'true' ||
        el.getAttribute('aria-selected') === 'true' || el.getAttribute('data-state') === 'on') return 'already';
    el.click();
    return 'clicked';
  }
  return 'none';
})();
