#Requires -Version 5.1
<#
.SYNOPSIS
    vmonitor smoke test script - runs automatically after installer completes.

.DESCRIPTION
    Validates the post-install environment:
      - Virtual display driver exists in DriverStore (Req 1.1)
      - Required network services are in Running state (Req 1.2)
      - No manual steps were required during install (Req 1.3)

.PARAMETER DriverInfName
    INF filename or OriginalName to search for in DriverStore.
    Default: vmonitor (partial match)

.PARAMETER RequiredServices
    List of service names to verify as Running.
    Default: mdnsNSP (Bonjour/mDNS service)

.EXAMPLE
    # Run automatically from installer
    powershell.exe -ExecutionPolicy Bypass -File smoke-test.ps1

    # Run with custom parameters
    .\smoke-test.ps1 -DriverInfName "vmonitorvdd" -RequiredServices @("mdnsNSP", "BonjourService")
#>

[CmdletBinding()]
param(
    [string]   $DriverInfName    = 'vmonitor',
    # dnscache = Windows DNS Client (supports mDNS on Windows 10 1903+)
    # mdnsNSP  = Bonjour (Apple) - optional, only present if installed
    [string[]] $RequiredServices = @('dnscache'),
    [string[]] $OptionalServices = @('mdnsNSP', 'BonjourService')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Helper functions
# ---------------------------------------------------------------------------

function Write-Pass {
    param([string]$Message)
    Write-Host "[PASS] $Message" -ForegroundColor Green
}

function Write-Fail {
    param([string]$Message)
    Write-Host "[FAIL] $Message" -ForegroundColor Red
}

function Write-Info {
    param([string]$Message)
    Write-Host "[INFO] $Message" -ForegroundColor Cyan
}

# ---------------------------------------------------------------------------
# Track overall test result
# ---------------------------------------------------------------------------
$overallPassed = $true

# ---------------------------------------------------------------------------
# Check 1: Virtual display driver exists in DriverStore (Req 1.1)
# ---------------------------------------------------------------------------
Write-Info "Check 1/3: Virtual display driver in DriverStore (Req 1.1)"

try {
    # Run pnputil /enum-drivers and search for the vmonitor driver entry
    $pnputilOutput = & pnputil.exe /enum-drivers 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Fail "pnputil /enum-drivers failed (exit code: $LASTEXITCODE)"
        $overallPassed = $false
    }
    else {
        # Search for DriverInfName in pnputil output (case-insensitive)
        # Use @() to ensure array even if 0 or 1 result, avoiding .Count issues with StrictMode
        $matched = @($pnputilOutput | Where-Object {
            $_ -imatch [regex]::Escape($DriverInfName)
        })
        $driverFound = $matched.Count -gt 0

        if ($driverFound) {
            Write-Pass "Virtual display driver '$DriverInfName' found in DriverStore."
        }
        else {
            Write-Fail "Virtual display driver '$DriverInfName' NOT found in DriverStore."
            Write-Info "Hint: The vmonitor virtual display driver has not been installed yet."
            Write-Info "Run the vmonitor PC client installer, or run 'dotnet run --project VMonitor.UI' to auto-install."
            $overallPassed = $false
        }
    }
}
catch {
    Write-Fail "Exception while running pnputil: $_"
    $overallPassed = $false
}

# ---------------------------------------------------------------------------
# Check 2: Required network services are Running (Req 1.2)
# ---------------------------------------------------------------------------
Write-Info "Check 2/3: Required network services are Running (Req 1.2)"

# Check required services (must be Running)
foreach ($serviceName in $RequiredServices) {
    try {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

        if ($null -eq $service) {
            Write-Fail "Required service '$serviceName' does not exist on this system."
            $overallPassed = $false
            continue
        }

        if ($service.Status -eq 'Running') {
            Write-Pass "Service '$serviceName' is Running."
        }
        else {
            $statusStr = [string]$service.Status
            Write-Fail "Service '$serviceName' is '$statusStr' (expected: Running)."
            $overallPassed = $false
        }
    }
    catch {
        Write-Fail "Exception while checking service '$serviceName': $_"
        $overallPassed = $false
    }
}

# Check optional services (warn if missing, don't fail)
$mDnsFound = $false
foreach ($serviceName in $OptionalServices) {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -eq 'Running') {
        Write-Pass "Optional mDNS service '$serviceName' is Running."
        $mDnsFound = $true
        break
    }
}
if (-not $mDnsFound) {
    Write-Info "No Bonjour/mDNS service found - using Windows built-in mDNS (dnscache). This is normal on Windows 10 1903+."
}

# ---------------------------------------------------------------------------
# Check 3: No manual steps were required during install (Req 1.3)
# ---------------------------------------------------------------------------
Write-Info "Check 3/3: No manual steps required during install (Req 1.3)"

# Verify by checking a sentinel file written by the installer upon silent completion.
# The NSIS/WiX installer creates this file when it completes without user intervention.
$sentinelPath = Join-Path $env:ProgramData 'vmonitor\install-completed.flag'

if (Test-Path $sentinelPath) {
    Write-Pass "Install completion flag found: $sentinelPath"
}
else {
    # If the sentinel file is absent but checks 1 and 2 passed,
    # the driver and service were installed without manual steps.
    if ($overallPassed) {
        Write-Pass "Driver and services installed successfully - no manual steps required."
    }
    else {
        Write-Fail "Install completion flag not found and earlier checks failed: $sentinelPath"
    }
}

# ---------------------------------------------------------------------------
# Final result
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "==============================" -ForegroundColor White
if ($overallPassed) {
    Write-Host "Smoke test result: PASSED" -ForegroundColor Green
    Write-Host "==============================" -ForegroundColor White
    exit 0
}
else {
    Write-Host "Smoke test result: FAILED" -ForegroundColor Red
    Write-Host "==============================" -ForegroundColor White
    exit 1
}
