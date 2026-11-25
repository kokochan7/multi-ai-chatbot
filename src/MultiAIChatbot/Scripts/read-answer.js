// Reads the newest AI answer on the page as Markdown (headings, bold, lists, links, code, tables),
// leaving out the site's own buttons, icons, "Searched the web" steps and source chips.
// The C# side fills in the site config (JSON with answerSelectors) below.
// Returns JSON: { count: number of answers on the page, text: newest answer, streaming: bool }
(() => {
  const config = __CONFIG__;
  let nodes = [];
  for (const sel of config.answerSelectors) {
    nodes = Array.from(document.querySelectorAll(sel));
    if (nodes.length) break;
  }
  const last = nodes[nodes.length - 1];
  const streaming = !!document.querySelector(
    "[data-is-streaming='true'], button[data-testid='stop-button'], button[aria-label='Stop streaming'], button[aria-label='Stop response'], button[aria-label='Stop generating']");

  // The answer's own text lives in "markdown" blocks; tool steps and chips sit next to them.
  const CONTENT = ".standard-markdown, .progressive-markdown, .markdown, .prose, [class*='markdown']";
  const SKIP = "button, svg, style, script, noscript, img, picture, video, canvas, input, textarea, select, " +
    "[aria-hidden='true'], .sr-only, [role='button'], [role='status'], [role='tooltip'], [role='toolbar'], " +
    "[data-testid*='citation'], [class*='citation'], source-footnote, sources-carousel, .katex-mathml";

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
    if (el.matches(SKIP) || isHidden(el) || isChip(el)) return '';
    const tag = el.tagName;
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
    let blocks = Array.from(root.querySelectorAll(CONTENT));
    blocks = blocks.filter(b => !blocks.some(o => o !== b && o.contains(b)));   // outermost only
    const parts = (blocks.length ? blocks : [root]).map(b => convert(b, false, 0));
    return parts.join('\n\n')
      .replace(/[-□�]/g, '')          // icon-font glyphs (□)
      .replace(/[ \t]+\n/g, '\n')
      .replace(/\n{3,}/g, '\n\n')
      .trim();
  };

  let text = '';
  try { text = toMarkdown(last); } catch (e) { text = ''; }
  if (!text && last) text = last.innerText.trim();
  return JSON.stringify({ count: nodes.length, text, streaming });
})();
