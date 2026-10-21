# Dot-sourced by publish.ps1 and test-publish.ps1.
#
# The app reads settings.json with comments and trailing commas allowed;
# Windows PowerShell 5.1's ConvertFrom-Json takes neither. Strip both outside
# strings first, so a file the app reads is a file this reads too. A comment
# becomes a space, never nothing: f/*x*/alse must stay two tokens (the app
# refuses it), not join into false.
function ConvertFrom-LooseJson([string]$text) {
    $out = New-Object System.Text.StringBuilder
    $i = 0
    $n = $text.Length
    $inString = $false
    while ($i -lt $n) {
        $c = $text[$i]
        if ($inString) {
            [void]$out.Append($c)
            if ($c -eq '\' -and $i + 1 -lt $n) { [void]$out.Append($text[$i + 1]); $i += 2; continue }
            if ($c -eq '"') { $inString = $false }
            $i++
            continue
        }
        if ($c -eq '"') { $inString = $true; [void]$out.Append($c); $i++; continue }
        if ($c -eq '/' -and $i + 1 -lt $n -and $text[$i + 1] -eq '/') {
            # The newline that ends it stays, so it already separates tokens.
            while ($i -lt $n -and $text[$i] -ne "`n") { $i++ }
            [void]$out.Append(' ')
            continue
        }
        if ($c -eq '/' -and $i + 1 -lt $n -and $text[$i + 1] -eq '*') {
            $end = $text.IndexOf('*/', $i + 2)
            if ($end -lt 0) { throw 'unterminated comment' }
            [void]$out.Append(' ')
            $i = $end + 2
            continue
        }
        if ($c -eq ',') {
            $j = $i + 1
            while ($j -lt $n) {
                if ([char]::IsWhiteSpace($text[$j])) { $j++; continue }
                if ($text[$j] -eq '/' -and $j + 1 -lt $n -and $text[$j + 1] -eq '/') {
                    while ($j -lt $n -and $text[$j] -ne "`n") { $j++ }
                    continue
                }
                if ($text[$j] -eq '/' -and $j + 1 -lt $n -and $text[$j + 1] -eq '*') {
                    $end = $text.IndexOf('*/', $j + 2)
                    if ($end -lt 0) { break }
                    $j = $end + 2
                    continue
                }
                break
            }
            if ($j -lt $n -and ($text[$j] -eq '}' -or $text[$j] -eq ']')) { $i++; continue }
        }
        [void]$out.Append($c)
        $i++
    }
    return ($out.ToString() | ConvertFrom-Json)
}
