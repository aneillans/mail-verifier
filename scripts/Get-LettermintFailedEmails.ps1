#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TeamToken,

    [string]$BaseUrl = 'https://api.lettermint.co/v1',

    [ValidateRange(1, 200)]
    [int]$PageSize = 100,

    # One or more statuses to pull from message activity.
    [ValidateSet('pending', 'queued', 'suppressed', 'processed', 'delivered', 'opened', 'clicked', 'soft_bounced', 'hard_bounced', 'spam_complaint', 'failed', 'blocked', 'policy_rejected', 'unsubscribed')]
    [string[]]$Statuses = @('failed'),

    [ValidateSet('inbound', 'outbound')]
    [string]$Type = 'outbound',

    [string]$Search,
    [string]$RouteId,
    [string]$DomainId,
    [string]$Tag,
    [string]$FromEmail,
    [string]$Subject,
    [string]$FromDate,
    [string]$ToDate,

    [ValidateSet('type', '-type', 'status', '-status', 'from_email', '-from_email', 'subject', '-subject', 'created_at', '-created_at', 'status_changed_at', '-status_changed_at')]
    [string]$Sort = '-created_at',

    # Optional output path. Use .csv for CSV; any other extension writes JSON.
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function New-QueryString {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Parameters
    )

    $pairs = foreach ($entry in $Parameters.GetEnumerator()) {
        if ($null -eq $entry.Value) {
            continue
        }

        $name = [Uri]::EscapeDataString([string]$entry.Key)
        $value = [Uri]::EscapeDataString([string]$entry.Value)
        "$name=$value"
    }

    return ($pairs -join '&')
}

function Join-RecipientEmails {
    param(
        [AllowNull()]
        [object]$Recipients
    )

    if ($null -eq $Recipients) {
        return ''
    }

    $emails = @($Recipients | ForEach-Object { $_.email } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    return ($emails -join ';')
}

function Invoke-LettermintGet {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [Parameter(Mandatory = $true)]
        [hashtable]$Headers
    )

    try {
        # Force HTTP/1.1 to avoid occasional HTTP/2 framing errors in some environments.
        return Invoke-RestMethod -Method Get -Uri $Uri -Headers $Headers -HttpVersion '1.1'
    }
    catch {
        $curlPath = Get-Command curl -ErrorAction SilentlyContinue
        if ($null -eq $curlPath) {
            throw
        }

        Write-Verbose 'Invoke-RestMethod failed; retrying with curl fallback.'

        $authHeader = [string]$Headers['Authorization']
        $acceptHeader = [string]$Headers['Accept']

        $responseJson = & $curlPath.Source --silent --show-error --fail --http1.1 `
            -H "Authorization: $authHeader" `
            -H "Accept: $acceptHeader" `
            "$Uri"

        return ($responseJson | ConvertFrom-Json)
    }
}

$headers = @{
    'Accept' = 'application/json'
    'Authorization' = "Bearer $TeamToken"
    'User-Agent' = 'mail-verifier/lettermint-script'
}

# Deduplicate across multiple requested statuses.
$messagesById = @{}

foreach ($status in $Statuses) {
    $cursor = $null

    while ($true) {
        $query = @{
            'page[size]' = $PageSize
            'sort' = $Sort
            'filter[type]' = $Type
            'filter[status]' = $status
        }

        if ($PSBoundParameters.ContainsKey('Search')) { $query['filter[search]'] = $Search }
        if ($PSBoundParameters.ContainsKey('RouteId')) { $query['filter[route_id]'] = $RouteId }
        if ($PSBoundParameters.ContainsKey('DomainId')) { $query['filter[domain_id]'] = $DomainId }
        if ($PSBoundParameters.ContainsKey('Tag')) { $query['filter[tag]'] = $Tag }
        if ($PSBoundParameters.ContainsKey('FromEmail')) { $query['filter[from_email]'] = $FromEmail }
        if ($PSBoundParameters.ContainsKey('Subject')) { $query['filter[subject]'] = $Subject }
        if ($PSBoundParameters.ContainsKey('FromDate')) { $query['filter[from_date]'] = $FromDate }
        if ($PSBoundParameters.ContainsKey('ToDate')) { $query['filter[to_date]'] = $ToDate }
        if ($null -ne $cursor -and $cursor -ne '') { $query['page[cursor]'] = $cursor }

        $queryString = New-QueryString -Parameters $query
        $requestUri = "$($BaseUrl.TrimEnd('/'))/messages?$queryString"

        Write-Verbose "Requesting: $requestUri"
        $response = Invoke-LettermintGet -Uri $requestUri -Headers $headers

        if ($null -eq $response -or $null -eq $response.data) {
            break
        }

        foreach ($message in @($response.data)) {
            if ($null -eq $message.id) {
                continue
            }

            $messagesById[[string]$message.id] = [pscustomobject]@{
                id         = $message.id
                type       = $message.type
                status     = $message.status
                from_email = $message.from_email
                from_name  = $message.from_name
                subject    = $message.subject
                to_emails  = (Join-RecipientEmails -Recipients $message.to)
                cc_emails  = (Join-RecipientEmails -Recipients $message.cc)
                bcc_emails = (Join-RecipientEmails -Recipients $message.bcc)
                tag        = $message.tag
                created_at = $message.created_at
            }
        }

        if ($null -eq $response.next_cursor -or [string]::IsNullOrWhiteSpace([string]$response.next_cursor)) {
            break
        }

        $cursor = [string]$response.next_cursor
    }
}

$results = @($messagesById.Values)

# Keep newest first if created_at is populated.
$results = $results | Sort-Object -Property created_at -Descending

if ($PSBoundParameters.ContainsKey('OutputPath')) {
    $outputDir = Split-Path -Path $OutputPath -Parent
    if (-not [string]::IsNullOrWhiteSpace($outputDir) -and -not (Test-Path -Path $outputDir)) {
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    }

    if ([IO.Path]::GetExtension($OutputPath).ToLowerInvariant() -eq '.csv') {
        $results | Export-Csv -Path $OutputPath -NoTypeInformation
    }
    else {
        $results | ConvertTo-Json -Depth 8 | Set-Content -Path $OutputPath
    }

    Write-Host "Wrote $($results.Count) message(s) to $OutputPath"
}
else {
    $results
}
