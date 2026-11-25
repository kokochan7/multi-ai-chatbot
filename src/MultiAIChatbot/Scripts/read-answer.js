// Reads the newest AI answer on the page.
// The C# side fills in the site config (JSON with answerSelectors) below.
// Returns JSON: { count: number of answers on the page, text: newest answer's text, streaming: bool }
(() => {
  const config = __CONFIG__;
  let nodes = [];
  for (const sel of config.answerSelectors) {
    nodes = Array.from(document.querySelectorAll(sel));
    if (nodes.length) break;
  }
  const last = nodes[nodes.length - 1];
  const streaming = !!document.querySelector(
    "[data-is-streaming='true'], button[data-testid='stop-button'], button[aria-label*='Stop']");
  return JSON.stringify({
    count: nodes.length,
    text: last ? last.innerText.trim() : '',
    streaming
  });
})();
