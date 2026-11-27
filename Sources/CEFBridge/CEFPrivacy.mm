// Privacy features of Mosaic's Chromium engine (owner: privacy area):
// private (in-memory) browsers, HTTPS-only upgrades with a fallback page,
// tracking-parameter removal, cosmetic-filter injection, preferences, cookies
// and site data. CEFBridge.mm calls the hooks in CEFPrivacyInternal.h.

#import "CEFBridge.h"
#import "CEFPrivacyInternal.h"

#import <objc/runtime.h>

#include <map>
#include <string>
#include <vector>

#include "include/cef_cookie.h"
#include "include/cef_devtools_message_observer.h"
#include "include/cef_parser.h"
#include "include/cef_preference.h"
#include "include/cef_values.h"
#include "include/wrapper/cef_helpers.h"

#if !__has_feature(objc_arc)
#error "CEFPrivacy must be compiled with ARC"
#endif

namespace {

NSString* _Nullable (^g_frameScriptProvider)(NSString*, NSString*, BOOL) = nil;
NSString* _Nullable (^g_navigationRewriter)(NSString*, BOOL, BOOL) = nil;
void (^g_insecureContinueHandler)(NSString*) = nil;

/// Same prefix as CEFBridge.mm's error pages, so OnAddressChange keeps such
/// data: URLs out of the address bar; the second comment marks Mosaic's
/// HTTPS-only page.
const char kHTTPSOnlyPagePrefix[] =
    "data:text/html;charset=utf-8,%3C%21--mosaic-error--%3E%3C%21--mosaic-https-only--%3E";

NSString* ToNS(const CefString& value) {
  std::string utf8 = value.ToString();
  return [[NSString alloc] initWithBytes:utf8.data() length:utf8.size() encoding:NSUTF8StringEncoding] ?: @"";
}

bool StartsWith(const std::string& text, const char* prefix) {
  return text.rfind(prefix, 0) == 0;
}

std::string HostOf(const std::string& url) {
  CefURLParts parts;
  if (!CefParseURL(url, parts)) {
    return "";
  }
  return CefString(&parts.host).ToString();
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

// MARK: Private profile and live browsers

CefRefPtr<CefRequestContext> g_privateContext;
int g_privateCount = 0;
std::vector<CefRefPtr<CefBrowser>> g_live;
NSDictionary<NSString*, id>* g_profilePreferences = @{};

bool IsPrivateBrowser(CefRefPtr<CefBrowser> browser) {
  return g_privateContext && browser && browser->GetHost() &&
         browser->GetHost()->GetRequestContext()->IsSame(g_privateContext);
}

CefRefPtr<CefValue> ToCefValue(id object) {
  CefRefPtr<CefValue> value = CefValue::Create();
  if ([object isKindOfClass:[NSNumber class]]) {
    NSNumber* number = object;
    const char* type = number.objCType;
    if (strcmp(type, @encode(BOOL)) == 0 || strcmp(type, "c") == 0 ||
        number == (id)kCFBooleanTrue || number == (id)kCFBooleanFalse) {
      value->SetBool(number.boolValue);
    } else if (strcmp(type, "d") == 0 || strcmp(type, "f") == 0) {
      value->SetDouble(number.doubleValue);
    } else {
      value->SetInt(number.intValue);
    }
  } else if ([object isKindOfClass:[NSString class]]) {
    value->SetString(((NSString*)object).UTF8String);
  } else if ([object isKindOfClass:[NSArray class]]) {
    CefRefPtr<CefListValue> list = CefListValue::Create();
    size_t index = 0;
    for (id item in (NSArray*)object) {
      list->SetValue(index++, ToCefValue(item));
    }
    value->SetList(list);
  } else {
    return nullptr;  // NSNull: restore the default
  }
  return value;
}

id FromCefValue(CefRefPtr<CefValue> value) {
  if (!value) {
    return nil;
  }
  switch (value->GetType()) {
    case VTYPE_BOOL: return @(value->GetBool());
    case VTYPE_INT: return @(value->GetInt());
    case VTYPE_DOUBLE: return @(value->GetDouble());
    case VTYPE_STRING: return ToNS(value->GetString());
    case VTYPE_LIST: {
      NSMutableArray* out = [NSMutableArray array];
      CefRefPtr<CefListValue> list = value->GetList();
      for (size_t i = 0; i < list->GetSize(); i++) {
        id item = FromCefValue(list->GetValue(i));
        if (item) [out addObject:item];
      }
      return out;
    }
    case VTYPE_DICTIONARY: return @"{…}";
    default: return [NSNull null];
  }
}

NSDictionary<NSString*, NSString*>* SetPreferences(CefRefPtr<CefPreferenceManager> manager,
                                                   NSDictionary<NSString*, id>* preferences) {
  NSMutableDictionary<NSString*, NSString*>* results = [NSMutableDictionary dictionary];
  if (!manager) {
    return results;
  }
  for (NSString* name in preferences) {
    if (!manager->HasPreference(name.UTF8String)) {
      results[name] = @"Chromium has no such preference";
      continue;
    }
    if (!manager->CanSetPreference(name.UTF8String)) {
      results[name] = @"Chromium does not allow changing it";
      continue;
    }
    CefString error;
    bool ok = manager->SetPreference(name.UTF8String, ToCefValue(preferences[name]), error);
    results[name] = ok ? @"" : (ToNS(error).length ? ToNS(error) : @"failed");
  }
  return results;
}

// MARK: DevTools jobs (site storage)

struct StorageJob {
  NSArray<NSString*>* origins;
  void (^completion)(BOOL);
};

std::vector<StorageJob> g_pendingJobs;
NSWindow* g_helperWindow = nil;
MCEFBrowserView* g_helperView = nil;
int g_nextMessageID = 900000;

class StorageClearObserver : public CefDevToolsMessageObserver {
 public:
  StorageClearObserver(std::vector<int> ids, void (^completion)(BOOL), bool closeHelper)
      : pending_(ids.begin(), ids.end()), completion_(completion), closeHelper_(closeHelper) {}

  void OnDevToolsMethodResult(CefRefPtr<CefBrowser> browser,
                              int message_id,
                              bool success,
                              const void* result,
                              size_t result_size) override {
    auto it = std::find(pending_.begin(), pending_.end(), message_id);
    if (it == pending_.end()) {
      return;
    }
    pending_.erase(it);
    allSucceeded_ = allSucceeded_ && success;
    if (pending_.empty()) {
      Finish();
    }
  }

  void Finish() {
    if (finished_) {
      return;
    }
    finished_ = true;
    void (^completion)(BOOL) = completion_;
    BOOL ok = allSucceeded_;
    bool closeHelper = closeHelper_;
    dispatch_async(dispatch_get_main_queue(), ^{
      if (closeHelper && g_helperView) {
        [g_helperView closeBrowser];
        g_helperView = nil;
        g_helperWindow = nil;
      }
      registration_ = nullptr;
      if (completion) completion(ok);
    });
  }

  CefRefPtr<CefRegistration> registration_;

 private:
  std::vector<int> pending_;
  void (^completion_)(BOOL);
  bool closeHelper_;
  bool allSucceeded_ = true;
  bool finished_ = false;

  IMPLEMENT_REFCOUNTING(StorageClearObserver);
};

void RunJob(CefRefPtr<CefBrowser> browser, const StorageJob& job, bool closeHelper) {
  std::vector<int> ids;
  for (NSUInteger i = 0; i < job.origins.count; i++) {
    ids.push_back(g_nextMessageID + static_cast<int>(i));
  }
  CefRefPtr<StorageClearObserver> observer = new StorageClearObserver(ids, job.completion, closeHelper);
  observer->registration_ = browser->GetHost()->AddDevToolsMessageObserver(observer);
  if (ids.empty()) {
    observer->Finish();
    return;
  }
  for (NSString* origin in job.origins) {
    CefRefPtr<CefDictionaryValue> params = CefDictionaryValue::Create();
    params->SetString("origin", origin.UTF8String);
    params->SetString("storageTypes", "all");
    browser->GetHost()->ExecuteDevToolsMethod(g_nextMessageID++, "Storage.clearDataForOrigin", params);
  }
  // A method that never answers must not leave the UI waiting.
  dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 10 * NSEC_PER_SEC), dispatch_get_main_queue(), ^{
    observer->Finish();
  });
}

CefRefPtr<CefBrowser> AnyDefaultProfileBrowser() {
  for (auto& browser : g_live) {
    if (!IsPrivateBrowser(browser)) {
      return browser;
    }
  }
  return nullptr;
}

void RunPendingJobs() {
  CefRefPtr<CefBrowser> browser = AnyDefaultProfileBrowser();
  if (!browser || g_pendingJobs.empty()) {
    return;
  }
  std::vector<StorageJob> jobs;
  jobs.swap(g_pendingJobs);
  for (size_t i = 0; i < jobs.size(); i++) {
    RunJob(browser, jobs[i], g_helperView != nil && i + 1 == jobs.size());
  }
}

// MARK: Cookies

NSDictionary<NSString*, id>* DescribeCookie(const CefCookie& cookie) {
  CefTime created;
  cef_time_from_basetime(cookie.creation, &created);
  return @{
    @"domain": ToNS(CefString(&cookie.domain)),
    @"name": ToNS(CefString(&cookie.name)),
    @"path": ToNS(CefString(&cookie.path)),
    @"secure": @(cookie.secure != 0),
    @"httpOnly": @(cookie.httponly != 0),
    @"creation": @(created.GetDoubleT()),
  };
}

class CookieCollector : public CefCookieVisitor {
 public:
  explicit CookieCollector(void (^completion)(NSArray*)) : completion_(completion) {}
  ~CookieCollector() override {
    NSArray* cookies = [cookies_ copy];
    void (^completion)(NSArray*) = completion_;
    dispatch_async(dispatch_get_main_queue(), ^{ completion(cookies); });
  }
  bool Visit(const CefCookie& cookie, int count, int total, bool& deleteCookie) override {
    [cookies_ addObject:DescribeCookie(cookie)];
    return true;
  }

 private:
  NSMutableArray* cookies_ = [NSMutableArray array];
  void (^completion_)(NSArray*);
  IMPLEMENT_REFCOUNTING(CookieCollector);
};

bool DomainMatches(const std::string& cookieDomain, const std::string& site) {
  std::string domain = cookieDomain;
  if (!domain.empty() && domain[0] == '.') {
    domain = domain.substr(1);
  }
  if (domain == site) {
    return true;
  }
  return domain.size() > site.size() && domain.compare(domain.size() - site.size(), site.size(), site) == 0 &&
         domain[domain.size() - site.size() - 1] == '.';
}

class CookieDeleter : public CefCookieVisitor {
 public:
  CookieDeleter(std::string domain, double since, CefRefPtr<CefCookieManager> manager,
                void (^completion)(NSInteger))
      : domain_(domain), since_(since), manager_(manager), completion_(completion) {}
  ~CookieDeleter() override {
    NSInteger deleted = deleted_;
    void (^completion)(NSInteger) = completion_;
    manager_->FlushStore(nullptr);
    dispatch_async(dispatch_get_main_queue(), ^{ completion(deleted); });
  }
  bool Visit(const CefCookie& cookie, int count, int total, bool& deleteCookie) override {
    bool matches = domain_.empty() || DomainMatches(CefString(&cookie.domain).ToString(), domain_);
    if (matches && since_ > 0) {
      CefTime created;
      cef_time_from_basetime(cookie.creation, &created);
      matches = created.GetDoubleT() >= since_;
    }
    if (matches) {
      deleteCookie = true;
      deleted_++;
    }
    return true;
  }

 private:
  std::string domain_;
  double since_;
  CefRefPtr<CefCookieManager> manager_;
  void (^completion_)(NSInteger);
  NSInteger deleted_ = 0;
  IMPLEMENT_REFCOUNTING(CookieDeleter);
};

class Completion : public CefCompletionCallback {
 public:
  explicit Completion(void (^block)(void)) : block_(block) {}
  void OnComplete() override {
    void (^block)(void) = block_;
    dispatch_async(dispatch_get_main_queue(), ^{ block(); });
  }

 private:
  void (^block_)(void);
  IMPLEMENT_REFCOUNTING(Completion);
};

// MARK: HTTPS-only

/// The last HTTPS upgrade in each browser, so its failure can be recognised.
struct Upgrade {
  std::string from;  // the http:// address asked for
  std::string to;    // the https:// address loaded instead
};
std::map<int, Upgrade> g_upgrades;

void ShowHTTPSOnlyPage(CefRefPtr<CefFrame> frame, const std::string& insecureURL) {
  std::string host = EscapeHTML(HostOf(insecureURL));
  std::string url = EscapeHTML(insecureURL);
  std::string html =
      "<!doctype html><meta charset=utf-8><title>Secure connection not available</title>"
      "<meta name=color-scheme content='light dark'>"
      "<style>body{font:15px -apple-system,sans-serif;margin:16vh auto;max-width:560px;padding:0 24px;"
      "color:#222;background:#fafafa}h1{font-size:22px;margin-bottom:8px}p{line-height:1.45}"
      ".actions{margin-top:28px;display:flex;gap:12px;flex-wrap:wrap}"
      "a.button{display:inline-block;padding:8px 16px;border-radius:8px;text-decoration:none;font-weight:500}"
      ".primary{background:#0a64d8;color:#fff}.secondary{border:1px solid #bbb;color:inherit}"
      ".icon{font-size:40px}code{color:#777}"
      "@media(prefers-color-scheme:dark){body{color:#e6e6e6;background:#1e1e1e}.secondary{border-color:#555}}"
      "</style><div class=icon role=img aria-label='Warning'>&#9888;&#xFE0E;</div>"
      "<h1>Secure connection not available</h1>"
      "<p>HTTPS-Only Mode is on, and <b>" + host + "</b> does not support a secure (HTTPS) connection, "
      "or the secure version did not load.</p>"
      "<p>On an insecure connection, anyone on the network can see and change what you send and "
      "receive. Avoid entering passwords, email or card details if you continue.</p>"
      "<div class=actions><a class='button primary' href='javascript:history.back()'>Go Back</a>"
      "<a class='button secondary' href=\"" + url + "\">Continue to HTTP Site</a></div>"
      "<p><code>" + url + "</code></p>";
  frame->LoadURL(std::string(kHTTPSOnlyPagePrefix) + CefURIEncode(html, false).ToString());
}

}  // namespace

// MARK: - Hooks

namespace mosaic_privacy {

CefRefPtr<CefRequestContext> RequestContextForView(MCEFBrowserView* view) {
  if (!view.isPrivate) {
    return nullptr;
  }
  if (!g_privateContext) {
    // An empty cache path makes an in-memory ("incognito") profile.
    CefRequestContextSettings settings;
    g_privateContext = CefRequestContext::CreateContext(settings, nullptr);
    SetPreferences(g_privateContext, g_profilePreferences);
  }
  return g_privateContext;
}

void BrowserCreated(CefRefPtr<CefBrowser> browser) {
  g_live.push_back(browser);
  if (IsPrivateBrowser(browser)) {
    g_privateCount++;
  }
  RunPendingJobs();
}

void BrowserClosing(CefRefPtr<CefBrowser> browser) {
  for (auto it = g_live.begin(); it != g_live.end(); ++it) {
    if ((*it)->IsSame(browser)) {
      g_live.erase(it);
      break;
    }
  }
  g_upgrades.erase(browser->GetIdentifier());
  if (IsPrivateBrowser(browser)) {
    g_privateCount--;
    if (g_privateCount <= 0) {
      // The last private browser: drop the profile, and with it every cookie,
      // storage area and cache entry it held.
      g_privateCount = 0;
      g_privateContext = nullptr;
    }
  }
}

bool OnBeforeBrowse(CefRefPtr<CefBrowser> browser,
                    CefRefPtr<CefFrame> frame,
                    CefRefPtr<CefRequest> request,
                    bool user_gesture,
                    bool is_redirect) {
  NSString* _Nullable (^rewriter)(NSString*, BOOL, BOOL) = g_navigationRewriter;
  if (!rewriter || !frame->IsMain()) {
    return false;
  }
  const std::string url = request->GetURL().ToString();
  Upgrade& upgrade = g_upgrades[browser->GetIdentifier()];
  // Only a click on the page's "Continue to HTTP Site" link counts; typing the
  // address again while the page shows must not grant an exception.
  const bool clickedLink = user_gesture && (request->GetTransitionType() & TT_SOURCE_MASK) == TT_LINK;
  const bool fromHTTPSOnlyPage =
      clickedLink && StartsWith(frame->GetURL().ToString(), kHTTPSOnlyPagePrefix) && url == upgrade.from;
  if (fromHTTPSOnlyPage && g_insecureContinueHandler) {
    g_insecureContinueHandler(ToNS(HostOf(url)));
  }
  NSString* replacement = rewriter(ToNS(url), is_redirect, fromHTTPSOnlyPage);
  if (!replacement || [replacement isEqualToString:ToNS(url)]) {
    return false;
  }
  const std::string target = replacement.UTF8String;
  if (StartsWith(url, "http://") && StartsWith(target, "https://")) {
    if (is_redirect && !upgrade.to.empty() && HostOf(upgrade.to) == HostOf(target)) {
      // The secure page redirected back to plain HTTP: upgrading again would loop.
      CefRefPtr<CefFrame> target_frame = frame;
      dispatch_async(dispatch_get_main_queue(), ^{ ShowHTTPSOnlyPage(target_frame, url); });
      return true;
    }
    upgrade.from = url;
    upgrade.to = target;
  }
  CefRefPtr<CefFrame> target_frame = frame;
  dispatch_async(dispatch_get_main_queue(), ^{ target_frame->LoadURL(target); });
  return true;
}

void OnLoadStart(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame) {
  const std::string url = frame->GetURL().ToString();
  if (frame->IsMain()) {
    auto it = g_upgrades.find(browser->GetIdentifier());
    if (it != g_upgrades.end() && url == it->second.to) {
      it->second = Upgrade();  // the secure version loaded
    }
  }
  NSString* _Nullable (^provider)(NSString*, NSString*, BOOL) = g_frameScriptProvider;
  if (!provider || !(StartsWith(url, "http://") || StartsWith(url, "https://"))) {
    return;
  }
  CefRefPtr<CefFrame> main = browser->GetMainFrame();
  NSString* pageURL = main ? ToNS(main->GetURL()) : ToNS(url);
  NSString* script = provider(ToNS(url), pageURL, frame->IsMain());
  if (script.length) {
    frame->ExecuteJavaScript(script.UTF8String, url, 0);
  }
}

bool OnLoadError(CefRefPtr<CefBrowser> browser,
                 CefRefPtr<CefFrame> frame,
                 int error_code,
                 const CefString& failed_url) {
  if (!frame->IsMain() || error_code == ERR_ABORTED) {
    return false;
  }
  auto it = g_upgrades.find(browser->GetIdentifier());
  if (it == g_upgrades.end() || it->second.to.empty()) {
    return false;
  }
  if (HostOf(failed_url.ToString()) != HostOf(it->second.to)) {
    return false;
  }
  ShowHTTPSOnlyPage(frame, it->second.from);
  return true;
}

}  // namespace mosaic_privacy

// MARK: - MosaicPrivacyCEF

@implementation MosaicPrivacyCEF

+ (NSString* _Nullable (^)(NSString*, NSString*, BOOL))frameScriptProvider {
  return g_frameScriptProvider;
}

+ (void)setFrameScriptProvider:(NSString* _Nullable (^)(NSString*, NSString*, BOOL))provider {
  g_frameScriptProvider = [provider copy];
}

+ (NSString* _Nullable (^)(NSString*, BOOL, BOOL))navigationRewriter {
  return g_navigationRewriter;
}

+ (void)setNavigationRewriter:(NSString* _Nullable (^)(NSString*, BOOL, BOOL))rewriter {
  g_navigationRewriter = [rewriter copy];
}

+ (void (^)(NSString*))insecureContinueHandler {
  return g_insecureContinueHandler;
}

+ (void)setInsecureContinueHandler:(void (^)(NSString*))handler {
  g_insecureContinueHandler = [handler copy];
}

+ (NSDictionary<NSString*, NSString*>*)setProfilePreferences:(NSDictionary<NSString*, id>*)preferences {
  if (![MosaicCEF isRunning]) {
    return @{};
  }
  NSMutableDictionary* remembered = [g_profilePreferences mutableCopy];
  [remembered addEntriesFromDictionary:preferences];
  g_profilePreferences = remembered;
  if (g_privateContext) {
    SetPreferences(g_privateContext, preferences);
  }
  return SetPreferences(CefRequestContext::GetGlobalContext(), preferences);
}

+ (NSDictionary<NSString*, NSString*>*)setGlobalPreferences:(NSDictionary<NSString*, id>*)preferences {
  if (![MosaicCEF isRunning]) {
    return @{};
  }
  return SetPreferences(CefPreferenceManager::GetGlobalPreferenceManager(), preferences);
}

+ (id)profilePreference:(NSString*)name {
  if (![MosaicCEF isRunning]) {
    return nil;
  }
  CefRefPtr<CefRequestContext> context = CefRequestContext::GetGlobalContext();
  return context->HasPreference(name.UTF8String) ? FromCefValue(context->GetPreference(name.UTF8String)) : nil;
}

+ (id)globalPreference:(NSString*)name {
  if (![MosaicCEF isRunning]) {
    return nil;
  }
  CefRefPtr<CefPreferenceManager> manager = CefPreferenceManager::GetGlobalPreferenceManager();
  return manager->HasPreference(name.UTF8String) ? FromCefValue(manager->GetPreference(name.UTF8String)) : nil;
}

+ (void)setCookiesSessionOnly:(BOOL)sessionOnly {
  if (![MosaicCEF isRunning]) {
    return;
  }
  CefRequestContext::GetGlobalContext()->SetContentSetting(
      CefString(), CefString(), CEF_CONTENT_SETTING_TYPE_COOKIES,
      sessionOnly ? CEF_CONTENT_SETTING_VALUE_SESSION_ONLY : CEF_CONTENT_SETTING_VALUE_DEFAULT);
}

+ (void)visitCookies:(void (^)(NSArray<NSDictionary<NSString*, id>*>*))completion {
  CefRefPtr<CefCookieManager> manager =
      [MosaicCEF isRunning] ? CefCookieManager::GetGlobalManager(nullptr) : nullptr;
  if (!manager || !manager->VisitAllCookies(new CookieCollector(completion))) {
    dispatch_async(dispatch_get_main_queue(), ^{ completion(@[]); });
  }
}

+ (void)visitPrivateCookies:(void (^)(NSArray<NSDictionary<NSString*, id>*>*))completion {
  CefRefPtr<CefCookieManager> manager = g_privateContext ? g_privateContext->GetCookieManager(nullptr) : nullptr;
  if (!manager || !manager->VisitAllCookies(new CookieCollector(completion))) {
    dispatch_async(dispatch_get_main_queue(), ^{ completion(@[]); });
  }
}

+ (void)deleteCookiesForDomain:(NSString*)domain
                         since:(NSDate*)since
                    completion:(void (^)(NSInteger))completion {
  CefRefPtr<CefCookieManager> manager =
      [MosaicCEF isRunning] ? CefCookieManager::GetGlobalManager(nullptr) : nullptr;
  std::string site = domain.length ? domain.lowercaseString.UTF8String : "";
  double from = since ? since.timeIntervalSince1970 : 0;
  if (!manager || !manager->VisitAllCookies(new CookieDeleter(site, from, manager, completion))) {
    dispatch_async(dispatch_get_main_queue(), ^{ completion(0); });
  }
}

+ (void)clearCache:(void (^)(void))completion {
  if (![MosaicCEF isRunning]) {
    dispatch_async(dispatch_get_main_queue(), ^{ completion(); });
    return;
  }
  CefRequestContext::GetGlobalContext()->ClearHttpCache(new Completion(completion));
}

+ (void)clearStorageForOrigins:(NSArray<NSString*>*)origins completion:(void (^)(BOOL))completion {
  if (![MosaicCEF isRunning]) {
    dispatch_async(dispatch_get_main_queue(), ^{ completion(NO); });
    return;
  }
  g_pendingJobs.push_back({[origins copy], [completion copy]});
  if (AnyDefaultProfileBrowser()) {
    RunPendingJobs();
    return;
  }
  if (g_helperView) {
    return;  // already being created; the job runs when it exists
  }
  // No Chromium tab is open: a hidden browser, never shown, to speak DevTools to.
  g_helperWindow = [[NSWindow alloc] initWithContentRect:NSMakeRect(-30000, -30000, 64, 64)
                                               styleMask:NSWindowStyleMaskBorderless
                                                 backing:NSBackingStoreBuffered
                                                   defer:NO];
  g_helperWindow.releasedWhenClosed = NO;
  g_helperView = [[MCEFBrowserView alloc] initWithURL:@"about:blank"];
  g_helperView.frame = NSMakeRect(0, 0, 64, 64);
  [g_helperWindow.contentView addSubview:g_helperView];
}

+ (NSInteger)privateBrowserCount {
  return g_privateCount;
}

+ (BOOL)hasPrivateProfile {
  return g_privateContext != nullptr;
}

@end

// MARK: - MCEFBrowserView (MosaicPrivacy)

static char kPrivateKey;

@implementation MCEFBrowserView (MosaicPrivacy)

- (BOOL)isPrivate {
  return [objc_getAssociatedObject(self, &kPrivateKey) boolValue];
}

- (void)setPrivate:(BOOL)isPrivate {
  objc_setAssociatedObject(self, &kPrivateKey, @(isPrivate), OBJC_ASSOCIATION_RETAIN_NONATOMIC);
}

@end
