param([string]$UpdatePublicKey)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') {
    throw 'Build the Windows flasher on Windows with Python 3.12 and the .NET 10 SDK.'
}

function Invoke-BuildTool {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable exited with code $LASTEXITCODE."
    }
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$buildDir = Join-Path $root '.voltra-flasher'
$python = Join-Path $buildDir 'Scripts\python.exe'
$dist = Join-Path $buildDir 'dist'
$flasher = Join-Path $dist 'VoltraFlasher.exe'
$toolsDir = Join-Path $root 'WindowsApp\tools'
$bundledFlasher = Join-Path $toolsDir 'VoltraFlasher.exe'

Invoke-BuildTool -Executable 'py' -Arguments @('-3.12', '-m', 'venv', $buildDir)
Invoke-BuildTool -Executable $python -Arguments @(
    '-m', 'pip', 'install', 'esptool==5.1.0', 'pyserial==3.5', 'pyinstaller==6.16.0'
)
Invoke-BuildTool -Executable $python -Arguments @(
    '-m', 'PyInstaller', '--clean', '--noconfirm', '--onefile', '--name', 'VoltraFlasher',
    '--distpath', $dist, '--workpath', (Join-Path $buildDir 'work'), '--specpath', $buildDir,
    '--collect-data', 'esptool', '--hidden-import', 'serial.tools.list_ports_windows',
    (Join-Path $PSScriptRoot 'voltra_flash.py')
)

$protocol = Invoke-BuildTool -Executable $flasher -Arguments @('--protocol-version')
if (($protocol -join "`n").Trim() -ne 'VOLTRA_PROTOCOL 2') {
    throw 'The generated helper did not report the expected Voltra protocol.'
}

$hash = (Get-FileHash -LiteralPath $flasher -Algorithm SHA256).Hash
New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
Copy-Item -LiteralPath $flasher -Destination $bundledFlasher -Force
$buildArgs = @('build', (Join-Path $root 'WindowsApp\Voltra.csproj'), '-c', 'Release', "-p:VoltraFlasherSha256=$hash")
if ($UpdatePublicKey) {
    $buildArgs += "-p:VoltraUpdatePublicKey=$UpdatePublicKey"
}
Invoke-BuildTool -Executable 'dotnet' -Arguments $buildArgs
Write-Output "VoltraFlasherSha256=$hash"
