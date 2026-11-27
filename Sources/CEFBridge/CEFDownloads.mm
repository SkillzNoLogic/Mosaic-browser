// Chromium downloads for Mosaic (downloads area). CEF calls everything here on its UI
// thread, which is the main thread because Mosaic runs CEF's message loop on it.

#import "CEFDownloads.h"

#include <map>
#include <memory>
#include <string>

#include "CEFDownloadsInternal.h"
#include "include/cef_cookie.h"
#include "include/cef_version.h"
#include "include/wrapper/cef_helpers.h"

#if !__has_feature(objc_arc)
#error "CEFDownloads must be compiled with ARC"
#endif

namespace {

__weak id<MCEFDownloadDelegate> g_delegate = nil;
BOOL g_showsFDMItem = NO;

/// Callbacks that pause/resume/cancel a download, while Chromium still runs it.
std::map<uint32_t, CefRefPtr<CefDownloadItemCallback>> g_itemCallbacks;
/// The page each download started from (OnBeforeDownload knows it; updates do not).
std::map<uint32_t, std::string> g_pageURLs;

// Context menu commands (CEF reserves MENU_ID_USER_FIRST…MENU_ID_USER_LAST for apps).
const int kCommandDownloadLink = MENU_ID_USER_FIRST + 410;
const int kCommandDownloadLinkWithFDM = MENU_ID_USER_FIRST + 411;

NSString* Str(const CefString& value) {
  std::string utf8 = value.ToString();
  return [[NSString alloc] initWithBytes:utf8.data() length:utf8.size() encoding:NSUTF8StringEncoding]
      ?: @"";
}

NSString* DescribeInterrupt(cef_download_interrupt_reason_t reason) {
  switch (reason) {
    case CEF_DOWNLOAD_INTERRUPT_REASON_NONE: return @"";
    case CEF_DOWNLOAD_INTERRUPT_REASON_FILE_ACCESS_DENIED: return @"Mosaic cannot write to the download folder";
    case CEF_DOWNLOAD_INTERRUPT_REASON_FILE_NO_SPACE: return @"The disk is full";
    case CEF_DOWNLOAD_INTERRUPT_REASON_FILE_NAME_TOO_LONG: return @"The file name is too long";
    case CEF_DOWNLOAD_INTERRUPT_REASON_FILE_TOO_LARGE: return @"The file is too large for the disk";
    case CEF_DOWNLOAD_INTERRUPT_REASON_FILE_VIRUS_INFECTED: return @"The file was reported as infected";
    case CEF_DOWNLOAD_INTERRUPT_REASON_FILE_BLOCKED: return @"The download was blocked";
    case CEF_DOWNLOAD_INTERRUPT_REASON_FILE_SECURITY_CHECK_FAILED: return @"A security check failed";
    case CEF_DOWNLOAD_INTERRUPT_REASON_FILE_TOO_SHORT:
    case CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_CONTENT_LENGTH_MISMATCH: return @"The server sent less than it promised";
    case CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_FAILED: return @"Network error";
    case CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_TIMEOUT: return @"The network timed out";
    case CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_DISCONNECTED: return @"The network was disconnected";
    case CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_SERVER_DOWN: return @"The server is not responding";
    case CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_INVALID_REQUEST: return @"The request was not valid";
    case CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_FAILED: return @"Server error";
    case CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_NO_RANGE: return @"The server cannot resume this download";
    case CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_BAD_CONTENT: return @"The file is not available on the server";
    case CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_UNAUTHORIZED: return @"The server requires you to sign in";
    case CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_CERT_PROBLEM: return @"The server's certificate is not valid";
    case CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_FORBIDDEN: return @"The server refused access";
    case CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_UNREACHABLE: return @"The server could not be reached";
    case CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_CROSS_ORIGIN_REDIRECT: return @"The server redirected to another site";
    case CEF_DOWNLOAD_INTERRUPT_REASON_USER_CANCELED: return @"Cancelled";
    case CEF_DOWNLOAD_INTERRUPT_REASON_USER_SHUTDOWN: return @"Mosaic quit during the download";
    case CEF_DOWNLOAD_INTERRUPT_REASON_CRASH: return @"Chromium stopped unexpectedly";
    default: return [NSString stringWithFormat:@"Download error %d", static_cast<int>(reason)];
  }
}

void RunOnMain(dispatch_block_t block) {
  if ([NSThread isMainThread]) {
    block();
  } else {
    dispatch_async(dispatch_get_main_queue(), block);
  }
}

}  // namespace

// MARK: - MCEFDownload

@interface MCEFDownload ()
- (instancetype)initWithItem:(CefRefPtr<CefDownloadItem>)item pageURL:(NSString*)pageURL;
- (void)mosaicSetSuggestedName:(NSString*)name;
@end

@implementation MCEFDownload

- (void)mosaicSetSuggestedName:(NSString*)name {
  _suggestedName = [name copy];
}

- (instancetype)initWithItem:(CefRefPtr<CefDownloadItem>)item pageURL:(NSString*)pageURL {
  if ((self = [super init])) {
    _identifier = item->GetId();
    _url = Str(item->GetURL());
    _originalURL = Str(item->GetOriginalUrl());
    _suggestedName = Str(item->GetSuggestedFileName());
    _fullPath = Str(item->GetFullPath());
    _mimeType = Str(item->GetMimeType());
    _pageURL = [pageURL copy] ?: @"";
    _totalBytes = item->GetTotalBytes();
    _receivedBytes = item->GetReceivedBytes();
    _speed = item->GetCurrentSpeed();
    cef_download_interrupt_reason_t reason = item->GetInterruptReason();
    _interruptReason = static_cast<int>(reason);
    _interruptDescription = DescribeInterrupt(reason);
    if (item->IsComplete()) {
      _state = MCEFDownloadStateComplete;
    } else if (item->IsCanceled()) {
      _state = MCEFDownloadStateCancelled;
    } else if (item->IsInterrupted()) {
      _state = MCEFDownloadStateInterrupted;
#if CEF_API_ADDED(14400)
    } else if (item->IsPaused()) {
      _state = MCEFDownloadStatePaused;
#endif
    } else {
      _state = MCEFDownloadStateInProgress;
    }
  }
  return self;
}

@end

// MARK: - Download handler

namespace {

class MosaicDownloads : public CefDownloadHandler {
 public:
  MosaicDownloads() = default;

  bool OnBeforeDownload(CefRefPtr<CefBrowser> browser,
                        CefRefPtr<CefDownloadItem> download_item,
                        const CefString& suggested_name,
                        CefRefPtr<CefBeforeDownloadCallback> callback) override {
    CEF_REQUIRE_UI_THREAD();
    id<MCEFDownloadDelegate> delegate = g_delegate;
    if (!delegate) {
      return false;  // Alloy's default: the download is cancelled.
    }
    const uint32_t identifier = download_item->GetId();
    std::string page;
    if (browser && browser->GetMainFrame()) {
      page = browser->GetMainFrame()->GetURL().ToString();
    }
    g_pageURLs[identifier] = page;
    MCEFDownload* download = [[MCEFDownload alloc] initWithItem:download_item pageURL:Str(page)];
    // Only this parameter carries the name from Content-Disposition; the item's own
    // suggested name is just the link's `download` attribute.
    if (!suggested_name.empty()) {
      [download mosaicSetSuggestedName:Str(suggested_name)];
    }

    // `decide` may run later (after a save panel or a prompt). Holding the callback keeps
    // the download waiting; it is released as soon as a decision is made.
    auto pending = std::make_shared<CefRefPtr<CefBeforeDownloadCallback>>(callback);
    void (^decide)(NSString* _Nullable) = ^(NSString* _Nullable path) {
      RunOnMain(^{
        if (!*pending) {
          return;
        }
        if (path.length) {
          (*pending)->Continue(path.UTF8String, false);
          pending->reset();
        } else {
          // Releasing the callback without continuing cancels the download; cancel
          // through the item callback too, in case Chromium already started it.
          pending->reset();
          auto it = g_itemCallbacks.find(identifier);
          if (it != g_itemCallbacks.end()) {
            it->second->Cancel();
          }
        }
      });
    };
    [delegate chromiumDownloadWillStart:download decide:decide];
    return true;
  }

  void OnDownloadUpdated(CefRefPtr<CefBrowser> browser,
                         CefRefPtr<CefDownloadItem> download_item,
                         CefRefPtr<CefDownloadItemCallback> callback) override {
    CEF_REQUIRE_UI_THREAD();
    if (!download_item->IsValid()) {
      return;
    }
    const uint32_t identifier = download_item->GetId();
    const bool finished = download_item->IsComplete() || download_item->IsCanceled();
    if (finished) {
      g_itemCallbacks.erase(identifier);
    } else {
      g_itemCallbacks[identifier] = callback;
    }
    auto page = g_pageURLs.find(identifier);
    MCEFDownload* download =
        [[MCEFDownload alloc] initWithItem:download_item
                                   pageURL:page != g_pageURLs.end() ? Str(page->second) : @""];
    if (finished) {
      g_pageURLs.erase(identifier);
    }
    [g_delegate chromiumDownloadDidUpdate:download];
  }

 private:
  IMPLEMENT_REFCOUNTING(MosaicDownloads);
  DISALLOW_COPY_AND_ASSIGN(MosaicDownloads);
};

/// Collects cookies into a Cookie header; reports when CEF releases it (visiting is over,
/// including when there were no cookies and Visit was never called).
class CookieCollector : public CefCookieVisitor {
 public:
  explicit CookieCollector(void (^completion)(NSString*)) : completion_([completion copy]) {}
  ~CookieCollector() override {
    NSString* header = Str(CefString(header_));
    void (^completion)(NSString*) = completion_;
    dispatch_async(dispatch_get_main_queue(), ^{
      completion(header);
    });
  }

  bool Visit(const CefCookie& cookie, int count, int total, bool& deleteCookie) override {
    if (!header_.empty()) {
      header_ += "; ";
    }
    header_ += CefString(&cookie.name).ToString() + "=" + CefString(&cookie.value).ToString();
    return true;
  }

 private:
  std::string header_;
  void (^completion_)(NSString*);

  IMPLEMENT_REFCOUNTING(CookieCollector);
  DISALLOW_COPY_AND_ASSIGN(CookieCollector);
};

bool IsFetchableLink(const std::string& url) {
  return url.rfind("http://", 0) == 0 || url.rfind("https://", 0) == 0 || url.rfind("ftp://", 0) == 0;
}

/// Context menu handler for browsers whose client has no other one: it only adds the
/// downloads items. A client with its own handler calls the two hook functions instead.
class DownloadsContextMenu : public CefContextMenuHandler {
 public:
  DownloadsContextMenu() = default;

  void OnBeforeContextMenu(CefRefPtr<CefBrowser> browser,
                           CefRefPtr<CefFrame> frame,
                           CefRefPtr<CefContextMenuParams> params,
                           CefRefPtr<CefMenuModel> model) override {
    MosaicDownloadsBeforeContextMenu(browser, params, model);
  }

  bool OnContextMenuCommand(CefRefPtr<CefBrowser> browser,
                            CefRefPtr<CefFrame> frame,
                            CefRefPtr<CefContextMenuParams> params,
                            int command_id,
                            EventFlags event_flags) override {
    return MosaicDownloadsContextMenuCommand(browser, params, command_id);
  }

 private:
  IMPLEMENT_REFCOUNTING(DownloadsContextMenu);
  DISALLOW_COPY_AND_ASSIGN(DownloadsContextMenu);
};

}  // namespace

CefRefPtr<CefContextMenuHandler> MosaicDownloadsContextMenuHandler() {
  static CefRefPtr<CefContextMenuHandler> handler = new DownloadsContextMenu();
  return handler;
}

CefRefPtr<CefDownloadHandler> MosaicDownloadHandler() {
  static CefRefPtr<CefDownloadHandler> handler = new MosaicDownloads();
  return handler;
}

void MosaicDownloadsBeforeContextMenu(CefRefPtr<CefBrowser> browser,
                                      CefRefPtr<CefContextMenuParams> params,
                                      CefRefPtr<CefMenuModel> model) {
  if (!(params->GetTypeFlags() & CM_TYPEFLAG_LINK)) {
    return;
  }
  const std::string link = params->GetLinkUrl().ToString();
  if (!IsFetchableLink(link)) {
    return;
  }
  if (model->GetCount() > 0) {
    model->AddSeparator();
  }
  model->AddItem(kCommandDownloadLink, "Download Linked File");
  if (g_showsFDMItem) {
    model->AddItem(kCommandDownloadLinkWithFDM, "Download Linked File with Free Download Manager");
  }
}

bool MosaicDownloadsContextMenuCommand(CefRefPtr<CefBrowser> browser,
                                       CefRefPtr<CefContextMenuParams> params,
                                       int command_id) {
  if (command_id == kCommandDownloadLink) {
    if (browser) {
      browser->GetHost()->StartDownload(params->GetLinkUrl());
    }
    return true;
  }
  if (command_id == kCommandDownloadLinkWithFDM) {
    NSString* link = Str(params->GetLinkUrl());
    NSString* page = Str(params->GetPageUrl());
    RunOnMain(^{
      [g_delegate chromiumContextMenuSendLinkToFDM:link pageURL:page];
    });
    return true;
  }
  return false;
}

// MARK: - MosaicCEFDownloads

@implementation MosaicCEFDownloads

+ (id<MCEFDownloadDelegate>)delegate {
  return g_delegate;
}

+ (void)setDelegate:(id<MCEFDownloadDelegate>)delegate {
  g_delegate = delegate;
}

+ (BOOL)showsFDMContextMenuItem {
  return g_showsFDMItem;
}

+ (void)setShowsFDMContextMenuItem:(BOOL)shows {
  g_showsFDMItem = shows;
}

+ (NSString*)userAgent {
  // CEF sends Chrome's default (reduced) User-Agent unless an app overrides it, and
  // Mosaic does not.
  return [NSString stringWithFormat:@"Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 "
                                    @"(KHTML, like Gecko) Chrome/%d.0.0.0 Safari/537.36",
                                    CHROME_VERSION_MAJOR];
}

+ (void)pauseDownload:(uint32_t)identifier {
  auto it = g_itemCallbacks.find(identifier);
  if (it != g_itemCallbacks.end()) {
    it->second->Pause();
  }
}

+ (void)resumeDownload:(uint32_t)identifier {
  auto it = g_itemCallbacks.find(identifier);
  if (it != g_itemCallbacks.end()) {
    it->second->Resume();
  }
}

+ (void)cancelDownload:(uint32_t)identifier {
  auto it = g_itemCallbacks.find(identifier);
  if (it != g_itemCallbacks.end()) {
    it->second->Cancel();
  }
}

+ (BOOL)isControllable:(uint32_t)identifier {
  return g_itemCallbacks.count(identifier) > 0;
}

+ (BOOL)startDownloadWithURL:(NSString*)url {
  CefRefPtr<CefBrowser> browser = MosaicAnyLiveBrowser();
  if (!browser || !url.length) {
    return NO;
  }
  browser->GetHost()->StartDownload(url.UTF8String);
  return YES;
}

+ (void)cookiesForURL:(NSString*)url completion:(void (^)(NSString*))completion {
  CefRefPtr<CefCookieManager> manager = CefCookieManager::GetGlobalManager(nullptr);
  CefRefPtr<CookieCollector> collector = new CookieCollector(completion);
  if (manager) {
    manager->VisitUrlCookies(url.UTF8String, true, collector);
  }
  // The collector reports when CEF lets go of it (at once if nothing was visited).
}

@end
