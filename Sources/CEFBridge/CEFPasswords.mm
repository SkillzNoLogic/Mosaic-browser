// Password save and fill for Chromium tabs. Area: passwords. See CEFBridgePasswords.h.
//
// Channel: each script run in a frame gets a fresh 128-bit random token and the origin the
// frame showed when the script was sent. The script refuses to run if the frame's
// `location.origin` is no longer that origin, and reports through
// `console.log("\u2063mosaic-pw:<token>:<json>")`. OnConsoleMessage has no frame, so the
// token is what identifies it: the bridge accepts a message only if the token is known,
// belongs to this browser, and its frame still exists and still shows the bound origin. The
// message is then reported with that origin. A page can read its own token (it can wrap
// console.log), but that only lets it send messages about itself.

#import "CEFBridgePasswords.h"

#include <algorithm>
#include <map>
#include <string>
#include <vector>

#include "CEFPasswords.h"
#include "include/cef_browser.h"
#include "include/cef_parser.h"

#if !__has_feature(objc_arc)
#error "CEFBridge must be compiled with ARC"
#endif

@interface MCEFBrowserView (MosaicPasswordsPrivate)
/// Defined in CEFBridge.mm: the view's browser, or null.
- (CefRefPtr<CefBrowser>)mosaicPasswordsBrowser;
@end

namespace {

NSString* g_pageScript = nil;
NSString* g_loadScript = nil;
void (^g_handler)(MCEFBrowserView*, NSString*, MCEFPasswordFrame, NSString*) = nil;

/// U+2063 INVISIBLE SEPARATOR, then a name: no page writes this by accident.
const char kPrefix[] = "\xE2\x81\xA3mosaic-pw:";
const size_t kTokenLength = 32;

struct Binding {
  int browser;
  std::string frame;
  std::string origin;
  uint64_t serial;
};

// UI thread only (OnLoadEnd, OnConsoleMessage and the Objective-C entry points all run there).
std::map<std::string, Binding> g_bindings;
uint64_t g_serial = 0;
const size_t kMaxBindings = 1024;

NSString* ToNSString(const std::string& utf8) {
  return [[NSString alloc] initWithBytes:utf8.data() length:utf8.size() encoding:NSUTF8StringEncoding] ?: @"";
}

std::string RandomToken() {
  uint8_t bytes[kTokenLength / 2];
  arc4random_buf(bytes, sizeof bytes);
  static const char digits[] = "0123456789abcdef";
  std::string token;
  for (uint8_t byte : bytes) {
    token += digits[byte >> 4];
    token += digits[byte & 15];
  }
  return token;
}

/// "https://example.com", "http://127.0.0.1:8000"; empty for anything but http(s).
std::string OriginOf(const CefString& url) {
  CefURLParts parts;
  if (!CefParseURL(url, parts)) {
    return "";
  }
  std::string scheme = CefString(&parts.scheme).ToString();
  if (scheme != "http" && scheme != "https") {
    return "";
  }
  // Chromium's canonical origin: lowercase host, default port dropped, "/" at the end.
  std::string origin = CefString(&parts.origin).ToString();
  if (!origin.empty() && origin.back() == '/') {
    origin.pop_back();
  }
  return origin;
}

/// A JavaScript string literal.
std::string JSString(const std::string& text) {
  std::string out = "\"";
  for (unsigned char c : text) {
    switch (c) {
      case '"': out += "\\\""; break;
      case '\\': out += "\\\\"; break;
      case '\n': out += "\\n"; break;
      case '\r': out += "\\r"; break;
      case '<': out += "\\x3c"; break;
      default:
        if (c < 0x20) {
          char escape[8];
          snprintf(escape, sizeof escape, "\\x%02x", c);
          out += escape;
        } else {
          out += static_cast<char>(c);
        }
    }
  }
  return out + "\"";
}

void Bind(const std::string& token, int browser, const std::string& frame, const std::string& origin) {
  if (g_bindings.size() >= kMaxBindings) {
    // Drop the oldest quarter; their documents are long gone in practice.
    std::vector<std::pair<uint64_t, std::string>> order;
    for (auto& [key, binding] : g_bindings) {
      order.emplace_back(binding.serial, key);
    }
    std::sort(order.begin(), order.end());
    for (size_t i = 0; i < order.size() / 4; ++i) {
      g_bindings.erase(order[i].second);
    }
  }
  g_bindings[token] = Binding{browser, frame, origin, ++g_serial};
}

/// Where `frame` is relative to the top-level document; -1 for a cross-origin subframe.
int FrameKind(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame, const std::string& origin) {
  if (frame->IsMain()) {
    return MCEFPasswordFrameMain;
  }
  return OriginOf(browser->GetMainFrame()->GetURL()) == origin ? MCEFPasswordFrameSameOrigin : -1;
}

void Execute(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame, const std::string& origin, NSString* body) {
  if (!g_pageScript || !body) {
    return;
  }
  std::string token = RandomToken();
  Bind(token, browser->GetIdentifier(), frame->GetIdentifier().ToString(), origin);
  std::string script;
  script += "(function(){\"use strict\";";
  script += "var __mosaicOrigin=" + JSString(origin) + ",__mosaicToken=" + JSString(token) + ";";
  // Sent to a frame that has since moved to another origin: do nothing there.
  script += "if(location.origin!==__mosaicOrigin)return;";
  script += "var __mosaicLog=console.log;";
  script += "function __mosaicSend(m){try{__mosaicLog.call(console,\"\\u2063mosaic-pw:\"+__mosaicToken+\":\"+JSON.stringify(m));}catch(e){}}\n";
  script += g_pageScript.UTF8String;
  script += "\n";
  script += body.UTF8String;
  script += "\n})();";
  frame->ExecuteJavaScript(script, frame->GetURL(), 0);
}

}  // namespace

namespace mosaic_passwords {

void FrameLoaded(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame) {
  if (!g_loadScript || !g_pageScript || !browser || !frame || !frame->IsValid()) {
    return;
  }
  std::string origin = OriginOf(frame->GetURL());
  if (origin.empty() || FrameKind(browser, frame, origin) < 0) {
    return;
  }
  Execute(browser, frame, origin, g_loadScript);
}

bool ConsoleMessage(MCEFBrowserView* view, CefRefPtr<CefBrowser> browser, const CefString& message) {
  std::string text = message.ToString();
  const size_t prefixLength = sizeof(kPrefix) - 1;
  if (text.compare(0, prefixLength, kPrefix) != 0) {
    return false;
  }
  // From here on the text may contain a password: it is consumed, never logged.
  if (text.size() < prefixLength + kTokenLength + 1 || text[prefixLength + kTokenLength] != ':') {
    return true;
  }
  auto found = g_bindings.find(text.substr(prefixLength, kTokenLength));
  if (found == g_bindings.end() || !browser || found->second.browser != browser->GetIdentifier()) {
    return true;
  }
  const Binding binding = found->second;
  CefRefPtr<CefFrame> frame = browser->GetFrameByIdentifier(binding.frame);
  if (!frame || !frame->IsValid() || OriginOf(frame->GetURL()) != binding.origin) {
    return true;
  }
  int kind = FrameKind(browser, frame, binding.origin);
  if (kind < 0 || !g_handler || !view) {
    return true;
  }
  g_handler(view, ToNSString(binding.origin), static_cast<MCEFPasswordFrame>(kind),
            ToNSString(text.substr(prefixLength + kTokenLength + 1)));
  return true;
}

}  // namespace mosaic_passwords

@implementation MosaicCEF (Passwords)

+ (NSString*)passwordPageScript {
  return g_pageScript;
}

+ (void)setPasswordPageScript:(NSString*)script {
  g_pageScript = [script copy];
}

+ (NSString*)passwordLoadScript {
  return g_loadScript;
}

+ (void)setPasswordLoadScript:(NSString*)script {
  g_loadScript = [script copy];
}

+ (void (^)(MCEFBrowserView*, NSString*, MCEFPasswordFrame, NSString*))passwordMessageHandler {
  return g_handler;
}

+ (void)setPasswordMessageHandler:(void (^)(MCEFBrowserView*, NSString*, MCEFPasswordFrame, NSString*))handler {
  g_handler = [handler copy];
}

@end

@implementation MCEFBrowserView (Passwords)

- (NSInteger)runPasswordScript:(NSString*)body inFramesWithOrigin:(NSString*)origin {
  CefRefPtr<CefBrowser> browser = [self mosaicPasswordsBrowser];
  if (!browser || !origin.length) {
    return 0;
  }
  const std::string wanted = origin.UTF8String;
  // Never into a frame of `origin` embedded in a page from somewhere else.
  if (OriginOf(browser->GetMainFrame()->GetURL()) != wanted) {
    return 0;
  }
  std::vector<CefString> identifiers;
  browser->GetFrameIdentifiers(identifiers);
  NSInteger sent = 0;
  for (const CefString& identifier : identifiers) {
    CefRefPtr<CefFrame> frame = browser->GetFrameByIdentifier(identifier);
    if (frame && frame->IsValid() && OriginOf(frame->GetURL()) == wanted) {
      Execute(browser, frame, wanted, body);
      ++sent;
    }
  }
  return sent;
}

@end
