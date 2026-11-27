// Replaces uBlock Origin's `src/js/publicsuffix.js` when the engine is bundled
// for Mosaic.
//
// The original picks between Firefox's built-in `browser.publicSuffix` API and
// uBO's own Public Suffix List implementation, loading the latter with a
// top-level `await import(...)`. Mosaic's bundle is a classic script (it has to
// load in JavaScriptCore, which has no module loader), where top-level await
// does not exist, and the extension API is never present anyway. So this
// always uses uBO's own implementation, imported statically.
export { default } from '../lib/publicsuffixlist/publicsuffixlist.js';
