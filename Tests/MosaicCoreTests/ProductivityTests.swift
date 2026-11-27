import Foundation
import Testing
@testable import MosaicCore

// MARK: - Command palette matching and ranking

@Suite struct PaletteMatcherTests {
    private func score(_ query: String, _ title: String, _ keywords: [String] = []) -> Double? {
        PaletteMatcher.score(query: query, title: title, keywords: keywords)
    }

    @Test func gradesFromExactToFuzzy() throws {
        let exact = try #require(score("new tab", "New Tab"))
        let prefix = try #require(score("new", "New Tab"))
        let wordPrefix = try #require(score("tab", "New Tab"))
        let initials = try #require(score("nt", "New Tab"))
        let substring = try #require(score("ew", "New Tab"))
        let fuzzy = try #require(score("nwtb", "New Tab"))
        #expect(exact > prefix)
        #expect(prefix > wordPrefix)
        #expect(wordPrefix > initials)
        #expect(initials > substring)
        #expect(substring > fuzzy)
    }

    @Test func noMatchIsNil() {
        #expect(score("xyz", "New Tab") == nil)
        #expect(score("tabs", "New Tab") == nil)  // more characters than the text has in order
    }

    @Test func everyWordMustMatch() {
        #expect(score("close tab", "Close Tab") != nil)
        #expect(score("close window", "Close Tab") == nil)
        #expect(score("tab close", "Close Tab") != nil)  // order of words does not matter
    }

    @Test func emptyQueryMatchesWithZero() {
        #expect(score("", "Anything") == 0)
        #expect(score("   ", "Anything") == 0)
    }

    @Test func caseAndDiacriticsAreIgnored() {
        #expect(score("ECOSIA", "Écosia") != nil)
        #expect(score("cafe", "Café Menu") != nil)
    }

    @Test func keywordsMatchButCountForLessThanTheTitle() throws {
        let viaTitle = try #require(score("settings", "Settings"))
        let viaKeyword = try #require(score("settings", "Preferences", ["settings"]))
        #expect(viaTitle > viaKeyword)
        #expect(score("prefs", "Settings", ["preferences"]) != nil)
    }

    @Test func shorterTitlesWinTies() throws {
        let short = try #require(score("tab", "Tab"))
        let long = try #require(score("tab", "Tab Groups and Workspaces"))
        #expect(short > long)
    }

    @Test func wholePhraseAtStartIsPreferred() throws {
        let phrase = try #require(score("show reader", "Show Reader View"))
        let scattered = try #require(score("show reader", "Reader: Show Larger Text"))
        #expect(phrase > scattered)
    }
}

@Suite struct PaletteRankerTests {
    let candidates = [
        PaletteCandidate(id: "tab.new", title: "New Tab"),
        PaletteCandidate(id: "tab.close", title: "Close Tab"),
        PaletteCandidate(id: "reader", title: "Show Reader View", keywords: ["article"]),
        PaletteCandidate(id: "settings.tabs", title: "Tabs & Workspaces Settings", keywords: ["settings"]),
    ]

    @Test func ranksBestMatchFirstAndDropsNonMatches() {
        let ids = PaletteRanker.rank(candidates, query: "tab").map(\.id)
        #expect(ids.first == "settings.tabs" || ids.first == "tab.new" || ids.first == "tab.close")
        #expect(!ids.contains("reader"))
        #expect(PaletteRanker.rank(candidates, query: "article").map(\.id) == ["reader"])
    }

    @Test func prefixBeatsWordPrefix() {
        let ids = PaletteRanker.rank(candidates, query: "clo").map(\.id)
        #expect(ids == ["tab.close"])
        let newFirst = PaletteRanker.rank(candidates, query: "new").map(\.id)
        #expect(newFirst.first == "tab.new")
    }

    @Test func recentChoicesRankHigher() {
        let plain = PaletteRanker.rank(candidates, query: "tab").map(\.id)
        let recent = PaletteRanker.rank(candidates, query: "tab", recent: ["tab.close"]).map(\.id)
        #expect(recent.first == "tab.close")
        #expect(Set(plain) == Set(recent))
    }

    @Test func boostBreaksTies() {
        let items = [PaletteCandidate(id: "a", title: "Example"), PaletteCandidate(id: "b", title: "Example", boost: 5)]
        #expect(PaletteRanker.rank(items, query: "exa").map(\.id) == ["b", "a"])
    }

    @Test func emptyQueryListsEverythingRecentFirst() {
        let ids = PaletteRanker.rank(candidates, query: "", recent: ["reader"]).map(\.id)
        #expect(ids.count == candidates.count)
        #expect(ids.first == "reader")
        #expect(Array(ids.dropFirst()) == ["tab.new", "tab.close", "settings.tabs"])  // given order
    }

    @Test func respectsLimit() {
        #expect(PaletteRanker.rank(candidates, query: "", limit: 2).count == 2)
    }

    @Test func recentsDeduplicateAndCap() {
        var recents = PaletteRecents(capacity: 3)
        for id in ["a", "b", "c", "a", "d"] { recents.note(id) }
        #expect(recents.ids == ["d", "a", "c"])
    }
}

// MARK: - Keyboard shortcuts

@Suite struct KeyComboTests {
    @Test func uppercaseLetterMeansShift() {
        let combo = KeyCombo(key: "K", modifiers: .command)
        #expect(combo == KeyCombo(key: "k", modifiers: [.command, .shift]))
        #expect(combo.displayString == "⇧⌘K")
    }

    @Test func shiftedSymbolsDropTheImpliedShift() {
        #expect(KeyCombo(key: "}", modifiers: [.command, .shift]) == KeyCombo(key: "}", modifiers: .command))
        #expect(KeyCombo(key: "}", modifiers: [.command, .shift]).displayString == "⌘}")
        // "]" does not imply Shift, so ⇧⌘] stays distinct from ⌘].
        #expect(KeyCombo(key: "]", modifiers: [.command, .shift]) != KeyCombo(key: "]", modifiers: .command))
    }

    @Test func displayUsesMenuOrderAndSymbols() {
        let combo = KeyCombo(key: KeyCombo.SpecialKey.left, modifiers: [.command, .option, .control, .shift])
        #expect(combo.displayString == "⌃⌥⇧⌘←")
        #expect(KeyCombo(key: KeyCombo.SpecialKey.function(5), modifiers: []).displayString == "F5")
        #expect(KeyCombo(key: KeyCombo.SpecialKey.returnKey, modifiers: .command).displayString == "⌘↩")
        #expect(KeyCombo(key: "k", modifiers: [.command, .shift]).spokenString == "Shift Command K")
    }

    @Test func deleteCharacterBecomesTheMenuKeyEquivalent() {
        #expect(KeyCombo(key: "\u{7f}", modifiers: .command).key == KeyCombo.SpecialKey.delete)
    }

    @Test func textFormRoundTrips() throws {
        for text in ["cmd+k", "shift+cmd+r", "ctrl+opt+f12", "cmd+up", "cmd+}", "ctrl+space", "cmd+delete", "cmd++"] {
            let combo = try #require(KeyCombo(string: text), "\(text)")
            #expect(KeyCombo(string: combo.stringValue) == combo, "\(text)")
        }
        #expect(KeyCombo(string: "cmd+shift+k")?.displayString == "⇧⌘K")
        #expect(KeyCombo(string: "cmd++")?.key == "+")
        #expect(KeyCombo(string: "hyper+k") == nil)
        #expect(KeyCombo(string: "cmd+banana") == nil)
        #expect(KeyCombo(string: "") == nil)
    }

    @Test func codableAsText() throws {
        let combo = KeyCombo(key: "r", modifiers: [.command, .shift])
        let data = try JSONEncoder().encode([combo])
        #expect(String(data: data, encoding: .utf8) == #"["shift+cmd+r"]"#)
        #expect(try JSONDecoder().decode([KeyCombo].self, from: data) == [combo])
    }

    @Test func requiredModifier() {
        #expect(KeyCombo(key: "k", modifiers: .command).hasRequiredModifier)
        #expect(KeyCombo(key: "k", modifiers: .control).hasRequiredModifier)
        #expect(!KeyCombo(key: "k", modifiers: .option).hasRequiredModifier)
        #expect(!KeyCombo(key: "k", modifiers: .shift).hasRequiredModifier)
        #expect(KeyCombo(key: KeyCombo.SpecialKey.function(3), modifiers: []).hasRequiredModifier)
    }

    @Test func domKeys() {
        #expect(KeyCombo(key: "k", modifiers: .command).domKey == "k")
        #expect(KeyCombo(key: KeyCombo.SpecialKey.up, modifiers: .command).domKey == "ArrowUp")
        #expect(KeyCombo(key: KeyCombo.SpecialKey.function(2), modifiers: []).domKey == "F2")
    }
}

@Suite struct ShortcutBindingsTests {
    let newTab = ShortcutCommand(id: "File/New Tab", title: "New Tab", group: "File", defaultCombo: KeyCombo(string: "cmd+t"))
    let palette = ShortcutCommand(id: "Tools/Command Palette…", title: "Command Palette…", group: "Tools", defaultCombo: KeyCombo(string: "cmd+k"))
    let reader = ShortcutCommand(id: "View/Reader View", title: "Reader View", group: "View", defaultCombo: KeyCombo(string: "shift+cmd+r"))
    let about = ShortcutCommand(id: "Mosaic/About Mosaic", title: "About Mosaic", group: "Mosaic", defaultCombo: nil)
    var all: [ShortcutCommand] { [newTab, palette, reader, about] }

    @Test func defaultsApplyUntilOverridden() {
        var bindings = ShortcutBindings()
        #expect(bindings.combo(for: newTab) == KeyCombo(string: "cmd+t"))
        bindings.set(KeyCombo(string: "cmd+opt+t"), for: newTab)
        #expect(bindings.combo(for: newTab) == KeyCombo(string: "cmd+opt+t"))
        #expect(bindings.isCustomised(newTab))
    }

    @Test func settingTheDefaultRemovesTheOverride() {
        var bindings = ShortcutBindings()
        bindings.set(KeyCombo(string: "cmd+opt+t"), for: newTab)
        bindings.set(KeyCombo(string: "cmd+t"), for: newTab)
        #expect(bindings.overrides.isEmpty)
    }

    @Test func clearingStoresNoShortcut() {
        var bindings = ShortcutBindings()
        bindings.set(nil, for: reader)
        #expect(bindings.combo(for: reader) == nil)
        #expect(bindings.isCustomised(reader))
        bindings.reset(reader)
        #expect(bindings.combo(for: reader) == KeyCombo(string: "shift+cmd+r"))
    }

    @Test func detectsConflicts() throws {
        let bindings = ShortcutBindings()
        let clash = try #require(KeyCombo(string: "cmd+t"))
        #expect(bindings.conflicts(for: clash, assigningTo: palette.id, among: all).map(\.id) == [newTab.id])
        #expect(bindings.problem(with: clash, assigningTo: palette.id, among: all) == .conflict([newTab.id]))
        // A command does not conflict with itself.
        #expect(bindings.problem(with: clash, assigningTo: newTab.id, among: all) == nil)
        #expect(bindings.problem(with: try #require(KeyCombo(string: "cmd+j")), assigningTo: palette.id, among: all) == nil)
    }

    @Test func conflictsFollowOverrides() throws {
        var bindings = ShortcutBindings()
        bindings.set(KeyCombo(string: "cmd+j"), for: newTab)
        // ⌘T is free once New Tab has moved; ⌘J is now taken.
        #expect(bindings.problem(with: try #require(KeyCombo(string: "cmd+t")), assigningTo: palette.id, among: all) == nil)
        #expect(bindings.problem(with: try #require(KeyCombo(string: "cmd+j")), assigningTo: palette.id, among: all) == .conflict([newTab.id]))
    }

    @Test func rejectsShortcutsWithoutModifierAndReservedOnes() throws {
        let bindings = ShortcutBindings()
        #expect(bindings.problem(with: KeyCombo(key: "k", modifiers: .option), assigningTo: palette.id, among: all) == .needsModifier)
        #expect(bindings.problem(with: try #require(KeyCombo(string: "cmd+space")), assigningTo: palette.id, among: all) == .reserved("Spotlight"))
        #expect(bindings.problem(with: try #require(KeyCombo(string: "shift+cmd+4")), assigningTo: palette.id, among: all) == .reserved("screenshots"))
    }

    @Test func assignTakesTheShortcutFromOthers() throws {
        var bindings = ShortcutBindings()
        bindings.assign(try #require(KeyCombo(string: "cmd+t")), to: palette, among: all)
        #expect(bindings.combo(for: palette) == KeyCombo(string: "cmd+t"))
        #expect(bindings.combo(for: newTab) == nil)
        bindings.resetAll()
        #expect(bindings.overrides.isEmpty)
    }

    @Test func storePersistsAndClears() throws {
        let suite = "dev.mosaic.tests.shortcuts.\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = ShortcutStore(defaults: defaults)
        #expect(store.load() == ShortcutBindings())

        var bindings = ShortcutBindings()
        bindings.set(KeyCombo(string: "ctrl+opt+k"), for: palette)
        bindings.set(nil, for: reader)
        store.save(bindings)
        let loaded = ShortcutStore(defaults: defaults).load()
        #expect(loaded == bindings)
        #expect(loaded.combo(for: palette) == KeyCombo(string: "ctrl+opt+k"))
        #expect(loaded.combo(for: reader) == nil)

        store.save(ShortcutBindings())
        #expect(defaults.data(forKey: ShortcutStore.defaultsKey) == nil)
    }

    @Test func corruptStoreFallsBackToDefaults() throws {
        let suite = "dev.mosaic.tests.shortcuts.\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(Data("not json".utf8), forKey: ShortcutStore.defaultsKey)
        #expect(ShortcutStore(defaults: defaults).load() == ShortcutBindings())
    }
}

// MARK: - Speed Dial

@Suite struct SpeedDialTests {
    @Test func normalisesAddresses() {
        #expect(SpeedDial.normalizedURL("example.com") == "https://example.com/")
        #expect(SpeedDial.normalizedURL("  HTTP://Example.COM/path?q=1 ") == "http://example.com/path?q=1")
        #expect(SpeedDial.normalizedURL("localhost:8000") == "https://localhost:8000/")
        #expect(SpeedDial.normalizedURL("javascript:alert(1)") == nil)
        #expect(SpeedDial.normalizedURL("file:///etc/passwd") == nil)
        #expect(SpeedDial.normalizedURL("not an address") == nil)
        #expect(SpeedDial.normalizedURL("") == nil)
        #expect(SpeedDial.normalizedURL("word") == nil)
    }

    @Test func identityIgnoresSchemeWwwAndTrailingSlash() {
        #expect(SpeedDial.identity(of: "https://www.example.com/") == SpeedDial.identity(of: "http://example.com"))
        #expect(SpeedDial.identity(of: "https://example.com/a/") == "example.com/a")
        #expect(SpeedDial.identity(of: "https://example.com/?q=1") != SpeedDial.identity(of: "https://example.com/"))
    }

    @Test func addEditRemove() throws {
        var dial = SpeedDial()
        let site = try dial.add(title: " Example ", url: "example.com")
        #expect(site.title == "Example")
        #expect(dial.pinned.map(\.url) == ["https://example.com/"])
        #expect(throws: SpeedDial.EditError.duplicate) { try dial.add(title: "", url: "https://www.example.com") }
        #expect(throws: SpeedDial.EditError.invalidAddress) { try dial.add(title: "", url: "nope") }

        try dial.update(site.id, title: "", url: "example.org/news")
        #expect(dial.pinned[0].url == "https://example.org/news")
        #expect(dial.pinned[0].displayTitle == "example.org")
        #expect(throws: SpeedDial.EditError.invalidAddress) { try dial.update(site.id, title: "", url: "ftp://x.y") }

        dial.remove(site.id)
        #expect(dial.pinned.isEmpty)
    }

    @Test func updateRefusesDuplicatesOfOtherSites() throws {
        var dial = SpeedDial()
        let a = try dial.add(title: "A", url: "a.com")
        _ = try dial.add(title: "B", url: "b.com")
        #expect(throws: SpeedDial.EditError.duplicate) { try dial.update(a.id, title: "A", url: "b.com") }
        try dial.update(a.id, title: "A2", url: "a.com")  // its own address is fine
        #expect(dial.pinned[0].title == "A2")
    }

    @Test func reorders() throws {
        var dial = SpeedDial()
        let a = try dial.add(title: "A", url: "a.com")
        let b = try dial.add(title: "B", url: "b.com")
        let c = try dial.add(title: "C", url: "c.com")
        dial.move(c.id, to: 0)
        #expect(dial.pinned.map(\.title) == ["C", "A", "B"])
        dial.shift(c.id, by: 1)
        #expect(dial.pinned.map(\.title) == ["A", "C", "B"])
        dial.shift(b.id, by: 5)  // clamped
        #expect(dial.pinned.map(\.title) == ["A", "C", "B"])
        dial.shift(a.id, by: -1)  // already first
        #expect(dial.pinned.map(\.title) == ["A", "C", "B"])
        _ = try dial.add(title: "D", url: "d.com", at: 1)
        #expect(dial.pinned.map(\.title) == ["A", "D", "C", "B"])
    }

    @Test func topSitesSkipPinnedHiddenDuplicatesAndNonWeb() throws {
        var dial = SpeedDial()
        _ = try dial.add(title: "Pinned", url: "pinned.com")
        dial.hideTopSite(url: "https://hidden.com/")
        let candidates: [(title: String, url: String)] = [
            ("Pinned again", "https://www.pinned.com/"),
            ("Hidden", "http://hidden.com"),
            ("One", "https://one.com/"),
            ("One again", "https://one.com"),
            ("Local file", "file:///tmp/x.html"),
            ("Two", "https://two.com/"),
            ("Three", "https://three.com/"),
        ]
        let top = dial.visibleTopSites(candidates, limit: 2)
        #expect(top.map(\.title) == ["One", "Two"])
        // Stable identities across calls, so tiles do not jump.
        #expect(dial.visibleTopSites(candidates, limit: 2).map(\.id) == top.map(\.id))

        dial.showsTopSites = false
        #expect(dial.visibleTopSites(candidates, limit: 5).isEmpty)
        dial.showsTopSites = true
        dial.restoreHiddenTopSites()
        #expect(dial.visibleTopSites(candidates, limit: 5).map(\.title).contains("Hidden"))
    }

    @Test func pinningAHiddenSiteUnhidesIt() throws {
        var dial = SpeedDial()
        dial.hideTopSite(url: "https://site.com")
        _ = try dial.add(title: "", url: "site.com")
        #expect(dial.hiddenTopSites.isEmpty)
    }

    @Test func storeRoundTripsAndToleratesMissingFile() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("mosaic-speeddial-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: folder) }
        let store = SpeedDialStore(fileURL: folder.appendingPathComponent("Productivity/speed-dial.json"))
        #expect(store.load() == SpeedDial())

        var dial = SpeedDial()
        _ = try dial.add(title: "A", url: "a.com")
        _ = try dial.add(title: "B", url: "b.com")
        dial.hideTopSite(url: "https://c.com")
        dial.showsIcons = false
        try store.save(dial)
        #expect(SpeedDialStore(fileURL: store.fileURL).load() == dial)
    }

    @Test func readsFilesMissingNewerKeys() throws {
        let json = #"{"pinned":[{"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","title":"A","url":"https://a.com/"}]}"#
        let dial = try JSONDecoder().decode(SpeedDial.self, from: Data(json.utf8))
        #expect(dial.pinned.count == 1)
        #expect(dial.showsTopSites && dial.showsIcons)
        #expect(dial.hiddenTopSites.isEmpty)
    }
}

// MARK: - Reader settings

@Suite struct ReaderSettingsTests {
    @Test func defaultsMatchFirefox() {
        let settings = ReaderSettings()
        #expect(settings.fontSizePixels == 20)
        #expect(settings.contentWidthEm == 30)
        #expect(abs(settings.lineHeightEm - 1.6) < 0.0001)
        #expect(settings.font == .sans)
    }

    @Test func clampsSteps() {
        let settings = ReaderSettings(fontSize: 40, contentWidth: 0, lineHeight: -3)
        #expect(settings.fontSize == 9 && settings.contentWidth == 1 && settings.lineHeight == 1)
        var edited = ReaderSettings()
        edited.fontSize = 12
        #expect(edited.clamped.fontSize == 9)
    }

    @Test func geckoPreferencesRoundTrip() {
        let settings = ReaderSettings(font: .serif, fontSize: 7, contentWidth: 5, lineHeight: 6, theme: .sepia)
        let prefs = settings.geckoPreferences
        #expect(prefs["reader.font_type"] as? String == "serif")
        #expect(prefs["reader.color_scheme"] as? String == "sepia")
        #expect(ReaderSettings().merging(geckoPreferences: prefs) == settings)
    }

    @Test func mergingIgnoresValuesMosaicDoesNotOffer() {
        let base = ReaderSettings(theme: .dark)
        let merged = base.merging(geckoPreferences: [
            "reader.color_scheme": "auto", "reader.font_type": "monospace", "reader.font_size": 14,
        ])
        #expect(merged.theme == .dark)
        #expect(merged.font == .sans)
        #expect(merged.fontSize == 9)
    }

    @Test func decodesPartialAndInvalidStoredValues() throws {
        let json = #"{"font":"serif","fontSize":99,"theme":"neon"}"#
        let settings = try JSONDecoder().decode(ReaderSettings.self, from: Data(json.utf8))
        #expect(settings.font == .serif)
        #expect(settings.fontSize == 9)
        #expect(settings.theme == .light)
        #expect(settings.lineHeight == 4)
        let encoded = try JSONEncoder().encode(settings)
        #expect(try JSONDecoder().decode(ReaderSettings.self, from: encoded) == settings)
    }
}

@Suite struct PaletteWholeWordTests {
    @Test func wholeWordBeatsPrefixOfALongerWord() throws {
        let word = try #require(PaletteMatcher.score(query: "tab", title: "New Tab"))
        let prefix = try #require(PaletteMatcher.score(query: "tab", title: "Tabs & Workspaces Settings"))
        let leading = try #require(PaletteMatcher.score(query: "tab", title: "Tab Groups"))
        #expect(leading > word)
        #expect(word > prefix)
    }
}
