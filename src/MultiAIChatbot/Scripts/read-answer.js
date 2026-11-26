// Reads the newest AI answer on the page as Markdown (headings, bold, lists, links, code, tables),
// leaving out the site's own buttons, icons, "Searched the web" steps and source chips.
// The C# side fills in the site config (JSON with answerSelectors) and which answer to read
// (__INDEX__: -1 for the newest, or 0, 1, 2… counting from the first answer on the page).
// Returns JSON: { count: number of answers on the page, text: newest answer, streaming: bool,
//   info: what the page looked like, written to a log when no answer could be read }
(() => {
  const config = __CONFIG__;
  // Uses the first selector that finds answers with text in them. Empty placeholders (a
  // "thinking" step, a row kept for buttons) are left out so they don't count as the answer.
  const hasText = el => (el.innerText || el.textContent || '').trim().length > 0;

  // The answers may sit inside a page part of their own (an open shadow root, or a same-site
  // frame), so search those too, not only the main page.
  const roots = [];
  let frames = [];
  const collect = root => {
    if (roots.length > 50) return;
    roots.push(root);
    root.querySelectorAll('*').forEach(el => {
      if (el.shadowRoot) collect(el.shadowRoot);
      if (el.tagName === 'IFRAME') {
        let src = '';
        try { src = new URL(el.src, location.href).host; } catch (e) { }
        let doc = null;
        try { doc = el.contentDocument; } catch (e) { }
        frames.push((src || 'blank') + (doc ? '' : '(closed)'));
        if (doc && doc.body) collect(doc);
      }
    });
  };
  try { collect(document); } catch (e) { }
  const qsa = sel => roots.flatMap(r => { try { return Array.from(r.querySelectorAll(sel)); } catch (e) { return []; } });
  const qs = sel => roots.some(r => { try { return !!r.querySelector(sel); } catch (e) { return false; } });

  let nodes = [];
  const diag = [];
  for (const sel of config.answerSelectors) {
    const found = qsa(sel);
    const withText = found.filter(hasText);
    diag.push(sel + '=' + found.length + '/' + withText.length);
    if (!nodes.length && withText.length)
      nodes = withText.filter(n => !withText.some(o => o !== n && o.contains(n)));   // outermost only
  }
  // ChatGPT's newer layout builds answers from components (data-d-component) with no answer
  // marker around them: the components side by side under one parent make up one answer.
  if (!nodes.length) {
    const parts = qsa('[data-d-component]').filter(hasText);
    const outer = parts.filter(n => !parts.some(o => o !== n && o.contains(n)));
    const groups = [];
    outer.forEach(n => { const p = n.parentElement; if (p && !groups.includes(p)) groups.push(p); });
    nodes = groups.filter(g => !groups.some(o => o !== g && o.contains(g)));
    diag.push('components=' + outer.length + '/' + nodes.length);
  }
  const index = __INDEX__;
  const last = index >= 0 ? nodes[index] : nodes[nodes.length - 1];
  const streaming = qs(
    "[data-is-streaming='true'], button[data-testid='stop-button'], button[aria-label='Stop streaming'], button[aria-label='Stop response'], button[aria-label='Stop generating'], " +
    "li[data-message-role='assistant']:not([data-message-complete]), [data-assistant-markdown-root] [aria-busy='true']");

  // The answer's own text lives in "markdown" blocks; tool steps and chips sit next to them.
  const CONTENT = "[data-assistant-markdown-root], .standard-markdown, .progressive-markdown, .markdown, .prose, [class*='markdown']";
  const SKIP = "button, svg, style, script, noscript, img, picture, video, canvas, input, textarea, select, " +
    "[aria-hidden='true'], .sr-only, [role='button'], [role='status'], [role='tooltip'], [role='toolbar'], " +
    "[data-testid*='citation'], [class*='citation'], source-footnote, sources-carousel, .katex-mathml, [class*='mapboxgl']";
  // In ChatGPT's component layout a listed place or product is a button with its name in bold;
  // keep those (other buttons are the site's own controls).
  const componentAnswer = !!last && !!last.querySelector('[data-d-component]');
  const keepButton = el => componentAnswer && el.tagName === 'BUTTON' && !!el.querySelector('strong, b');

  const isHidden = el => {
    const s = getComputedStyle(el);
    if (s.display === 'none' || s.visibility === 'hidden') return true;
    if (s.position === 'absolute') {            // screen-reader-only text ("Claude responded:")
      const r = el.getBoundingClientRect();
      if (r.width <= 1 && r.height <= 1) return true;
    }
    return false;
  };
  // Rounded little boxes inside a sentence are source chips ("goat", "nike"), not text.
  const isChip = el => {
    if (!el.closest('p, li, td') || el.matches('code, strong, b, em, i')) return false;
    const s = getComputedStyle(el);
    return (s.display === 'inline-flex' || s.display === 'inline-block') &&
      (parseFloat(s.borderTopLeftRadius) > 3) && (s.backgroundColor !== 'rgba(0, 0, 0, 0)' || parseFloat(s.borderTopWidth) > 0);
  };

  const BLOCK = /^(P|DIV|SECTION|ARTICLE|H[1-6]|UL|OL|LI|PRE|BLOCKQUOTE|TABLE|HR|FIGURE|HEADER|FOOTER|DETAILS)$/;

  const inline = (node, inPre) => {
    let out = '';
    node.childNodes.forEach(child => { out += convert(child, inPre, 0); });
    return out;
  };

  const listItems = (list, depth) => {
    const ordered = list.tagName === 'OL';
    let n = parseInt(list.getAttribute('start') || '1', 10);
    let out = '';
    Array.from(list.children).filter(li => li.tagName === 'LI').forEach(li => {
      let text = '', nested = '';
      li.childNodes.forEach(c => {
        if (c.nodeType === 1 && (c.tagName === 'UL' || c.tagName === 'OL')) nested += listItems(c, depth + 1);
        else text += convert(c, false, depth);
      });
      text = text.replace(/\n{2,}/g, '\n').trim().replace(/\n/g, '\n' + '  '.repeat(depth + 1));
      out += '  '.repeat(depth) + (ordered ? (n++) + '. ' : '- ') + text + '\n' + nested;
    });
    return out;
  };

  const table = el => {
    const rows = Array.from(el.querySelectorAll('tr'));
    if (!rows.length) return '';
    let out = '';
    rows.forEach((tr, i) => {
      const cells = Array.from(tr.children).map(td => inline(td, false).replace(/\s+/g, ' ').replace(/\|/g, '\\|').trim());
      out += '| ' + cells.join(' | ') + ' |\n';
      if (i === 0) out += '|' + cells.map(() => ' --- |').join('') + '\n';
    });
    return '\n\n' + out + '\n';
  };

  const convert = (node, inPre, depth) => {
    if (node.nodeType === 3) return inPre ? node.textContent : node.textContent.replace(/\s+/g, ' ');
    if (node.nodeType !== 1) return '';
    const el = node;
    if ((el.matches(SKIP) && !keepButton(el)) || isHidden(el) || isChip(el)) return '';
    const tag = el.tagName;
    if (tag === 'BUTTON') return '\n- ' + Array.from(el.childNodes).map(c => convert(c, false, depth).trim()).filter(t => t).join(' ').replace(/\s+/g, ' ') + '\n';   // a kept place/product
    switch (tag) {
      case 'BR': return '\n';
      case 'HR': return '\n\n---\n\n';
      case 'H1': case 'H2': case 'H3': case 'H4': case 'H5': case 'H6':
        return '\n\n' + '#'.repeat(+tag[1]) + ' ' + inline(el).trim() + '\n\n';
      case 'STRONG': case 'B': { const t = inline(el); return t.trim() ? '**' + t.trim() + '**' + (/\s$/.test(t) ? ' ' : '') : t; }
      case 'EM': case 'I': { const t = inline(el); return t.trim() ? '*' + t.trim() + '*' + (/\s$/.test(t) ? ' ' : '') : t; }
      case 'CODE': return inPre ? el.textContent : '`' + el.textContent + '`';
      case 'PRE': {
        const code = el.querySelector('code') || el;
        const lang = ((code.className || '').match(/language-([\w+#-]+)/) || [])[1] || '';
        return '\n\n```' + lang + '\n' + code.textContent.replace(/\n+$/, '') + '\n```\n\n';
      }
      case 'A': {
        const text = inline(el).trim();
        const href = el.href || '';
        if (!text) return '';
        return /^https?:/.test(href) && text !== href ? '[' + text + '](' + href + ')' : text;
      }
      case 'UL': case 'OL': return '\n\n' + listItems(el, depth) + '\n';
      case 'BLOCKQUOTE':
        return '\n\n' + inline(el).trim().split('\n').map(l => '> ' + l).join('\n') + '\n\n';
      case 'TABLE': return table(el);
    }
    const body = inline(el, inPre);
    return BLOCK.test(tag) ? '\n\n' + body + '\n\n' : body;
  };

  const toMarkdown = root => {
    if (!root) return '';
    let blocks = root.matches(CONTENT) ? [root] : Array.from(root.querySelectorAll(CONTENT));
    blocks = blocks.filter(b => !blocks.some(o => o !== b && o.contains(b)));   // outermost only
    const parts = (blocks.length ? blocks : [root]).map(b => convert(b, false, 0));
    return parts.join('\n\n')
      .replace(/[\uE000-\uF8FF\u25A1\uFFFD]/g, '')          // icon-font glyphs (□)
      .replace(/[ \t]+\n/g, '\n')
      .replace(/\n{3,}/g, '\n\n')
      .trim();
  };

  let text = '';
  try { text = toMarkdown(last); } catch (e) { text = ''; }
  if (!text && last) text = last.innerText.trim();
  // What the page is made of, so a new layout can be matched: page parts, text size, and the
  // most common data-* attribute names (no page text is logged).
  let info = location.pathname + location.search + ' | ' + diag.join(' ');
  try {
    const attrs = {};
    roots.forEach(r => r.querySelectorAll('*').forEach(el => {
      for (const a of el.attributes) if (a.name.startsWith('data-')) attrs[a.name] = (attrs[a.name] || 0) + 1;
    }));
    const top = Object.entries(attrs).sort((a, b) => b[1] - a[1]).slice(0, 30).map(([k, v]) => k + '=' + v).join(' ');
    info += ' | roots=' + roots.length + ' frames=[' + frames.slice(0, 8).join(',') + ']' +
      ' text=' + (document.body ? document.body.innerText.length : 0) +
      ' markdown=' + qsa('.markdown, [data-assistant-markdown-root], [class*=markdown]').length +
      ' main=' + qsa('main').length + ' articles=' + qsa('article').length +
      ' | attrs: ' + top;
  } catch (e) { info += ' | info failed: ' + e; }
  return JSON.stringify({ count: nodes.length, text, streaming, info });
})();
