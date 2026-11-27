#import "CEFBridge.h"

#include <list>
#include <string>

#include "include/cef_app.h"
#include "include/cef_application_mac.h"
#include "include/cef_browser.h"
#include "include/cef_client.h"
#include "include/cef_command_line.h"
#include "include/cef_parser.h"
#include "include/cef_resource_request_handler.h"
#include "include/cef_version.h"
#include "include/wrapper/cef_helpers.h"
#include "include/wrapper/cef_library_loader.h"

#include "CEFPasswords.h"  // passwords: save/fill hooks (CEFPasswords.mm)

#if !__has_feature(objc_arc)
#error "CEFBridge must be compiled with ARC"
#endif

@interface MCEFBrowserView ()
- (void)mosaicBrowserCreated:(CefRefPtr<CefBrowser>)browser;
- (void)mosaicBrowserClosed;
@end

namespace {

bool g_running = false;
bool g_quitting = false;
CefScopedLibraryLoader* g_library = nullptr;
std::list<CefRefPtr<CefBrowser>> g_browsers;
void (^g_willQuit)(void) = nil;
// Written once on the main thread before any browser exists; read on the IO thread.
BOOL (^g_requestFilter)(NSString*, NSString*, NSString*) = nil;

/// Chromium's resource types under the names the webRequest API (and so
/// uBlock Origin's filter syntax) uses for them.
NSString* WebRequestType(cef_resource_type_t type) {
  switch (type) {
    case RT_MAIN_FRAME: return @"main_frame";
    case RT_SUB_FRAME: return @"sub_frame";
    case RT_STYLESHEET: return @"stylesheet";
    case RT_SCRIPT: return @"script";
    case RT_IMAGE: return @"image";
    case RT_FAVICON: return @"image";
    case RT_FONT_RESOURCE: return @"font";
    case RT_OBJECT: return @"object";
    case RT_PLUGIN_RESOURCE: return @"object";
    case RT_MEDIA: return @"media";
    case RT_WORKER: return @"script";
    case RT_SHARED_WORKER: return @"script";
    case RT_SERVICE_WORKER: return @"script";
    case RT_XHR: return @"xmlhttprequest";
    case RT_PING: return @"ping";
    case RT_CSP_REPORT: return @"csp_report";
    default: return @"other";
  }
}

/// Marks the placeholder page shown when a navigation fails, so its data: URL
/// is never reported to the address bar.
const char kErrorPagePrefix[] = "data:text/html;charset=utf-8,%3C%21--mosaic-error--%3E";

NSString* ToNSString(const CefString& value) {
  std::string utf8 = value.ToString();
  return [[NSString alloc] initWithBytes:utf8.data() length:utf8.size() encoding:NSUTF8StringEncoding]
      ?: @"";
}

std::string EscapeHTML(const std::string& text) {
  std::string out;
  for (char c : text) {
    switch (c) {
      case '&': out += "&amp;"; break;
      case '<': out += "&lt;"; break;
      case '>': out += "&gt;"; break;
      case '"': out += "&quot;"; break;
      default: out += c;
    }
  }
  return out;
}

// Browser-process configuration.
class MosaicApp : public CefApp, public CefBrowserProcessHandler {
 public:
  MosaicApp() = default;

  CefRefPtr<CefBrowserProcessHandler> GetBrowserProcessHandler() override { return this; }

  void OnBeforeCommandLineProcessing(const CefString& process_type,
                                     CefRefPtr<CefCommandLine> command_line) override {
    if (!process_type.empty()) {
      return;
    }
    // Development builds are ad-hoc signed, so every rebuild looks like a new
    // app to the Keychain and would prompt for "Chromium Safe Storage" again.
    // TODO(release): drop this once Mosaic ships with a stable signing identity.
    command_line->AppendSwitch("use-mock-keychain");
  }

 private:
  IMPLEMENT_REFCOUNTING(MosaicApp);
  DISALLOW_COPY_AND_ASSIGN(MosaicApp);
};

// Per-browser callbacks, forwarded to the owning MCEFBrowserView.
class MosaicClient : public CefClient,
                     public CefDisplayHandler,
                     public CefLifeSpanHandler,
                     public CefLoadHandler,
                     public CefRequestHandler,
                     public CefResourceRequestHandler {
 public:
  explicit MosaicClient(MCEFBrowserView* view) : view_(view) {}

  CefRefPtr<CefDisplayHandler> GetDisplayHandler() override { return this; }
  CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
  CefRefPtr<CefLoadHandler> GetLoadHandler() override { return this; }
  CefRefPtr<CefRequestHandler> GetRequestHandler() override { return this; }

  // CefDisplayHandler
  void OnTitleChange(CefRefPtr<CefBrowser> browser, const CefString& title) override {
    CEF_REQUIRE_UI_THREAD();
    MCEFBrowserView* view = view_;
    [view.delegate browserView:view didChangeTitle:ToNSString(title)];
  }

  void OnAddressChange(CefRefPtr<CefBrowser> browser,
                       CefRefPtr<CefFrame> frame,
                       const CefString& url) override {
    CEF_REQUIRE_UI_THREAD();
    if (!frame->IsMain()) {
      return;
    }
    if (url.ToString().rfind(kErrorPagePrefix, 0) == 0) {
      return;
    }
    MCEFBrowserView* view = view_;
    [view.delegate browserView:view didChangeURL:ToNSString(url)];
  }

  // Passwords (area: passwords): page-script messages; see CEFPasswords.mm.
  bool OnConsoleMessage(CefRefPtr<CefBrowser> browser,
                        cef_log_severity_t level,
                        const CefString& message,
                        const CefString& source,
                        int line) override {
    CEF_REQUIRE_UI_THREAD();
    MCEFBrowserView* view = view_;
    return mosaic_passwords::ConsoleMessage(view, browser, message);
  }

  // CefLifeSpanHandler
  bool OnBeforePopup(CefRefPtr<CefBrowser> browser,
                     CefRefPtr<CefFrame> frame,
                     int popup_id,
                     const CefString& target_url,
                     const CefString& target_frame_name,
                     WindowOpenDisposition target_disposition,
                     bool user_gesture,
                     const CefPopupFeatures& popupFeatures,
                     CefWindowInfo& windowInfo,
                     CefRefPtr<CefClient>& client,
                     CefBrowserSettings& settings,
                     CefRefPtr<CefDictionaryValue>& extra_info,
                     bool* no_javascript_access) override {
    CEF_REQUIRE_UI_THREAD();
    // Mosaic has tabs, not popup windows: reroute into a new tab.
    if (!target_url.empty()) {
      MCEFBrowserView* view = view_;
      [view.delegate browserView:view wantsNewTabWithURL:ToNSString(target_url)];
    }
    return true;
  }

  void OnAfterCreated(CefRefPtr<CefBrowser> browser) override {
    CEF_REQUIRE_UI_THREAD();
    g_browsers.push_back(browser);
    MCEFBrowserView* view = view_;
    if (view && !g_quitting) {
      [view mosaicBrowserCreated:browser];
    } else {
      browser->GetHost()->CloseBrowser(true);
    }
  }

  bool DoClose(CefRefPtr<CefBrowser> browser) override {
    CEF_REQUIRE_UI_THREAD();
    // The default (returning false) asks the top-level window to close, which
    // is right for one-browser-per-window apps and wrong for tabs. Returning
    // true means "the app finishes the close itself": removing the browser's
    // view from the hierarchy is what completes it and leads to OnBeforeClose.
    NSView* content = CAST_CEF_WINDOW_HANDLE_TO_NSVIEW(browser->GetHost()->GetWindowHandle());
    dispatch_async(dispatch_get_main_queue(), ^{
      [content removeFromSuperview];
    });
    return true;
  }

  void OnBeforeClose(CefRefPtr<CefBrowser> browser) override {
    CEF_REQUIRE_UI_THREAD();
    for (auto it = g_browsers.begin(); it != g_browsers.end(); ++it) {
      if ((*it)->IsSame(browser)) {
        g_browsers.erase(it);
        break;
      }
    }
    MCEFBrowserView* view = view_;
    [view mosaicBrowserClosed];
    if (g_quitting && g_browsers.empty()) {
      CefQuitMessageLoop();
    }
  }

  // CefLoadHandler
  void OnLoadingStateChange(CefRefPtr<CefBrowser> browser,
                            bool isLoading,
                            bool canGoBack,
                            bool canGoForward) override {
    CEF_REQUIRE_UI_THREAD();
    MCEFBrowserView* view = view_;
    [view.delegate browserView:view
              didChangeLoading:isLoading
                     canGoBack:canGoBack
                  canGoForward:canGoForward];
  }

  // Passwords (area: passwords): injects the form watcher; see CEFPasswords.mm.
  void OnLoadEnd(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame, int httpStatusCode) override {
    CEF_REQUIRE_UI_THREAD();
    mosaic_passwords::FrameLoaded(browser, frame);
  }

  void OnLoadError(CefRefPtr<CefBrowser> browser,
                   CefRefPtr<CefFrame> frame,
                   ErrorCode errorCode,
                   const CefString& errorText,
                   const CefString& failedUrl) override {
    CEF_REQUIRE_UI_THREAD();
    // ERR_ABORTED is a cancelled load (user pressed stop, download started…).
    if (errorCode == ERR_ABORTED || !frame->IsMain()) {
      return;
    }
    std::string html =
        "<!doctype html><meta charset=utf-8><title>Can’t open this page</title>"
        "<style>body{font:15px -apple-system,sans-serif;margin:18vh auto;max-width:520px;"
        "padding:0 24px;color:#333;background:#fafafa}h1{font-size:22px}code{color:#888}"
        "@media(prefers-color-scheme:dark){body{color:#ddd;background:#1e1e1e}}</style>"
        "<h1>Can’t open this page</h1><p>Chromium could not load <b>" +
        EscapeHTML(failedUrl.ToString()) + "</b>.</p><p><code>" +
        EscapeHTML(errorText.ToString()) + " (" + std::to_string(static_cast<int>(errorCode)) +
        ")</code></p>";
    frame->LoadURL(std::string(kErrorPagePrefix) + CefURIEncode(html, false).ToString());
  }

  // CefRequestHandler
  CefRefPtr<CefResourceRequestHandler> GetResourceRequestHandler(
      CefRefPtr<CefBrowser> browser,
      CefRefPtr<CefFrame> frame,
      CefRefPtr<CefRequest> request,
      bool is_navigation,
      bool is_download,
      const CefString& request_initiator,
      bool& disable_default_handling) override {
    return this;
  }

  // CefResourceRequestHandler. Runs on the IO thread for every request.
  cef_return_value_t OnBeforeResourceLoad(CefRefPtr<CefBrowser> browser,
                                          CefRefPtr<CefFrame> frame,
                                          CefRefPtr<CefRequest> request,
                                          CefRefPtr<CefCallback> callback) override {
    const cef_resource_type_t type = request->GetResourceType();
    __weak MCEFBrowserView* weakView = view_;
    if (type == RT_MAIN_FRAME) {
      // A page's own document is never blocked; note the navigation so the
      // per-page blocked count starts over.
      dispatch_async(dispatch_get_main_queue(), ^{
        MCEFBrowserView* view = weakView;
        [view.delegate browserViewDidStartDocumentLoad:view];
      });
      return RV_CONTINUE;
    }
    BOOL (^filter)(NSString*, NSString*, NSString*) = g_requestFilter;
    if (!filter) {
      return RV_CONTINUE;
    }
    // "First party for cookies" is the top-level document the request belongs
    // to, which is what filter rules mean by the page.
    CefString document = request->GetFirstPartyForCookies();
    if (document.empty() && browser) {
      document = browser->GetMainFrame()->GetURL();
    }
    NSString* url = ToNSString(request->GetURL());
    if (!filter(url, ToNSString(document), WebRequestType(type))) {
      return RV_CONTINUE;
    }
    dispatch_async(dispatch_get_main_queue(), ^{
      MCEFBrowserView* view = weakView;
      [view.delegate browserView:view didBlockRequest:url];
    });
    return RV_CANCEL;
  }

  bool OnOpenURLFromTab(CefRefPtr<CefBrowser> browser,
                        CefRefPtr<CefFrame> frame,
                        const CefString& target_url,
                        WindowOpenDisposition target_disposition,
                        bool user_gesture) override {
    CEF_REQUIRE_UI_THREAD();
    // ⌘-click / middle-click.
    MCEFBrowserView* view = view_;
    [view.delegate browserView:view wantsNewTabWithURL:ToNSString(target_url)];
    return true;
  }

 private:
  __weak MCEFBrowserView* view_;

  IMPLEMENT_REFCOUNTING(MosaicClient);
  DISALLOW_COPY_AND_ASSIGN(MosaicClient);
};

}  // namespace

// MARK: - MosaicApplication

@interface MosaicApplication () <CefAppProtocol>
@end

@implementation MosaicApplication {
  BOOL _handlingSendEvent;
}

- (BOOL)isHandlingSendEvent {
  return _handlingSendEvent;
}

- (void)setHandlingSendEvent:(BOOL)handlingSendEvent {
  _handlingSendEvent = handlingSendEvent;
}

- (void)sendEvent:(NSEvent*)event {
  CefScopedSendingEvent sendingEventScoper;
  [super sendEvent:event];
}

// Chromium must close its browsers and leave its own event loop before the
// process exits, so quitting is routed through MosaicCEF instead of exit().
- (void)terminate:(id)sender {
  if (g_running) {
    [MosaicCEF quit];
  } else {
    void (^handler)(void) = g_willQuit;
    g_willQuit = nil;
    if (handler) {
      handler();
    }
    [super terminate:sender];
  }
}

@end

// MARK: - MosaicCEF

@implementation MosaicCEF

+ (NSString*)frameworkPath {
  return [[NSBundle mainBundle].privateFrameworksPath
      stringByAppendingPathComponent:@"Chromium Embedded Framework.framework"];
}

+ (BOOL)isBundled {
  return [[NSFileManager defaultManager]
      fileExistsAtPath:[[self frameworkPath] stringByAppendingPathComponent:@"Chromium Embedded Framework"]];
}

+ (BOOL)isRunning {
  return g_running;
}

+ (NSString*)chromiumVersion {
  return [NSString stringWithFormat:@"%d.%d.%d.%d", CHROME_VERSION_MAJOR, CHROME_VERSION_MINOR,
                                    CHROME_VERSION_BUILD, CHROME_VERSION_PATCH];
}

+ (void (^)(void))willQuitHandler {
  return g_willQuit;
}

+ (void)setWillQuitHandler:(void (^)(void))handler {
  g_willQuit = [handler copy];
}

+ (BOOL (^)(NSString*, NSString*, NSString*))requestFilter {
  return g_requestFilter;
}

+ (void)setRequestFilter:(BOOL (^)(NSString*, NSString*, NSString*))filter {
  g_requestFilter = [filter copy];
}

+ (BOOL)startWithArgc:(int)argc argv:(char**)argv dataDirectory:(NSString*)dataDirectory {
  if (g_running) {
    return YES;
  }
  if (![self isBundled]) {
    return NO;
  }
  if (!g_library) {
    g_library = new CefScopedLibraryLoader();
    if (!g_library->LoadInMain()) {
      delete g_library;
      g_library = nullptr;
      return NO;
    }
  }

  [[NSFileManager defaultManager] createDirectoryAtPath:dataDirectory
                            withIntermediateDirectories:YES
                                             attributes:nil
                                                  error:nil];

  CefMainArgs mainArgs(argc, argv);
  CefSettings settings;
  // TODO(release): enable the Chromium sandbox (needs a signed build that
  // links cef_sandbox).
  settings.no_sandbox = true;
  settings.persist_session_cookies = true;
  settings.log_severity = LOGSEVERITY_WARNING;
  CefString(&settings.root_cache_path) = dataDirectory.UTF8String;
  CefString(&settings.cache_path) = dataDirectory.UTF8String;
  CefString(&settings.log_file) =
      [dataDirectory stringByAppendingPathComponent:@"chromium.log"].UTF8String;

  CefRefPtr<MosaicApp> app = new MosaicApp();
  g_running = CefInitialize(mainArgs, settings, app, nullptr);
  return g_running;
}

+ (void)runEventLoop {
  if (g_running) {
    CefRunMessageLoop();
  } else {
    [NSApp run];
  }
}

+ (void)quit {
  if (!g_running) {
    [NSApp terminate:nil];
    return;
  }
  if (g_quitting) {
    return;
  }
  g_quitting = true;

  void (^handler)(void) = g_willQuit;
  g_willQuit = nil;
  if (handler) {
    handler();
  }

  if (g_browsers.empty()) {
    CefQuitMessageLoop();
    return;
  }
  // Copy: closing mutates g_browsers.
  std::list<CefRefPtr<CefBrowser>> browsers = g_browsers;
  for (auto& browser : browsers) {
    browser->GetHost()->CloseBrowser(true);
  }
  // A page that refuses to go away must not keep the app alive.
  dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 3 * NSEC_PER_SEC), dispatch_get_main_queue(), ^{
    if (g_running) {
      CefQuitMessageLoop();
    }
  });
}

+ (void)shutdown {
  if (!g_running) {
    return;
  }
  g_browsers.clear();
  CefShutdown();
  g_running = false;
  delete g_library;
  g_library = nullptr;
}

@end

// MARK: - MCEFBrowserView

@implementation MCEFBrowserView {
  CefRefPtr<CefBrowser> _browser;
  CefRefPtr<MosaicClient> _client;
  NSString* _pendingURL;
  BOOL _requested;
  BOOL _closed;
  BOOL _navigatedBeforeCreation;
}

- (instancetype)initWithURL:(NSString*)url {
  if ((self = [super initWithFrame:NSMakeRect(0, 0, 800, 600)])) {
    _pendingURL = [url copy];
    self.autoresizesSubviews = YES;
  }
  return self;
}

- (void)viewDidMoveToWindow {
  [super viewDidMoveToWindow];
  if (self.window) {
    [self createBrowserIfNeeded];
  }
}

- (void)createBrowserIfNeeded {
  if (_requested || _closed || !g_running || g_quitting) {
    return;
  }
  _requested = YES;

  CefWindowInfo windowInfo;
  NSRect bounds = self.bounds;
  windowInfo.SetAsChild(CAST_NSVIEW_TO_CEF_WINDOW_HANDLE(self),
                        CefRect(0, 0, static_cast<int>(NSWidth(bounds)), static_cast<int>(NSHeight(bounds))));
  // Embedded (native-parented) browsers are always Alloy style on macOS.
  windowInfo.runtime_style = CEF_RUNTIME_STYLE_ALLOY;

  CefBrowserSettings browserSettings;
  _client = new MosaicClient(self);
  std::string url = _pendingURL.length ? _pendingURL.UTF8String : "about:blank";
  CefBrowserHost::CreateBrowser(windowInfo, _client, url, browserSettings, nullptr, nullptr);
}

- (void)mosaicBrowserCreated:(CefRefPtr<CefBrowser>)browser {
  if (_closed) {
    browser->GetHost()->CloseBrowser(true);
    return;
  }
  _browser = browser;
  NSView* content = CAST_CEF_WINDOW_HANDLE_TO_NSVIEW(browser->GetHost()->GetWindowHandle());
  content.frame = self.bounds;
  content.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;

  // A navigation requested while the browser was still being created.
  NSString* initial = _pendingURL;
  std::string current = browser->GetMainFrame()->GetURL().ToString();
  if (initial.length && current != initial.UTF8String && _navigatedBeforeCreation) {
    browser->GetMainFrame()->LoadURL(initial.UTF8String);
  }
  _navigatedBeforeCreation = NO;
}

// Passwords (area: passwords): CEFPasswords.mm reaches the browser through this.
- (CefRefPtr<CefBrowser>)mosaicPasswordsBrowser {
  return _browser;
}

- (void)mosaicBrowserClosed {
  _browser = nullptr;
  _client = nullptr;
}

- (void)loadURL:(NSString*)url {
  if (_closed) {
    return;
  }
  _pendingURL = [url copy];
  if (_browser) {
    _browser->GetMainFrame()->LoadURL(url.UTF8String);
  } else if (_requested) {
    _navigatedBeforeCreation = YES;
  }
}

- (void)goBack {
  if (_browser) {
    _browser->GoBack();
  }
}

- (void)goForward {
  if (_browser) {
    _browser->GoForward();
  }
}

- (void)reload {
  if (_browser) {
    _browser->Reload();
  }
}

- (void)stopLoading {
  if (_browser) {
    _browser->StopLoad();
  }
}

- (void)focusContent {
  if (_browser) {
    NSView* content = CAST_CEF_WINDOW_HANDLE_TO_NSVIEW(_browser->GetHost()->GetWindowHandle());
    [self.window makeFirstResponder:content];
    _browser->GetHost()->SetFocus(true);
  }
}

- (void)closeBrowser {
  if (_closed) {
    return;
  }
  _closed = YES;
  self.delegate = nil;
  if (_browser) {
    _browser->GetHost()->CloseBrowser(true);
  }
  [self removeFromSuperview];
}

@end
