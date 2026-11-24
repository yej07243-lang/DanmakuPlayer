param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Video,
    [int]$FirstRunSeconds = 9,
    [int]$SecondRunSeconds = 4
)

# Two-pass resume check: play, kill, reopen, and assert the second pass starts near the saved position.

$resumePath = Join-Path $env:LOCALAPPDATA "DanmakuPlayer\resume.json"
Remove-Item -LiteralPath $resumePath -ErrorAction SilentlyContinue

function Get-SavedPosition {
    param([string]$Path, [string]$Key)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $data = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $prop = $data.PSObject.Properties | Where-Object { $_.Name -ieq $Key } | Select-Object -First 1
    if ($null -eq $prop) { return $null }
    return [double]$prop.Value.Position
}

$key = (Resolve-Path -LiteralPath $Video).Path

$p1 = Start-Process -FilePath $Exe -ArgumentList @($Video) -PassThru
Start-Sleep -Seconds $FirstRunSeconds
Stop-Process -Id $p1.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$pos1 = Get-SavedPosition -Path $resumePath -Key $key
if ($null -eq $pos1) { Write-Output "FAIL: first pass wrote no resume entry"; exit 1 }

$p2 = Start-Process -FilePath $Exe -ArgumentList @($Video) -PassThru
Start-Sleep -Seconds $SecondRunSeconds
Stop-Process -Id $p2.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$pos2 = Get-SavedPosition -Path $resumePath -Key $key
if ($null -eq $pos2) { Write-Output "FAIL: second pass wrote no resume entry"; exit 1 }

$ok = $pos2 -ge ($pos1 - 0.5)
[pscustomobject]@{
    ResumeFile   = $resumePath
    FirstPassPos = [math]::Round($pos1, 2)
    SecondPassPos = [math]::Round($pos2, 2)
    ExpectedAtLeast = [math]::Round($pos1 - 0.5, 2)
    Pass = $ok
} | Format-List

if (-not $ok) { exit 1 }
exit 0
