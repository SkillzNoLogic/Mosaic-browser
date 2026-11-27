// Hooks from MosaicClient (CEFBridge.mm) into password support (CEFPasswords.mm).
// Area: passwords. Private to the bridge (C++ types; not visible to Swift).
#pragma once

#include "include/cef_browser.h"

@class MCEFBrowserView;

namespace mosaic_passwords {

/// CefLoadHandler::OnLoadEnd: injects the load script into an http(s) frame.
void FrameLoaded(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame);

/// CefDisplayHandler::OnConsoleMessage: returns true (and so keeps the message out of
/// Chromium's log) for every message that carries the password prefix.
bool ConsoleMessage(MCEFBrowserView* view, CefRefPtr<CefBrowser> browser, const CefString& message);

}  // namespace mosaic_passwords
