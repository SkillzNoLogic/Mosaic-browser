import Foundation
import JavaScriptCore
import Testing
@testable import MosaicCore

// Passwords (area: passwords). Nothing here touches the Keychain: stores are in memory.

/// Deterministic generator for reproducible tests.
private struct SplitMix64: RandomNumberGenerator {
    var state: UInt64
    mutating func next() -> UInt64 {
        state &+= 0x9E37_79B9_7F4A_7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        return z ^ (z >> 31)
    }
}

private func origin(_ text: String) -> SiteOrigin { SiteOrigin(url: text)! }

@Suite struct SiteOriginTests {
    @Test func parsesAndNormalises() {
        let o = origin("HTTPS://Example.COM:443/login?x=1#y")
        #expect(o.string == "https://example.com")
        #expect(o.port == nil)
        #expect(origin("http://127.0.0.1:8766/").string == "http://127.0.0.1:8766")
        #expect(origin("http://example.com:80").string == "http://example.com")
        #expect(origin("https://[::1]:8443/").string == "https://[::1]:8443")
    }

    @Test func exactMatchOnly() {
        let saved = origin("https://example.com")
        #expect(saved == origin("https://example.com/other/page"))
        #expect(saved != origin("http://example.com"))
        #expect(saved != origin("https://www.example.com"))
        #expect(saved != origin("https://example.com:8443"))
        #expect(saved != origin("https://sub.example.com"))
        #expect(saved != origin("https://example.com.evil.test"))
    }

    @Test func rejectsNonWebAddresses() {
        #expect(SiteOrigin(url: "file:///etc/passwd") == nil)
        #expect(SiteOrigin(url: "about:blank") == nil)
        #expect(SiteOrigin(url: "javascript:alert(1)") == nil)
        #expect(SiteOrigin(url: "android://hash@com.example/") == nil)
        #expect(SiteOrigin(url: "mosaic://history") == nil)
        #expect(SiteOrigin(url: "https://") == nil)
        #expect(SiteOrigin(url: "http://example.com:99999") == nil)
    }

    @Test func userInputDefaultsToHTTPS() {
        #expect(SiteOrigin(userInput: "example.com")?.string == "https://example.com")
        #expect(SiteOrigin(userInput: " example.com/login ")?.string == "https://example.com")
        #expect(SiteOrigin(userInput: "localhost:8080")?.string == "https://localhost:8080")
        #expect(SiteOrigin(userInput: "http://intranet.test")?.string == "http://intranet.test")
        #expect(SiteOrigin(userInput: "") == nil)
    }

    @Test func displayNameFlagsPlainHTTP() {
        #expect(origin("https://example.com").displayName == "example.com")
        #expect(origin("https://example.com:8443").displayName == "example.com:8443")
        #expect(origin("http://example.com").displayName == "http://example.com")
    }

    @Test func codableAsString() throws {
        let data = try JSONEncoder().encode([origin("https://a.test:444")])
        #expect(String(data: data, encoding: .utf8) == #"["https:\/\/a.test:444"]"#)
        #expect(try JSONDecoder().decode([SiteOrigin].self, from: data) == [origin("https://a.test:444")])
    }
}

@Suite struct PasswordGeneratorTests {
    @Test func defaultIsStrongAndUsesEverySet() {
        var random = SplitMix64(state: 1)
        for _ in 0..<200 {
            let password = PasswordGenerator.generate(.default, using: &random)
            #expect(password.count == 20)
            #expect(password.contains(where: { $0.isLowercase }))
            #expect(password.contains(where: { $0.isUppercase }))
            #expect(password.contains(where: { $0.isNumber }))
            #expect(password.contains(where: { "-_.!@#$%&*+=?".contains($0) }))
            #expect(!password.contains(where: { "l1IO0".contains($0) }))
            #expect(PasswordStrength.evaluate(password) == .strong)
        }
        #expect(PasswordGeneratorOptions.default.entropyBits > 110)
    }

    @Test func respectsOptions() {
        var random = SplitMix64(state: 2)
        let digitsOnly = PasswordGeneratorOptions(length: 12, lowercase: false, uppercase: false, digits: true,
                                                  symbols: false, avoidAmbiguous: false)
        let pin = PasswordGenerator.generate(digitsOnly, using: &random)
        #expect(pin.count == 12 && pin.allSatisfy({ $0.isNumber }))
        let none = PasswordGeneratorOptions(length: 10, lowercase: false, uppercase: false, digits: false, symbols: false)
        #expect(PasswordGenerator.generate(none, using: &random).allSatisfy({ $0.isLowercase }))
    }

    @Test func clampsLength() {
        var random = SplitMix64(state: 3)
        #expect(PasswordGenerator.generate(PasswordGeneratorOptions(length: 2), using: &random).count == 8)
        #expect(PasswordGenerator.generate(PasswordGeneratorOptions(length: 1000), using: &random).count == 128)
    }

    @Test func systemGeneratorDoesNotRepeat() {
        let passwords = (0..<100).map { _ in PasswordGenerator.generate() }
        #expect(Set(passwords).count == 100)
    }
}

@Suite struct PasswordStrengthTests {
    @Test func classifies() {
        #expect(PasswordStrength.evaluate("password") == .weak)
        #expect(PasswordStrength.evaluate("Password1!") == .weak)
        #expect(PasswordStrength.evaluate("12345678") == .weak)
        #expect(PasswordStrength.evaluate("aaaaaaaaaaaa") == .weak)
        #expect(PasswordStrength.evaluate("abcdefgh") == .weak)
        #expect(PasswordStrength.evaluate("short") == .weak)
        #expect(PasswordStrength.evaluate("Kp7#qz2!Lm") >= .fair)
        #expect(PasswordStrength.evaluate("q3+fsU7i-WBTZbM=CF46") == .strong)
    }

    @Test func auditFindsWeakAndReused() {
        let a = CredentialID(origin: origin("https://a.test"), username: "u")
        let b = CredentialID(origin: origin("https://b.test"), username: "u")
        let c = CredentialID(origin: origin("https://c.test"), username: "u")
        let audit = PasswordAudit.run([a: "q3+fsU7i-WBTZbM=CF46", b: "q3+fsU7i-WBTZbM=CF46", c: "letmein"])
        #expect(audit.reused == [a, b])
        #expect(audit.weak == [c])
    }
}

@Suite struct PasswordCSVTests {
    @Test func parsesRFC4180() {
        let rows = PasswordCSV.parse("\u{FEFF}a,b,c\r\n\"x,1\",\"he said \"\"hi\"\"\",\"line\nbreak\"\n\n,,\n")
        #expect(rows == [["a", "b", "c"], ["x,1", "he said \"hi\"", "line\nbreak"], ["", "", ""]])
    }

    @Test func importsChrome() throws {
        let text = "name,url,username,password,note\nexample.com,https://example.com/login,alice,s3cret,\nbad,android://x@com.app/,bob,pw,\n"
        let result = try PasswordCSV.parseExport(text)
        #expect(result.entries == [.init(origin: origin("https://example.com"), username: "alice", password: "s3cret")])
        #expect(result.skipped.map(\.line) == [3])
    }

    @Test func importsFirefoxSkippingHTTPAuth() throws {
        let text = """
            "url","username","password","httpRealm","formActionOrigin","guid","timeCreated","timeLastUsed","timePasswordChanged"
            "https://example.com","alice","pw1","","https://example.com","{a}","1","1","1"
            "https://example.com","bob","pw2","Admin area","","{b}","1","1","1"
            "https://example.com","carol","","","","{c}","1","1","1"
            """
        let result = try PasswordCSV.parseExport(text)
        #expect(result.entries.map(\.username) == ["alice"])
        #expect(result.skipped.count == 2)
    }

    @Test func importsSafari() throws {
        let text = "Title,URL,Username,Password,Notes,OTPAuth\nExample,https://example.com:8443/,alice,\"a,\"\"b\",,\n"
        let result = try PasswordCSV.parseExport(text)
        #expect(result.entries == [.init(origin: origin("https://example.com:8443"), username: "alice", password: "a,\"b")])
    }

    @Test func rejectsFilesWithoutColumns() {
        #expect(throws: PasswordCSV.ImportError.missingColumns) { try PasswordCSV.parseExport("a,b\n1,2\n") }
        #expect(throws: PasswordCSV.ImportError.empty) { try PasswordCSV.parseExport("") }
    }

    @Test func skipsDuplicates() throws {
        let text = "url,username,password\nhttps://a.test,u,1\nhttps://a.test/x,u,2\n"
        let result = try PasswordCSV.parseExport(text)
        #expect(result.entries.count == 1)
        #expect(result.skipped.first?.reason.contains("duplicate") == true)
    }

    @Test func exportRoundTrips() throws {
        let entries: [PasswordCSV.Entry] = [
            .init(origin: origin("https://b.test"), username: "bob", password: "with,comma \"and quotes\"\nnewline"),
            .init(origin: origin("http://a.test:8080"), username: "", password: " spaced "),
        ]
        let text = PasswordCSV.export(entries)
        #expect(text.hasPrefix("name,url,username,password\n"))
        let back = try PasswordCSV.parseExport(text)
        #expect(Set(back.entries.map(\.id)) == Set(entries.map(\.id)))
        #expect(back.entries.first { $0.username == "bob" }?.password == entries[0].password)
        #expect(back.entries.first { $0.username == "" }?.password == " spaced ")
    }
}

@Suite struct PasswordStoreTests {
    @Test func inMemoryStoreCRUD() throws {
        let store = InMemoryPasswordStore()
        let id = CredentialID(origin: origin("https://a.test"), username: "u")
        try store.add(id, password: "p1")
        #expect(throws: PasswordStoreError.duplicate) { try store.add(id, password: "p2") }
        #expect(try store.password(for: id) == "p1")
        try store.update(id, username: "v", password: "p2")
        let renamed = CredentialID(origin: id.origin, username: "v")
        #expect(throws: PasswordStoreError.notFound) { try store.password(for: id) }
        #expect(try store.password(for: renamed) == "p2")
        #expect(try store.credentials().map(\.id) == [renamed])
        try store.delete(renamed)
        #expect(try store.credentials().isEmpty)
        #expect(throws: PasswordStoreError.notFound) { try store.delete(renamed) }
    }

    @Test func keychainScopesAreSeparate() {
        // Only the scope values; the Keychain itself is never touched in tests.
        let test = KeychainPasswordStore.Scope.testProfile("abc")
        #expect(test.securityDomain != KeychainPasswordStore.Scope.standard.securityDomain)
        #expect(test.labelSuffix.contains("test"))
    }
}

@Suite struct PasswordVaultTests {
    private func makeVault(_ directory: URL? = nil) -> (PasswordVault, URL) {
        let folder = directory ?? FileManager.default.temporaryDirectory
            .appendingPathComponent("mosaic-passwords-tests-\(UUID().uuidString)")
        let file = folder.appendingPathComponent("settings.json")
        return (PasswordVault(store: InMemoryPasswordStore(), preferencesStore: PasswordPreferencesStore(fileURL: file)), folder)
    }

    private let site = SiteOrigin(url: "https://shop.test")!

    @Test func offersSaveThenNothingThenUpdate() throws {
        let (vault, folder) = makeVault()
        defer { try? FileManager.default.removeItem(at: folder) }
        let login = PasswordSubmission(origin: site, username: "alice", password: "pw1")
        #expect(vault.offer(for: login, isPrivate: false) == .save)
        #expect(try vault.accept(.save, for: login) == .added)
        #expect(vault.offer(for: login, isPrivate: false) == nil)
        let changed = PasswordSubmission(origin: site, username: "alice", password: "pw2")
        let id = CredentialID(origin: site, username: "alice")
        #expect(vault.offer(for: changed, isPrivate: false) == .update(id))
        #expect(try vault.accept(.update(id), for: changed) == .updated)
        #expect(try vault.password(for: id) == "pw2")
        #expect(vault.credentials.count == 1)
    }

    @Test func quietInPrivateTabsNeverSaveAndWhenOff() {
        let (vault, folder) = makeVault()
        defer { try? FileManager.default.removeItem(at: folder) }
        let login = PasswordSubmission(origin: site, username: "alice", password: "pw1")
        #expect(vault.offer(for: login, isPrivate: true) == nil)
        vault.setNeverSave(true, for: site)
        #expect(vault.offer(for: login, isPrivate: false) == nil)
        vault.setNeverSave(false, for: site)
        vault.update { $0.offerToSave = false }
        #expect(vault.offer(for: login, isPrivate: false) == nil)
        vault.update { $0.offerToSave = true }
        #expect(vault.offer(for: PasswordSubmission(origin: site, username: "a", password: ""), isPrivate: false) == nil)
    }

    @Test func changeFormWithoutUsernameUpdatesTheOnlyLogin() throws {
        let (vault, folder) = makeVault()
        defer { try? FileManager.default.removeItem(at: folder) }
        try vault.save(CredentialID(origin: site, username: "alice"), password: "old")
        let change = PasswordSubmission(origin: site, username: "", password: "new", mode: .change)
        #expect(vault.offer(for: change, isPrivate: false) == .update(CredentialID(origin: site, username: "alice")))
        let signInWithKnownPassword = PasswordSubmission(origin: site, username: "", password: "old")
        #expect(vault.offer(for: signInWithKnownPassword, isPrivate: false) == nil)
    }

    @Test func matchesAreExactOriginMostRecentFirst() throws {
        let (vault, folder) = makeVault()
        defer { try? FileManager.default.removeItem(at: folder) }
        try vault.save(CredentialID(origin: site, username: "a"), password: "1")
        try vault.save(CredentialID(origin: site, username: "b"), password: "2")
        try vault.save(CredentialID(origin: origin("http://shop.test"), username: "c"), password: "3")
        try vault.save(CredentialID(origin: origin("https://www.shop.test"), username: "d"), password: "4")
        vault.markUsed(CredentialID(origin: site, username: "a"), at: Date().addingTimeInterval(60))
        #expect(vault.matches(for: site).map(\.username) == ["a", "b"])
        #expect(vault.search("WWW").map(\.username) == ["d"])
    }

    @Test func preferencesPersistWithoutSecrets() throws {
        let (vault, folder) = makeVault()
        defer { try? FileManager.default.removeItem(at: folder) }
        try vault.save(CredentialID(origin: site, username: "alice"), password: "TopSecret-123")
        vault.setNeverSave(true, for: origin("https://news.test"))
        vault.update { $0.generator.length = 32 }
        let text = try String(contentsOf: folder.appendingPathComponent("settings.json"), encoding: .utf8)
        #expect(!text.contains("TopSecret"))
        let reloaded = PasswordPreferencesStore(fileURL: folder.appendingPathComponent("settings.json")).load()
        #expect(reloaded.neverSave == [origin("https://news.test")])
        #expect(reloaded.generator.length == 32)
        #expect(reloaded.lastUsed[CredentialID(origin: site, username: "alice").key] != nil)
    }

    @Test func importCountsAndDoesNotMarkUsed() throws {
        let (vault, folder) = makeVault()
        defer { try? FileManager.default.removeItem(at: folder) }
        try vault.save(CredentialID(origin: site, username: "alice"), password: "same")
        try vault.save(CredentialID(origin: site, username: "bob"), password: "old")
        let csv = "url,username,password\nhttps://shop.test,alice,same\nhttps://shop.test,bob,new\nhttps://shop.test,carol,pw\nnot a url,x,y\n"
        let summary = vault.importEntries(try PasswordCSV.parseExport(csv))
        #expect(summary.added == 1 && summary.updated == 1 && summary.unchanged == 1 && summary.skipped.count == 1)
        #expect(vault.lastUsed(CredentialID(origin: site, username: "carol")) == nil)
        #expect(try vault.exportEntries().count == 3)
    }
}

@Suite struct PasswordPageMessageTests {
    @Test func decodesAndNeverThrows() throws {
        let message = PasswordPageMessage.decode(json: #"{"kind":"submit","doc":"d1","username":"  alice ","password":"pw","mode":"signup","confirmed":true}"#)
        #expect(message?.kind == .submit)
        #expect(message?.mode == .signup)
        // Malformed fields are dropped, not fatal.
        let odd = try JSONDecoder().decode(PasswordPageMessage.self, from: Data(#"{"kind":7,"password":["x"],"origin":"javascript:x"}"#.utf8))
        #expect(odd.kind == nil && odd.password == nil && odd.origin == nil)
        #expect(PasswordPageMessage.decode(json: "[1,2]") == nil)
        #expect(PasswordPageMessage.decode(json: "not json") == nil)
    }

    @Test func submissionNeedsTrustedOriginAndFrame() {
        var message = PasswordPageMessage.decode(json: #"{"kind":"submit","username":" alice ","password":"pw"}"#)!
        #expect(message.submission == nil)  // no origin from the engine yet
        message.origin = origin("https://a.test")
        message.frame = .crossOrigin
        #expect(message.submission == nil)
        message.frame = .main
        #expect(message.submission == PasswordSubmission(origin: origin("https://a.test"), username: "alice", password: "pw"))
    }

    @Test func dropsOversizedPasswords() {
        let long = String(repeating: "x", count: 2000)
        #expect(PasswordPageMessage.decode(json: "{\"kind\":\"submit\",\"password\":\"\(long)\"}")?.password == nil)
    }

    @Test func geckoEventFieldNames() throws {
        let json = #"{"kind":"page","login":true,"signup":false,"origin":"http://127.0.0.1:8766","frame":"same","tab":3,"private":true}"#
        let message = try JSONDecoder().decode(PasswordPageMessage.self, from: Data(json.utf8))
        #expect(message.hasLogin == true && message.hasNewPassword == false)
        #expect(message.origin == origin("http://127.0.0.1:8766"))
        #expect(message.frame == .sameOrigin && message.tab == 3 && message.isPrivate == true)
    }

    @Test func scriptLiteralsAreSafe() {
        let literal = PasswordPageScripts.literal("a\"b\\c</script>\u{2028}'")
        #expect(literal == #""a\"b\\c\u003c\/script>\u2028'""#)
        let context = JSContext()!
        #expect(context.evaluateScript(literal).toString() == "a\"b\\c</script>\u{2028}'")
        #expect(PasswordPageScripts.fill(username: "u", password: "p\");alert(1);//").contains(#"p\");alert(1);\/\/"#))
    }
}

/// The pure heuristics of the page script (Resources/gecko/passwords-page.js), run in
/// JavaScriptCore.
@Suite struct PasswordFormHeuristicsTests {
    private static let context: JSContext = {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Resources/gecko/passwords-page.js")
        let context = JSContext()!
        context.evaluateScript(try! String(contentsOf: url, encoding: .utf8))
        return context
    }()

    private func classify(_ fields: String) -> [String: Any]? {
        let value = Self.context.evaluateScript("JSON.stringify(MosaicPasswordHeuristics.classify(\(fields)))")!
        guard let text = value.toString(), text != "null" else { return nil }
        return try? JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: Any]
    }

    private func kind(_ field: String) -> String {
        Self.context.evaluateScript("MosaicPasswordHeuristics.fieldKind(\(field))").toString()
    }

    @Test func scriptDefinesItsGlobals() {
        #expect(Self.context.evaluateScript("typeof createMosaicPasswordPage").toString() == "function")
        #expect(Self.context.exception == nil)
    }

    @Test func fieldKinds() {
        #expect(kind(#"{type:"password"}"#) == "password")
        #expect(kind(#"{type:"password",autocomplete:"new-password"}"#) == "new-password")
        #expect(kind(#"{type:"password",name:"confirm_password"}"#) == "new-password")
        #expect(kind(#"{type:"password",id:"oldPassword"}"#) == "current-password")
        #expect(kind(#"{type:"email"}"#) == "username")
        #expect(kind(#"{type:"text",name:"login"}"#) == "username")
        #expect(kind(#"{type:"text",autocomplete:"username"}"#) == "username")
        #expect(kind(#"{type:"text",name:"q",placeholder:"Search"}"#) == "other")
        #expect(kind(#"{type:"text",autocomplete:"one-time-code"}"#) == "other")
        #expect(kind(#"{type:"text",name:"nickname2"}"#) == "username")
        #expect(kind(#"{type:"text",name:"field1"}"#) == "text")
        #expect(kind(#"{type:"checkbox",name:"remember"}"#) == "other")
        #expect(kind(#"{type:"password",visible:false}"#) == "hidden")
    }

    @Test func loginForm() {
        let result = classify(#"[{type:"text",name:"q",placeholder:"search"},{type:"email",name:"email"},{type:"password",name:"pw"},{type:"checkbox"}]"#)
        #expect(result?["mode"] as? String == "login")
        #expect(result?["username"] as? Int == 1)
        #expect(result?["password"] as? Int == 2)
    }

    @Test func signupWithConfirmation() {
        let result = classify(#"[{type:"text",name:"first_name"},{type:"email",name:"email"},{type:"password",value:"Abc-123-xyz"},{type:"password",value:"Abc-123-xyz"}]"#)
        #expect(result?["mode"] as? String == "signup")
        #expect(result?["username"] as? Int == 1)
        #expect(result?["password"] as? Int == 2)
        #expect(result?["newFields"] as? [Int] == [2, 3])
    }

    @Test func changePasswordForm() {
        let result = classify(#"[{type:"text",autocomplete:"username",visible:false,value:"alice"},{type:"password",value:"old"},{type:"password",value:"new-1"},{type:"password",value:"new-1"}]"#)
        #expect(result?["mode"] as? String == "change")
        #expect(result?["current"] as? Int == 1)
        #expect(result?["password"] as? Int == 2)
        #expect(result?["username"] as? Int == 0)
        let twoDifferent = classify(#"[{type:"password",value:"old"},{type:"password",value:"new"}]"#)
        #expect(twoDifferent?["mode"] as? String == "change")
        #expect(twoDifferent?["password"] as? Int == 1)
    }

    @Test func noVisiblePasswordMeansNoLoginForm() {
        #expect(classify(#"[{type:"text",name:"user"},{type:"password",visible:false}]"#) == nil)
        #expect(classify(#"[{type:"search"}]"#) == nil)
    }

    @Test func identifierFirstStep() {
        let index = Self.context.evaluateScript(#"MosaicPasswordHeuristics.identifierField([{type:"email",name:"identifier"},{type:"hidden"}])"#)
        #expect(index?.toInt32() == 0)
        let newsletter = Self.context.evaluateScript(#"MosaicPasswordHeuristics.identifierField([{type:"text",name:"name"},{type:"email"}])"#)
        #expect(newsletter?.toInt32() == -1)
    }
}
