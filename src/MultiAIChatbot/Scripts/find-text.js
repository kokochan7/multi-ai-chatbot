// Ctrl+F search inside one AI page. Marks every match with the browser's CSS Custom Highlight
// API, so the site's own page is never changed (no extra elements that could upset it).
// The C# side fills in __QUERY__ (JSON string) and __STEP__: 0 = search again from the top,
// 1 = next match, -1 = previous match, 'clear' = remove all marks.
// Returns JSON: { count: matches on the page, index: the current match (0-based, -1 if none) }
(() => {
  const query = __QUERY__;
  const step = __STEP__;
  const state = window.__mafFind || (window.__mafFind = { index: -1, scopes: [] });
  const ALL = 'maf-find', CURRENT = 'maf-find-current';

  // Every place text can live: the page, open shadow roots, and same-site frames.
  const scopes = [];   // { root, win }
  const collect = (root, win) => {
    if (scopes.length > 60) return;
    scopes.push({ root, win });
    root.querySelectorAll('*').forEach(el => {
      if (el.shadowRoot) collect(el.shadowRoot, win);
      if (el.tagName === 'IFRAME') {
        let doc = null;
        try { doc = el.contentDocument; } catch (e) { }
        if (doc && doc.body) collect(doc, doc.defaultView);
      }
    });
  };

  const clearMarks = () => {
    for (const s of state.scopes) {
      try { s.win.CSS.highlights.delete(ALL); s.win.CSS.highlights.delete(CURRENT); } catch (e) { }
    }
    state.scopes = [];
  };

  if (step === 'clear' || !query) {
    clearMarks();
    state.index = -1;
    return JSON.stringify({ count: 0, index: -1 });
  }

  if (typeof Highlight === 'undefined' || !window.CSS || !CSS.highlights)
    return JSON.stringify({ count: 0, index: -1, error: 'unsupported' });

  try { collect(document, window); } catch (e) { }

  // The colours for the marks, added once to each page part (styles don't cross into shadow roots).
  const css = '::highlight(' + ALL + '){background-color:#FFE066;color:#111}' +
              '::highlight(' + CURRENT + '){background-color:#FF9632;color:#111}';
  for (const s of scopes) {
    try {
      const host = s.root.nodeType === 9 ? (s.root.head || s.root.documentElement) : s.root;
      if (!host || host.querySelector(':scope > style[data-maf-find]')) continue;
      const style = (s.root.nodeType === 9 ? s.root : s.root.ownerDocument).createElement('style');
      style.setAttribute('data-maf-find', '');
      style.textContent = css;
      host.appendChild(style);
    } catch (e) { }
  }

  // Visible text only: skip scripts, styles, and anything hidden.
  const skipTags = /^(SCRIPT|STYLE|NOSCRIPT|TEMPLATE|TEXTAREA|INPUT|SELECT|OPTION)$/;
  const visibleCache = new Map();
  const isVisible = el => {
    if (!el) return false;
    if (visibleCache.has(el)) return visibleCache.get(el);
    let ok = !skipTags.test(el.tagName);
    if (ok && el.checkVisibility) {
      try { ok = el.checkVisibility({ checkOpacity: false, checkVisibilityCSS: true }); } catch (e) { }
    }
    visibleCache.set(el, ok);
    return ok;
  };

  const needle = query.toLowerCase();
  const matches = [];   // { range, win }
  for (const s of scopes) {
    const doc = s.root.nodeType === 9 ? s.root : s.root.ownerDocument;
    // Join each scope's text so a match can span bold/links ("ASUS <b>ROG</b> Swift").
    const nodes = [], starts = [];
    let text = '';
    const walker = doc.createTreeWalker(s.root, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) {
      if (!n.nodeValue || !isVisible(n.parentElement)) continue;
      nodes.push(n); starts.push(text.length);
      text += n.nodeValue;
    }
    if (!nodes.length) continue;
    const hay = text.toLowerCase();
    if (hay.length !== text.length) continue;   // rare letters whose lowercase changes length
    const locate = pos => {   // text position -> [node, offset]
      let lo = 0, hi = starts.length - 1;
      while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (starts[mid] <= pos) lo = mid; else hi = mid - 1; }
      return [nodes[lo], pos - starts[lo]];
    };
    for (let at = hay.indexOf(needle); at >= 0 && matches.length < 2000; at = hay.indexOf(needle, at + needle.length)) {
      try {
        const range = doc.createRange();
        const [sn, so] = locate(at);
        const [en, eo] = locate(at + needle.length - 1);
        range.setStart(sn, so);
        range.setEnd(en, eo + 1);
        matches.push({ range, win: s.win });
      } catch (e) { }
    }
  }

  clearMarks();
  const count = matches.length;
  if (!count) { state.index = -1; return JSON.stringify({ count: 0, index: -1 }); }

  // New search: start at the first match on screen (or the first one below it).
  let index;
  if (step === 0 || state.index < 0) {
    index = matches.findIndex(m => {
      const r = m.range.getBoundingClientRect();
      return r.height > 0 && r.bottom >= 0;
    });
    if (index < 0) index = 0;
    if (step !== 0) index = (index + (step > 0 ? 0 : count - 1)) % count;
  } else {
    index = ((Math.min(state.index, count - 1) + step) % count + count) % count;
  }
  state.index = index;

  // Mark all matches in each page part, plus the current one in orange.
  const byWin = new Map();
  for (const m of matches) {
    if (!byWin.has(m.win)) byWin.set(m.win, []);
    byWin.get(m.win).push(m.range);
  }
  for (const [win, ranges] of byWin) {
    try {
      win.CSS.highlights.set(ALL, new win.Highlight(...ranges));
      state.scopes.push({ win });
    } catch (e) { }
  }
  const cur = matches[index];
  try {
    const h = new cur.win.Highlight(cur.range);
    h.priority = 1;
    cur.win.CSS.highlights.set(CURRENT, h);
  } catch (e) { }

  // Bring the current match into view.
  if (step !== 0 || index >= 0) {
    const el = cur.range.startContainer.parentElement;
    const r = cur.range.getBoundingClientRect();
    const vh = (cur.win.innerHeight || 0);
    if (el && (step !== 0 || r.top < 0 || r.bottom > vh))
      el.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'instant' });
  }
  return JSON.stringify({ count, index });
})()
