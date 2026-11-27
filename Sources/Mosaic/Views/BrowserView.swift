import MosaicCore
import SwiftUI

enum Chrome {
    static let tabStripHeight: CGFloat = 38
    static let toolbarHeight: CGFloat = 40
    /// Width of the frame Mosaic draws around web content.
    static let frame: CGFloat = 6
    /// Room left for the window's close/minimise/zoom buttons.
    static let trafficLights: CGFloat = 78
    static let background = Color(nsColor: .windowBackgroundColor)

    static func tint(for engine: EngineKind) -> Color {
        switch engine {
        case .gecko: Color(red: 1.0, green: 0.45, blue: 0.1)
        case .chromium: Color(red: 0.2, green: 0.52, blue: 0.96)
        }
    }
}

/// The whole window: tab strip, toolbar, and the content area.
///
/// The window itself is transparent. Mosaic paints its chrome around the
/// content area but never behind it, because for Gecko tabs that area is a real
/// hole through which the docked Gecko window shows (see `GeckoHole`).
struct BrowserView: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        VStack(spacing: 0) {
            TabStrip()
                .frame(height: Chrome.tabStripHeight)
                .background(Chrome.background)
            ToolbarView()
                .frame(height: Chrome.toolbarHeight)
                .background(Chrome.background)
            ZStack {
                ContentFrame(width: Chrome.frame)
                    .fill(Chrome.background, style: FillStyle(eoFill: true))
                ContentArea()
                    .padding(EdgeInsets(top: 0, leading: Chrome.frame, bottom: Chrome.frame, trailing: Chrome.frame))
            }
        }
        .ignoresSafeArea()
        .overlay(alignment: .bottom) { NoticeBanner() }
        // platform: extensions are managed in Settings › Extensions (the sheet is gone).
        .onChange(of: model.showExtensions) { _, show in
            guard show else { return }
            model.showExtensions = false
            model.openSettings(.extensions)
        }
    }
}

/// The strips left, right and below the content area; the middle stays empty.
private struct ContentFrame: Shape {
    let width: CGFloat

    func path(in rect: CGRect) -> Path {
        var path = Path()
        path.addRect(rect)
        path.addRect(CGRect(
            x: rect.minX + width, y: rect.minY,
            width: max(0, rect.width - 2 * width), height: max(0, rect.height - width)))
        return path
    }
}

/// Short-lived message at the bottom of the window.
private struct NoticeBanner: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        if let notice = model.notice {
            Text(notice)
                .font(.callout)
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
                .background(.regularMaterial, in: Capsule())
                .overlay(Capsule().strokeBorder(.quaternary))
                .padding(.bottom, 22)
                .transition(.opacity)
                .task(id: notice) {
                    try? await Task.sleep(nanoseconds: 3_500_000_000)
                    if model.notice == notice { model.notice = nil }
                }
        }
    }
}

// MARK: - Tab strip

struct TabStrip: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        HStack(spacing: 6) {
            Color.clear.frame(width: Chrome.trafficLights)
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 4) {
                    ForEach(model.tabs) { tab in
                        TabChip(tab: tab, isSelected: tab.id == model.selectedTabID)
                    }
                }
                .padding(.vertical, 5)
            }
            Button {
                model.newTab()
            } label: {
                Image(systemName: "plus")
                    .frame(width: 26, height: 26)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help("New Tab (⌘T)")
            .accessibilityLabel("New Tab")
            Spacer(minLength: 8)
        }
    }
}

private struct TabChip: View {
    @EnvironmentObject private var model: BrowserModel
    @ObservedObject var tab: BrowserTab
    let isSelected: Bool
    // Plain `State` storage rather than `@State`: see README, "Building without Xcode".
    private var hovering = State(initialValue: false)
    private var isHovering: Bool { hovering.wrappedValue }

    var body: some View {
        HStack(spacing: 7) {
            if tab.isLoading {
                ProgressView().controlSize(.mini).frame(width: 10, height: 10)
            } else {
                Circle()
                    .fill(Chrome.tint(for: tab.engine))
                    .frame(width: 8, height: 8)
                    .help("Rendered by \(tab.engine.displayName) (\(tab.engine.engineName))")
            }
            Text(tab.displayTitle)
                .font(.system(size: 12))
                .lineLimit(1)
                .truncationMode(.tail)
                .frame(maxWidth: .infinity, alignment: .leading)
            Button {
                model.close(tab)
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 9, weight: .bold))
                    .frame(width: 16, height: 16)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .opacity(isSelected || isHovering ? 0.7 : 0)
            .help("Close Tab (⌘W)")
            .accessibilityLabel("Close Tab")
        }
        .padding(.horizontal, 10)
        .frame(width: 190, height: 28)
        .background(
            RoundedRectangle(cornerRadius: 7)
                .fill(isSelected ? Color.primary.opacity(0.12) : (isHovering ? Color.primary.opacity(0.06) : .clear))
        )
        .contentShape(RoundedRectangle(cornerRadius: 7))
        .onTapGesture { model.select(tab) }
        .onHover { hovering.wrappedValue = $0 }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(tab.displayTitle), \(tab.engine.displayName)")
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
    }
}

// MARK: - Toolbar

struct ToolbarView: View {
    @EnvironmentObject private var model: BrowserModel
    @FocusState private var addressFocused: Bool

    var body: some View {
        HStack(spacing: 8) {
            navigationButtons
            addressField
            if let id = model.webStoreExtensionID {
                AddExtensionButton(chromeID: id)
            }
            searchEngineMenu
            enginePicker
            BlockerButton()
            Button {
                model.showExtensions = true
            } label: {
                Image(systemName: "puzzlepiece.extension")
            }
            .help("Extensions")
            .accessibilityLabel("Extensions")
        }
        .padding(.horizontal, 10)
        .onChange(of: model.addressFocusRequest) { _, _ in addressFocused = true }
        .onChange(of: addressFocused) { _, focused in
            model.isEditingAddress = focused
            if !focused, let tab = model.selectedTab { model.addressText = tab.url }
        }
    }

    private var navigationButtons: some View {
        let tab = model.selectedTab
        return HStack(spacing: 2) {
            ToolbarIcon(symbol: "chevron.left", label: "Back (⌘[)", enabled: tab?.canGoBack ?? false) {
                model.goBack()
            }
            ToolbarIcon(symbol: "chevron.right", label: "Forward (⌘])", enabled: tab?.canGoForward ?? false) {
                model.goForward()
            }
            if tab?.isLoading == true {
                ToolbarIcon(symbol: "xmark", label: "Stop", enabled: true) { model.stopLoading() }
            } else {
                ToolbarIcon(symbol: "arrow.clockwise", label: "Reload (⌘R)", enabled: !(tab?.isStartPage ?? true)) {
                    model.reload()
                }
            }
        }
    }

    private var addressField: some View {
        HStack(spacing: 6) {
            Image(systemName: addressIcon)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
            TextField("Search \(model.searchEngine.name) or enter address", text: $model.addressText)
                .textFieldStyle(.plain)
                .font(.system(size: 13))
                .focused($addressFocused)
                .onSubmit {
                    let input = model.addressText
                    addressFocused = false
                    model.navigate(input)
                }
                .accessibilityLabel("Address and search")
        }
        .padding(.horizontal, 10)
        .frame(height: 28)
        .background(
            RoundedRectangle(cornerRadius: 8)
                .fill(Color(nsColor: .controlBackgroundColor))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 8)
                .strokeBorder(addressFocused ? Color.accentColor : Color.primary.opacity(0.12),
                              lineWidth: addressFocused ? 2 : 1)
        )
    }

    private var addressIcon: String {
        let text = model.addressText
        if text.isEmpty || !AddressResolver.isAddress(text) { return "magnifyingglass" }
        return text.hasPrefix("https://") ? "lock.fill" : "globe"
    }

    private var searchEngineMenu: some View {
        Menu {
            Picker("Search Engine", selection: $model.searchEngine) {
                ForEach(SearchEngine.builtIn) { engine in
                    Text(engine.name).tag(engine)
                }
            }
            .pickerStyle(.inline)
        } label: {
            Label(model.searchEngine.name, systemImage: "magnifyingglass")
                .labelStyle(.titleAndIcon)
        }
        .fixedSize()
        .help("Search engine (⌘⇧S cycles)")
    }

    private var enginePicker: some View {
        Picker("Engine", selection: Binding(
            get: { model.selectedTab?.engine ?? model.engine },
            set: { model.setEngine($0) }
        )) {
            ForEach(EngineKind.allCases) { kind in
                Text(kind.displayName).tag(kind)
            }
        }
        .pickerStyle(.segmented)
        .labelsHidden()
        .fixedSize()
        .help("Rendering engine for this tab (⌘⇧E switches)")
    }
}

private struct ToolbarIcon: View {
    let symbol: String
    let label: String
    let enabled: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 13, weight: .medium))
                .frame(width: 28, height: 28)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.35)
        .help(label)
        .accessibilityLabel(label)
    }
}

/// Appears while a Chrome Web Store listing is open.
private struct AddExtensionButton: View {
    @EnvironmentObject private var model: BrowserModel
    let chromeID: String

    var body: some View {
        let installed = model.extensions.isInstalled(chromeID: chromeID)
        Button {
            Task {
                await model.extensions.install(fromWebStore: chromeID)
                if let error = model.extensions.errorMessage {
                    model.notice = error
                } else {
                    model.notice = "Extension added to Mosaic."
                }
            }
        } label: {
            if model.extensions.isBusy {
                ProgressView().controlSize(.small)
            } else {
                Label(installed ? "Reinstall" : "Add to Mosaic", systemImage: "plus.circle.fill")
            }
        }
        .buttonStyle(.borderedProminent)
        .controlSize(.small)
        .disabled(model.extensions.isBusy)
        .help("Convert this Chrome extension and run it on the Mozilla engine")
    }
}
