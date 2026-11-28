// Engine area: page features of Chromium browsers (find, zoom, print, developer tools,
// audio, the DevTools protocol, permission prompts, pop-up blocking, full screen and
// JavaScript dialogs). The public API is the PageFeatures section of CEFBridge.h.

#import "CEFBridgeInternal.h"

#import <objc/runtime.h>

#include <string>
#include <vector>

#include "include/cef_client.h"
#include "include/cef_devtools_message_observer.h"
#include "include/cef_parser.h"
#include "include/cef_registration.h"
#include "include/cef_request_context.h"
#include "include/wrapper/cef_helpers.h"

#if !__has_feature(objc_arc)
#error "CEFBridge must be compiled with ARC"
#endif

namespace {

NSString* PageString(const CefString& value) {
  std::string utf8 = value.ToString();
  return [[NSString alloc] initWithBytes:utf8.data() length:utf8.size() encoding:NSUTF8StringEncoding]
      ?: @"";
}

__weak id<MCEFPageDelegate> g_pageDelegate = nil;
uint64_t g_nextRequestID = 1;

}  // namespace

// MARK: - Per-view state

@class MCEFPageState;

/// Settings made before the browser exists, DevTools calls in flight, open prompts.
@interface MCEFPageState : NSObject {
 @public
  CefRefPtr<CefRegistration> devToolsRegistration;
}
@property(nonatomic, weak) id<MCEFPageDelegate> delegate;
@property(nonatomic) double zoomLevel;
@property(nonatomic) BOOL audioMuted;
@property(nonatomic, strong) NSMutableDictionary<NSNumber*, id>* devToolsCallbacks;
/// prompt id → request (weak: the delegate owns pending requests).
@property(nonatomic, strong) NSMapTable<NSNumber*, MCEFPermissionRequest*>* prompts;
@end

@implementation MCEFPageState
- (instancetype)init {
  if ((self = [super init])) {
    _devToolsCallbacks = [NSMutableDictionary dictionary];
    _prompts = [NSMapTable strongToWeakObjectsMapTable];
  }
  return self;
}
@end

static char kPageStateKey;

static MCEFPageState* PageState(MCEFBrowserView* view) {
  MCEFPageState* state = objc_getAssociatedObject(view, &kPageStateKey);
  if (!state) {
    state = [[MCEFPageState alloc] init];
    objc_setAssociatedObject(view, &kPageStateKey, state, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
  }
  return state;
}

static id<MCEFPageDelegate> PageDelegate(MCEFBrowserView* view) {
  if (!view) {
    return nil;
  }
  return PageState(view).delegate ?: g_pageDelegate;
}

// MARK: - Permission requests

@interface MCEFPermissionRequest ()
- (instancetype)initWithOrigin:(NSString*)origin
                         kinds:(MCEFPermissionKinds)kinds
                    otherKinds:(NSArray<NSString*>*)otherKinds
                 mediaCallback:(CefRefPtr<CefMediaAccessCallback>)mediaCallback
                    mediaMask:(uint32_t)mediaMask
                promptCallback:(CefRefPtr<CefPermissionPromptCallback>)promptCallback;
- (void)markCancelled;
@end

@implementation MCEFPermissionRequest {
  CefRefPtr<CefMediaAccessCallback> _media;
  uint32_t _mediaMask;
  CefRefPtr<CefPermissionPromptCallback> _prompt;
}

- (instancetype)initWithOrigin:(NSString*)origin
                         kinds:(MCEFPermissionKinds)kinds
                    otherKinds:(NSArray<NSString*>*)otherKinds
                 mediaCallback:(CefRefPtr<CefMediaAccessCallback>)mediaCallback
                     mediaMask:(uint32_t)mediaMask
                promptCallback:(CefRefPtr<CefPermissionPromptCallback>)promptCallback {
  if ((self = [super init])) {
    _origin = [origin copy];
    _kinds = kinds;
    _otherKinds = [otherKinds copy];
    _identifier = g_nextRequestID++;
    _media = mediaCallback;
    _mediaMask = mediaMask;
    _prompt = promptCallback;
  }
  return self;
}

- (void)dealloc {
  // Never leave a page waiting.
  if (!_isAnswered) {
    [self deny];
  }
}

- (void)finish:(cef_permission_request_result_t)result {
  if (_isAnswered) {
    return;
  }
  _isAnswered = YES;
  if (_media) {
    _media->Continue(result == CEF_PERMISSION_RESULT_ACCEPT ? _mediaMask : CEF_MEDIA_PERMISSION_NONE);
  } else if (_prompt) {
    _prompt->Continue(result);
  }
  _media = nullptr;
  _prompt = nullptr;
}

- (void)allow {
  [self finish:CEF_PERMISSION_RESULT_ACCEPT];
}

- (void)deny {
  [self finish:CEF_PERMISSION_RESULT_DENY];
}

- (void)dismiss {
  [self finish:CEF_PERMISSION_RESULT_DISMISS];
}

/// Chromium dropped the request itself; its callback must not be run any more.
- (void)markCancelled {
  _isAnswered = YES;
  _media = nullptr;
  _prompt = nullptr;
}

@end

// MARK: - JavaScript dialogs

@interface MCEFJavaScriptDialog ()
- (instancetype)initWithKind:(MCEFDialogKind)kind
                     message:(NSString*)message
                 defaultText:(NSString*)defaultText
                      origin:(NSString*)origin
                    callback:(CefRefPtr<CefJSDialogCallback>)callback;
@end

@implementation MCEFJavaScriptDialog {
  CefRefPtr<CefJSDialogCallback> _callback;
}

- (instancetype)initWithKind:(MCEFDialogKind)kind
                     message:(NSString*)message
                 defaultText:(NSString*)defaultText
                      origin:(NSString*)origin
                    callback:(CefRefPtr<CefJSDialogCallback>)callback {
  if ((self = [super init])) {
    _kind = kind;
    _message = [message copy];
    _defaultText = [defaultText copy];
    _origin = [origin copy];
    _callback = callback;
  }
  return self;
}

- (void)dealloc {
  if (!_isFinished) {
    [self finishAccepted:NO text:nil];
  }
}

- (void)finishAccepted:(BOOL)accepted text:(NSString*)text {
  if (_isFinished) {
    return;
  }
  _isFinished = YES;
  if (_callback) {
    _callback->Continue(accepted, (text ?: @"").UTF8String);
    _callback = nullptr;
  }
}

@end

// MARK: - DevTools protocol

namespace {

/// Delivers DevTools method results to the completion blocks waiting for them.
class MosaicDevToolsObserver : public CefDevToolsMessageObserver {
 public:
  explicit MosaicDevToolsObserver(MCEFBrowserView* view) : view_(view) {}

  void OnDevToolsMethodResult(CefRefPtr<CefBrowser> browser,
                              int message_id,
                              bool success,
                              const void* result,
                              size_t result_size) override {
    CEF_REQUIRE_UI_THREAD();
    MCEFBrowserView* view = view_;
    if (!view) {
      return;
    }
    MCEFPageState* state = PageState(view);
    void (^completion)(NSDictionary*, NSString*) = state.devToolsCallbacks[@(message_id)];
    if (!completion) {
      return;
    }
    [state.devToolsCallbacks removeObjectForKey:@(message_id)];
    NSDictionary* parsed = nil;
    if (result && result_size) {
      NSData* data = [NSData dataWithBytes:result length:result_size];
      id object = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
      parsed = [object isKindOfClass:[NSDictionary class]] ? object : nil;
    }
    if (success) {
      completion(parsed ?: @{}, nil);
    } else {
      completion(nil, [parsed[@"message"] isKindOfClass:[NSString class]] ? parsed[@"message"]
                                                                          : @"DevTools method failed");
    }
  }

 private:
  __weak MCEFBrowserView* view_;

  IMPLEMENT_REFCOUNTING(MosaicDevToolsObserver);
  DISALLOW_COPY_AND_ASSIGN(MosaicDevToolsObserver);
};

class MosaicPdfCallback : public CefPdfPrintCallback {
 public:
  explicit MosaicPdfCallback(void (^completion)(BOOL)) : completion_([completion copy]) {}

  void OnPdfPrintFinished(const CefString& path, bool ok) override {
    void (^completion)(BOOL) = completion_;
    completion_ = nil;
    if (completion) {
      dispatch_async(dispatch_get_main_queue(), ^{
        completion(ok);
      });
    }
  }

 private:
  void (^completion_)(BOOL);

  IMPLEMENT_REFCOUNTING(MosaicPdfCallback);
  DISALLOW_COPY_AND_ASSIGN(MosaicPdfCallback);
};

}  // namespace

// MARK: - MosaicPageHandler

void MosaicPageHandler::OnFindResult(CefRefPtr<CefBrowser> browser,
                                     int identifier,
                                     int count,
                                     const CefRect& selectionRect,
                                     int activeMatchOrdinal,
                                     bool finalUpdate) {
  MCEFBrowserView* view = view_;
  id<MCEFPageDelegate> delegate = PageDelegate(view);
  if ([delegate respondsToSelector:@selector(browserView:didUpdateFindMatches:activeMatch:finalUpdate:)]) {
    [delegate browserView:view
        didUpdateFindMatches:count
                 activeMatch:MAX(activeMatchOrdinal, 0)
                 finalUpdate:finalUpdate];
  }
}

bool MosaicPageHandler::OnRequestMediaAccessPermission(CefRefPtr<CefBrowser> browser,
                                                       CefRefPtr<CefFrame> frame,
                                                       const CefString& requesting_origin,
                                                       uint32_t requested_permissions,
                                                       CefRefPtr<CefMediaAccessCallback> callback) {
  MCEFBrowserView* view = view_;
  id<MCEFPageDelegate> delegate = PageDelegate(view);
  if (![delegate respondsToSelector:@selector(browserView:requestsPermission:)]) {
    return false;  // Alloy's default: deny
  }
  MCEFPermissionKinds kinds = 0;
  if (requested_permissions & CEF_MEDIA_PERMISSION_DEVICE_VIDEO_CAPTURE) kinds |= MCEFPermissionCamera;
  if (requested_permissions & CEF_MEDIA_PERMISSION_DEVICE_AUDIO_CAPTURE) kinds |= MCEFPermissionMicrophone;
  if (requested_permissions & (CEF_MEDIA_PERMISSION_DESKTOP_AUDIO_CAPTURE | CEF_MEDIA_PERMISSION_DESKTOP_VIDEO_CAPTURE)) {
    kinds |= MCEFPermissionScreenCapture;
  }
  MCEFPermissionRequest* request = [[MCEFPermissionRequest alloc] initWithOrigin:PageString(requesting_origin)
                                                                           kinds:kinds
                                                                      otherKinds:@[]
                                                                   mediaCallback:callback
                                                                       mediaMask:requested_permissions
                                                                  promptCallback:nullptr];
  [delegate browserView:view requestsPermission:request];
  return true;
}

bool MosaicPageHandler::OnShowPermissionPrompt(CefRefPtr<CefBrowser> browser,
                                               uint64_t prompt_id,
                                               const CefString& requesting_origin,
                                               uint32_t requested_permissions,
                                               CefRefPtr<CefPermissionPromptCallback> callback) {
  MCEFBrowserView* view = view_;
  id<MCEFPageDelegate> delegate = PageDelegate(view);
  if (![delegate respondsToSelector:@selector(browserView:requestsPermission:)]) {
    return false;  // Alloy's default: ignore
  }
  struct Named {
    uint32_t bit;
    MCEFPermissionKinds kind;
    NSString* name;
  };
  static const Named kTypes[] = {
      {CEF_PERMISSION_TYPE_CAMERA_STREAM, MCEFPermissionCamera, nil},
      {CEF_PERMISSION_TYPE_CAMERA_PAN_TILT_ZOOM, MCEFPermissionCamera, nil},
      {CEF_PERMISSION_TYPE_MIC_STREAM, MCEFPermissionMicrophone, nil},
      {CEF_PERMISSION_TYPE_GEOLOCATION, MCEFPermissionGeolocation, nil},
      {CEF_PERMISSION_TYPE_NOTIFICATIONS, MCEFPermissionNotifications, nil},
      {CEF_PERMISSION_TYPE_CLIPBOARD, MCEFPermissionClipboard, nil},
      {CEF_PERMISSION_TYPE_CAPTURED_SURFACE_CONTROL, MCEFPermissionScreenCapture, nil},
      {CEF_PERMISSION_TYPE_MIDI_SYSEX, MCEFPermissionOther, @"midi-sysex"},
      {CEF_PERMISSION_TYPE_STORAGE_ACCESS, MCEFPermissionOther, @"storage-access"},
      {CEF_PERMISSION_TYPE_TOP_LEVEL_STORAGE_ACCESS, MCEFPermissionOther, @"storage-access"},
      {CEF_PERMISSION_TYPE_MULTIPLE_DOWNLOADS, MCEFPermissionOther, @"multiple-downloads"},
      {CEF_PERMISSION_TYPE_WINDOW_MANAGEMENT, MCEFPermissionOther, @"window-management"},
      {CEF_PERMISSION_TYPE_LOCAL_FONTS, MCEFPermissionOther, @"local-fonts"},
      {CEF_PERMISSION_TYPE_IDLE_DETECTION, MCEFPermissionOther, @"idle-detection"},
      {CEF_PERMISSION_TYPE_LOCAL_NETWORK, MCEFPermissionOther, @"local-network"},
      {CEF_PERMISSION_TYPE_LOOPBACK_NETWORK, MCEFPermissionOther, @"local-network"},
      {CEF_PERMISSION_TYPE_KEYBOARD_LOCK, MCEFPermissionOther, @"keyboard-lock"},
      {CEF_PERMISSION_TYPE_POINTER_LOCK, MCEFPermissionOther, @"pointer-lock"},
      {CEF_PERMISSION_TYPE_FILE_SYSTEM_ACCESS, MCEFPermissionOther, @"file-system"},
      {CEF_PERMISSION_TYPE_SENSORS, MCEFPermissionOther, @"sensors"},
  };
  MCEFPermissionKinds kinds = 0;
  NSMutableArray<NSString*>* other = [NSMutableArray array];
  uint32_t known = 0;
  for (const Named& type : kTypes) {
    known |= type.bit;
    if (requested_permissions & type.bit) {
      kinds |= type.kind;
      if (type.name && ![other containsObject:type.name]) {
        [other addObject:type.name];
      }
    }
  }
  if (requested_permissions & ~known) {
    kinds |= MCEFPermissionOther;
    [other addObject:@"other"];
  }
  MCEFPermissionRequest* request = [[MCEFPermissionRequest alloc] initWithOrigin:PageString(requesting_origin)
                                                                           kinds:kinds
                                                                      otherKinds:other
                                                                   mediaCallback:nullptr
                                                                       mediaMask:0
                                                                  promptCallback:callback];
  [PageState(view).prompts setObject:request forKey:@(prompt_id)];
  [delegate browserView:view requestsPermission:request];
  return true;
}

void MosaicPageHandler::OnDismissPermissionPrompt(CefRefPtr<CefBrowser> browser,
                                                  uint64_t prompt_id,
                                                  cef_permission_request_result_t result) {
  MCEFBrowserView* view = view_;
  if (!view) {
    return;
  }
  MCEFPageState* state = PageState(view);
  MCEFPermissionRequest* request = [state.prompts objectForKey:@(prompt_id)];
  [state.prompts removeObjectForKey:@(prompt_id)];
  if (!request || request.isAnswered) {
    return;
  }
  [request markCancelled];
  id<MCEFPageDelegate> delegate = PageDelegate(view);
  if ([delegate respondsToSelector:@selector(browserView:didCancelPermissionRequest:)]) {
    [delegate browserView:view didCancelPermissionRequest:request];
  }
}

bool MosaicPageHandler::GetAudioParameters(CefRefPtr<CefBrowser> browser, CefAudioParameters& params) {
  // Called each time the page becomes audible. Declining keeps normal playback.
  MCEFBrowserView* view = view_;
  dispatch_async(dispatch_get_main_queue(), ^{
    id<MCEFPageDelegate> delegate = PageDelegate(view);
    if ([delegate respondsToSelector:@selector(browserViewDidStartAudio:)]) {
      [delegate browserViewDidStartAudio:view];
    }
  });
  return false;
}

bool MosaicPageHandler::OnPreKeyEvent(CefRefPtr<CefBrowser> browser,
                                      const CefKeyEvent& event,
                                      CefEventHandle os_event,
                                      bool* is_keyboard_shortcut) {
  // Alloy-style browsers leave "Esc leaves full screen" to the app.
  const int kEscape = 0x1B;
  if (event.type == KEYEVENT_RAWKEYDOWN && event.windows_key_code == kEscape && browser->GetHost()->IsFullscreen()) {
    browser->GetHost()->ExitFullscreen(true);
    return true;
  }
  return false;
}

bool MosaicPageHandler::OnJSDialog(CefRefPtr<CefBrowser> browser,
                                   const CefString& origin_url,
                                   JSDialogType dialog_type,
                                   const CefString& message_text,
                                   const CefString& default_prompt_text,
                                   CefRefPtr<CefJSDialogCallback> callback,
                                   bool& suppress_message) {
  MCEFBrowserView* view = view_;
  id<MCEFPageDelegate> delegate = PageDelegate(view);
  if (![delegate respondsToSelector:@selector(browserView:runJavaScriptDialog:)]) {
    return false;
  }
  MCEFDialogKind kind = dialog_type == JSDIALOGTYPE_CONFIRM  ? MCEFDialogKindConfirm
                        : dialog_type == JSDIALOGTYPE_PROMPT ? MCEFDialogKindPrompt
                                                             : MCEFDialogKindAlert;
  std::string origin = origin_url.ToString();
  // data: pages have no meaningful origin to show.
  NSString* shown = origin.rfind("data:", 0) == 0 ? @"" : PageString(CefFormatUrlForSecurityDisplay(origin_url));
  MCEFJavaScriptDialog* dialog = [[MCEFJavaScriptDialog alloc] initWithKind:kind
                                                                    message:PageString(message_text)
                                                                defaultText:PageString(default_prompt_text)
                                                                     origin:shown
                                                                   callback:callback];
  [delegate browserView:view runJavaScriptDialog:dialog];
  return true;
}

bool MosaicPageHandler::OnBeforeUnloadDialog(CefRefPtr<CefBrowser> browser,
                                             const CefString& message_text,
                                             bool is_reload,
                                             CefRefPtr<CefJSDialogCallback> callback) {
  MCEFBrowserView* view = view_;
  id<MCEFPageDelegate> delegate = PageDelegate(view);
  if (![delegate respondsToSelector:@selector(browserView:runJavaScriptDialog:)]) {
    return false;
  }
  NSString* origin = PageString(CefFormatUrlForSecurityDisplay(browser->GetMainFrame()->GetURL()));
  MCEFJavaScriptDialog* dialog =
      [[MCEFJavaScriptDialog alloc] initWithKind:MCEFDialogKindBeforeUnload
                                         message:is_reload ? @"Reload this page? Changes you made may not be saved."
                                                           : @"Leave this page? Changes you made may not be saved."
                                     defaultText:@""
                                          origin:[origin hasPrefix:@"data:"] ? @"" : origin
                                        callback:callback];
  [delegate browserView:view runJavaScriptDialog:dialog];
  return true;
}

void MosaicPageHandler::OnResetDialogState(CefRefPtr<CefBrowser> browser) {
  MCEFBrowserView* view = view_;
  id<MCEFPageDelegate> delegate = PageDelegate(view);
  if ([delegate respondsToSelector:@selector(browserViewDidResetJavaScriptDialogs:)]) {
    [delegate browserViewDidResetJavaScriptDialogs:view];
  }
}

bool MosaicPageHandler::BlockPopupIfNeeded(const CefString& target_url, bool user_gesture) {
  if (user_gesture || target_url.empty()) {
    return false;
  }
  MCEFBrowserView* view = view_;
  id<MCEFPageDelegate> delegate = PageDelegate(view);
  if (![delegate respondsToSelector:@selector(browserView:didBlockPopupWithURL:)]) {
    return false;
  }
  [delegate browserView:view didBlockPopupWithURL:PageString(target_url)];
  return true;
}

void MosaicPageHandler::FullscreenChanged(bool fullscreen) {
  MCEFBrowserView* view = view_;
  id<MCEFPageDelegate> delegate = PageDelegate(view);
  if ([delegate respondsToSelector:@selector(browserView:didChangeFullscreen:)]) {
    [delegate browserView:view didChangeFullscreen:fullscreen];
  }
}

void MosaicPageHandler::MediaAccessChanged(bool video, bool audio) {
  MCEFBrowserView* view = view_;
  id<MCEFPageDelegate> delegate = PageDelegate(view);
  if ([delegate respondsToSelector:@selector(browserView:didChangeMediaAccessVideo:audio:)]) {
    [delegate browserView:view didChangeMediaAccessVideo:video audio:audio];
  }
}

// MARK: - MosaicCEF (PageFeatures)

@implementation MosaicCEF (PageFeatures)

+ (id<MCEFPageDelegate>)pageDelegate {
  return g_pageDelegate;
}

+ (void)setPageDelegate:(id<MCEFPageDelegate>)delegate {
  g_pageDelegate = delegate;
}

+ (void)routePopupBlockingToPageDelegate {
  if (!MosaicCEF.isRunning) {
    return;
  }
  CefRefPtr<CefRequestContext> context = CefRequestContext::GetGlobalContext();
  if (context) {
    // Empty URLs set the default for every site.
    context->SetContentSetting(CefString(), CefString(), CEF_CONTENT_SETTING_TYPE_POPUPS,
                               CEF_CONTENT_SETTING_VALUE_ALLOW);
  }
}

+ (void)setContentSetting:(MCEFContentSetting)setting
                    value:(MCEFContentSettingValue)value
                forOrigin:(NSString*)origin {
  if (!MosaicCEF.isRunning || origin.length == 0) {
    return;
  }
  CefRefPtr<CefRequestContext> context = CefRequestContext::GetGlobalContext();
  if (!context) {
    return;
  }
  cef_content_setting_types_t type;
  switch (setting) {
    case MCEFContentSettingGeolocation: type = CEF_CONTENT_SETTING_TYPE_GEOLOCATION; break;
    case MCEFContentSettingNotifications: type = CEF_CONTENT_SETTING_TYPE_NOTIFICATIONS; break;
    case MCEFContentSettingClipboard: type = CEF_CONTENT_SETTING_TYPE_CLIPBOARD_READ_WRITE; break;
    case MCEFContentSettingCamera: type = CEF_CONTENT_SETTING_TYPE_MEDIASTREAM_CAMERA; break;
    case MCEFContentSettingMicrophone: type = CEF_CONTENT_SETTING_TYPE_MEDIASTREAM_MIC; break;
  }
  cef_content_setting_values_t cefValue = value == MCEFContentSettingValueAllow   ? CEF_CONTENT_SETTING_VALUE_ALLOW
                                          : value == MCEFContentSettingValueBlock ? CEF_CONTENT_SETTING_VALUE_BLOCK
                                                                                  : CEF_CONTENT_SETTING_VALUE_DEFAULT;
  std::string url = std::string(origin.UTF8String) + "/";
  context->SetContentSetting(url, url, type, cefValue);
}

@end

// MARK: - MCEFBrowserView (PageFeatures)

@implementation MCEFBrowserView (PageFeatures)

- (id<MCEFPageDelegate>)pageDelegate {
  return PageState(self).delegate;
}

- (void)setPageDelegate:(id<MCEFPageDelegate>)delegate {
  PageState(self).delegate = delegate;
}

- (BOOL)hasBrowser {
  return [self mosaicBrowser] != nullptr;
}

- (void)mosaicPageBrowserCreated {
  CefRefPtr<CefBrowser> browser = [self mosaicBrowser];
  if (!browser) {
    return;
  }
  MCEFPageState* state = PageState(self);
  if (state.zoomLevel != 0) {
    browser->GetHost()->SetZoomLevel(state.zoomLevel);
  }
  if (state.audioMuted) {
    browser->GetHost()->SetAudioMuted(true);
  }
  id<MCEFPageDelegate> delegate = PageDelegate(self);
  if ([delegate respondsToSelector:@selector(browserViewDidCreateBrowser:)]) {
    [delegate browserViewDidCreateBrowser:self];
  }
}

- (void)mosaicPageBrowserClosed {
  MCEFPageState* state = PageState(self);
  state->devToolsRegistration = nullptr;
  NSDictionary* callbacks = [state.devToolsCallbacks copy];
  [state.devToolsCallbacks removeAllObjects];
  for (void (^completion)(NSDictionary*, NSString*) in callbacks.allValues) {
    completion(nil, @"The page was closed.");
  }
}

- (void)findText:(NSString*)text forward:(BOOL)forward matchCase:(BOOL)matchCase findNext:(BOOL)findNext {
  CefRefPtr<CefBrowser> browser = [self mosaicBrowser];
  if (browser && text.length) {
    browser->GetHost()->Find(text.UTF8String, forward, matchCase, findNext);
  }
}

- (void)stopFindingAndClearSelection:(BOOL)clearSelection {
  CefRefPtr<CefBrowser> browser = [self mosaicBrowser];
  if (browser) {
    browser->GetHost()->StopFinding(clearSelection);
  }
}

- (double)zoomLevel {
  CefRefPtr<CefBrowser> browser = [self mosaicBrowser];
  return browser ? browser->GetHost()->GetZoomLevel() : PageState(self).zoomLevel;
}

- (void)setZoomLevel:(double)zoomLevel {
  PageState(self).zoomLevel = zoomLevel;
  CefRefPtr<CefBrowser> browser = [self mosaicBrowser];
  if (browser) {
    browser->GetHost()->SetZoomLevel(zoomLevel);
  }
}

- (void)print {
  CefRefPtr<CefBrowser> browser = [self mosaicBrowser];
  if (browser) {
    browser->GetHost()->Print();
  }
}

- (void)printToPDF:(NSString*)path completion:(void (^)(BOOL))completion {
  CefRefPtr<CefBrowser> browser = [self mosaicBrowser];
  if (!browser) {
    completion(NO);
    return;
  }
  CefPdfPrintSettings settings;
  // The paper size the user set up for printing (A4, Letter, …), in inches.
  NSSize paper = NSPrintInfo.sharedPrintInfo.paperSize;
  if (paper.width > 0 && paper.height > 0) {
    settings.paper_width = paper.width / 72.0;
    settings.paper_height = paper.height / 72.0;
  }
  settings.print_background = false;
  browser->GetHost()->PrintToPDF(path.UTF8String, settings, new MosaicPdfCallback(completion));
}

- (void)showDevTools {
  CefRefPtr<CefBrowser> browser = [self mosaicBrowser];
  if (!browser) {
    return;
  }
  CefWindowInfo windowInfo;
  // No parent: CEF gives the tools a window of their own.
  windowInfo.runtime_style = CEF_RUNTIME_STYLE_ALLOY;
  NSRect frame = self.window ? self.window.frame : NSMakeRect(0, 0, 1100, 760);
  windowInfo.bounds = CefRect(static_cast<int>(NSMinX(frame)) + 40, 80, 1000, 720);
  CefString(&windowInfo.window_name) = "Developer Tools";
  CefBrowserSettings settings;
  browser->GetHost()->ShowDevTools(windowInfo, nullptr, settings, CefPoint());
}

- (void)closeDevTools {
  CefRefPtr<CefBrowser> browser = [self mosaicBrowser];
  if (browser) {
    browser->GetHost()->CloseDevTools();
  }
}

- (BOOL)hasDevTools {
  CefRefPtr<CefBrowser> browser = [self mosaicBrowser];
  return browser ? browser->GetHost()->HasDevTools() : NO;
}

- (BOOL)audioMuted {
  return PageState(self).audioMuted;
}

- (void)setAudioMuted:(BOOL)audioMuted {
  PageState(self).audioMuted = audioMuted;
  CefRefPtr<CefBrowser> browser = [self mosaicBrowser];
  if (browser) {
    browser->GetHost()->SetAudioMuted(audioMuted);
  }
}

- (void)executeDevToolsMethod:(NSString*)method
                       params:(NSDictionary<NSString*, id>*)params
                   completion:(void (^)(NSDictionary<NSString*, id>*, NSString*))completion {
  CefRefPtr<CefBrowser> browser = [self mosaicBrowser];
  if (!browser) {
    completion(nil, @"The page is not ready.");
    return;
  }
  CefRefPtr<CefDictionaryValue> cefParams;
  if (params.count) {
    NSData* json = [NSJSONSerialization dataWithJSONObject:params options:0 error:nil];
    CefRefPtr<CefValue> value = json ? CefParseJSON(json.bytes, json.length, JSON_PARSER_RFC) : nullptr;
    if (!value || value->GetType() != VTYPE_DICTIONARY) {
      completion(nil, @"Invalid parameters.");
      return;
    }
    cefParams = value->GetDictionary();
  }
  MCEFPageState* state = PageState(self);
  if (!state->devToolsRegistration) {
    state->devToolsRegistration = browser->GetHost()->AddDevToolsMessageObserver(new MosaicDevToolsObserver(self));
  }
  int messageID = browser->GetHost()->ExecuteDevToolsMethod(0, method.UTF8String, cefParams);
  if (messageID == 0) {
    completion(nil, @"The DevTools method could not be sent.");
    return;
  }
  state.devToolsCallbacks[@(messageID)] = [completion copy];
}

- (NSView*)pageContentView {
  CefRefPtr<CefBrowser> browser = [self mosaicBrowser];
  return browser ? CAST_CEF_WINDOW_HANDLE_TO_NSVIEW(browser->GetHost()->GetWindowHandle()) : nil;
}

- (BOOL)isFullscreen {
  CefRefPtr<CefBrowser> browser = [self mosaicBrowser];
  return browser ? browser->GetHost()->IsFullscreen() : NO;
}

- (void)exitFullscreen {
  CefRefPtr<CefBrowser> browser = [self mosaicBrowser];
  if (browser && browser->GetHost()->IsFullscreen()) {
    browser->GetHost()->ExitFullscreen(true);
  }
}

@end
