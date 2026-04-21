#!/usr/bin/env pwsh
<#
.SYNOPSIS
Splits a large CSV file into smaller chunks by size while preserving the header row.

.PARAMETER InputFile
Path to the CSV file to split.

.PARAMETER ChunkSizeMb
Size of each chunk in megabytes (default: 1).

.PARAMETER OutputDirectory
Directory where chunk files will be saved (default: same as input file).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InputFile,

    [ValidateRange(1, 1024)]
    [int]$ChunkSizeMb = 1,

    [string]$OutputDirectory = (Split-Path -Parent $InputFile)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -PathType Leaf $InputFile)) {
    Write-Error "Input file not found: $InputFile"
    exit 1
}

$chunkSizeBytes = $ChunkSizeMb * 1MB
$fileName = [System.IO.Path]::GetFileNameWithoutExtension($InputFile)
$fileExtension = [System.IO.Path]::GetExtension($InputFile)

Write-Host "Splitting '$InputFile' into ${ChunkSizeMb}MB chunks..."
Write-Host "Output directory: $OutputDirectory"

# Ensure output directory exists
if (-not (Test-Path -PathType Container $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

# Read the header row
$headerLine = $null
$lineCount = 0
$chunkNumber = 1
$currentChunkSize = 0
$currentChunkLines = @()

$reader = $null
try {
    $reader = [System.IO.File]::OpenText($InputFile)
    $headerLine = $reader.ReadLine()
    $headerSizeBytes = [System.Text.Encoding]::UTF8.GetByteCount($headerLine) + 2  # +2 for CRLF

    while ($null -ne ($line = $reader.ReadLine())) {
        $lineCount++
        $lineSizeBytes = [System.Text.Encoding]::UTF8.GetByteCount($line) + 2  # +2 for CRLF

        # If adding this line exceeds chunk size and we have data, write current chunk
        if ($currentChunkSize -gt 0 -and ($currentChunkSize + $lineSizeBytes) -gt $chunkSizeBytes) {
            $outputFile = Join-Path $OutputDirectory "${fileName}_part_${chunkNumber}${fileExtension}"
            Write-Host "  Writing part $chunkNumber ($($currentChunkLines.Count) rows, $(([math]::Round($currentChunkSize / 1MB, 2)))MB)..."

            @($headerLine) + $currentChunkLines | Set-Content -Path $outputFile -Encoding UTF8
            
            $chunkNumber++
            $currentChunkSize = $headerSizeBytes
            $currentChunkLines = @()
        }

        $currentChunkLines += $line
        $currentChunkSize += $lineSizeBytes
    }

    # Write final chunk
    if ($currentChunkLines.Count -gt 0) {
        $outputFile = Join-Path $OutputDirectory "${fileName}_part_${chunkNumber}${fileExtension}"
        Write-Host "  Writing part $chunkNumber ($($currentChunkLines.Count) rows, $(([math]::Round($currentChunkSize / 1MB, 2)))MB)..."
        @($headerLine) + $currentChunkLines | Set-Content -Path $outputFile -Encoding UTF8
    }

    Write-Host ""
    Write-Host "Split complete!"
    Write-Host "Total lines processed: $lineCount"
    Write-Host "Total chunks created: $chunkNumber"
}
catch {
    Write-Error "Error splitting file: $_"
    exit 1
}
finally {
    if ($null -ne $reader) {
        $reader.Dispose()
    }
}
