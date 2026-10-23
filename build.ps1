# Deep Translation 빌드 스크립트
#   .\build.ps1              → dist\DeepTranslation.exe (단일 실행 파일)
#   .\build.ps1 -Installer   → dist\DeepTranslation-Setup-*.exe (설치 프로그램까지)
param([switch]$Installer, [string]$IsccPath)

$ErrorActionPreference = "Stop"
Push-Location -LiteralPath $PSScriptRoot
try {
$root = $PSScriptRoot

$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" }
          else { "C:\Program Files\dotnet\dotnet.exe" }

if (-not (Test-Path "$root\DeepTranslation\Assets\app.ico")) {
    & "$root\tools\make-icon.ps1"
}

& $dotnet publish "$root\DeepTranslation\DeepTranslation.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None `
    -o "$root\dist"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 실패" }

if ($Installer) {
    $iscc = if ($IsccPath) { $IsccPath } else { @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1 }

    if (-not $iscc) {
        throw "Inno Setup(ISCC.exe)을 찾을 수 없습니다. 설치: winget install -e --id JRSoftware.InnoSetup"
    }
    $version = ([xml](Get-Content -LiteralPath "$root\DeepTranslation\DeepTranslation.csproj" -Raw -Encoding utf8)).Project.PropertyGroup.Version
    & $iscc "/DMyAppVersion=$version" "$root\installer\setup.iss"
    if ($LASTEXITCODE -ne 0) { throw "ISCC 컴파일 실패" }
}

Write-Host ""
Write-Host "빌드 완료 → $root\dist" -ForegroundColor Green
Get-ChildItem "$root\dist" -Filter *.exe | ForEach-Object {
    Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
}
} finally {
    Pop-Location
}
