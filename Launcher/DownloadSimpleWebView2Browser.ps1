$ErrorActionPreference = 'Stop'

$releaseUrl = 'https://github.com/felixhaedicke/SimpleWebView2Browser/releases/download/v0.1/SimpleWebView2Browser.zip'
$expectedSha256 = 'a8bd3c3a609e68434deb06216cee5a434048a7f3e97c5a9cf7411bf867ab11c1'
$launcherDir = $PSScriptRoot
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("cypress-simplewebview-" + [guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $tempRoot 'SimpleWebView2Browser.zip'
$extractPath = Join-Path $tempRoot 'extracted'

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    Invoke-WebRequest -Uri $releaseUrl -OutFile $archivePath -MaximumRedirection 10

    $actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedSha256) {
        throw "SimpleWebView2Browser archive SHA-256 mismatch. Expected $expectedSha256, got $actualSha256."
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath -Force
    $browser = Get-ChildItem -LiteralPath $extractPath -Filter 'SimpleWebView2Browser.exe' -File -Recurse | Select-Object -First 1
    $loader = Get-ChildItem -LiteralPath $extractPath -Filter 'WebView2Loader.dll' -File -Recurse | Select-Object -First 1
    if (-not $browser -or -not $loader) {
        throw 'The verified SimpleWebView2Browser release did not contain both SimpleWebView2Browser.exe and WebView2Loader.dll.'
    }

    Copy-Item -LiteralPath $browser.FullName -Destination (Join-Path $launcherDir 'SimpleWebView2Browser.exe') -Force
    Copy-Item -LiteralPath $loader.FullName -Destination (Join-Path $launcherDir 'WebView2Loader.dll') -Force
    Write-Host 'Downloaded and verified SimpleWebView2Browser v0.1 for the Cypress GameNative/Wine fallback.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
