# Writes src\VoidCrewModLauncher.Core\Localization\keys.json: every English text the launcher translates, sorted, for
# translators. A key is the first argument of Loc.T("...") / Loc.F("...") in src\**\*.cs, or {i:T ...} in src\**\*.xaml.
# Each translation is a file next to it, <code>.json: { "English text": "translation", ... }, with the same {0} {1}
# placeholders as the English. LocalizationTests checks keys.json is up to date and every translation file is complete.
# Usage: powershell -ExecutionPolicy Bypass -File tools\extract-strings.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$src = Join-Path $root "src"
$out = Join-Path $src "VoidCrewModLauncher.Core\Localization\keys.json"

function Get-SourceFiles([string]$pattern) {
    Get-ChildItem -Path $src -Filter $pattern -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and $_.FullName -notlike '*\Localization\Loc.cs' -and $_.Name -ne 'TExtension.cs' } |
        Sort-Object FullName
}

# // comment lines don't count (doc comments show examples).
function Remove-Comments([string]$text) {
    (($text -replace "`r`n", "`n").Split("`n") | ForEach-Object { if ($_.TrimStart().StartsWith("//")) { "" } else { $_ } }) -join "`n"
}

$keys = New-Object 'System.Collections.Generic.SortedSet[string]' ([StringComparer]::Ordinal)

foreach ($file in Get-SourceFiles "*.cs") {
    $code = Remove-Comments ([IO.File]::ReadAllText($file.FullName))
    foreach ($call in [regex]::Matches($code, '\bLoc\.[TF]\(\s*(@?)"')) {
        $verbatim = $call.Groups[1].Value -eq "@"
        $key = New-Object System.Text.StringBuilder
        for ($i = $call.Index + $call.Length; $i -lt $code.Length; $i++) {
            $c = $code[$i]
            if ($verbatim) {
                if ($c -eq '"') {
                    if ($i + 1 -lt $code.Length -and $code[$i + 1] -eq '"') { [void]$key.Append('"'); $i++; continue }
                    break
                }
                [void]$key.Append($c)
            }
            else {
                if ($c -eq '"') { break }
                if ($c -ne '\') { [void]$key.Append($c); continue }
                $i++
                $next = $code[$i]
                switch -CaseSensitive ($next) {
                    'n' { [void]$key.Append("`n") }
                    'r' { [void]$key.Append("`r") }
                    't' { [void]$key.Append("`t") }
                    '0' { [void]$key.Append([char]0) }
                    'u' { [void]$key.Append([char][Convert]::ToInt32($code.Substring($i + 1, 4), 16)); $i += 4 }
                    default { [void]$key.Append($next) }
                }
            }
        }
        [void]$keys.Add($key.ToString())
    }
}

# {i:T text}: quoted ('...' or "...") or bare (trimmed, up to , or }); a backslash escapes the next character; XML
# entities (&lt; &amp; ...) are decoded, as WPF reads them.
foreach ($file in Get-SourceFiles "*.xaml") {
    $xaml = [IO.File]::ReadAllText($file.FullName)
    foreach ($m in [regex]::Matches($xaml, '\{i:T\s')) {
        $i = $m.Index + $m.Length
        while ($i -lt $xaml.Length -and [char]::IsWhiteSpace($xaml[$i])) { $i++ }
        $quote = [char]0
        if ($xaml[$i] -eq "'" -or $xaml[$i] -eq '"') { $quote = $xaml[$i]; $i++ }
        $raw = New-Object System.Text.StringBuilder
        for (; $i -lt $xaml.Length; $i++) {
            $c = $xaml[$i]
            if ($c -eq '\' -and $i + 1 -lt $xaml.Length) { $i++; [void]$raw.Append($xaml[$i]); continue }
            if ($quote -ne [char]0) { if ($c -eq $quote) { break } }
            elseif ($c -eq '}' -or $c -eq ',') { break }
            [void]$raw.Append($c)
        }
        $key = [System.Net.WebUtility]::HtmlDecode($raw.ToString())
        if ($quote -eq [char]0) { $key = $key.Trim() }
        [void]$keys.Add($key)
    }
}

function ConvertTo-JsonString([string]$s) {
    $b = New-Object System.Text.StringBuilder
    [void]$b.Append('"')
    foreach ($c in $s.ToCharArray()) {
        switch ([int]$c) {
            34 { [void]$b.Append('\"') }
            92 { [void]$b.Append('\\') }
            10 { [void]$b.Append('\n') }
            13 { [void]$b.Append('\r') }
            9 { [void]$b.Append('\t') }
            default { if ([int]$c -lt 32) { [void]$b.Append(('\u{0:x4}' -f [int]$c)) } else { [void]$b.Append($c) } }
        }
    }
    [void]$b.Append('"')
    $b.ToString()
}

$lines = @($keys | ForEach-Object { "  " + (ConvertTo-JsonString $_) })
$json = "[`n" + ($lines -join ",`n") + "`n]`n"
[IO.File]::WriteAllText($out, $json, (New-Object System.Text.UTF8Encoding $false))
Write-Output "$($keys.Count) keys written to $out"
