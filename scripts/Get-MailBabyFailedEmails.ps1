#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApiKey,

    [string]$BaseUrl = 'https://api.mailbaby.net',

    # Accepts Unix epoch seconds, DateTime, or a string accepted by the API (for example "2026-04-01").
    [object]$StartDate,

    # Accepts Unix epoch seconds, DateTime, or a string accepted by the API (for example "2026-04-17").
    [object]$EndDate,

    [ValidateRange(1, 10000)]
    [int]$Limit = 1000,

    [ValidateSet('asc', 'desc')]
    [string]$Direction = 'desc',

    [ValidateSet('recipient', 'message')]
    [string]$GroupBy = 'recipient',

    # Optional mail order/account id.
    [int]$Id,

    # Optional additional filters from GET /mail/log.
    [string]$From,
    [string]$To,
    [string]$HeaderFrom,
    [string]$ReplyTo,
    [string]$Subject,
    [string]$MessageId,
    [string]$MailId,
    [string]$Mx,
    [string]$Origin
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Convert-ToMailBabyDateValue {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value
    )

    if ($Value -is [DateTime]) {
        return [int64][DateTimeOffset]::new($Value.ToUniversalTime()).ToUnixTimeSeconds()
    }

    if ($Value -is [int] -or $Value -is [long]) {
        return [int64]$Value
    }

    $text = [string]$Value
    if ($text -match '^\d+$') {
        return [int64]$text
    }

    # Preserve string formats because the API accepts date strings parseable by strtotime.
    return $text
}

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

$headers = @{
    'Accept' = 'application/json'
    'X-API-KEY' = $ApiKey
}

$skip = 0
$total = $null
$allEmails = New-Object System.Collections.Generic.List[object]

while ($true) {
    $query = @{
        delivered = 0
        sort      = 'time'
        dir       = $Direction
        groupby   = $GroupBy
        skip      = $skip
        limit     = $Limit
    }

    if ($PSBoundParameters.ContainsKey('StartDate')) { $query.startDate = (Convert-ToMailBabyDateValue -Value $StartDate) }
    if ($PSBoundParameters.ContainsKey('EndDate')) { $query.endDate = (Convert-ToMailBabyDateValue -Value $EndDate) }
    if ($PSBoundParameters.ContainsKey('Id')) { $query.id = $Id }
    if ($PSBoundParameters.ContainsKey('From')) { $query.from = $From }
    if ($PSBoundParameters.ContainsKey('To')) { $query.to = $To }
    if ($PSBoundParameters.ContainsKey('HeaderFrom')) { $query.headerfrom = $HeaderFrom }
    if ($PSBoundParameters.ContainsKey('ReplyTo')) { $query.replyto = $ReplyTo }
    if ($PSBoundParameters.ContainsKey('Subject')) { $query.subject = $Subject }
    if ($PSBoundParameters.ContainsKey('MessageId')) { $query.messageId = $MessageId }
    if ($PSBoundParameters.ContainsKey('MailId')) { $query.mailid = $MailId }
    if ($PSBoundParameters.ContainsKey('Mx')) { $query.mx = $Mx }
    if ($PSBoundParameters.ContainsKey('Origin')) { $query.origin = $Origin }

    $queryString = New-QueryString -Parameters $query
    $requestUri = "$($BaseUrl.TrimEnd('/'))/mail/log?$queryString"

    Write-Verbose "Requesting: $requestUri"
    $response = Invoke-RestMethod -Method Get -Uri $requestUri -Headers $headers

    if ($null -eq $response.total) {
        throw 'Unexpected API response: missing total field.'
    }

    if ($null -eq $total) {
        $total = [int64]$response.total
        Write-Verbose "Total matched rows: $total"
    }

    $pageEmails = @($response.emails)
    foreach ($email in $pageEmails) {
        $allEmails.Add($email)
    }

    $skip += $Limit

    if ($allEmails.Count -ge $total) {
        break
    }

    if ($pageEmails.Count -eq 0) {
        break
    }
}

$allEmails
