[CmdletBinding()]
param([string]$IsccPath)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Build-Installer.ps1 requires Windows and Inno Setup.'
}

function Invoke-BuildTool {
    param([string]$Executable, [string[]]$Arguments, [string]$Step)

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE."
    }
}

function Find-Iscc {
    param([string]$CompilerPath)

    if ($CompilerPath) {
        return (Get-Item -LiteralPath $CompilerPath -ErrorAction Stop).FullName
    }

    $compiler = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($compiler) { return $compiler.Source }

    $uninstallRoots = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    foreach ($registryPath in $uninstallRoots) {
        foreach ($install in Get-ItemProperty -Path "$registryPath\Inno Setup*_is1" -ErrorAction SilentlyContinue) {
            if (!$install.InstallLocation) { continue }
            $candidate = Join-Path $install.InstallLocation 'ISCC.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }

    $programFolders = @([Environment]::GetEnvironmentVariable('ProgramFiles(x86)'), $env:ProgramFiles)
    if ($env:LOCALAPPDATA) { $programFolders += (Join-Path $env:LOCALAPPDATA 'Programs') }
    foreach ($folder in $programFolders) {
        if (!$folder) { continue }
        foreach ($generation in '7', '6') {
            $candidate = Join-Path $folder "Inno Setup $generation\ISCC.exe"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }

    throw 'ISCC.exe was not found. Install Inno Setup or pass its compiler path with -IsccPath.'
}

function Confirm-PublishedPayload {
    param([string]$Dotnet, [string]$Payload, [string]$Version, [string]$FlasherHash, [string]$WorkDirectory)

    foreach ($name in 'Voltra.exe', 'Voltra.dll', 'Voltra.deps.json', 'Voltra.runtimeconfig.json', 'coreclr.dll', 'tools\VoltraFlasher.exe') {
        $path = Join-Path $Payload $name
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
            throw "The published payload is missing $name."
        }
    }
    $publishedFlasher = Join-Path $Payload 'tools\VoltraFlasher.exe'
    if ((Get-FileHash -LiteralPath $publishedFlasher -Algorithm SHA256).Hash -ne $FlasherHash) {
        throw 'The published VoltraFlasher.exe does not match the flasher used for the build.'
    }

    # Read PE metadata without loading or running Voltra. The .NET SDK runs the verifier.
    $verifier = Join-Path $WorkDirectory 'VerifyPayload.cs'
    @'
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

var payload = args[0];
using var host = new PEReader(File.OpenRead(Path.Combine(payload, "Voltra.exe")));
using var app = new PEReader(File.OpenRead(Path.Combine(payload, "Voltra.dll")));
if (host.PEHeaders.CoffHeader.Machine != Machine.Amd64 || app.PEHeaders.CoffHeader.Machine != Machine.Amd64)
{
    Console.Error.WriteLine("The published app and executable must both target x64.");
    return 1;
}

var metadata = app.GetMetadataReader();
var assembly = metadata.GetAssemblyDefinition();
if (assembly.Version != Version.Parse(args[1] + ".0"))
{
    Console.Error.WriteLine($"The published application reports assembly version {assembly.Version}, expected {args[1]}.0.");
    return 1;
}

int pins = 0;
foreach (var handle in assembly.GetCustomAttributes())
{
    var attribute = metadata.GetCustomAttribute(handle);
    if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
    var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
    if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
    var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
    if (!metadata.StringComparer.Equals(type.Namespace, "System.Reflection") ||
        !metadata.StringComparer.Equals(type.Name, "AssemblyMetadataAttribute")) continue;

    var data = metadata.GetBlobReader(attribute.Value);
    if (data.ReadUInt16() != 1 || data.ReadSerializedString() != "FirmwareFlasherSha256") continue;
    pins++;
    if (!string.Equals(data.ReadSerializedString(), args[2], StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Voltra.dll pins a SHA-256 that does not match its bundled flasher.");
        return 1;
    }
}
if (pins != 1)
{
    Console.Error.WriteLine("Expected exactly one FirmwareFlasherSha256 pin in Voltra.dll.");
    return 1;
}
return 0;
'@ | Set-Content -LiteralPath $verifier -Encoding UTF8

    Invoke-BuildTool $Dotnet @('run', '--file', $verifier, '--', $Payload, $Version, $FlasherHash) 'Payload verification'
}

$versionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
$project = Join-Path $PSScriptRoot 'WindowsApp\Voltra.csproj'
$props = Join-Path $PSScriptRoot 'Directory.Build.props'
$setupScript = Join-Path $PSScriptRoot 'Installer\Voltra.iss'
$sourceFlasher = Join-Path $PSScriptRoot 'WindowsApp\tools\VoltraFlasher.exe'
$outputPath = Join-Path $PSScriptRoot 'Installer\Output'
$publishRoot = Join-Path $PSScriptRoot 'publish'
$publishPath = Join-Path $publishRoot 'win-x64'

foreach ($path in $project, $props, $setupScript, (Join-Path $PSScriptRoot 'LICENSE')) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing required file: $path" }
}

$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
$iscc = Find-Iscc $IsccPath
if (!(Test-Path -LiteralPath $iscc -PathType Leaf)) { throw "Invalid ISCC path: $iscc" }

Push-Location -LiteralPath $PSScriptRoot
$buildLock = $null
$workDirectory = $null
$installerStage = $null
$keepWorkDirectory = $false
try {
    $buildProperties = @('-p:Configuration=Release', '-p:RuntimeIdentifier=win-x64', '-p:PlatformTarget=x64', '-p:SelfContained=true')
    Write-Host '[1/4] Reading the application version'
    $versionOutput = Invoke-BuildTool $dotnet (@('msbuild', $project, '-nologo', '-getProperty:Version') + $buildProperties) 'Version evaluation'
    $versions = @($versionOutput | ForEach-Object { $_.Trim() } | Where-Object { $_ -match $versionPattern })
    if ($versions.Count -ne 1) { throw 'MSBuild did not return a single major.minor.patch version from Directory.Build.props.' }
    $version = $versions[0]
    Write-Host "Voltra $version"

    [void][IO.Directory]::CreateDirectory($publishRoot)
    $lockPath = Join-Path $publishRoot '.voltra-installer.lock'
    try {
        $buildLock = [IO.FileStream]::new($lockPath, [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 1, [IO.FileOptions]::DeleteOnClose)
    }
    catch [IO.IOException] {
        throw "Cannot lock $lockPath. Close any installer build already running in this checkout."
    }

    $buildId = [Guid]::NewGuid().ToString('N')
    $workDirectory = Join-Path $publishRoot ".installer-$buildId"
    [void][IO.Directory]::CreateDirectory($workDirectory)
    $payload = Join-Path $workDirectory 'payload'

    Write-Host '[2/4] Publishing Release win-x64 (self-contained)'
    if (!(Test-Path -LiteralPath $sourceFlasher -PathType Leaf)) {
        Write-Host 'Building the missing firmware flasher with the existing Python 3.12 packaging script.'
        & (Join-Path $PSScriptRoot 'VoltraFirmware\Tools\Build-VoltraFlasher.ps1') | Out-Host
        if (!(Test-Path -LiteralPath $sourceFlasher -PathType Leaf)) {
            throw "The flasher build did not produce $sourceFlasher."
        }
    }
    $flasherHash = (Get-FileHash -LiteralPath $sourceFlasher -Algorithm SHA256).Hash
    Invoke-BuildTool $dotnet (@('publish', $project, '--nologo', '--output', $payload,
        "-p:VoltraFlasherSha256=$flasherHash") + $buildProperties) 'Application publish'

    Write-Host '[3/4] Verifying the published files and embedded firmware flasher SHA-256'
    Confirm-PublishedPayload $dotnet $payload $version $flasherHash $workDirectory

    $backup = Join-Path $workDirectory 'previous-payload'
    if ([IO.Directory]::Exists($publishPath)) { [IO.Directory]::Move($publishPath, $backup) }
    try { [IO.Directory]::Move($payload, $publishPath) }
    catch {
        if ([IO.Directory]::Exists($backup)) {
            try { [IO.Directory]::Move($backup, $publishPath) }
            catch {
                $keepWorkDirectory = $true
                throw "Could not restore the previous publish directory. It is available at $backup."
            }
        }
        throw
    }

    Write-Host "[4/4] Compiling Installer\Voltra.iss with $iscc"
    $installerStage = Join-Path $outputPath ".build-$buildId"
    [void][IO.Directory]::CreateDirectory($installerStage)
    Invoke-BuildTool $iscc @("/DMyAppVersion=$version", "/O$installerStage", $setupScript) 'Inno Setup compilation'

    $installerName = "Voltra-$version-win-x64.exe"
    $stagedInstaller = Join-Path $installerStage $installerName
    if (!(Test-Path -LiteralPath $stagedInstaller -PathType Leaf) -or (Get-Item -LiteralPath $stagedInstaller).Length -eq 0) {
        throw "Inno Setup did not produce $installerName."
    }
    $installerHash = (Get-FileHash -LiteralPath $stagedInstaller -Algorithm SHA256).Hash
    $finalInstaller = Join-Path $outputPath $installerName
    if ([IO.File]::Exists($finalInstaller)) {
        [IO.File]::Replace($stagedInstaller, $finalInstaller, (Join-Path $installerStage 'previous-installer.exe'))
    }
    else {
        [IO.File]::Move($stagedInstaller, $finalInstaller)
    }

    $size = (Get-Item -LiteralPath $finalInstaller).Length
    Write-Host ''
    Write-Host "Application version: $version"
    Write-Host "Installer: $finalInstaller"
    Write-Host ("Size: {0:N0} bytes" -f $size)
    Write-Host "SHA-256: $installerHash"
}
finally {
    if ($installerStage) { Remove-Item -LiteralPath $installerStage -Recurse -Force -ErrorAction SilentlyContinue }
    if ($workDirectory -and !$keepWorkDirectory) {
        Remove-Item -LiteralPath $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($buildLock) { $buildLock.Dispose() }
    Pop-Location
}
