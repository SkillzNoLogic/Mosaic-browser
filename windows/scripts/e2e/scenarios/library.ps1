# library: bookmarks, history, reading list, search (windows/To Do.md "library"). Commands and
# dump fields of Sources/Mosaic/Features/Library/LibraryAutomation.swift; dump key "library".

function Get-Library {
    param([Parameter(Mandatory = $true)] $State)
    Require-Field $State 'library' -Owner 'library'
}

Register-Scenario -Area library -Name 'bookmark the current page in both engines' -Tags smoke -Todo 'Bookmarks (folders, tags, bar, manager, search); import/export Netscape HTML and Chrome JSON' -Body {
    Invoke-OnEngines {
        param($engine)
        $title = "Bookmark $engine"
        $null = Open-TestPage "/page?title=Bookmark%20$engine" -Engine $engine -Title $title
        Invoke-Mosaic 'libraryBookmark' | Out-Null
        $null = Get-Library (Get-MosaicState)
        $null = Wait-State 'selectedIsBookmarked' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'library.selectedIsBookmarked') -eq $true }
        $out = New-OutputPath "bookmarks-$engine.json"
        Invoke-Mosaic 'libraryBookmarks' $out | Out-Null
        $bookmarks = @(Wait-OutputFile $out -Json)
        Assert-True (@($bookmarks | Where-Object { (Get-Field $_ 'title') -eq $title -and (Get-Field $_ 'kind') -ne 'folder' }).Count -ge 1) "the bookmark '$title' is listed"
    }
}

Register-Scenario -Area library -Name 'export bookmarks as Netscape HTML' -Todo 'Bookmarks (folders, tags, bar, manager, search); import/export Netscape HTML and Chrome JSON' -Body {
    $null = Open-TestPage '/page?title=Export%20Me' -Engine chromium -Title 'Export Me'
    Invoke-Mosaic 'libraryBookmark' | Out-Null
    $out = New-OutputPath 'bookmarks-export.html'
    Invoke-Mosaic 'libraryExport' $out | Out-Null
    $html = Wait-OutputFile $out
    Assert-True ($html -match 'NETSCAPE-Bookmark-file-1') 'the export is a Netscape bookmark file'
    Assert-True ($html -match 'Export Me') 'the export contains the bookmark'
}

Register-Scenario -Area library -Name 'import bookmarks: Netscape HTML and Chrome JSON' -Todo 'Bookmarks (folders, tags, bar, manager, search); import/export Netscape HTML and Chrome JSON' -Body {
    $before = [int](Get-Field (Get-Library (Get-MosaicState)) 'bookmarks')
    Invoke-Mosaic 'libraryImport' (Get-FixturePath 'bookmarks-netscape.html') | Out-Null
    $null = Wait-State 'three bookmarks imported from HTML' -Condition { param($s) [int](Get-Field $s 'library.bookmarks') -ge $before + 3 }
    $middle = [int](Get-Field (Get-MosaicState) 'library.bookmarks')
    Invoke-Mosaic 'libraryImport' (Get-FixturePath 'bookmarks-chrome.json') | Out-Null
    $null = Wait-State 'two bookmarks imported from Chrome JSON' -Condition { param($s) [int](Get-Field $s 'library.bookmarks') -ge $middle + 2 }
    $out = New-OutputPath 'bookmarks-after-import.json'
    Invoke-Mosaic 'libraryBookmarks' $out | Out-Null
    $all = @(Wait-OutputFile $out -Json)
    foreach ($title in @('E2E Import One', 'E2E Import Two', 'E2E Chrome Two')) {
        Assert-True (@($all | Where-Object { (Get-Field $_ 'title') -eq $title }).Count -ge 1) "imported '$title'"
    }
    Assert-True (@($all | Where-Object { (Get-Field $_ 'title') -eq 'E2E Folder' -and (Get-Field $_ 'kind') -eq 'folder' }).Count -ge 1) 'the folder came along'
    $two = @($all | Where-Object { (Get-Field $_ 'title') -eq 'E2E Import Two' })[0]
    Assert-True (@(Get-Field $two 'tags') -contains 'e2e') 'tags came along'
}

Register-Scenario -Area library -Name 'bookmarks bar shows and hides' -Todo 'Bookmarks (folders, tags, bar, manager, search); import/export Netscape HTML and Chrome JSON' -Body {
    Invoke-Mosaic 'libraryBar' 'show' | Out-Null
    $null = Get-Library (Get-MosaicState)
    $null = Wait-State 'bookmarks bar shown' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'library.showsBookmarksBar') -eq $true }
    $null = Save-Screenshot 'bookmarks-bar'
    Invoke-Mosaic 'libraryBar' 'hide' | Out-Null
    $null = Wait-State 'bookmarks bar hidden' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'library.showsBookmarksBar') -eq $false }
}

Register-Scenario -Area library -Name 'history records visits in both engines; clear history' -Tags smoke -Todo 'History (searchable, delete by visit/site/range) in both engines' -Body {
    $token = New-Token
    $null = Open-TestPage "/page?title=History%20Gecko%20$token" -Engine gecko -Title "History Gecko $token"
    $null = Open-TestPage "/page?title=History%20Chromium%20$token" -Engine chromium -Title "History Chromium $token"
    $null = Get-Library (Get-MosaicState)
    $out = New-OutputPath 'history.json'
    $history = $null
    $null = Wait-Until -What 'both visits in history' -Condition {
        if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
        Invoke-Mosaic 'libraryHistory' $out -Quiet | Out-Null
        $script:LibraryHistory = Wait-OutputFile $out -Json
        $titles = @(Get-Field $script:LibraryHistory 'latest') | ForEach-Object { Get-Field $_ 'title' }
        ($titles -contains "History Gecko $token") -and ($titles -contains "History Chromium $token")
    }
    Invoke-Mosaic 'libraryClearHistory' 'all' | Out-Null
    $null = Wait-State 'history empty' -Condition { param($s) [int](Get-Field $s 'library.historyVisits') -eq 0 }
}

Register-Scenario -Area library -Name 'reading list' -Todo 'Reading list' -Body {
    $null = Open-TestPage '/article.html' -Engine gecko -Title 'The Quiet Harbour'
    Invoke-Mosaic 'libraryReadingAdd' | Out-Null
    $null = Get-Library (Get-MosaicState)
    $null = Wait-State 'one item in the reading list' -TimeoutSeconds 10 -Condition { param($s) [int](Get-Field $s 'library.readingList') -ge 1 }
    $out = New-OutputPath 'reading-list.json'
    Invoke-Mosaic 'libraryReadingList' $out | Out-Null
    $items = @(Wait-OutputFile $out -Json)
    Assert-True (@($items | Where-Object { (Get-Field $_ 'title') -eq 'The Quiet Harbour' }).Count -eq 1) 'the article is in the reading list'
}

Register-Scenario -Area library -Name 'custom search engine with a keyword' -Todo 'Custom search engines with keywords; search suggestions (off by default); Search settings' -Body {
    $template = (Get-TestUrl '/search') + '?q=%s'
    Invoke-Mosaic 'libraryAddEngine' "E2E Engine|e2e|$template" | Out-Null
    $null = Get-Library (Get-MosaicState)
    $null = Wait-State 'the engine is saved' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'library.customEngines') -contains 'e2e=E2E Engine' }
    Invoke-OnEngines {
        param($engine)
        $null = Set-Engine $engine
        Invoke-Mosaic 'navigate' "e2e mosaic $engine" | Out-Null
        $null = Wait-State "a keyword search on $engine" -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s) (Get-Field (Get-SelectedTab $s) 'title') -eq "Search: mosaic $engine" }
    }
}

Register-Scenario -Area library -Name 'search suggestions are off by default; local suggestions' -Todo 'Custom search engines with keywords; search suggestions (off by default); Search settings' -Body {
    $state = Get-MosaicState
    $null = Get-Library $state
    Assert-Equal 'False' (Get-Field $state 'library.remoteSuggestions') 'remote suggestions on a new profile'
    $null = Open-TestPage '/page?title=Suggest%20Target%20Page' -Engine gecko -Title 'Suggest Target Page'
    $out = New-OutputPath 'suggest.json'
    Invoke-Mosaic 'librarySuggest' 'Suggest Target' -Out $out | Out-Null
    $result = Wait-OutputFile $out -Json
    $local = @(Get-Field $result 'local')
    Write-Step "$($local.Count) local suggestion(s)"
    Assert-True (@($local | Where-Object { (Get-Field $_ 'remote') -eq $true }).Count -eq 0) 'no remote suggestions while they are off'
    Assert-True (@(Get-TestRequests -PathPrefix '/search' | Where-Object { $_.Query -like '*Suggest*' }).Count -eq 0) 'nothing was sent to a search engine'
}

Register-Scenario -Area library -Name 'library pages and Search settings' -Todo 'Custom search engines with keywords; search suggestions (off by default); Search settings' -Body {
    foreach ($page in @('bookmarks', 'history', 'reading-list')) {
        Invoke-Mosaic 'libraryOpen' $page | Out-Null
        Start-Sleep -Milliseconds 1000
        $null = Save-Screenshot "page-$page"
    }
    Invoke-Mosaic 'libraryOpen' 'search-settings' | Out-Null
    $null = Wait-State 'Settings > Search open' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'settingsCategory') -eq 'search' -or (Get-Field $s 'platform.settingsCategory') -eq 'search' }
    $null = Save-Screenshot 'settings-search'
    Invoke-Mosaic 'closeSettings' -AllowError | Out-Null
}
