// Hooks CEFBridge.mm calls into the privacy features (CEFPrivacy.mm).
// Owner: privacy area. All of them run on CEF's UI thread (the main thread).
#pragma once

#import "CEFBridge.h"

#include "include/cef_browser.h"
#include "include/cef_frame.h"
#include "include/cef_request.h"
#include "include/cef_request_context.h"
#include "include/cef_request_context_handler.h"

namespace mosaic_privacy {

/// The request context a new browser for `view` should use: the in-memory
/// private profile for private views, nullptr (the default profile) otherwise.
CefRefPtr<CefRequestContext> RequestContextForView(MCEFBrowserView* view);

/// Browser lifecycle, for the private-profile count and DevTools helpers.
void BrowserCreated(CefRefPtr<CefBrowser> browser);
void BrowserClosing(CefRefPtr<CefBrowser> browser);

/// HTTPS-only upgrade and tracking-parameter removal. True cancels the
/// navigation (a replacement load has been scheduled).
bool OnBeforeBrowse(CefRefPtr<CefBrowser> browser,
                    CefRefPtr<CefFrame> frame,
                    CefRefPtr<CefRequest> request,
                    bool user_gesture,
                    bool is_redirect);

/// Cosmetic filtering: runs the style-injection script in a frame that just
/// committed a document.
void OnLoadStart(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame);

/// True when the failure was an HTTPS-only upgrade that did not work; Mosaic's
/// HTTPS-only page has then been shown instead of the generic error page.
bool OnLoadError(CefRefPtr<CefBrowser> browser,
                 CefRefPtr<CefFrame> frame,
                 int error_code,
                 const CefString& failed_url);

}  // namespace mosaic_privacy
