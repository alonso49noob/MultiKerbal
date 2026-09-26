<#
.SYNOPSIS
    Empaqueta una versión de MultiKerbal en dos zips, listos para subir a GitHub.

.DESCRIPTION
    dist/MultiKerbal-<versión>-mod.zip      Carpeta MultiKerbal/: se descomprime y se arrastra a GameData.
                                            Es el que descargan CKAN y los jugadores.
    dist/MultiKerbal-Server-<versión>.zip   Carpeta MultiKerbal-Server/: se descomprime donde sea y se ejecuta.

    Compila desde el último commit en una copia aparte (git worktree): lo que haya sin guardar en tu carpeta
    no acaba en la release. La versión sale de Directory.Build.props.

    Las rutas dentro del zip se escriben con "/" a mano. El Compress-Archive de Windows PowerShell las guarda
    con "\", y entonces CKAN y Linux no ven carpetas sino archivos llamados "MultiKerbal\Plugins\...".

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Package.ps1
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
$work = Join-Path ([System.IO.Path]::GetTempPath()) "MultiKerbal-package-$version"
$stage = Join-Path $work "stage"

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

Write-Host "Empaquetando MultiKerbal $version desde el último commit..."

if (Test-Path $work) {
    Invoke-Checked "quitar la copia anterior" { git -C $root worktree remove --force $work 2>$null }
    if (Test-Path $work) {
        Remove-Item $work -Recurse -Force
    }
}

Invoke-Checked "crear la copia del último commit" { git -C $root worktree add --detach --quiet $work HEAD }
try {
    # La ruta de KSP no está en git (es de cada uno): sin ella no se puede compilar el mod.
    $localProps = Join-Path $root "LocalDev.props"
    if (-not (Test-Path $localProps)) {
        throw "Falta LocalDev.props: cópialo de LocalDev.props.example y pon tu carpeta de KSP."
    }
    Copy-Item $localProps $work

    Invoke-Checked "compilar el mod" {
        dotnet build (Join-Path $work "src\MultiKerbal.Client") -c $Configuration -p:DeployToKSP=false -nologo -v:q
    }
    Invoke-Checked "publicar el servidor" {
        dotnet publish (Join-Path $work "src\MultiKerbal.Server") -c $Configuration -o (Join-Path $stage "server-bin") -nologo -v:q
    }

    # El mod: lo que va dentro de GameData/MultiKerbal.
    $mod = Join-Path $stage "mod"
    $modBin = Join-Path $work "src\MultiKerbal.Client\bin\$Configuration\net472"
    New-Item -ItemType Directory -Force -Path (Join-Path $mod "Plugins") | Out-Null
    Copy-Item (Join-Path $modBin "MultiKerbal.Client.dll"), (Join-Path $modBin "MultiKerbal.Common.dll") (Join-Path $mod "Plugins")
    Copy-Item (Join-Path $work "src\MultiKerbal.Client\MultiKerbal.version") $mod
    Copy-Item (Join-Path $work "README.md"), (Join-Path $work "LICENSE") $mod

    # El servidor: el programa publicado, sin los símbolos de depuración.
    $server = Join-Path $stage "server"
    New-Item -ItemType Directory -Force -Path $server | Out-Null
    Get-ChildItem (Join-Path $stage "server-bin") -File | Where-Object { $_.Extension -ne ".pdb" } | Copy-Item -Destination $server
    Copy-Item (Join-Path $work "README.md"), (Join-Path $work "LICENSE") $server

    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $modZip = Join-Path $dist "MultiKerbal-$version-mod.zip"
    $serverZip = Join-Path $dist "MultiKerbal-Server-$version.zip"
    New-Zip $modZip $mod "MultiKerbal"
    New-Zip $serverZip $server "MultiKerbal-Server"

    Show-Zip $modZip
    Show-Zip $serverZip
    Write-Host ""
    Write-Host "Listo en $dist"
}
finally {
    git -C $root worktree remove --force $work 2>$null
    if (Test-Path $work) {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
