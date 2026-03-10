<#
.SYNOPSIS
    Master build script for MfaSrv installer packages.

.DESCRIPTION
    Builds all MfaSrv components (Server, DC Agent, Endpoint Agent), packages them
    as ZIP archives, and optionally builds MSI installers using WiX v4.

.PARAMETER Version
    Product version string (default: 1.0.0.0).

.PARAMETER Configuration
    Build configuration: Release or Debug (default: Release).

.PARAMETER SkipMsi
    Skip MSI installer generation (useful when WiX toolset is not installed).

.PARAMETER SkipNative
    Skip C++ native project builds (useful when MSVC is not installed).

.PARAMETER SkipAdminPortal
    Skip npm build for admin portal frontend.

.EXAMPLE
    .\Build-Installers.ps1
    .\Build-Installers.ps1 -Version 1.2.0.0 -SkipNative
    .\Build-Installers.ps1 -SkipMsi -SkipNative
#>

[CmdletBinding()]
param(
    [string]$Version = "1.0.0.0",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$SkipMsi,
    [switch]$SkipNative,
    [switch]$SkipAdminPortal
)

$ErrorActionPreference = "Stop"

$RepoRoot = $PSScriptRoot
$ArtifactsDir = Join-Path $RepoRoot "artifacts"
$PublishRoot = Join-Path $ArtifactsDir "publish"
$WixDir = Join-Path $RepoRoot "deploy\wix"

# Component definitions
$Components = @(
    @{
        Name        = "Server"
        Project     = "src\Server\MfaSrv.Server\MfaSrv.Server.csproj"
        PublishDir  = Join-Path $PublishRoot "Server"
        WixFile     = Join-Path $WixDir "Server.wxs"
        WixExt      = @("-ext", "WixToolset.Util.wixext", "-ext", "WixToolset.Firewall.wixext", "-ext", "WixToolset.UI.wixext")
        NativeProj  = $null
        NativeDll   = $null
    },
    @{
        Name        = "DcAgent"
        Project     = "src\Agents\MfaSrv.DcAgent\MfaSrv.DcAgent.csproj"
        PublishDir  = Join-Path $PublishRoot "DcAgent"
        WixFile     = Join-Path $WixDir "DcAgent.wxs"
        WixExt      = @("-ext", "WixToolset.Util.wixext", "-ext", "WixToolset.UI.wixext")
        NativeProj  = "src\Agents\MfaSrv.DcAgent.Native\MfaSrv.DcAgent.Native.vcxproj"
        NativeDll   = "MfaSrvLsaAuth.dll"
    },
    @{
        Name        = "EndpointAgent"
        Project     = "src\Agents\MfaSrv.EndpointAgent\MfaSrv.EndpointAgent.csproj"
        PublishDir  = Join-Path $PublishRoot "EndpointAgent"
        WixFile     = Join-Path $WixDir "EndpointAgent.wxs"
        WixExt      = @("-ext", "WixToolset.Util.wixext", "-ext", "WixToolset.UI.wixext")
        NativeProj  = "src\Agents\MfaSrv.EndpointAgent.Native\MfaSrv.EndpointAgent.Native.vcxproj"
        NativeDll   = "MfaSrvCredentialProvider.dll"
    }
)

# ============================================================================
#  Helper functions
# ============================================================================

function Write-Header([string]$Message) {
    Write-Host ""
    Write-Host ("=" * 70) -ForegroundColor Cyan
    Write-Host "  $Message" -ForegroundColor Cyan
    Write-Host ("=" * 70) -ForegroundColor Cyan
}

function Write-Step([string]$Message) {
    Write-Host "  -> $Message" -ForegroundColor Yellow
}

function Write-Ok([string]$Message) {
    Write-Host "  [OK] $Message" -ForegroundColor Green
}

function Write-Skip([string]$Message) {
    Write-Host "  [SKIP] $Message" -ForegroundColor DarkGray
}

function Find-MSBuild {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $installPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath 2>$null
        if ($installPath) {
            $msbuild = Join-Path $installPath "MSBuild\Current\Bin\amd64\MSBuild.exe"
            if (Test-Path $msbuild) { return $msbuild }
            $msbuild = Join-Path $installPath "MSBuild\Current\Bin\MSBuild.exe"
            if (Test-Path $msbuild) { return $msbuild }
        }
    }
    return $null
}

function Test-WixInstalled {
    $wixPath = Get-Command wix -ErrorAction SilentlyContinue
    if (-not $wixPath) {
        # Also check .NET global tools path
        $globalToolsWix = Join-Path $env:USERPROFILE ".dotnet\tools\wix.exe"
        if (Test-Path $globalToolsWix) { return $true }
        return $false
    }
    try {
        $null = & wix --version 2>$null
        return $LASTEXITCODE -eq 0
    }
    catch {
        return $false
    }
}

# ============================================================================
#  Step 0: Prerequisites check and auto-install
# ============================================================================

Write-Header "Step 0: Prerequisites"

$prereqOk = $true

# --- .NET SDK ---
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnetCmd) {
    $dotnetVersion = & dotnet --version 2>$null
    Write-Ok ".NET SDK $dotnetVersion"
}
else {
    Write-Host "  [MISSING] .NET SDK" -ForegroundColor Red
    Write-Step "Installing .NET SDK via winget..."
    $wingetCmd = Get-Command winget -ErrorAction SilentlyContinue
    if ($wingetCmd) {
        & winget install Microsoft.DotNet.SDK.8 --accept-source-agreements --accept-package-agreements --silent 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Ok ".NET SDK installed. Please restart your terminal and re-run the script."
        }
        else {
            Write-Warning "Failed to install .NET SDK via winget. Install manually from https://dotnet.microsoft.com/download"
        }
    }
    else {
        Write-Warning "winget not available. Install .NET SDK manually from https://dotnet.microsoft.com/download"
    }
    $prereqOk = $false
}

# --- Node.js / npm (for admin portal) ---
if (-not $SkipAdminPortal) {
    $npmCmd = Get-Command npm -ErrorAction SilentlyContinue
    if ($npmCmd) {
        $npmVersion = & npm --version 2>$null
        $nodeVersion = & node --version 2>$null
        Write-Ok "Node.js $nodeVersion / npm $npmVersion"
    }
    else {
        Write-Host "  [MISSING] Node.js / npm" -ForegroundColor Red
        Write-Step "Installing Node.js via winget..."
        $wingetCmd = Get-Command winget -ErrorAction SilentlyContinue
        if ($wingetCmd) {
            & winget install OpenJS.NodeJS.LTS --accept-source-agreements --accept-package-agreements --silent 2>$null
            if ($LASTEXITCODE -eq 0) {
                Write-Ok "Node.js installed. Please restart your terminal and re-run the script."
            }
            else {
                Write-Warning "Failed to install Node.js via winget. Install manually from https://nodejs.org/"
            }
        }
        else {
            Write-Warning "winget not available. Install Node.js manually from https://nodejs.org/"
        }
        $prereqOk = $false
    }
}
else {
    Write-Skip "Node.js check skipped (admin portal skipped)"
}

# --- MSBuild / Visual Studio (for C++ native builds) ---
if (-not $SkipNative) {
    $msbuildPath = Find-MSBuild
    if ($msbuildPath) {
        Write-Ok "MSBuild found: $msbuildPath"
    }
    else {
        Write-Host "  [MISSING] MSBuild (Visual Studio C++ Build Tools)" -ForegroundColor Red
        Write-Step "Installing VS Build Tools with C++ workload via winget..."
        $wingetCmd = Get-Command winget -ErrorAction SilentlyContinue
        if ($wingetCmd) {
            & winget install Microsoft.VisualStudio.2022.BuildTools --accept-source-agreements --accept-package-agreements --silent --override "--add Microsoft.VisualStudio.Workload.VCTools --includeRecommended --quiet --wait" 2>$null
            if ($LASTEXITCODE -eq 0) {
                Write-Ok "VS Build Tools installed. Please restart your terminal and re-run the script."
            }
            else {
                Write-Warning "Failed to install VS Build Tools via winget. Install manually from https://visualstudio.microsoft.com/downloads/"
            }
        }
        else {
            Write-Warning "winget not available. Install Visual Studio Build Tools with C++ workload manually."
        }
        $prereqOk = $false
    }
}
else {
    Write-Skip "MSBuild check skipped (native builds skipped)"
}

# --- WiX toolset (for MSI generation) ---
if (-not $SkipMsi) {
    if (Test-WixInstalled) {
        $globalToolsWix = Join-Path $env:USERPROFILE ".dotnet\tools\wix.exe"
        if (Test-Path $globalToolsWix) {
            $wixVer = & $globalToolsWix --version 2>$null
        }
        else {
            $wixVer = & wix --version 2>$null
        }
        Write-Ok "WiX toolset $wixVer"

        # Check required WiX extensions
        $requiredExts = @("WixToolset.Util.wixext", "WixToolset.Firewall.wixext", "WixToolset.UI.wixext")
        $wixExe = if (Test-Path $globalToolsWix) { $globalToolsWix } else { "wix" }
        $installedExts = & $wixExe extension list 2>$null
        foreach ($ext in $requiredExts) {
            if ($installedExts -match $ext) {
                Write-Ok "WiX extension: $ext"
            }
            else {
                Write-Step "Installing WiX extension: $ext..."
                & $wixExe extension add "$ext/6.0.0" 2>$null
                if ($LASTEXITCODE -eq 0) {
                    Write-Ok "WiX extension installed: $ext"
                }
                else {
                    Write-Warning "Failed to install WiX extension: $ext. Run manually: wix extension add $ext"
                    $prereqOk = $false
                }
            }
        }
    }
    else {
        Write-Host "  [MISSING] WiX toolset" -ForegroundColor Red
        Write-Step "Installing WiX toolset as .NET global tool..."
        if ($dotnetCmd) {
            & dotnet tool install -g wix 2>$null
            if ($LASTEXITCODE -eq 0) {
                Write-Ok "WiX toolset installed"
                # Install required extensions
                $globalToolsWix = Join-Path $env:USERPROFILE ".dotnet\tools\wix.exe"
                foreach ($ext in @("WixToolset.Util.wixext/6.0.0", "WixToolset.Firewall.wixext/6.0.0", "WixToolset.UI.wixext/6.0.0")) {
                    & $globalToolsWix extension add $ext 2>$null
                    Write-Ok "WiX extension installed: $ext"
                }
            }
            else {
                Write-Warning "Failed to install WiX toolset. Run manually: dotnet tool install -g wix"
                $prereqOk = $false
            }
        }
        else {
            Write-Warning "Cannot install WiX without .NET SDK."
            $prereqOk = $false
        }
    }
}
else {
    Write-Skip "WiX check skipped (MSI generation skipped)"
}

# --- Prerequisite summary ---
if (-not $prereqOk) {
    Write-Host ""
    Write-Host "  Some prerequisites were missing and may have been installed." -ForegroundColor Yellow
    Write-Host "  If tools were just installed, restart your terminal and re-run the script." -ForegroundColor Yellow
    Write-Host ""
    Write-Error "Prerequisites check failed. Resolve the issues above and re-run."
    exit 1
}

Write-Host ""
Write-Ok "All prerequisites satisfied"

# ============================================================================
#  Preparation
# ============================================================================

Write-Header "MfaSrv Build - Version $Version ($Configuration)"
Write-Host "  Repo root:  $RepoRoot"
Write-Host "  Artifacts:  $ArtifactsDir"

# Clean artifacts
if (Test-Path $ArtifactsDir) {
    Remove-Item $ArtifactsDir -Recurse -Force
}
New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null
New-Item -ItemType Directory -Path $PublishRoot -Force | Out-Null

$artifactNames = @()

# ============================================================================
#  Step 1: dotnet publish
# ============================================================================

Write-Header "Step 1: dotnet publish"

foreach ($comp in $Components) {
    $projPath = Join-Path $RepoRoot $comp.Project
    Write-Step "Publishing $($comp.Name)..."

    dotnet publish $projPath `
        --configuration $Configuration `
        --output $comp.PublishDir `
        --no-self-contained `
        -p:Version=$Version `
        -p:PublishReadyToRun=false

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to publish $($comp.Name)"
        exit 1
    }

    $fileCount = (Get-ChildItem $comp.PublishDir -File).Count
    Write-Ok "$($comp.Name) published ($fileCount files)"

    # Publish the Server Configurator alongside the Server
    if ($comp.Name -eq "Server") {
        $configuratorProj = Join-Path $RepoRoot "src\Server\MfaSrv.Server.Configurator\MfaSrv.Server.Configurator.csproj"
        if (Test-Path $configuratorProj) {
            Write-Step "Publishing Server Configurator..."
            $configuratorPublishDir = Join-Path $PublishRoot "ServerConfigurator"
            dotnet publish $configuratorProj `
                --configuration $Configuration `
                --output $configuratorPublishDir `
                --no-self-contained `
                -p:Version=$Version `
                -p:PublishReadyToRun=false

            if ($LASTEXITCODE -ne 0) {
                Write-Warning "Failed to publish Server Configurator. Continuing..."
            }
            else {
                # Copy configurator files to Server publish dir
                $configuratorExe = Join-Path $configuratorPublishDir "MfaSrv.Server.Configurator.exe"
                $configuratorDll = Join-Path $configuratorPublishDir "MfaSrv.Server.Configurator.dll"
                $configuratorDeps = Join-Path $configuratorPublishDir "MfaSrv.Server.Configurator.deps.json"
                $configuratorRc = Join-Path $configuratorPublishDir "MfaSrv.Server.Configurator.runtimeconfig.json"
                foreach ($f in @($configuratorExe, $configuratorDll, $configuratorDeps, $configuratorRc)) {
                    if (Test-Path $f) {
                        Copy-Item $f $comp.PublishDir -Force
                    }
                }
                # Copy unique dependency DLLs (not already in Server publish)
                Get-ChildItem $configuratorPublishDir -Filter "*.dll" | ForEach-Object {
                    $destFile = Join-Path $comp.PublishDir $_.Name
                    if (-not (Test-Path $destFile)) {
                        Copy-Item $_.FullName $comp.PublishDir -Force
                    }
                }
                Write-Ok "Server Configurator published"
            }
        }
    }
}

# ============================================================================
#  Step 2: Admin Portal (npm build)
# ============================================================================

$adminPortalDir = Join-Path $RepoRoot "src\AdminPortal\mfasrv-admin"

if (-not $SkipAdminPortal -and (Test-Path (Join-Path $adminPortalDir "package.json"))) {
    Write-Header "Step 2: Admin Portal (npm build)"

    Push-Location $adminPortalDir
    try {
        Write-Step "Installing npm dependencies..."
        npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "npm ci failed, falling back to npm install"
            npm install --no-audit --no-fund
        }

        Write-Step "Building admin portal..."
        npm run build
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Admin portal build failed"
            exit 1
        }

        # Copy dist to Server wwwroot
        $wwwroot = Join-Path $Components[0].PublishDir "wwwroot"
        $distDir = Join-Path $adminPortalDir "dist"
        if (Test-Path $distDir) {
            if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
            Copy-Item $distDir $wwwroot -Recurse
            Write-Ok "Admin portal copied to Server/wwwroot/"
        }
        else {
            Write-Warning "Admin portal dist/ not found after build"
        }
    }
    finally {
        Pop-Location
    }
}
else {
    Write-Header "Step 2: Admin Portal"
    Write-Skip "Admin portal build skipped"
}

# ============================================================================
#  Step 3: C++ native builds
# ============================================================================

if (-not $SkipNative) {
    Write-Header "Step 3: C++ Native Builds"
    $msbuild = Find-MSBuild

    if ($msbuild) {
        Write-Step "Using MSBuild: $msbuild"

        foreach ($comp in $Components) {
            if (-not $comp.NativeProj) { continue }

            $nativeProjPath = Join-Path $RepoRoot $comp.NativeProj
            if (-not (Test-Path $nativeProjPath)) {
                Write-Warning "Native project not found: $nativeProjPath"
                continue
            }

            Write-Step "Building $($comp.Name) native DLL..."
            & $msbuild $nativeProjPath `
                /p:Configuration=$Configuration `
                /p:Platform=x64 `
                /v:minimal `
                /nologo

            if ($LASTEXITCODE -ne 0) {
                Write-Error "Native build failed for $($comp.Name)"
                exit 1
            }

            # Find and copy the native DLL
            $nativeOutputDir = Join-Path (Split-Path $nativeProjPath) "x64\$Configuration"
            $nativeDllPath = Join-Path $nativeOutputDir $comp.NativeDll
            if (Test-Path $nativeDllPath) {
                Copy-Item $nativeDllPath $comp.PublishDir -Force
                Write-Ok "$($comp.NativeDll) copied to publish dir"
            }
            else {
                Write-Warning "Native DLL not found at: $nativeDllPath"
            }
        }
    }
    else {
        Write-Warning "MSBuild not found (install Visual Studio with C++ workload). Skipping native builds."
    }
}
else {
    Write-Header "Step 3: C++ Native Builds"
    Write-Skip "Native builds skipped"
}

# ============================================================================
#  Step 4: ZIP packages
# ============================================================================

Write-Header "Step 4: ZIP Packages"

foreach ($comp in $Components) {
    $zipName = "MfaSrv.$($comp.Name)-$Version.zip"
    $zipPath = Join-Path $ArtifactsDir $zipName

    Write-Step "Creating $zipName..."
    Compress-Archive -Path (Join-Path $comp.PublishDir "*") -DestinationPath $zipPath -Force

    $sizeMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Write-Ok "$zipName ($sizeMB MB)"
    $artifactNames += "$zipName ($sizeMB MB)"
}

# ============================================================================
#  Step 5: MSI packages (WiX v4)
# ============================================================================

if (-not $SkipMsi) {
    Write-Header "Step 5: MSI Packages (WiX v4)"

    if (Test-WixInstalled) {
        foreach ($comp in $Components) {
            if (-not (Test-Path $comp.WixFile)) {
                Write-Warning "WiX file not found: $($comp.WixFile)"
                continue
            }

            $msiName = "MfaSrv.$($comp.Name)-$Version.msi"
            $msiPath = Join-Path $ArtifactsDir $msiName

            $nativeDir = $comp.PublishDir

            Write-Step "Building $msiName..."

            $wixArgs = @(
                "build"
                $comp.WixFile
                "-arch", "x64"
                "-o", $msiPath
                "-d", "PublishDir=$($comp.PublishDir)"
                "-d", "NativeDir=$nativeDir"
                "-d", "Version=$Version"
            )
            $wixArgs += $comp.WixExt

            & wix @wixArgs

            if ($LASTEXITCODE -ne 0) {
                Write-Warning "WiX build failed for $($comp.Name). Continuing..."
                continue
            }

            $sizeMB = [math]::Round((Get-Item $msiPath).Length / 1MB, 2)
            Write-Ok "$msiName ($sizeMB MB)"
            $artifactNames += "$msiName ($sizeMB MB)"
        }
    }
    else {
        Write-Warning "WiX toolset not found. Install with: dotnet tool install -g wix"
        Write-Warning "Skipping MSI generation."
    }
}
else {
    Write-Header "Step 5: MSI Packages"
    Write-Skip "MSI generation skipped"
}

# ============================================================================
#  Summary
# ============================================================================

Write-Header "Build Complete"
Write-Host ""

if ($artifactNames.Count -gt 0) {
    Write-Host "  Produced artifacts:" -ForegroundColor White
    Write-Host "  ---------------------------------------------------" -ForegroundColor DarkGray
    $artifactNames | ForEach-Object { Write-Host "  $_" -ForegroundColor Green }
    Write-Host "  ---------------------------------------------------" -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "  Output directory: $ArtifactsDir" -ForegroundColor White
}
else {
    Write-Host "  No artifacts produced." -ForegroundColor Yellow
}

Write-Host ""
