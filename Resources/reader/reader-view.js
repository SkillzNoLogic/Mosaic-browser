/* Mosaic reader view for Chromium tabs.
 *
 * Mosaic evaluates this in the page, inside a function that also contains Mozilla's
 * Readability.js and Readability-readerable.js (vendored next to this file), so none of
 * their names leak into the page. It defines `window.__mosaicReader` once per document:
 *
 *   enter(settings) → { ok, title } | { ok: false, reason }   extract and show the article
 *   apply(settings)                                           restyle an open reader view
 *   exit()                                                    remove it; the page is untouched
 *   isActive()
 *
 * (Class names avoid "page": Readability keeps that class on its output's wrapper.)
 * The reader view is drawn over the page in a closed shadow root, so the page's styles do
 * not reach it, and the page itself is left as it was (scroll position, scripts, forms):
 * leaving reader view simply removes the overlay. Settings changes and the close button are
 * reported to Mosaic through `window.__mosaicReaderPost(json)` when Mosaic has installed it.
 *
 * Settings use Firefox's reader scales (see ReaderSettings.swift):
 *   { font: "sans-serif"|"serif", fontSize: 1…9, contentWidth: 1…9, lineHeight: 1…9,
 *     theme: "light"|"sepia"|"dark" }
 */
/* global Readability */
(function () {
  "use strict";
  if (window.__mosaicReader) {
    return;
  }

  const clamp = value => Math.min(9, Math.max(1, Math.round(Number(value) || 1)));
  const normalise = s => ({
    font: s && s.font === "serif" ? "serif" : "sans-serif",
    fontSize: clamp(s && s.fontSize !== undefined ? s.fontSize : 5),
    contentWidth: clamp(s && s.contentWidth !== undefined ? s.contentWidth : 3),
    lineHeight: clamp(s && s.lineHeight !== undefined ? s.lineHeight : 4),
    theme: s && (s.theme === "sepia" || s.theme === "dark") ? s.theme : "light",
  });

  const CSS = `
/* Important declarations of a shadow tree's :host win over the page's (and inline ones). */
:host {
  all: initial !important; position: fixed !important; inset: 0 !important; z-index: 2147483647 !important;
  display: block !important; visibility: visible !important; opacity: 1 !important;
}
.reader-root {
  position: absolute; inset: 0; overflow: auto; overscroll-behavior: contain;
  background: var(--bg); color: var(--fg); font-family: var(--font);
  -webkit-font-smoothing: antialiased; text-rendering: optimizeLegibility;
  color-scheme: var(--scheme);
}
.reader-root:focus { outline: none; }
.theme-light { --bg: #ffffff; --fg: #15141a; --muted: #5b5b66; --link: #0060df; --rule: #e0e0e6;
  --bar: rgba(249, 249, 251, 0.86); --bar-fg: #15141a; --hover: rgba(21, 20, 26, 0.08); --scheme: light; }
.theme-sepia { --bg: #f4ecd8; --fg: #5b4636; --muted: #7d6a58; --link: #0b5bb5; --rule: #ddd0b0;
  --bar: rgba(237, 226, 199, 0.88); --bar-fg: #5b4636; --hover: rgba(91, 70, 54, 0.1); --scheme: light; }
.theme-dark { --bg: #1c1b22; --fg: #fbfbfe; --muted: #bfbfc9; --link: #8ab4ff; --rule: #3a3944;
  --bar: rgba(43, 42, 51, 0.88); --bar-fg: #fbfbfe; --hover: rgba(251, 251, 254, 0.12); --scheme: dark; }
.font-sans { --font: -apple-system, BlinkMacSystemFont, "Helvetica Neue", Helvetica, Arial, sans-serif; }
.font-serif { --font: Charter, "Iowan Old Style", Georgia, "Times New Roman", serif; }

.toolbar {
  position: sticky; top: 12px; z-index: 1; margin: 12px auto 0; width: max-content; max-width: calc(100% - 24px);
  display: flex; flex-wrap: wrap; align-items: center; justify-content: center; gap: 4px; padding: 5px;
  border-radius: 14px; background: var(--bar); color: var(--bar-fg);
  -webkit-backdrop-filter: blur(18px) saturate(1.6); backdrop-filter: blur(18px) saturate(1.6);
  box-shadow: 0 0 0 1px var(--rule), 0 4px 18px rgba(0, 0, 0, 0.12);
  font: 13px/1 -apple-system, BlinkMacSystemFont, "Helvetica Neue", sans-serif;
}
.group { display: flex; align-items: center; gap: 2px; }
.group + .group { border-left: 1px solid var(--rule); padding-left: 6px; margin-left: 2px; }
button {
  all: unset; box-sizing: border-box; min-width: 32px; height: 32px; padding: 0 8px; border-radius: 9px;
  display: inline-flex; align-items: center; justify-content: center; gap: 4px; cursor: default;
  color: inherit; font: inherit;
}
button:hover { background: var(--hover); }
button:focus-visible { outline: 2px solid var(--link); outline-offset: 1px; }
button[aria-pressed="true"] { background: var(--hover); font-weight: 600; }
button:disabled { opacity: 0.35; }
button svg { width: 16px; height: 16px; fill: none; stroke: currentColor; stroke-width: 1.6;
  stroke-linecap: round; stroke-linejoin: round; }
.serif-label { font-family: Charter, Georgia, serif; font-size: 15px; }
.small-a { font-size: 12px; } .large-a { font-size: 16px; }
.swatch { width: 18px; height: 18px; border-radius: 50%; box-shadow: inset 0 0 0 1px rgba(0, 0, 0, 0.25); }
.swatch-light { background: #ffffff; } .swatch-sepia { background: #f4ecd8; } .swatch-dark { background: #1c1b22; }
.step { min-width: 18px; text-align: center; font-variant-numeric: tabular-nums; opacity: 0.75; }

.container {
  box-sizing: content-box; max-width: var(--content-width); margin: 0 auto; padding: 40px 32px 120px;
  font-size: var(--font-size); line-height: var(--line-height); overflow-wrap: break-word;
}
.domain { display: block; color: var(--muted); font-size: 0.75em; text-decoration: none; margin-bottom: 0.6em; }
.domain:hover { text-decoration: underline; }
h1.title { font-size: 1.6em; line-height: 1.25; margin: 0 0 0.4em; font-weight: 700; }
.byline { color: var(--muted); font-size: 0.8em; margin-bottom: 1.2em; }
hr.rule { border: 0; border-top: 1px solid var(--rule); margin: 0 0 1.6em; }
.content a { color: var(--link); }
.content p, .content ul, .content ol, .content blockquote, .content pre, .content figure, .content table { margin: 0 0 1.2em; }
.content h1, .content h2, .content h3, .content h4, .content h5, .content h6 { line-height: 1.3; margin: 1.6em 0 0.5em; }
.content h2 { font-size: 1.3em; } .content h3 { font-size: 1.15em; } .content h4 { font-size: 1em; }
.content img, .content video, .content svg, .content canvas, .content iframe { max-width: 100%; height: auto; }
.content img { display: block; margin: 0 auto; border-radius: 4px; }
.content figure { margin-left: 0; margin-right: 0; }
.content figcaption { color: var(--muted); font-size: 0.8em; line-height: 1.4; margin-top: 0.5em; text-align: center; }
.content blockquote { border-left: 3px solid var(--rule); padding-left: 1em; margin-left: 0; color: var(--muted); }
.content pre { background: var(--hover); padding: 0.8em 1em; border-radius: 6px; overflow: auto; font-size: 0.85em; line-height: 1.45; }
.content code { font-family: ui-monospace, Menlo, monospace; font-size: 0.9em; }
.content pre code { font-size: inherit; }
.content table { border-collapse: collapse; display: block; overflow-x: auto; font-size: 0.9em; }
.content td, .content th { border: 1px solid var(--rule); padding: 0.3em 0.6em; }
.content ul, .content ol { padding-left: 1.4em; }
.content li { margin-bottom: 0.3em; }
@media (prefers-reduced-transparency: reduce) {
  .toolbar { -webkit-backdrop-filter: none; backdrop-filter: none; background: var(--bg); }
}
@media print { .toolbar { display: none; } }
`;

  const ICONS = {
    close: '<svg viewBox="0 0 16 16"><path d="M4 4l8 8M12 4l-8 8"/></svg>',
    narrower: '<svg viewBox="0 0 16 16"><path d="M1.5 8h4M5.5 8l-2-2M5.5 8l-2 2M14.5 8h-4M10.5 8l2-2M10.5 8l2 2"/></svg>',
    wider: '<svg viewBox="0 0 16 16"><path d="M1.5 8h5M1.5 8l2-2M1.5 8l2 2M14.5 8h-5M14.5 8l-2-2M14.5 8l-2 2"/></svg>',
    tighter: '<svg viewBox="0 0 16 16"><path d="M3 5.5h10M3 8h10M3 10.5h10"/></svg>',
    looser: '<svg viewBox="0 0 16 16"><path d="M3 3h10M3 8h10M3 13h10"/></svg>',
  };

  const state = { host: null, root: null, page: null, settings: normalise(null), saved: null };

  function post(message) {
    try {
      if (typeof window.__mosaicReaderPost === "function") {
        window.__mosaicReaderPost(JSON.stringify(message));
      }
    } catch (_) {}
  }

  function el(tag, attributes, children) {
    const node = document.createElement(tag);
    for (const [name, value] of Object.entries(attributes || {})) {
      if (name === "text") {
        node.textContent = value;
      } else if (name === "html") {
        node.innerHTML = value;
      } else {
        node.setAttribute(name, value);
      }
    }
    for (const child of children || []) {
      node.append(child);
    }
    return node;
  }

  function button(label, content, data, extra) {
    const node = el("button", Object.assign({ type: "button", "aria-label": label, title: label }, data, extra));
    if (typeof content === "string") {
      node.innerHTML = content;
    } else {
      node.append(content);
    }
    return node;
  }

  function buildToolbar() {
    const t = el("div", { class: "toolbar", role: "toolbar", "aria-label": "Reader view settings" });
    t.append(
      el("div", { class: "group" }, [button("Close reader view (Esc)", ICONS.close, { "data-action": "close" })]),
      el("div", { class: "group", role: "group", "aria-label": "Font" }, [
        button("Sans-serif font", el("span", { text: "Sans" }), { "data-font": "sans-serif" }),
        button("Serif font", el("span", { class: "serif-label", text: "Serif" }), { "data-font": "serif" }),
      ]),
      el("div", { class: "group", role: "group", "aria-label": "Text size" }, [
        button("Smaller text", el("span", { class: "small-a", text: "A−" }), { "data-action": "fontSize", "data-step": "-1" }),
        el("span", { class: "step", "data-value": "fontSize", "aria-hidden": "true" }),
        button("Larger text", el("span", { class: "large-a", text: "A+" }), { "data-action": "fontSize", "data-step": "1" }),
      ]),
      el("div", { class: "group", role: "group", "aria-label": "Line width" }, [
        button("Narrower lines", ICONS.narrower, { "data-action": "contentWidth", "data-step": "-1" }),
        button("Wider lines", ICONS.wider, { "data-action": "contentWidth", "data-step": "1" }),
      ]),
      el("div", { class: "group", role: "group", "aria-label": "Line spacing" }, [
        button("Less line spacing", ICONS.tighter, { "data-action": "lineHeight", "data-step": "-1" }),
        button("More line spacing", ICONS.looser, { "data-action": "lineHeight", "data-step": "1" }),
      ]),
      el("div", { class: "group", role: "group", "aria-label": "Background" }, ["light", "sepia", "dark"].map(theme =>
        button(theme[0].toUpperCase() + theme.slice(1) + " background", el("span", { class: "swatch swatch-" + theme }), { "data-theme": theme })
      ))
    );
    t.addEventListener("click", event => {
      const target = event.target.closest("button");
      if (!target) {
        return;
      }
      const s = Object.assign({}, state.settings);
      if (target.dataset.action === "close") {
        post({ type: "closed" });
        exit();
        return;
      }
      if (target.dataset.font) {
        s.font = target.dataset.font;
      } else if (target.dataset.theme) {
        s.theme = target.dataset.theme;
      } else if (target.dataset.action) {
        s[target.dataset.action] = clamp(s[target.dataset.action] + Number(target.dataset.step));
      }
      apply(s);
      post({ type: "settings", settings: state.settings });
    });
    return t;
  }

  function apply(settings) {
    state.settings = normalise(settings);
    const page = state.page;
    if (!page) {
      return false;
    }
    const s = state.settings;
    page.className = "reader-root theme-" + s.theme + (s.font === "serif" ? " font-serif" : " font-sans");
    page.style.setProperty("--font-size", 10 + 2 * s.fontSize + "px");
    page.style.setProperty("--content-width", 20 + 5 * (s.contentWidth - 1) + "em");
    page.style.setProperty("--line-height", 1 + 0.2 * (s.lineHeight - 1) + "em");
    for (const node of state.root.querySelectorAll("[data-font]")) {
      node.setAttribute("aria-pressed", String(node.dataset.font === s.font));
    }
    for (const node of state.root.querySelectorAll("[data-theme]")) {
      node.setAttribute("aria-pressed", String(node.dataset.theme === s.theme));
    }
    for (const node of state.root.querySelectorAll("[data-action][data-step]")) {
      const value = s[node.dataset.action];
      node.disabled = Number(node.dataset.step) < 0 ? value <= 1 : value >= 9;
    }
    const step = state.root.querySelector('[data-value="fontSize"]');
    if (step) {
      step.textContent = String(s.fontSize);
    }
    return true;
  }

  function readingTime(text) {
    const words = (text || "").trim().split(/\s+/).filter(Boolean).length;
    const minutes = Math.max(1, Math.round(words / 230));
    return minutes + " min read";
  }

  function enter(settings) {
    if (state.host) {
      apply(settings);
      return { ok: true, title: document.title };
    }
    let article = null;
    try {
      article = new Readability(document.cloneNode(true), { charThreshold: 400 }).parse();
    } catch (error) {
      return { ok: false, reason: String(error && error.message || error) };
    }
    if (!article || !article.content || (article.textContent || "").trim().length < 200) {
      return { ok: false, reason: "no-article" };
    }

    const host = document.createElement("mosaic-reader-view");
    // Also inline, for the moment before the shadow root's sheet applies.
    for (const [name, value] of [["position", "fixed"], ["inset", "0"], ["z-index", "2147483647"], ["display", "block"]]) {
      host.style.setProperty(name, value, "important");
    }
    const root = host.attachShadow({ mode: "closed" });
    try {
      // Constructed sheets are not inline <style>, so a strict page CSP cannot block them.
      const sheet = new CSSStyleSheet();
      sheet.replaceSync(CSS);
      root.adoptedStyleSheets = [sheet];
    } catch (_) {
      root.append(el("style", { text: CSS }));
    }

    const page = el("div", { class: "reader-root", tabindex: "-1", role: "document", "aria-label": "Reader view" });
    const container = el("main", { class: "container" });
    if (article.lang) {
      container.setAttribute("lang", article.lang);
    }
    if (article.dir) {
      container.setAttribute("dir", article.dir);
    }
    const site = article.siteName || location.hostname.replace(/^www\./, "");
    container.append(el("a", { class: "domain", href: location.href, text: site }));
    container.append(el("h1", { class: "title", text: article.title || document.title }));
    const byline = [article.byline, readingTime(article.textContent)].filter(Boolean).join(" · ");
    container.append(el("div", { class: "byline", text: byline }));
    container.append(el("hr", { class: "rule" }));
    // Readability's output comes from this page's own DOM with scripts removed.
    container.append(el("div", { class: "content", html: article.content }));
    page.append(buildToolbar(), container);
    root.append(page);
    page.addEventListener("keydown", event => {
      if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        post({ type: "closed" });
        exit();
      }
    });

    const html = document.documentElement;
    state.saved = { overflow: html.style.getPropertyValue("overflow"), priority: html.style.getPropertyPriority("overflow") };
    html.style.setProperty("overflow", "hidden", "important");
    html.append(host);
    Object.assign(state, { host, root, page });
    apply(settings);
    page.focus({ preventScroll: true });
    return { ok: true, title: article.title || document.title };
  }

  function exit() {
    if (!state.host) {
      return false;
    }
    state.host.remove();
    const html = document.documentElement;
    if (state.saved && state.saved.overflow) {
      html.style.setProperty("overflow", state.saved.overflow, state.saved.priority);
    } else {
      html.style.removeProperty("overflow");
    }
    Object.assign(state, { host: null, root: null, page: null, saved: null });
    // Mosaic's message function is only installed while reader view is open.
    try {
      delete window.__mosaicReaderPost;
    } catch (_) {}
    return true;
  }

  Object.defineProperty(window, "__mosaicReader", {
    value: Object.freeze({ enter, apply, exit, isActive: () => state.host !== null }),
    configurable: true,
  });
})();
