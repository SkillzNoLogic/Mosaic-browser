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

const fctx = new FilteringContext();
let hasSuffixList = false;

function runDeferredTasks() {
    if (typeof globalThis.__mosaicRunTimers === 'function') {
        globalThis.__mosaicRunTimers();
    }
}

// Same walk as uBO's Node build: only network filters are compiled. Cosmetic
// and scriptlet filters need a content-script side that Mosaic does not have.
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
        if (parser.isNetworkFilter() === false) { continue; }
        compiler.compile(parser, writer);
    }
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
