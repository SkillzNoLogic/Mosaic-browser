// Page scripts for MCEFBrowserView: evaluate JavaScript in the page and receive messages
// from it. Added by the productivity area for reader mode; usable by any feature.
//
// Both go through the DevTools protocol that CEF exposes in-process
// (CefBrowserHost::ExecuteDevToolsMethod): `Runtime.evaluate` returns a script's value, and
// `Runtime.addBinding` installs a page function whose calls arrive as
// `Runtime.bindingCalled` events. No renderer-process code is involved, so the helper app
// is unchanged. All of it runs on the main thread (CEF's UI thread on macOS).

#import "CEFBridge.h"

#import <objc/runtime.h>

#include "include/cef_browser.h"
#include "include/cef_devtools_message_observer.h"
#include "include/cef_parser.h"
#include "include/cef_registration.h"
#include "include/cef_values.h"

#if !__has_feature(objc_arc)
#error "CEFBridge must be compiled with ARC"
#endif

/// Implemented in CEFBridge.mm: the view's browser (borrowed, may be null).
@interface MCEFBrowserView (MosaicBrowserAccess)
- (void*)mosaicBrowserHandle;
@end

typedef void (^MCEFScriptCompletion)(id _Nullable value, NSString* _Nullable error);
typedef void (^MCEFMessageHandler)(NSString* payload);

@class MCEFScriptChannel;

namespace {

NSDictionary* ParseJSONObject(const void* bytes, size_t size) {
  if (!bytes || size == 0) {
    return nil;
  }
  NSData* data = [NSData dataWithBytesNoCopy:const_cast<void*>(bytes) length:size freeWhenDone:NO];
  id object = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
  return [object isKindOfClass:[NSDictionary class]] ? object : nil;
}

NSString* ToNSString(const CefString& value) {
  std::string utf8 = value.ToString();
  return [[NSString alloc] initWithBytes:utf8.data() length:utf8.size() encoding:NSUTF8StringEncoding]
      ?: @"";
}

}  // namespace

/// Per-browser state: the DevTools observer, calls awaiting their result, message handlers.
@interface MCEFScriptChannel : NSObject
@property(nonatomic, readonly) NSMutableDictionary<NSNumber*, MCEFScriptCompletion>* pending;
@property(nonatomic, readonly) NSMutableDictionary<NSString*, MCEFMessageHandler>* handlers;
@property(nonatomic) BOOL runtimeEnabled;
/// Message IDs whose completion wants the raw result dictionary (callDevToolsMethod).
@property(nonatomic, readonly) NSMutableSet<NSNumber*>* rawResults;
- (void)attachToBrowser:(CefRefPtr<CefBrowser>)browser;
- (void)detachIfIdle;
- (void)detachIfIdleNow;
- (void)methodResult:(int)messageID success:(bool)success json:(NSDictionary*)json;
- (void)event:(NSString*)method json:(NSDictionary*)json;
@end

namespace {

class ScriptObserver : public CefDevToolsMessageObserver {
 public:
  explicit ScriptObserver(MCEFScriptChannel* channel) : channel_(channel) {}

  void OnDevToolsMethodResult(CefRefPtr<CefBrowser> browser,
                              int message_id,
                              bool success,
                              const void* result,
                              size_t result_size) override {
    MCEFScriptChannel* channel = channel_;
    // Other observers' calls arrive here too; only ours are pending.
    if (!channel || !channel.pending[@(message_id)]) {
      return;
    }
    [channel methodResult:message_id success:success json:ParseJSONObject(result, result_size)];
  }

  void OnDevToolsEvent(CefRefPtr<CefBrowser> browser,
                       const CefString& method,
                       const void* params,
                       size_t params_size) override {
    // Runtime.enable also streams console and context events; skip them unparsed.
    if (method != "Runtime.bindingCalled") {
      return;
    }
    MCEFScriptChannel* channel = channel_;
    [channel event:ToNSString(method) json:ParseJSONObject(params, params_size)];
  }

 private:
  __weak MCEFScriptChannel* channel_;

  IMPLEMENT_REFCOUNTING(ScriptObserver);
};

}  // namespace

@implementation MCEFScriptChannel {
  CefRefPtr<CefRegistration> _registration;
  CefRefPtr<ScriptObserver> _observer;
}

- (instancetype)init {
  if ((self = [super init])) {
    _pending = [NSMutableDictionary dictionary];
    _handlers = [NSMutableDictionary dictionary];
    _rawResults = [NSMutableSet set];
  }
  return self;
}

- (void)attachToBrowser:(CefRefPtr<CefBrowser>)browser {
  if (_registration) {
    return;
  }
  _observer = new ScriptObserver(self);
  _registration = browser->GetHost()->AddDevToolsMessageObserver(_observer);
}

/// The observer stays registered only while something is waiting for it: a lingering
/// registration must not outlive the browser (it would hold up closing it).
- (void)detachIfIdle {
  // Called from inside observer callbacks: not while CEF is still delivering to it.
  dispatch_async(dispatch_get_main_queue(), ^{
    [self detachIfIdleNow];
  });
}

- (void)detachIfIdleNow {
  if (self.pending.count == 0 && self.handlers.count == 0) {
    _registration = nullptr;
    _observer = nullptr;
    self.runtimeEnabled = NO;
  }
}

- (void)methodResult:(int)messageID success:(bool)success json:(NSDictionary*)json {
  MCEFScriptCompletion completion = self.pending[@(messageID)];
  [self.pending removeObjectForKey:@(messageID)];
  [self detachIfIdle];
  if (!completion) {
    return;
  }
  if ([self.rawResults containsObject:@(messageID)]) {
    [self.rawResults removeObject:@(messageID)];
    completion(success ? (json ?: @{}) : nil, success ? nil : ([json[@"message"] description] ?: @"DevTools call failed"));
    return;
  }
  if (!success) {
    completion(nil, [json[@"message"] description] ?: @"DevTools call failed");
    return;
  }
  NSDictionary* exception = json[@"exceptionDetails"];
  if ([exception isKindOfClass:[NSDictionary class]]) {
    NSString* text = exception[@"exception"][@"description"] ?: exception[@"text"] ?: @"script error";
    completion(nil, [text description]);
    return;
  }
  NSDictionary* result = json[@"result"];
  completion([result isKindOfClass:[NSDictionary class]] ? (result[@"value"] ?: [NSNull null]) : [NSNull null], nil);
}

- (void)event:(NSString*)method json:(NSDictionary*)json {
  NSString* name = json[@"name"];
  NSString* payload = json[@"payload"];
  if (![name isKindOfClass:[NSString class]] || ![payload isKindOfClass:[NSString class]]) {
    return;
  }
  MCEFMessageHandler handler = self.handlers[name];
  if (handler) {
    handler(payload);
  }
}

@end

@implementation MCEFBrowserView (MosaicScript)

static const void* kChannelKey = &kChannelKey;

/// The channel for this view's browser, or nil while the browser does not exist.
- (MCEFScriptChannel*)mosaicScriptChannel:(CefRefPtr<CefBrowser>*)outBrowser {
  CefBrowser* raw = static_cast<CefBrowser*>([self mosaicBrowserHandle]);
  if (!raw) {
    return nil;
  }
  CefRefPtr<CefBrowser> browser(raw);
  MCEFScriptChannel* channel = objc_getAssociatedObject(self, kChannelKey);
  if (!channel) {
    channel = [[MCEFScriptChannel alloc] init];
    objc_setAssociatedObject(self, kChannelKey, channel, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
  }
  [channel attachToBrowser:browser];
  if (outBrowser) {
    *outBrowser = browser;
  }
  return channel;
}

- (BOOL)hasPageScriptSupport {
  return [self mosaicBrowserHandle] != nullptr;
}

- (void)evaluatePageScript:(NSString*)script
                completion:(void (^)(id _Nullable value, NSString* _Nullable error))completion {
  CefRefPtr<CefBrowser> browser;
  MCEFScriptChannel* channel = [self mosaicScriptChannel:&browser];
  if (!channel) {
    completion(nil, @"The page is not ready yet.");
    return;
  }
  CefRefPtr<CefDictionaryValue> params = CefDictionaryValue::Create();
  params->SetString("expression", script.UTF8String);
  params->SetBool("returnByValue", true);
  params->SetBool("awaitPromise", true);
  int messageID = browser->GetHost()->ExecuteDevToolsMethod(0, "Runtime.evaluate", params);
  if (messageID == 0) {
    completion(nil, @"Could not run the script.");
    return;
  }
  channel.pending[@(messageID)] = [completion copy];
}

- (void)addPageMessageHandlerNamed:(NSString*)name handler:(void (^)(NSString* payload))handler {
  CefRefPtr<CefBrowser> browser;
  MCEFScriptChannel* channel = [self mosaicScriptChannel:&browser];
  if (!channel) {
    return;
  }
  BOOL isNew = channel.handlers[name] == nil;
  channel.handlers[name] = [handler copy];
  if (!isNew) {
    return;
  }
  // Binding calls are only reported while the Runtime domain is enabled.
  if (!channel.runtimeEnabled) {
    channel.runtimeEnabled = YES;
    browser->GetHost()->ExecuteDevToolsMethod(0, "Runtime.enable", nullptr);
  }
  CefRefPtr<CefDictionaryValue> params = CefDictionaryValue::Create();
  params->SetString("name", name.UTF8String);
  browser->GetHost()->ExecuteDevToolsMethod(0, "Runtime.addBinding", params);
}

- (void)removePageMessageHandlerNamed:(NSString*)name {
  CefRefPtr<CefBrowser> browser;
  MCEFScriptChannel* channel = [self mosaicScriptChannel:&browser];
  if (!channel || channel.handlers[name] == nil) {
    return;
  }
  [channel.handlers removeObjectForKey:name];
  CefRefPtr<CefDictionaryValue> params = CefDictionaryValue::Create();
  params->SetString("name", name.UTF8String);
  browser->GetHost()->ExecuteDevToolsMethod(0, "Runtime.removeBinding", params);
  if (channel.handlers.count == 0 && channel.runtimeEnabled) {
    browser->GetHost()->ExecuteDevToolsMethod(0, "Runtime.disable", nullptr);
  }
  [channel detachIfIdleNow];
}

- (void)callDevToolsMethod:(NSString*)method
                    params:(NSDictionary<NSString*, id>*)params
                completion:(void (^)(NSDictionary* _Nullable result, NSString* _Nullable error))completion {
  CefRefPtr<CefBrowser> browser;
  MCEFScriptChannel* channel = [self mosaicScriptChannel:&browser];
  if (!channel) {
    completion(nil, @"The page is not ready yet.");
    return;
  }
  CefRefPtr<CefDictionaryValue> dictionary;
  if (params.count) {
    NSData* data = [NSJSONSerialization dataWithJSONObject:params options:0 error:nil];
    NSString* text = data ? [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding] : nil;
    CefRefPtr<CefValue> value = text ? CefParseJSON(text.UTF8String, JSON_PARSER_RFC) : nullptr;
    if (!value || value->GetType() != VTYPE_DICTIONARY) {
      completion(nil, @"Parameters must be a JSON dictionary.");
      return;
    }
    dictionary = value->GetDictionary();
  }
  int messageID = browser->GetHost()->ExecuteDevToolsMethod(0, method.UTF8String, dictionary);
  if (messageID == 0) {
    completion(nil, @"Could not call the DevTools method.");
    return;
  }
  [channel.rawResults addObject:@(messageID)];
  channel.pending[@(messageID)] = [completion copy];
}

- (void)releasePageScripts {
  MCEFScriptChannel* channel = objc_getAssociatedObject(self, kChannelKey);
  if (!channel) {
    return;
  }
  CefBrowser* raw = static_cast<CefBrowser*>([self mosaicBrowserHandle]);
  if (raw && (channel.handlers.count || channel.runtimeEnabled)) {
    CefRefPtr<CefBrowser> browser(raw);
    for (NSString* name in channel.handlers) {
      CefRefPtr<CefDictionaryValue> params = CefDictionaryValue::Create();
      params->SetString("name", name.UTF8String);
      browser->GetHost()->ExecuteDevToolsMethod(0, "Runtime.removeBinding", params);
    }
    browser->GetHost()->ExecuteDevToolsMethod(0, "Runtime.disable", nullptr);
  }
  NSDictionary<NSNumber*, MCEFScriptCompletion>* pending = [channel.pending copy];
  [channel.pending removeAllObjects];
  [channel.handlers removeAllObjects];
  [channel detachIfIdleNow];
  objc_setAssociatedObject(self, kChannelKey, nil, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
  for (MCEFScriptCompletion completion in pending.allValues) {
    completion(nil, @"The page was closed.");
  }
}

@end
