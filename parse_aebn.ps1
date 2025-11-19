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

# Parse anchors
$anchors = [regex]::Matches($response.Content, '<a[^>]*href\s*=\s*"([^"]+)"[^>]*>(.*?)</a>', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)

$results = @()
foreach ($match in $anchors) {
    $href = $match.Groups[1].Value.Trim()
    $title = [regex]::Replace($match.Groups[2].Value, '<[^>]+>', '').Trim()

    if ([string]::IsNullOrEmpty($href)) { continue }

    $url = if ($href.StartsWith("http", [System.StringComparison]::OrdinalIgnoreCase)) { $href } else { [System.Uri]::new([System.Uri]$searchUrl, $href).ToString() }

    $lower = $url.ToLower()
    if (-not ($lower.Contains("/movies/") -or $lower.Contains("/video/") -or $lower.Contains("/watch/") -or $lower.Contains("gayeroticvideoindex"))) {
        continue
    }

    # Skip scene links
    if ($lower.Contains("#scene")) {
        continue
    }

    if ([string]::IsNullOrEmpty($title)) {
        try {
            $title = [System.Net.WebUtility]::UrlDecode([System.Uri]::new($url).Segments[-1].Trim('/'))
        } catch {
            $title = $url
        }
    }

    # Clean title: replace - with space and capitalize words
    $title = (Get-Culture).TextInfo.ToTitleCase($title.Replace("-", " "))

    # Skip non-movie links
    if ($title -like "*matching*" -or $title -like "*view all*") {
        continue
    }

    $results += [PSCustomObject]@{
        Name = $title
        Url = $url
    }
}

Write-Host "Found $($results.Count) search results for '$name'"
foreach ($result in $results) {
    Write-Host "Result: $($result.Name) - $($result.Url)"
}