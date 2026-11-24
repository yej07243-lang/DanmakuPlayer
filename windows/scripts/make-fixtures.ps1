param(
    [string]$MediaDirectory = (Join-Path $PSScriptRoot "..\media"),
    [string]$LocalMedia
)

# Generates synthetic test assets and path-dependent fixtures so the repo never
# carries machine-specific paths or copyrighted media.
Set-Location (Join-Path $PSScriptRoot "..")
$media = (Resolve-Path $MediaDirectory).Path
New-Item -ItemType Directory -Force -Path $media | Out-Null

$testVideo = Join-Path $media "calibration-720p.mp4"
if (-not (Test-Path $testVideo)) {
    Write-Host "generating 600s test clip: $testVideo"
    ffmpeg -y -loglevel error -f lavfi -i "color=c=black:s=1280x720:r=10:d=600" `
        -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=600" `
        -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -b:a 96k -shortest $testVideo
}

$fixture = @{}
$fixture[$testVideo] = @{
    DanmakuSource = "calibration-fixture.xml"
    Anchors       = @(
        @{ Local = 0.0; Bili = 0.0 },
        @{ Local = 400.0; Bili = 300.0 }
    )
    UpdatedUtc    = (Get-Date).ToUniversalTime().ToString("o")
}

$fixturePath = Join-Path $media "calibration-fixture.json"
$fixture | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $fixturePath -Encoding UTF8
Write-Host "wrote calibration fixture: $fixturePath"

if ($LocalMedia) {
    $local = (Resolve-Path $LocalMedia).Path
    # Demo mapping only: local 2000s <-> bilibili 1686.091s. Real anchors must be measured by hand.
    $demo = @{}
    $demo[$local] = @{
        DanmakuSource = "wire-s01e01-ep830768.xml"
        Anchors       = @(
            @{ Local = 0.0; Bili = 0.0 },
            @{ Local = 2000.0; Bili = 1686.091 }
        )
        UpdatedUtc    = (Get-Date).ToUniversalTime().ToString("o")
    }

    $demoPath = Join-Path $media "local-demo-calibration.json"
    $demo | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $demoPath -Encoding UTF8
    Write-Host "wrote demo calibration for local media: $demoPath"
}
