<#
.SYNOPSIS
    Publishes DOC2MD and builds a dependency-complete Inno Setup installer.

.DESCRIPTION
    Creates framework-dependent Windows x64 executables, stages a portable
    Python/MarkItDown runtime, English and Polish OCR data, and LibreOffice,
    validates the staged payload, then compiles a versioned installer.

    The generated installer intentionally does not include the .NET runtime.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string] $Runtime = 'win-x64',

    [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')]
    [string] $Version = '1.0.0',

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $PythonVersion = '3.12.10',

    [string] $LibreOfficePath = '',

    [string] $IsccPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$buildRoot = Join-Path $artifactsRoot 'installer-build'
$payloadDirectory = Join-Path $buildRoot 'payload'
$cacheDirectory = Join-Path $artifactsRoot 'cache'
$outputDirectory = Join-Path $artifactsRoot 'installer'
$installerDefinition = Join-Path $PSScriptRoot 'DOC2MD.iss'
$markItDownPackage = Join-Path $repositoryRoot 'lib\packages\markitdown'

function Reset-GeneratedDirectory
{
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $AllowedRoot
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($AllowedRoot)
    $rootPrefix = $fullRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to reset generated directory outside '$fullRoot': '$fullPath'."
    }

    if (Test-Path -LiteralPath $fullPath)
    {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

function Invoke-CheckedCommand
{
    param(
        [Parameter(Mandatory)]
        [string] $Description,

        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [string[]] $ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0)
    {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Get-CachedDownload
{
    param(
        [Parameter(Mandatory)]
        [uri] $Uri,

        [Parameter(Mandatory)]
        [string] $Destination,

        [long] $MinimumBytes = 1
    )

    if ((Test-Path -LiteralPath $Destination -PathType Leaf) -and
        (Get-Item -LiteralPath $Destination).Length -ge $MinimumBytes)
    {
        return
    }

    Write-Host "Downloading $Uri" -ForegroundColor Cyan
    Invoke-WebRequest -Uri $Uri -OutFile $Destination

    $download = Get-Item -LiteralPath $Destination
    if ($download.Length -lt $MinimumBytes)
    {
        Remove-Item -LiteralPath $Destination -Force
        throw "The download from '$Uri' was unexpectedly small ($($download.Length) bytes)."
    }
}

function Resolve-InnoSetupCompiler
{
    param([string] $ExplicitPath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath))
    {
        $candidates += $ExplicitPath
    }

    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command)
    {
        $candidates += $command.Source
    }

    $candidates += @(
        'C:\Program Files\Inno Setup 7\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files (x86)\Inno Setup\ISCC.exe'
    )

    foreach ($candidate in $candidates | Select-Object -Unique)
    {
        if (Test-Path -LiteralPath $candidate -PathType Leaf)
        {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    throw 'ISCC.exe was not found. Install Inno Setup, add ISCC.exe to PATH, or pass -IsccPath.'
}

function Resolve-LibreOfficeRoot
{
    param([string] $ExplicitPath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath))
    {
        $candidates += $ExplicitPath
    }

    $candidates += @(
        (Join-Path $env:ProgramW6432 'LibreOffice'),
        (Join-Path $env:ProgramFiles 'LibreOffice'),
        (Join-Path ${env:ProgramFiles(x86)} 'LibreOffice')
    )

    foreach ($candidate in $candidates)
    {
        if ([string]::IsNullOrWhiteSpace($candidate))
        {
            continue
        }

        $candidatePath = [System.IO.Path]::GetFullPath($candidate)
        if ([System.IO.Path]::GetFileName($candidatePath).Equals('soffice.exe', [System.StringComparison]::OrdinalIgnoreCase))
        {
            $candidatePath = Split-Path -Parent (Split-Path -Parent $candidatePath)
        }

        if (Test-Path -LiteralPath (Join-Path $candidatePath 'program\soffice.exe') -PathType Leaf)
        {
            return $candidatePath
        }
    }

    throw 'LibreOffice was not found. Install LibreOffice or pass -LibreOfficePath with its installation root.'
}

function Publish-Project
{
    param(
        [Parameter(Mandatory)]
        [string] $ProjectPath
    )

    $arguments = @(
        'publish', $ProjectPath,
        '--nologo',
        '--configuration', $Configuration,
        '--runtime', $Runtime,
        '--self-contained', 'false',
        '--output', $payloadDirectory,
        "-p:Version=$Version",
        "-p:FileVersion=$script:versionInfoVersion",
        "-p:AssemblyVersion=$script:versionInfoVersion",
        '-p:UseAppHost=true',
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        '-p:DebugSymbols=false',
        '-p:DebugType=None',
        '-p:ContinuousIntegrationBuild=true',
        '-p:Deterministic=true'
    )

    Invoke-CheckedCommand -Description "Publishing '$ProjectPath'" -FilePath 'dotnet' -ArgumentList $arguments
}

function Assert-File
{
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        throw "$Description was not produced: '$Path'."
    }
}

New-Item -ItemType Directory -Path $artifactsRoot, $cacheDirectory, $outputDirectory -Force | Out-Null
Reset-GeneratedDirectory -Path $buildRoot -AllowedRoot $artifactsRoot

$versionParts = @($Version -split '\.')
while ($versionParts.Count -lt 4)
{
    $versionParts += '0'
}

$versionInfoVersion = $versionParts[0..3] -join '.'
$outputBaseFilename = "DOC2MD-$Version-$Runtime-Setup"
$expectedInstaller = Join-Path $outputDirectory "$outputBaseFilename.exe"

Write-Host 'Publishing DOC2MD .NET entry points (framework-dependent)...' -ForegroundColor Cyan
foreach ($project in @(
    'src\DOC2MD.Cli\DOC2MD.Cli.csproj',
    'src\DOC2MD.Gui\DOC2MD.Gui.csproj',
    'src\DOC2MD.Api\DOC2MD.Api.csproj',
    'src\DOC2MD.Mcp\DOC2MD.Mcp.csproj'
))
{
    Publish-Project -ProjectPath (Join-Path $repositoryRoot $project)
}

foreach ($executable in @('DOC2MD.Cli.exe', 'DOC2MD.Gui.exe', 'DOC2MD.Api.exe', 'DOC2MD.Mcp.exe'))
{
    Assert-File -Path (Join-Path $payloadDirectory $executable) -Description $executable
}

Write-Host 'Staging the portable Python and MarkItDown runtime...' -ForegroundColor Cyan
$pythonArchive = Join-Path $cacheDirectory "python-$PythonVersion-embed-amd64.zip"
$pythonUri = [uri] "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip"
Get-CachedDownload -Uri $pythonUri -Destination $pythonArchive -MinimumBytes 5MB

$pythonRoot = Join-Path $payloadDirectory '.markitdown-venv\Scripts'
New-Item -ItemType Directory -Path $pythonRoot -Force | Out-Null
Expand-Archive -LiteralPath $pythonArchive -DestinationPath $pythonRoot -Force

$pythonPathFile = Get-ChildItem -LiteralPath $pythonRoot -Filter 'python*._pth' | Select-Object -First 1
if ($null -eq $pythonPathFile)
{
    throw 'The embeddable Python archive did not contain a python*._pth file.'
}

$pythonZip = Get-ChildItem -LiteralPath $pythonRoot -Filter 'python*.zip' | Select-Object -First 1
if ($null -eq $pythonZip)
{
    throw 'The embeddable Python archive did not contain its standard-library zip.'
}

@(
    $pythonZip.Name,
    '.',
    'Lib\site-packages',
    'import site'
) | Set-Content -LiteralPath $pythonPathFile.FullName -Encoding ASCII

$pythonExecutable = Join-Path $pythonRoot 'python.exe'
$sitePackages = Join-Path $pythonRoot 'Lib\site-packages'
New-Item -ItemType Directory -Path $sitePackages -Force | Out-Null

$getPipPath = Join-Path $cacheDirectory 'get-pip.py'
Get-CachedDownload -Uri ([uri] 'https://bootstrap.pypa.io/get-pip.py') -Destination $getPipPath -MinimumBytes 1MB
Invoke-CheckedCommand -Description 'Bootstrapping pip' -FilePath $pythonExecutable -ArgumentList @(
    $getPipPath,
    '--disable-pip-version-check',
    '--no-warn-script-location'
)

$markItDownSpecifier = "$markItDownPackage[all]"
Invoke-CheckedCommand -Description 'Installing the MarkItDown package build backend' -FilePath $pythonExecutable -ArgumentList @(
    '-m', 'pip', 'install',
    '--disable-pip-version-check',
    '--no-cache-dir',
    '--no-warn-script-location',
    '--upgrade',
    '--target', $sitePackages,
    'hatchling'
)
Invoke-CheckedCommand -Description 'Installing MarkItDown and all optional Python libraries' -FilePath $pythonExecutable -ArgumentList @(
    '-m', 'pip', 'install',
    '--disable-pip-version-check',
    '--no-cache-dir',
    '--no-warn-script-location',
    '--no-build-isolation',
    '--upgrade',
    '--target', $sitePackages,
    $markItDownSpecifier
)

$vendoredMarkItDown = Join-Path $payloadDirectory 'lib\packages\markitdown'
New-Item -ItemType Directory -Path $vendoredMarkItDown -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $markItDownPackage 'src') -Destination $vendoredMarkItDown -Recurse -Force
Copy-Item -LiteralPath (Join-Path $markItDownPackage 'README.md') -Destination $vendoredMarkItDown -Force
Copy-Item -LiteralPath (Join-Path $markItDownPackage 'ThirdPartyNotices.md') -Destination $vendoredMarkItDown -Force

$previousPythonPath = $env:PYTHONPATH
try
{
    $env:PYTHONPATH = Join-Path $vendoredMarkItDown 'src'
    Invoke-CheckedCommand -Description 'Checking the portable Python dependency graph' -FilePath $pythonExecutable -ArgumentList @('-m', 'pip', 'check')
    Invoke-CheckedCommand -Description 'Importing the packaged document conversion libraries' -FilePath $pythonExecutable -ArgumentList @(
        '-c',
        'from markitdown import MarkItDown; import bs4, requests, markdownify, magika, charset_normalizer, defusedxml, pptx, mammoth, pandas, openpyxl, xlrd, lxml, pdfminer, pdfplumber, olefile, pydub, speech_recognition, youtube_transcript_api; from azure.ai.documentintelligence import DocumentIntelligenceClient'
    )
}
finally
{
    $env:PYTHONPATH = $previousPythonPath
}

Write-Host 'Staging English and Polish Tesseract OCR models...' -ForegroundColor Cyan
$tessdataDirectory = Join-Path $payloadDirectory 'tessdata'
New-Item -ItemType Directory -Path $tessdataDirectory -Force | Out-Null
foreach ($language in @('eng', 'pol'))
{
    $modelCachePath = Join-Path $cacheDirectory "$language.traineddata"
    $modelUri = [uri] "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/$language.traineddata"
    Get-CachedDownload -Uri $modelUri -Destination $modelCachePath -MinimumBytes 1MB
    Copy-Item -LiteralPath $modelCachePath -Destination (Join-Path $tessdataDirectory "$language.traineddata") -Force
}

Write-Host 'Staging the complete LibreOffice runtime...' -ForegroundColor Cyan
$libreOfficeSource = Resolve-LibreOfficeRoot -ExplicitPath $LibreOfficePath
$libreOfficeDestination = Join-Path $payloadDirectory 'runtime\libreoffice'
New-Item -ItemType Directory -Path $libreOfficeDestination -Force | Out-Null
$libreOfficeItems = Get-ChildItem -LiteralPath $libreOfficeSource -Force
Copy-Item -LiteralPath $libreOfficeItems.FullName -Destination $libreOfficeDestination -Recurse -Force

$stagedSoffice = Join-Path $libreOfficeDestination 'program\soffice.exe'
Assert-File -Path $stagedSoffice -Description 'LibreOffice soffice.exe'
Invoke-CheckedCommand -Description 'Starting the staged LibreOffice runtime' -FilePath $stagedSoffice -ArgumentList @('--headless', '--version')

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $payloadDirectory -Force

Write-Host 'Smoke-testing the staged DOC2MD conversion path...' -ForegroundColor Cyan
$smokeInput = Join-Path $buildRoot 'smoke-input.txt'
$smokeOutput = Join-Path $buildRoot 'smoke-output.md'
'DOC2MD installer dependency smoke test.' | Set-Content -LiteralPath $smokeInput -Encoding UTF8
$cliExecutable = Join-Path $payloadDirectory 'DOC2MD.Cli.exe'
Invoke-CheckedCommand -Description 'DOC2MD staged text conversion' -FilePath $cliExecutable -ArgumentList @(
    'convert', '--input', $smokeInput, '--output', $smokeOutput, '--overwrite', '--json'
)
Assert-File -Path $smokeOutput -Description 'The Markdown smoke-test output'

$ocrInput = Join-Path $repositoryRoot 'lib\packages\markitdown-ocr\tests\ocr_test_data\pdf_scanned_minimal.pdf'
$ocrOutput = Join-Path $buildRoot 'ocr-smoke-output.md'
Invoke-CheckedCommand -Description 'DOC2MD English and Polish OCR conversion' -FilePath $cliExecutable -ArgumentList @(
    'convert',
    '--input', $ocrInput,
    '--output', $ocrOutput,
    '--overwrite',
    '--pdf-processing', 'local',
    '--ocr-languages', 'eng+pol',
    '--tessdata', $tessdataDirectory,
    '--json'
)
Assert-File -Path $ocrOutput -Description 'The local OCR smoke-test output'

$legacyInput = Join-Path $buildRoot 'legacy-smoke-input.rtf'
$legacyOutput = Join-Path $buildRoot 'legacy-smoke-output.md'
'{\rtf1\ansi\deff0 {\fonttbl {\f0 Arial;}}\f0\fs24 DOC2MD bundled LibreOffice smoke test.\par}' |
    Set-Content -LiteralPath $legacyInput -Encoding ASCII
Invoke-CheckedCommand -Description 'DOC2MD bundled LibreOffice conversion' -FilePath $cliExecutable -ArgumentList @(
    'convert', '--input', $legacyInput, '--output', $legacyOutput, '--overwrite', '--json'
)
Assert-File -Path $legacyOutput -Description 'The LibreOffice smoke-test output'

$iscc = Resolve-InnoSetupCompiler -ExplicitPath $IsccPath
Write-Host "Compiling the installer with '$iscc'..." -ForegroundColor Cyan
$compilerArguments = @(
    "/DPayloadDir=$payloadDirectory",
    "/DOutputDir=$outputDirectory",
    "/DAppVersion=$Version",
    "/DVersionInfoVersion=$versionInfoVersion",
    "/DOutputBaseFilename=$outputBaseFilename",
    $installerDefinition
)
Invoke-CheckedCommand -Description 'Inno Setup compilation' -FilePath $iscc -ArgumentList $compilerArguments

Assert-File -Path $expectedInstaller -Description 'The DOC2MD installer'
$installer = Get-Item -LiteralPath $expectedInstaller
Write-Host 'Installer created successfully.' -ForegroundColor Green
Write-Host "Artifact: $($installer.FullName)"
Write-Host "Size: $([Math]::Round($installer.Length / 1MB, 1)) MB"
