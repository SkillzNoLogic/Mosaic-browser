using System.Globalization;
using System.Text.Json;

namespace Mosaic.App.Features.Engine;

/// <summary>
/// Scripts the engine area runs in Chromium pages. WinUI's WebView2 (SDK 1.0.3179) has no find
/// or zoom API, so find in page and zoom are done in the page; both are built to be repeated
/// safely on the same document.
/// </summary>
internal static class ChromiumScripts
{
    /// <summary>
    /// Find in page: every match is highlighted (CSS Custom Highlight API), the current one
    /// stronger, and scrolled into view. Matches can span inline elements but not blocks.
    /// Arguments: op ("find" starts over, "again" moves on, "stop" clears), text, matchCase,
    /// backwards. Returns {current, total, limit}.
    /// </summary>
    private const string FindFunction = """
        ((op, text, matchCase, backwards) => {
          const LIMIT = 1000;
          const S = window.__mosaicFind || (window.__mosaicFind = { ranges: [], index: -1, key: null });
          const highlights = typeof CSS !== 'undefined' && CSS.highlights && typeof Highlight === 'function' ? CSS.highlights : null;
          const clear = () => {
            if (highlights) { highlights.delete('mosaic-find'); highlights.delete('mosaic-find-current'); }
            S.ranges = []; S.index = -1; S.key = null;
          };
          if (op === 'stop' || !text) {
            clear();
            document.getElementById('__mosaic-find-style')?.remove();
            return { current: 0, total: 0, limit: 0 };
          }
          const key = (matchCase ? '1' : '0') + text;
          const stale = S.key !== key || S.ranges.some(range => !range.startContainer.isConnected);
          if (op === 'find' || stale) {
            clear();
            S.key = key;
            const root = document.body || document.documentElement;
            const display = new Map();
            const isBlock = element => {
              if (!element) return true;
              if (!display.has(element)) display.set(element, !getComputedStyle(element).display.startsWith('inline'));
              return display.get(element);
            };
            const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
              acceptNode(node) {
                const parent = node.parentElement;
                if (!parent || !node.nodeValue) return NodeFilter.FILTER_REJECT;
                if (parent.closest('script,style,noscript,template,title,head,select')) return NodeFilter.FILTER_REJECT;
                if (parent.checkVisibility && !parent.checkVisibility({ checkVisibilityCSS: true })) return NodeFilter.FILTER_REJECT;
                return NodeFilter.FILTER_ACCEPT;
              },
            });
            const nodes = [];
            let all = '';
            let previous = null;
            for (let node = walker.nextNode(); node; node = walker.nextNode()) {
              const parent = node.parentElement;
              if (previous && previous !== parent && (isBlock(previous) || isBlock(parent))) all += '\n';
              nodes.push({ node, start: all.length });
              all += node.nodeValue;
              previous = parent;
            }
            const fold = value => (matchCase ? value : value.toLowerCase());
            const haystack = fold(all);
            const needle = fold(text);
            // Lower-casing changes the length of a few characters; positions then come from the original.
            const folded = haystack.length === all.length;
            const source = folded ? haystack : all;
            const sought = folded ? needle : text;
            const locate = offset => {
              let low = 0, high = nodes.length - 1;
              while (low < high) {
                const middle = (low + high + 1) >> 1;
                if (nodes[middle].start <= offset) low = middle; else high = middle - 1;
              }
              return nodes[low];
            };
            for (let at = source.indexOf(sought); at >= 0 && S.ranges.length < LIMIT; at = source.indexOf(sought, at + Math.max(sought.length, 1))) {
              const first = locate(at);
              const last = locate(at + sought.length - 1);
              const range = document.createRange();
              range.setStart(first.node, Math.min(at - first.start, first.node.nodeValue.length));
              range.setEnd(last.node, Math.min(at + sought.length - last.start, last.node.nodeValue.length));
              S.ranges.push(range);
            }
            if (highlights && S.ranges.length) {
              if (!document.getElementById('__mosaic-find-style')) {
                const style = document.createElement('style');
                style.id = '__mosaic-find-style';
                style.textContent = '::highlight(mosaic-find){background-color:#fde047;color:#000}' +
                  '::highlight(mosaic-find-current){background-color:#f97316;color:#000}';
                (document.head || document.documentElement).appendChild(style);
              }
              highlights.set('mosaic-find', new Highlight(...S.ranges));
            }
            // The first match at or below the top of the window (the last one above it, backwards).
            const visible = S.ranges.findIndex(range => range.getBoundingClientRect().bottom >= 0);
            S.index = S.ranges.length === 0 ? -1 : visible < 0 ? 0 : visible;
          } else if (S.ranges.length) {
            S.index = (S.index + (backwards ? -1 : 1) + S.ranges.length) % S.ranges.length;
          }
          if (S.index >= 0) {
            const range = S.ranges[S.index];
            if (highlights) {
              highlights.set('mosaic-find-current', new Highlight(range));
            } else {
              const selection = getSelection();
              selection.removeAllRanges();
              selection.addRange(range);
            }
            const box = range.getBoundingClientRect();
            if (box.top < 0 || box.bottom > innerHeight || box.left < 0 || box.right > innerWidth) {
              range.startContainer.parentElement?.scrollIntoView({ block: 'center', inline: 'nearest' });
            }
          }
          return { current: S.index + 1, total: S.ranges.length, limit: S.ranges.length >= LIMIT ? LIMIT : 0 };
        })
        """;

    public static string Find(string op, string text, bool matchCase, bool backwards) =>
        $"{FindFunction}({Json(op)}, {Json(text)}, {Json(matchCase)}, {Json(backwards)})";

    /// <summary>
    /// Zoom by CSS on the root element. Runs at document creation (before the page's own
    /// scripts) and again whenever the factor changes.
    /// </summary>
    public static string Zoom(double factor)
    {
        var value = Math.Abs(factor - 1) < 0.005 ? "" : factor.ToString("0.###", CultureInfo.InvariantCulture);
        return $$"""
            (() => {
              const zoom = {{Json(value)}};
              const apply = () => {
                const root = document.documentElement;
                if (!root) return false;
                if (zoom) root.style.setProperty('zoom', zoom, 'important'); else root.style.removeProperty('zoom');
                return true;
              };
              if (!apply()) {
                new MutationObserver((_, observer) => { if (apply()) observer.disconnect(); }).observe(document, { childList: true });
              }
            })();
            """;
    }

    /// <summary>The focused video, else the one playing, else the longest; toggles Picture in Picture.</summary>
    public const string PictureInPicture = """
        (async () => {
          if (!document.pictureInPictureEnabled) return 'unsupported';
          if (document.pictureInPictureElement) { await document.exitPictureInPicture(); return 'left'; }
          const videos = [...document.querySelectorAll('video')].filter(v => !isNaN(v.duration));
          const video = (document.activeElement instanceof HTMLVideoElement && document.activeElement)
            || videos.find(v => !v.paused) || videos.sort((a, b) => b.duration - a.duration)[0];
          if (!video) return 'none';
          await video.requestPictureInPicture();
          return 'entered';
        })()
        """;

    public const string ExitFullScreen = "document.fullscreenElement && document.exitFullscreen(); true";

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
}
