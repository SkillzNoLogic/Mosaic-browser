// Web-platform globals uBlock Origin's engine expects, for hosts that lack them.
//
// The engine runs in two places: a bare JavaScriptCore context inside Mosaic
// (for Chromium tabs), which has the language but none of the web APIs, and
// Gecko's parent process (for Mozilla tabs), which has nearly all of them.
// Each shim is installed only when the host does not already provide it.

const g = globalThis;

if (typeof g.console === 'undefined') {
    const log = typeof g.__mosaicLog === 'function' ? g.__mosaicLog : () => {};
    const emit = level => (...args) => log(level, args.map(String).join(' '));
    g.console = { log: emit('log'), info: emit('info'), warn: emit('warn'), error: emit('error'), debug() {} };
}

if (typeof g.performance === 'undefined') {
    g.performance = { now: () => Date.now() };
}

if (typeof g.structuredClone === 'undefined') {
    // The engine only clones plain data (strings, and JSON-like records).
    g.structuredClone = value =>
        (value === null || typeof value !== 'object') ? value : JSON.parse(JSON.stringify(value));
}

if (typeof g.TextEncoder === 'undefined') {
    g.TextEncoder = class TextEncoder {
        get encoding() { return 'utf-8'; }

        encode(text = '') {
            const out = new Uint8Array(text.length * 3);
            return out.slice(0, this.encodeInto(text, out).written);
        }

        encodeInto(text, out) {
            let read = 0;
            let written = 0;
            const end = out.length;
            while (read < text.length) {
                let code = text.charCodeAt(read);
                // Combine a surrogate pair into one code point; a lone surrogate becomes U+FFFD.
                if (code >= 0xD800 && code <= 0xDBFF && read + 1 < text.length) {
                    const low = text.charCodeAt(read + 1);
                    if (low >= 0xDC00 && low <= 0xDFFF) {
                        code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
                    } else {
                        code = 0xFFFD;
                    }
                } else if (code >= 0xD800 && code <= 0xDFFF) {
                    code = 0xFFFD;
                }
                const size = code < 0x80 ? 1 : code < 0x800 ? 2 : code < 0x10000 ? 3 : 4;
                if (written + size > end) { break; }
                if (size === 1) {
                    out[written++] = code;
                } else if (size === 2) {
                    out[written++] = 0xC0 | (code >> 6);
                    out[written++] = 0x80 | (code & 0x3F);
                } else if (size === 3) {
                    out[written++] = 0xE0 | (code >> 12);
                    out[written++] = 0x80 | ((code >> 6) & 0x3F);
                    out[written++] = 0x80 | (code & 0x3F);
                } else {
                    out[written++] = 0xF0 | (code >> 18);
                    out[written++] = 0x80 | ((code >> 12) & 0x3F);
                    out[written++] = 0x80 | ((code >> 6) & 0x3F);
                    out[written++] = 0x80 | (code & 0x3F);
                }
                read += size === 4 ? 2 : 1;
            }
            return { read, written };
        }
    };
}

if (typeof g.TextDecoder === 'undefined') {
    g.TextDecoder = class TextDecoder {
        get encoding() { return 'utf-8'; }

        decode(input) {
            if (input === undefined) { return ''; }
            const bytes = input instanceof Uint8Array
                ? input
                : new Uint8Array(input.buffer || input, input.byteOffset || 0, input.byteLength);
            const parts = [];
            let chunk = [];
            const flush = () => {
                parts.push(String.fromCharCode.apply(null, chunk));
                chunk = [];
            };
            let i = 0;
            const end = bytes.length;
            while (i < end) {
                const b0 = bytes[i++];
                let code;
                if (b0 < 0x80) {
                    code = b0;
                } else if (b0 >= 0xC2 && b0 < 0xE0 && i < end) {
                    code = ((b0 & 0x1F) << 6) | (bytes[i++] & 0x3F);
                } else if (b0 >= 0xE0 && b0 < 0xF0 && i + 1 < end) {
                    code = ((b0 & 0x0F) << 12) | ((bytes[i++] & 0x3F) << 6) | (bytes[i++] & 0x3F);
                } else if (b0 >= 0xF0 && b0 < 0xF5 && i + 2 < end) {
                    code = ((b0 & 0x07) << 18) | ((bytes[i++] & 0x3F) << 12)
                        | ((bytes[i++] & 0x3F) << 6) | (bytes[i++] & 0x3F);
                } else {
                    code = 0xFFFD;
                }
                if (code > 0xFFFF) {
                    code -= 0x10000;
                    chunk.push(0xD800 | (code >> 10), 0xDC00 | (code & 0x3FF));
                } else {
                    chunk.push(code);
                }
                if (chunk.length >= 8192) { flush(); }
            }
            flush();
            return parts.join('');
        }
    };
}

if (typeof g.setTimeout === 'undefined') {
    // No event loop here. The engine only uses timers to defer optional
    // housekeeping; the entry point runs whatever was queued before returning
    // (see `runDeferredTasks` in engine.js).
    const pending = new Map();
    let nextId = 1;
    g.setTimeout = callback => {
        const id = nextId++;
        pending.set(id, callback);
        return id;
    };
    g.clearTimeout = id => { pending.delete(id); };
    g.__mosaicRunTimers = () => {
        for (const [id, callback] of Array.from(pending)) {
            pending.delete(id);
            try { callback(); } catch (error) { g.console.error(error); }
        }
    };
}

if (typeof g.URL === 'undefined') {
    // The filter parser uses a URL object purely as a hostname normaliser
    // (assign `.hostname`, read it back punycoded); the rest of the class is
    // what a few rarely-taken paths need. This is not a general URL parser.
    const forbiddenInHost = /[\s#%/:<>?@[\\\]^|]/;
    const pattern = /^([a-zA-Z][a-zA-Z0-9+.-]*:)(?:\/\/(?:[^/?#@]*@)?([^/?#:]*)(?::(\d*))?)?([^?#]*)(\?[^#]*)?(#.*)?$/;

    g.URL = class URL {
        constructor(input) {
            this.href = String(input);
        }

        get href() {
            const authority = this._hasAuthority
                ? `//${this._hostname}${this._port ? `:${this._port}` : ''}`
                : '';
            return `${this.protocol}${authority}${this.pathname}${this.search}${this.hash}`;
        }

        set href(value) {
            const match = pattern.exec(String(value).trim());
            if (match === null) { throw new TypeError(`Invalid URL: ${value}`); }
            this.protocol = match[1].toLowerCase();
            this._hasAuthority = match[2] !== undefined;
            this._hostname = '';
            if (this._hasAuthority) { this.hostname = match[2]; }
            this._port = match[3] || '';
            this.pathname = match[4] || (this._hasAuthority ? '/' : '');
            this.search = match[5] || '';
            this.hash = match[6] || '';
        }

        get hostname() { return this._hostname; }

        // Like the real setter, an invalid value is ignored rather than thrown.
        set hostname(value) {
            const candidate = String(value);
            if (candidate === '' || forbiddenInHost.test(candidate)) { return; }
            try {
                this._hostname = g.__mosaicToASCII(candidate.toLowerCase());
            } catch (_) {
                // Not encodable as a hostname; keep the previous one.
            }
        }

        get host() { return this._port ? `${this._hostname}:${this._port}` : this._hostname; }
        get origin() { return this._hasAuthority ? `${this.protocol}//${this.host}` : 'null'; }
        toString() { return this.href; }
    };
}
