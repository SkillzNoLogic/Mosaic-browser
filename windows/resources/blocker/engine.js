// Mosaic's built-in content blocker: uBlock Origin's static network filtering
// engine, taken from uBlock Origin's source and wrapped for embedding.
//
// uBlock Origin is Copyright (C) 2014-present Raymond Hill and licensed under
// the GNU General Public License v3 (https://github.com/gorhill/uBlock). This
// file plays the role of uBO's own `platform/nodejs/index.js`: it wires the
// engine's modules together for a host that is neither a browser extension nor
// Node. Scripts/bootstrap.sh bundles it, with the uBO sources it imports, into
// a single script that Mosaic loads in two places:
//
//   - a JavaScriptCore context inside the app, consulted for Chromium requests;
//   - a sandbox in Gecko's parent process, consulted for Mozilla requests.
//
// Both get the same engine, the same filter lists, and so the same decisions.

// Order matters: punycode before the shims (the URL shim uses it), shims
// before any engine module evaluates.
import punycode from 'ublock/lib/punycode.js';
globalThis.__mosaicToASCII = punycode.toASCII;
import './polyfills.js';

import * as sfp from 'ublock/js/static-filtering-parser.js';
import { CompiledListReader, CompiledListWriter } from 'ublock/js/static-filtering-io.js';
import { FilteringContext } from 'ublock/js/filtering-context.js';
import { LineIterator } from 'ublock/js/text-utils.js';
import publicSuffixList from 'ublock/lib/publicsuffixlist/publicsuffixlist.js';
import snfe from 'ublock/js/static-net-filtering.js';
import { entityFromHostname } from 'ublock/js/uri-utils.js';

const fctx = new FilteringContext();
let hasSuffixList = false;

function runDeferredTasks() {
    if (typeof globalThis.__mosaicRunTimers === 'function') {
        globalThis.__mosaicRunTimers();
    }
}

// Same walk as uBO's Node build for network filters. Cosmetic filters
// (`##selector`, `example.com##selector`, `#@#` exceptions) are collected too,
// into Mosaic's own cosmetic store (see "Cosmetic filtering" below); the
// network engine sees exactly the filters it saw before.
function compileList(raw, compiler, writer) {
    const lines = new LineIterator(raw);
    const parser = new sfp.AstFilterParser({ maxTokenLength: snfe.MAX_TOKEN_LENGTH });
    while (lines.eot() === false) {
        let line = lines.next();
        while (line.endsWith(' \\')) {
            if (lines.peek(4) !== '    ') { break; }
            line = line.slice(0, -2).trim() + lines.next().trim();
        }
        parser.parse(line);
        if (parser.isFilter() === false) { continue; }
        if (parser.isNetworkFilter() === false) {
            if (parser.isExtendedFilter()) { cosmeticStore.add(parser); }
            continue;
        }
        compiler.compile(parser, writer);
    }
    parser.selectorCompiler.finish();
    return writer.toString();
}

/** Version of uBlock Origin this engine was built from; replaced at bundle time. */
export const version = __UBO_VERSION__;

/**
 * Loads the Public Suffix List (the text of public_suffix_list.dat). Required
 * before anything else: first-party/third-party decisions depend on it.
 */
export function init(suffixListText) {
    publicSuffixList.parse(suffixListText, punycode.toASCII);
    hasSuffixList = true;
    return true;
}

// Filter lists are loaded in three steps so that a host with an event loop to
// keep responsive (Gecko's main thread) can yield between lists:
// beginLists(), addList() per list, commitLists(). Until commitLists() returns
// the engine must not be asked to match.

let compiler = null;

/** Discards all filters and prepares to receive lists. */
export function beginLists() {
    if (hasSuffixList === false) { throw new Error('init() must be called first'); }
    snfe.reset();
    cosmeticStore.reset();
    compiler = snfe.createCompiler();
}

/** Compiles one filter list (`raw` is the list's text) into the engine. */
export function addList(name, raw) {
    if (compiler === null) { throw new Error('beginLists() must be called first'); }
    if (typeof raw !== 'string' || raw === '') { return; }
    const writer = new CompiledListWriter();
    writer.properties.set('name', name || '');
    snfe.fromCompiled(new CompiledListReader(compileList(raw, compiler, writer)));
}

/** Makes the lists added since beginLists() effective. Returns the filter count. */
export function commitLists() {
    compiler = null;
    cosmeticStore.freeze();
    snfe.freeze();
    snfe.optimize();
    runDeferredTasks();
    return snfe.getFilterCount();
}

/** Convenience for hosts that can afford to load everything in one go. */
export function loadLists(lists) {
    beginLists();
    for (const list of lists) { addList(list.name, list.raw); }
    return commitLists();
}

export function filterCount() {
    return snfe.getFilterCount();
}

/**
 * The engine's verdict on one request.
 *   url        the request URL
 *   originURL  URL of the page (top-level document) the request belongs to
 *   type       a webRequest resource type: script, image, stylesheet, font,
 *              media, xmlhttprequest, sub_frame, websocket, ping, object, other
 * Returns 0 (no filter matched), 1 (blocked) or 2 (explicitly allowed by an
 * exception filter).
 */
export function match(url, originURL, type) {
    return snfe.matchRequest(fctx.fromDetails({ url, originURL, type }));
}

// ---- Request details (blocker dashboard) ------------------------------------

/**
 * Like `match`, and also says which filter decided: `{ result, filter }`,
 * where `result` is 0, 1 or 2 as for `match` and `filter` is the filter's text
 * as uBO's logger shows it (`toLogData().raw`), or '' when none matched.
 * Costs one extra match; meant for the dashboard, not the request path.
 */
export function matchDetails(url, originURL, type) {
    const result = snfe.matchRequest(fctx.fromDetails({ url, originURL, type }));
    const logData = result !== 0 ? snfe.toLogData() : undefined;
    return { result, filter: logData?.raw ?? '' };
}

// ---- Cosmetic filtering -----------------------------------------------------
//
// uBO's own CosmeticFilteringEngine (cosmetic-filtering.js) is wired to the
// extension's background page (µb, vAPI, the logger), so Mosaic keeps the
// compiled filters in a small store of its own, using uBO's parser for the
// syntax and selector validation and uBO's rules for how generic, specific and
// exception filters combine (retrieveSpecificSelectors).
//
// One deliberate difference: uBO injects "lowly generic" selectors (`##.ad`)
// only after its content script has seen a matching class or id in the page.
// Mosaic has no such surveyor, so every generic selector is part of one
// stylesheet that is the same for all pages (browsers bucket rules by their
// rightmost class/id, so unmatched selectors cost little). Specific filters are
// computed per hostname on demand and cached by the host.
//
// Not supported: procedural filters (`:has-text()`, `:upward()`, …, except the
// purely declarative `:style()` and `:matches-media()` forms uBO converts to
// CSS), scriptlets (`##+js(...)`), HTML filters (`##^`) and response-header
// filters. They are counted in `cosmeticStats().unsupported`.

const HIDE = '{display:none!important;}';

// Port of CosmeticFilteringEngine.prototype.cssRuleFromProcedural.
function cssRuleFromProcedural(pfilter) {
    if (pfilter.cssable !== true) { return; }
    const { tasks, action } = pfilter;
    let mq, selector;
    if (Array.isArray(tasks)) {
        if (tasks[0][0] !== 'matches-media') { return; }
        mq = tasks[0][1];
        if (tasks.length > 2) { return; }
        if (tasks.length === 2) {
            if (tasks[1][0] !== 'spath') { return; }
            selector = tasks[1][1];
        }
    }
    let style;
    if (Array.isArray(action)) {
        if (action[0] !== 'style') { return; }
        selector = selector || pfilter.selector;
        style = action[1];
    }
    if (mq === undefined && style === undefined && selector === undefined) { return; }
    if (mq === undefined) { return `${selector}\n{${style}}`; }
    if (style === undefined) { return `@media ${mq} {\n${selector}\n${HIDE}\n}`; }
    return `@media ${mq} {\n${selector}\n{${style}}\n}`;
}

const cosmeticStore = {
    /** Generic hiding selectors (`##sel`). */
    generic: new Set(),
    /**
     * Hostname or entity (`example.*`) -> entries. An entry is '+selector'
     * (hide), '=css rule' (converted procedural) or '-selector' (exception).
     * The key '' holds generic exceptions (`#@#sel`), which apply everywhere.
     */
    specific: new Map(),
    accepted: 0,
    unsupported: 0,
    genericCSS: null,

    reset() {
        this.generic = new Set();
        this.specific = new Map();
        this.globalExceptionSet = undefined;
        this.accepted = 0;
        this.unsupported = 0;
        this.genericCSS = null;
    },

    freeze() {
        this.genericCSS = null;
        this.globalExceptionSet = undefined;
    },

    store(hostname, entry) {
        // Regex and path-based targets (`/re/##…`, `example.com/path##…`) need
        // the page URL; Mosaic matches by hostname only.
        if (hostname.includes('/')) { this.unsupported += 1; return; }
        let list = this.specific.get(hostname);
        if (list === undefined) {
            list = [];
            this.specific.set(hostname, list);
        }
        list.push(entry);
    },

    /** Mirrors CosmeticFilteringEngine.prototype.compile. */
    add(parser) {
        if (parser.isCosmeticFilter() === false || parser.hasError()) {
            this.unsupported += 1;
            return;
        }
        const { compiled } = parser.result;
        if (compiled === undefined) { this.unsupported += 1; return; }
        const isException = parser.isException();
        let hideEntry = `+${compiled}`;
        if (compiled.charCodeAt(0) === 0x7B /* { */) {
            if (isException === false) {
                const rule = cssRuleFromProcedural(JSON.parse(compiled));
                if (rule === undefined) { this.unsupported += 1; return; }
                hideEntry = `=${rule}`;
            }
        }
        if (parser.hasOptions() === false) {
            this.addGeneric(compiled, isException, hideEntry);
            return;
        }
        let applyGlobally = true;
        for (const { hn, not, bad } of parser.getExtFilterDomainIterator()) {
            if (bad) { continue; }
            if (not === false) { applyGlobally = false; }
            if (isException && not) { continue; }
            this.store(hn, (isException || not) ? `-${compiled}` : hideEntry);
        }
        if (applyGlobally) { this.addGeneric(compiled, isException, hideEntry); }
        this.accepted += 1;
    },

    addGeneric(compiled, isException, hideEntry) {
        if (isException) {
            this.store('', `-${compiled}`);
        } else if (hideEntry.charCodeAt(0) === 0x2B /* + */) {
            this.generic.add(compiled);
        } else {
            // uBO does not apply generic procedural filters either.
            this.unsupported += 1;
            return;
        }
        this.accepted += 1;
    },

    collect(out, hostname) {
        let hn = hostname;
        for (;;) {
            const list = this.specific.get(hn);
            if (list !== undefined) { for (const entry of list) { out.add(entry); } }
            const pos = hn.indexOf('.');
            if (pos === -1) { break; }
            hn = hn.slice(pos + 1);
            if (hn === '*') { break; }
        }
    },

    /** Specific CSS and the generic selectors excepted on `hostname`. */
    forHostname(hostname) {
        const own = new Set();
        if (hostname !== '') {
            this.collect(own, hostname);
            const entity = entityFromHostname(hostname);
            if (entity !== '') { this.collect(own, entity); }
        }
        const exceptions = new Set();
        const hide = new Set();
        const rules = [];
        const sort = entry => {
            const body = entry.slice(1);
            switch (entry.charCodeAt(0)) {
            case 0x2D /* - */: exceptions.add(body); break;
            case 0x2B /* + */: hide.add(body); break;
            default: rules.push(body); break;
            }
        };
        for (const entry of own) { sort(entry); }
        // Generic exceptions this site adds; the ones that apply everywhere
        // are already left out of the generic stylesheet.
        const excepted = [];
        for (const selector of exceptions) {
            if (this.generic.has(selector) && this.globalExceptions().has(selector) === false) {
                excepted.push(selector);
            }
        }
        for (const selector of this.globalExceptions()) { exceptions.add(selector); }
        for (const selector of exceptions) { hide.delete(selector); }
        const css = Array.from(hide, selector => `${selector}\n${HIDE}`);
        css.push(...rules);
        return { css: css.join('\n'), exceptions: excepted, selectors: hide.size + rules.length };
    },

    /** Selectors of `#@#selector` filters with no site: excepted everywhere. */
    globalExceptions() {
        if (this.globalExceptionSet === undefined) {
            this.globalExceptionSet = new Set();
            for (const entry of this.specific.get('') ?? []) {
                if (entry.charCodeAt(0) === 0x2D /* - */) { this.globalExceptionSet.add(entry.slice(1)); }
            }
        }
        return this.globalExceptionSet;
    },

    /**
     * Every generic selector, one rule each: in a combined selector list one
     * selector the browser does not support would void the whole rule.
     */
    genericStylesheet(excluded) {
        const global = this.globalExceptions();
        if (excluded === undefined || excluded.length === 0) {
            if (this.genericCSS === null) {
                const out = [];
                for (const selector of this.generic) {
                    if (global.has(selector) === false) { out.push(`${selector}${HIDE}`); }
                }
                this.genericCSS = out.join('\n');
            }
            return this.genericCSS;
        }
        const skip = new Set(excluded);
        const out = [];
        for (const selector of this.generic) {
            if (skip.has(selector) === false && global.has(selector) === false) { out.push(`${selector}${HIDE}`); }
        }
        return out.join('\n');
    },
};

/**
 * Cosmetic filtering for one page's hostname: `{ css, exceptions, selectors }`.
 * `css` hides what the site-specific filters name (with exceptions applied);
 * `exceptions` lists generic selectors this site excepts, to be left out of
 * the generic stylesheet (see `cosmeticGeneric`); `selectors` counts rules.
 */
export function cosmeticFor(hostname) {
    return cosmeticStore.forHostname(String(hostname || '').toLowerCase());
}

/** The generic stylesheet, optionally without the `excluded` selectors. */
export function cosmeticGeneric(excluded) {
    return cosmeticStore.genericStylesheet(excluded);
}

export function cosmeticStats() {
    let specific = 0;
    for (const list of cosmeticStore.specific.values()) { specific += list.length; }
    return {
        generic: cosmeticStore.generic.size,
        specific,
        unsupported: cosmeticStore.unsupported,
    };
}

// ---- Filter validation ("My filters") ---------------------------------------

/** Option names of a network filter (`||a.com^$script,~third-party` -> ['script', 'third-party']). */
function networkOptions(raw) {
    const anchor = raw.lastIndexOf('$');
    if (anchor === -1 || raw.startsWith('/') && raw.endsWith('/')) { return []; }
    return raw.slice(anchor + 1).split(',')
        .map(option => option.trim().replace(/^~/, '').split('=')[0].toLowerCase())
        .filter(option => option !== '');
}

const knownOptionPattern = new RegExp('^(?:' + [
    '1p', '3p', 'all', 'badfilter', 'cname', 'css', 'csp', 'denyallow', 'doc', 'document', 'domain', 'ehide',
    'elemhide', 'empty', 'first-party', 'font', 'frame', 'from', 'generichide', 'ghide', 'header', 'image',
    'important', 'inline-font', 'inline-script', 'ipaddress', 'match-case', 'media', 'method', 'mp4', 'object',
    'other', 'permissions', 'ping', 'popunder', 'popup', 'redirect', 'redirect-rule', 'removeparam', 'replace',
    'requestheader', 'responseheader', 'script', 'shide', 'specifichide', 'stylesheet', 'subdocument', 'strict1p',
    'strict3p', 'strict-first-party', 'strict-third-party', 'third-party', 'to', 'uritransform', 'urltransform',
    'urlskip', 'webrtc', 'websocket', 'xhr', 'xmlhttprequest', 'object-subrequest', 'beacon', 'rewrite', 'queryprune',
    '_+',
].join('|') + ')$');

const modifierOptionPattern = /^(?:csp|header|permissions|removeparam|queryprune|replace|requestheader|responseheader|rewrite|uritransform|urltransform|urlskip)$/;

/**
 * Checks filters the user typed, one per line, with uBO's own parser and
 * network compiler. Returns one record per non-empty line:
 *   { line, text, kind, message }
 * `kind` is 'comment', 'network', 'cosmetic', 'unsupported' (valid uBO
 * syntax Mosaic does not apply: scriptlets, procedural/HTML filters) or
 * 'error' (not a valid filter).
 */
export function validateFilters(text) {
    const parser = new sfp.AstFilterParser({ maxTokenLength: snfe.MAX_TOKEN_LENGTH, interactive: true });
    const checker = snfe.createCompiler();
    const writer = new CompiledListWriter();
    writer.properties.set('name', 'My filters');
    const out = [];
    const lines = String(text || '').split(/\r?\n/);
    for (let i = 0; i < lines.length; i++) {
        const raw = lines[i].trim();
        if (raw === '') { continue; }
        const record = { line: i + 1, text: raw, kind: 'error', message: '' };
        out.push(record);
        parser.parse(raw);
        if (parser.isComment()) { record.kind = 'comment'; continue; }
        if (parser.isFilter() === false) { record.message = 'Not a filter.'; continue; }
        if (parser.isNetworkFilter()) {
            if (parser.hasError()) { record.message = 'Invalid network filter.'; continue; }
            checker.error = undefined;
            if (checker.compile(parser, writer) === false) {
                record.message = checker.error
                    ? 'This network filter uses options Mosaic’s engine does not support.'
                    : 'Invalid network filter.';
                continue;
            }
            record.kind = 'network';
            const options = networkOptions(raw);
            const unknown = options.find(name => knownOptionPattern.test(name) === false);
            if (unknown !== undefined && parser.hasOptions() === false) {
                record.kind = 'error';
                record.message = `Unknown filter option “${unknown}”.`;
            } else if (options.some(name => modifierOptionPattern.test(name))) {
                record.kind = 'unsupported';
                record.message = 'Modifier filters ($removeparam, $csp, …) are not applied by Mosaic.';
            } else if (options.some(name => /^redirect(?:-rule)?$/.test(name))) {
                record.message = 'Mosaic blocks the request instead of redirecting it.';
            }
            continue;
        }
        if (parser.isScriptletFilter()) {
            record.kind = 'unsupported';
            record.message = 'Scriptlet filters (+js) are not supported.';
            continue;
        }
        if (parser.isHtmlFilter() || parser.isResponseheaderFilter()) {
            record.kind = 'unsupported';
            record.message = 'HTML and response-header filters are not supported.';
            continue;
        }
        if (parser.hasError() || parser.result.compiled === undefined) {
            record.message = parser.result.error ? `Invalid selector: ${parser.result.error}` : 'Invalid cosmetic filter.';
            continue;
        }
        const compiled = parser.result.compiled;
        if (compiled.charCodeAt(0) === 0x7B /* { */ && parser.isException() === false) {
            const rule = cssRuleFromProcedural(JSON.parse(compiled));
            if (rule === undefined) {
                record.kind = 'unsupported';
                record.message = 'Procedural filters (:has-text, :upward, …) are not supported.';
                continue;
            }
            if (parser.hasOptions() === false) {
                record.kind = 'unsupported';
                record.message = 'Generic :style() filters are not supported; add a site.';
                continue;
            }
        }
        record.kind = 'cosmetic';
    }
    parser.selectorCompiler.finish();
    return out;
}
