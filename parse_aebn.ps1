$name = "Armed Services"
$encodedName = [System.Net.WebUtility]::UrlEncode($name)
$searchUrl = "https://gay.aebn.com/gay/search?queryType=Free+Form&query=$encodedName"

# Create session
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

# Bypass age-gate
Invoke-WebRequest -Uri "https://gay.aebn.com/avs/gate-redirect?f=%2Fgay" -WebSession $session -UseBasicParsing | Out-Null

# Fetch search page
$response = Invoke-WebRequest -Uri $searchUrl -WebSession $session -UseBasicParsing

# Check for age-gate
if ($response.Content -match "(are you over|age verification|age_gate|over 18|please verify your age|enter your birth)") {
    Write-Host "Age-gate still present"
    exit
}

# Collect movie anchors (direct movie links)
$anchors = [regex]::Matches($response.Content, '<a[^>]*href\s*=\s*"([^"]+)"[^>]*>(.*?)</a>', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)
$movieAnchors = @()
foreach ($match in $anchors) {
    $href = $match.Groups[1].Value.Trim()
    $inner = [regex]::Replace($match.Groups[2].Value, '<[^>]+>', '').Trim()
    if ([string]::IsNullOrEmpty($href)) { continue }
    $url = if ($href.StartsWith("http", [System.StringComparison]::OrdinalIgnoreCase)) { $href } else { [System.Uri]::new([System.Uri]$searchUrl, $href).ToString() }
    $lower = $url.ToLower()
    if (-not ($lower.Contains("/movies/") -or $lower.Contains("/video/") -or $lower.Contains("/watch/"))) { continue }
    if ($lower.Contains("#scene")) { continue }
    $title = $inner
    if ([string]::IsNullOrEmpty($title)) {
        try { $title = [System.Net.WebUtility]::UrlDecode([System.Uri]::new($url).Segments[-1].Trim('/')) } catch { $title = $url }
    }
    $movieAnchors += [PSCustomObject]@{ Name = $title; Url = $url }
}

# Parse card blocks and prefer their displayed titles
$cardMatches = [regex]::Matches($response.Content, '<button[^>]*class=[^>]*card[^>]*>(.*?)</button>', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)
$results = @()
foreach ($card in $cardMatches) {
    $block = $card.Groups[1].Value
    $displayTitle = $null
    $m = [regex]::Match($block, '<div[^>]*class=[^>]*cardText[^>]*cardText-first[^>]*>(.*?)</div>', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if ($m.Success) { $displayTitle = [regex]::Replace($m.Groups[1].Value, '<[^>]+>', '').Trim() }
    if ([string]::IsNullOrEmpty($displayTitle)) {
        $m = [regex]::Match($block, '<div[^>]*class=[^>]*cardText[^>]*cardCenteredText[^>]*>(.*?)</div>', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)
        if ($m.Success) { $displayTitle = [regex]::Replace($m.Groups[1].Value, '<[^>]+>', '').Trim() }
    }
    if ([string]::IsNullOrEmpty($displayTitle)) { continue }

    # find image if present
    $styleMatch = [regex]::Match($block, 'background-image\s*:\s*url\((.*?)\)', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $imageUrl = $null
    if ($styleMatch.Success) { $imageUrl = $styleMatch.Groups[1].Value.Trim().Trim('"','''') }
    else {
        $imgMatch = [regex]::Match($block, '<img[^>]*src\s*=\s*([^\s>]+)[^>]*>', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($imgMatch.Success) { $imageUrl = $imgMatch.Groups[1].Value.Trim().Trim('"','''') }
    }

    # slug helper
    function To-Slug($s) {
        if ([string]::IsNullOrWhiteSpace($s)) { return '' }
        $lower = $s.ToLower()
        $lower = [regex]::Replace($lower, '[^a-z0-9]+', '-')
        $lower = [regex]::Replace($lower, '-+', '-')
        return $lower.Trim('-')
    }

    $slug = To-Slug($displayTitle)
    $matched = $null
    foreach ($a in $movieAnchors) {
        if (To-Slug($a.Name) -eq $slug -or $a.Url.TrimEnd('/') -like "*/$slug") { $matched = $a; break }
    }
    if ($matched -ne $null) {
        $results += [PSCustomObject]@{ Name = $displayTitle; Url = $matched.Url; Image = $imageUrl }
    }
}

# Fallback: if none matched, include movie anchors
if ($results.Count -eq 0) {
    foreach ($a in $movieAnchors) { $results += [PSCustomObject]@{ Name = $a.Name; Url = $a.Url; Image = $null } }
}

# Deduplicate by Url
$dedup = @{}
$final = @()
foreach ($r in $results) { if (-not $dedup.ContainsKey($r.Url)) { $dedup[$r.Url] = $true; $final += $r } }

Write-Host "Found $($final.Count) search results for '$name'"
foreach ($result in $final) { Write-Host "Result: $($result.Name) - $($result.Url) - Image: $($result.Image)" }