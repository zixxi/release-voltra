@echo off
setlocal
title Voltra Release Builder

cd /d "%~dp0"

set "OPENSSL=C:\Program Files\OpenSSL-Win64\bin\openssl.exe"
set "PRIVATE_KEY=C:\Users\Isys\Documents\Voltra-Signing\release-private.pem"
set "PUBLIC_KEY=C:\Users\Isys\Documents\Voltra-Signing\release-public.pem"

echo.
echo ==========================================
echo           VOLTRA RELEASE BUILDER
echo ==========================================
echo.

if not exist "Build-Installer.ps1" (
    echo [ERROR] Build-Installer.ps1 not found.
    goto fail
)

if not exist "%OPENSSL%" (
    echo [ERROR] OpenSSL not found.
    goto fail
)

if not exist "%PRIVATE_KEY%" (
    echo [ERROR] RSA private key not found.
    goto fail
)

if not exist "%PUBLIC_KEY%" (
    echo [ERROR] RSA public key not found.
    goto fail
)

echo [1/5] Building Voltra and installer...
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\Build-Installer.ps1"

if errorlevel 1 (
    echo [ERROR] Installer build failed.
    goto fail
)

echo.
echo [2/5] Enter release version.
echo.

set /p "VERSION=Version (example: 0.9.4): "

if "%VERSION%"=="" (
    echo [ERROR] Version cannot be empty.
    goto fail
)

set "INSTALLER=Installer\Output\Voltra-%VERSION%-win-x64.exe"
set "MANIFEST=%INSTALLER%.json"
set "SIGNATURE=%MANIFEST%.sig"

if not exist "%INSTALLER%" (
    echo [ERROR] Installer not found:
    echo %INSTALLER%
    goto fail
)

echo.
echo [3/5] Generating signed release manifest...
echo.

powershell.exe -NoProfile -Command "$ErrorActionPreference='Stop'; $file='%INSTALLER%'; $info=Get-Item -LiteralPath $file; $hash=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash; $data=[ordered]@{product='Voltra';repository='zixxi/release-voltra';version='%VERSION%';asset=$info.Name;size=$info.Length;sha256=$hash}; $json=ConvertTo-Json -InputObject $data -Compress; [System.IO.File]::WriteAllText('%MANIFEST%',$json,(New-Object System.Text.UTF8Encoding($false))); Write-Host 'SHA256:' $hash; Write-Host 'Size:' $info.Length"

if errorlevel 1 (
    echo [ERROR] Manifest generation failed.
    goto fail
)

echo.
echo [4/5] Signing manifest with RSA-4096...
echo.

"%OPENSSL%" dgst -sha256 -sigopt rsa_padding_mode:pss -sigopt rsa_pss_saltlen:digest -sign "%PRIVATE_KEY%" -out "%SIGNATURE%" "%MANIFEST%"

if errorlevel 1 (
    echo [ERROR] RSA signing failed.
    goto fail
)

echo.
echo [5/5] Verifying RSA signature...
echo.

"%OPENSSL%" dgst -sha256 -sigopt rsa_padding_mode:pss -sigopt rsa_pss_saltlen:digest -verify "%PUBLIC_KEY%" -signature "%SIGNATURE%" "%MANIFEST%"

if errorlevel 1 (
    echo [ERROR] Signature verification failed.
    goto fail
)

echo.
echo ==========================================
echo             RELEASE READY!
echo ==========================================
echo.
echo Upload these 3 files to GitHub Releases:
echo.
echo Voltra-%VERSION%-win-x64.exe
echo Voltra-%VERSION%-win-x64.exe.json
echo Voltra-%VERSION%-win-x64.exe.json.sig
echo.
echo Release tag: v%VERSION%
echo.

explorer "Installer\Output"

pause
exit /b 0

:fail
echo.
echo Release preparation failed.
pause
exit /b 1