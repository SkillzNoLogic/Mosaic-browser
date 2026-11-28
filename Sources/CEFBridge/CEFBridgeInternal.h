// Shared between CEFBridge.mm and CEFBridge+Engine.mm; not visible to Swift.
// Engine area: the page-feature handlers of one browser view.

#import "CEFBridge.h"

#include "include/cef_audio_handler.h"
#include "include/cef_browser.h"
#include "include/cef_find_handler.h"
#include "include/cef_jsdialog_handler.h"
#include "include/cef_keyboard_handler.h"
#include "include/cef_permission_handler.h"

@interface MCEFBrowserView (Internal)
/// The live browser, or null. Implemented in CEFBridge.mm.
- (CefRefPtr<CefBrowser>)mosaicBrowser;
/// Implemented in CEFBridge+Engine.mm, called by CEFBridge.mm.
- (void)mosaicPageBrowserCreated;
- (void)mosaicPageBrowserClosed;
@end

/// Find results, permission prompts, audio start, Esc-to-leave-full-screen and JavaScript
/// dialogs for one browser view, forwarded to its page delegate.
class MosaicPageHandler : public CefFindHandler,
                          public CefPermissionHandler,
                          public CefAudioHandler,
                          public CefKeyboardHandler,
                          public CefJSDialogHandler {
 public:
  explicit MosaicPageHandler(MCEFBrowserView* view) : view_(view) {}

  // CefFindHandler
  void OnFindResult(CefRefPtr<CefBrowser> browser, int identifier, int count, const CefRect& selectionRect,
                    int activeMatchOrdinal, bool finalUpdate) override;

  // CefPermissionHandler
  bool OnRequestMediaAccessPermission(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
                                      const CefString& requesting_origin, uint32_t requested_permissions,
                                      CefRefPtr<CefMediaAccessCallback> callback) override;
  bool OnShowPermissionPrompt(CefRefPtr<CefBrowser> browser, uint64_t prompt_id,
                              const CefString& requesting_origin, uint32_t requested_permissions,
                              CefRefPtr<CefPermissionPromptCallback> callback) override;
  void OnDismissPermissionPrompt(CefRefPtr<CefBrowser> browser, uint64_t prompt_id,
                                 cef_permission_request_result_t result) override;

  // CefAudioHandler: used only to learn that a page became audible. Capture is always
  // declined, so the page's sound keeps playing normally.
  bool GetAudioParameters(CefRefPtr<CefBrowser> browser, CefAudioParameters& params) override;
  void OnAudioStreamStarted(CefRefPtr<CefBrowser> browser, const CefAudioParameters& params,
                            int channels) override {}
  void OnAudioStreamPacket(CefRefPtr<CefBrowser> browser, const float** data, int frames,
                           int64_t pts) override {}
  void OnAudioStreamStopped(CefRefPtr<CefBrowser> browser) override {}
  void OnAudioStreamError(CefRefPtr<CefBrowser> browser, const CefString& message) override {}

  // CefKeyboardHandler
  bool OnPreKeyEvent(CefRefPtr<CefBrowser> browser, const CefKeyEvent& event, CefEventHandle os_event,
                     bool* is_keyboard_shortcut) override;

  // CefJSDialogHandler
  bool OnJSDialog(CefRefPtr<CefBrowser> browser, const CefString& origin_url, JSDialogType dialog_type,
                  const CefString& message_text, const CefString& default_prompt_text,
                  CefRefPtr<CefJSDialogCallback> callback, bool& suppress_message) override;
  bool OnBeforeUnloadDialog(CefRefPtr<CefBrowser> browser, const CefString& message_text, bool is_reload,
                            CefRefPtr<CefJSDialogCallback> callback) override;
  void OnResetDialogState(CefRefPtr<CefBrowser> browser) override;

  // Hooks called from MosaicClient (CEFBridge.mm).
  /// True if the pop-up was blocked (and reported); false to open it as before.
  bool BlockPopupIfNeeded(const CefString& target_url, bool user_gesture);
  void FullscreenChanged(bool fullscreen);
  void MediaAccessChanged(bool video, bool audio);

 private:
  __weak MCEFBrowserView* view_;

  IMPLEMENT_REFCOUNTING(MosaicPageHandler);
  DISALLOW_COPY_AND_ASSIGN(MosaicPageHandler);
};
