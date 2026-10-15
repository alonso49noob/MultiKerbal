<#
.SYNOPSIS
    Publica el relay de MultiKerbal como un único ejecutable por plataforma, y un zip de cada uno.

.DESCRIPTION
    dist/relay-linux-arm64/MultiKerbal.Relay            Para la máquina de Oracle Cloud (Ubuntu en Ampere).
    dist/relay-win-x64/MultiKerbal.Relay.exe            Para probar en Windows.
    dist/MultiKerbal-Relay-<versión>-linux-arm64.zip    Carpeta MultiKerbal-Relay/ con el ejecutable, LICENSE y RELAY.md.
    dist/MultiKerbal-Relay-<versión>-win-x64.zip        Lo mismo para Windows.

    Los ejecutables llevan dentro el runtime de .NET (self-contained): en la máquina del relay no hace falta
    instalar nada. Se publica lo que haya en tu carpeta (no hace falta haber hecho commit). La versión sale de
    Directory.Build.props.

    Las rutas dentro del zip se escriben con "/" a mano (ver Package.ps1). El zip no guarda el permiso de
    ejecución de Linux: tras descomprimir en Linux hay que hacer "chmod +x MultiKerbal.Relay" (ver docs/RELAY.md).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\PublishRelay.ps1
#>
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$version = (Select-Xml -Path (Join-Path $root "Directory.Build.props") -XPath "//Version").Node.InnerText
$dist = Join-Path $root "dist"
$project = Join-Path $root "src\MultiKerbal.Relay"

function Invoke-Checked([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "Falló: $What"
    }
}

# Crea un zip cuyo contenido va dentro de la carpeta $Folder, con "/" como separador.
function New-Zip([string]$ZipPath, [string]$SourceDir, [string]$Folder) {
    if (Test-Path $ZipPath) {
        Remove-Item $ZipPath -Force
    }

    $base = (Resolve-Path $SourceDir).Path.TrimEnd('\') + '\'
    $zip = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        [void]$zip.CreateEntry("$Folder/")
        foreach ($directory in Get-ChildItem $SourceDir -Recurse -Directory) {
            $relative = $directory.FullName.Substring($base.Length).Replace('\', '/')
            [void]$zip.CreateEntry("$Folder/$relative/")
        }

        foreach ($file in Get-ChildItem $SourceDir -Recurse -File) {
            $relative = $file.FullName.Substring($base.Length).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $file.FullName, "$Folder/$relative", [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Show-Zip([string]$ZipPath) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        Write-Host ""
        Write-Host "$(Split-Path -Leaf $ZipPath) ($([int]((Get-Item $ZipPath).Length / 1KB)) KB)"
        $zip.Entries | Where-Object { -not $_.FullName.EndsWith("/") } | ForEach-Object { Write-Host "  $($_.FullName)" }
    }
    finally {
        $zip.Dispose()
    }
}

# Publica el relay para un sistema ($Runtime) y deja el ejecutable en dist/relay-<runtime>.
function Publish-Relay([string]$Runtime, [string]$Executable) {
    $out = Join-Path $dist "relay-$Runtime"
    if (Test-Path $out) {
        Remove-Item $out -Recurse -Force
    }

    Invoke-Checked "publicar el relay para $Runtime" {
        dotnet publish $project -c $Configuration -r $Runtime --self-contained -p:PublishSingleFile=true -p:DebugType=None -o $out -nologo -v:q
    }

    if (-not (Test-Path (Join-Path $out $Executable))) {
        throw "Falta $Executable en $out"
    }

    # Lo que va al zip: el ejecutable, la licencia y la guía. Nada de símbolos de depuración.
    $stage = Join-Path $dist "stage-relay-$Runtime"
    if (Test-Path $stage) {
        Remove-Item $stage -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    try {
        Copy-Item (Join-Path $out $Executable) $stage
        Copy-Item (Join-Path $root "LICENSE") $stage
        Copy-Item (Join-Path $root "docs\RELAY.md") $stage

        $zipPath = Join-Path $dist "MultiKerbal-Relay-$version-$Runtime.zip"
        New-Zip $zipPath $stage "MultiKerbal-Relay"
        Show-Zip $zipPath
    }
    finally {
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Publicando el relay de MultiKerbal $version..."
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Publish-Relay "linux-arm64" "MultiKerbal.Relay"
Publish-Relay "win-x64" "MultiKerbal.Relay.exe"

Write-Host ""
Write-Host "Listo en $dist"
