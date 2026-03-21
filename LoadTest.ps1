[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [ValidateNotNullOrEmpty()]
    [string]$FunctionUrl,

    [Parameter(Mandatory=$false)]
    [string]$FunctionKey,

    [Parameter(Mandatory=$false)]
    [ValidateRange(1, 200)]
    [int]$ThreadCount = 6,

    [Parameter(Mandatory=$false)]
    [ValidateRange(1, 100000)]
    [int]$RequestsPerThread = 80,

    [Parameter(Mandatory=$false)]
    [ValidateRange(1, 100)]
    [int]$BatchSize = 3,

    [Parameter(Mandatory=$false)]
    [ValidateRange(0, 5000)]
    [int]$ThreadRampUpMs = 500,

    [Parameter(Mandatory=$false)]
    [ValidateRange(0, 5000)]
    [int]$InterRequestDelayMs = 250,

    [Parameter(Mandatory=$false)]
    [ValidateRange(0, 5000)]
    [int]$InterRequestJitterMs = 250,

    [Parameter(Mandatory=$false)]
    [ValidateRange(1, 300)]
    [int]$RequestTimeoutSeconds = 60,

    [Parameter(Mandatory=$false)]
    [ValidateRange(0, 100)]
    [int]$LookupProbabilityPercent = 100,

    [Parameter(Mandatory=$false)]
    [ValidateRange(0, 50)]
    [int]$DuplicateBurstSize = 0,

    [Parameter(Mandatory=$false)]
    [switch]$IncludeNegativeTests,

    [Parameter(Mandatory=$false)]
    [string]$ReportPath,

    [Parameter(Mandatory=$false)]
    [switch]$AbortOnHigh429,

    [Parameter(Mandatory=$false)]
    [ValidateRange(1, 100)]
    [int]$Abort429Percent = 60,

    [Parameter(Mandatory=$false)]
    [ValidateRange(5, 10000)]
    [int]$AbortWindowRequests = 30,

    [Parameter(Mandatory=$false)]
    [ValidateRange(1, 20)]
    [int]$AbortConsecutiveWindows = 2,

    [Parameter(Mandatory=$false)]
    [ValidateSet("Safe", "Normal", "Stress", "Custom")]
    [string]$Profile = "Normal"
)

function Get-Percentile {
    param(
        [double[]]$Values,
        [double]$Percent
    )

    if (-not $Values -or $Values.Count -eq 0) {
        return 0
    }

    $sorted = $Values | Sort-Object
    $index = [Math]::Ceiling(($Percent / 100.0) * $sorted.Count) - 1
    if ($index -lt 0) { $index = 0 }
    if ($index -ge $sorted.Count) { $index = $sorted.Count - 1 }
    return [double]$sorted[$index]
}

function Set-IfNotBound {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][object]$Value
    )

    if (-not $script:PSBoundParameters.ContainsKey($Name)) {
        Set-Variable -Name $Name -Value $Value -Scope Script
    }
}

if ($Profile -ne "Custom") {
    switch ($Profile) {
        "Safe" {
            Set-IfNotBound -Name "ThreadCount" -Value 4
            Set-IfNotBound -Name "RequestsPerThread" -Value 40
            Set-IfNotBound -Name "BatchSize" -Value 2
            Set-IfNotBound -Name "ThreadRampUpMs" -Value 1000
            Set-IfNotBound -Name "InterRequestDelayMs" -Value 500
            Set-IfNotBound -Name "InterRequestJitterMs" -Value 300
            Set-IfNotBound -Name "DuplicateBurstSize" -Value 0
        }
        "Normal" {
            Set-IfNotBound -Name "ThreadCount" -Value 6
            Set-IfNotBound -Name "RequestsPerThread" -Value 80
            Set-IfNotBound -Name "BatchSize" -Value 3
            Set-IfNotBound -Name "ThreadRampUpMs" -Value 500
            Set-IfNotBound -Name "InterRequestDelayMs" -Value 250
            Set-IfNotBound -Name "InterRequestJitterMs" -Value 250
        }
        "Stress" {
            Set-IfNotBound -Name "ThreadCount" -Value 12
            Set-IfNotBound -Name "RequestsPerThread" -Value 120
            Set-IfNotBound -Name "BatchSize" -Value 5
            Set-IfNotBound -Name "ThreadRampUpMs" -Value 100
            Set-IfNotBound -Name "InterRequestDelayMs" -Value 50
            Set-IfNotBound -Name "InterRequestJitterMs" -Value 50
        }
    }
}

# Main script
Write-Host "Load profile: $Profile"
Write-Host "Starting load test with $ThreadCount threads, $RequestsPerThread requests per thread, $BatchSize payloads per request"
Write-Host "Total expected payloads: $($ThreadCount * $RequestsPerThread * $BatchSize)"
Write-Host "Target URL: $FunctionUrl"
Write-Host "Request timeout: $RequestTimeoutSeconds seconds"
Write-Host "Lookup probability: $LookupProbabilityPercent%"
Write-Host "Duplicate burst size: $DuplicateBurstSize"
Write-Host "Thread ramp-up: $ThreadRampUpMs ms"
Write-Host "Inter-request delay: $InterRequestDelayMs ms (+ jitter up to $InterRequestJitterMs ms)"
Write-Host "Abort on sustained 429: $AbortOnHigh429"
if ($AbortOnHigh429) {
    Write-Host "429 abort rule: >= $Abort429Percent% for $AbortConsecutiveWindows consecutive window(s) of $AbortWindowRequests request(s) per thread"
}
Write-Host "Include negative tests: $IncludeNegativeTests"
Write-Host ""

$startTime = Get-Date

# Start parallel jobs
$jobs = @()
for ($threadId = 0; $threadId -lt $ThreadCount; $threadId++) {
    $job = Start-Job -ScriptBlock {
        param($Url, $Key, $ThreadId, $RequestsPerThread, $BatchSize, $RequestTimeoutSeconds, $LookupProbabilityPercent, $DuplicateBurstSize, $IncludeNegativeTests, $InterRequestDelayMs, $InterRequestJitterMs, $AbortOnHigh429, $Abort429Percent, $AbortWindowRequests, $AbortConsecutiveWindows)

        # Function to generate random payload (defined inside job)
        function New-RandomPayload {
            param([int]$Index)

            $accountNames = @("Contoso Corp", "Adventure Works", "Fabrikam Inc", "Northwind Traders", "Tailspin Toys", "Blue Yonder Airlines", "City Power & Light", "Humongous Insurance", "Lucerne Publishing", "Margie's Travel")
            $cities = @("Seattle", "New York", "London", "Tokyo", "Berlin", "Sydney", "Toronto", "Paris", "Amsterdam", "Singapore")
            $firstNames = @("John", "Jane", "Michael", "Sarah", "David", "Emma", "Chris", "Lisa", "Robert", "Anna")
            $lastNames = @("Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez")

            $randomName = $accountNames | Get-Random
            $randomCity = $cities | Get-Random
            $externalId = "EXT-{0:D6}" -f $Index

            # Sometimes add a lookup to contact
            $includeLookup = (Get-Random -Minimum 1 -Maximum 101) -le $LookupProbabilityPercent

            $payload = @{
                EntityLogicalName = "account"
                KeyAttributes = @{
                    accountnumber = $externalId
                }
                Attributes = @{
                    name = "$randomName ($Index)"
                    address1_city = $randomCity
                    description = "Load test account created at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
                }
                SourceSystem = "LoadTest"
            }

            if ($includeLookup) {
                $firstName = $firstNames | Get-Random
                $lastName = $lastNames | Get-Random
                $email = "$firstName.$lastName$Index@example.com".ToLower()

                $payload.Lookups = @{
                    primarycontactid = @{
                        EntityLogicalName = "contact"
                        KeyAttributes = @{
                            emailaddress1 = $email
                        }
                        CreateIfNotExists = $true
                        CreateAttributes = @{
                            firstname = $firstName
                            lastname = $lastName
                            emailaddress1 = $email
                        }
                    }
                }
            }

            return $payload
        }

        # Function to send batch request
        function Send-BatchRequest {
            param(
                [string]$Url,
                [string]$Key,
                [int]$ThreadId,
                [int]$RequestsPerThread,
                [int]$BatchSize,
                [bool]$AbortOnHigh429,
                [int]$Abort429Percent,
                [int]$AbortWindowRequests,
                [int]$AbortConsecutiveWindows
            )

            $results = @()
            $httpClient = [System.Net.Http.HttpClient]::new()
            $httpClient.Timeout = [TimeSpan]::FromSeconds($RequestTimeoutSeconds)
            $windowRequests = 0
            $window429 = 0
            $high429Windows = 0

            try {
                for ($i = 0; $i -lt $RequestsPerThread; $i++) {
                    $payloads = @()

                    # Negative test: send invalid JSON every 50th request
                    $sendInvalid = $IncludeNegativeTests -and (($i + 1) % 50 -eq 0)
                    # Negative test: send type error every 25th request
                    $sendTypeError = $IncludeNegativeTests -and -not $sendInvalid -and (($i + 1) % 25 -eq 0)

                    if (-not $sendInvalid) {
                        for ($j = 0; $j -lt $BatchSize; $j++) {
                            $globalIndex = ($ThreadId * $RequestsPerThread * $BatchSize) + ($i * $BatchSize) + $j
                            $p = New-RandomPayload -Index $globalIndex

                            if ($sendTypeError -and $j -eq 0) {
                                # Inject a type error: put a string where an int is expected
                                $p.Attributes["address1_utcoffset"] = "not-a-number"
                            }

                            $payloads += $p
                        }

                        # Duplicate burst: repeat the first payload N times
                        if ($DuplicateBurstSize -gt 0 -and $payloads.Count -gt 0) {
                            for ($d = 0; $d -lt $DuplicateBurstSize; $d++) {
                                $payloads += $payloads[0]
                            }
                        }
                    }

                    if ($sendInvalid) {
                        $requestBody = '{"Payloads": [{"broken json'
                    } else {
                        $requestBody = @{
                            Payloads = $payloads
                        } | ConvertTo-Json -Depth 10
                    }

                    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $Url)
                    $request.Headers.Add("x-correlation-id", [Guid]::NewGuid().ToString())
                    if (-not [string]::IsNullOrWhiteSpace($Key)) {
                        $request.Headers.Add("x-functions-key", $Key)
                    }

                    $content = [System.Net.Http.StringContent]::new($requestBody, [System.Text.Encoding]::UTF8, "application/json")
                    $request.Content = $content

                    $startTime = Get-Date

                    try {
                        $response = $httpClient.SendAsync($request).GetAwaiter().GetResult()
                        $responseTime = (Get-Date) - $startTime
                        $statusCode = [int]$response.StatusCode

                        $result = @{
                            ThreadId = $ThreadId
                            RequestIndex = $i
                            StatusCode = $statusCode
                            ResponseTime = $responseTime.TotalMilliseconds
                            Success = $response.IsSuccessStatusCode
                            Error = $null
                        }

                        if (-not $response.IsSuccessStatusCode) {
                            $errorContent = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                            $result.Error = $errorContent
                        }
                    }
                    catch {
                        $responseTime = (Get-Date) - $startTime
                        $result = @{
                            ThreadId = $ThreadId
                            RequestIndex = $i
                            StatusCode = $null
                            ResponseTime = $responseTime.TotalMilliseconds
                            Success = $false
                            Error = $_.Exception.Message
                        }
                    }
                    finally {
                        if ($response) { $response.Dispose() }
                        $request.Dispose()
                        $content.Dispose()
                    }

                    $results += $result

                    if ($AbortOnHigh429) {
                        $windowRequests += 1
                        if ($result.StatusCode -eq 429) {
                            $window429 += 1
                        }

                        if ($windowRequests -ge $AbortWindowRequests) {
                            $windowPercent = [math]::Round((100.0 * $window429) / $windowRequests, 2)
                            if ($windowPercent -ge $Abort429Percent) {
                                $high429Windows += 1
                                Write-Warning "Thread $ThreadId high-429 window detected: $window429/$windowRequests ($windowPercent%). Consecutive windows=$high429Windows"
                            }
                            else {
                                $high429Windows = 0
                            }

                            $windowRequests = 0
                            $window429 = 0

                            if ($high429Windows -ge $AbortConsecutiveWindows) {
                                Write-Warning "Thread $ThreadId aborting early due to sustained 429 rate."
                                break
                            }
                        }
                    }

                    if ($InterRequestDelayMs -gt 0 -or $InterRequestJitterMs -gt 0) {
                        $jitter = if ($InterRequestJitterMs -gt 0) { Get-Random -Minimum 0 -Maximum ($InterRequestJitterMs + 1) } else { 0 }
                        Start-Sleep -Milliseconds ($InterRequestDelayMs + $jitter)
                    }

                    # Progress update every 10 requests
                    if (($i + 1) % 10 -eq 0) {
                        Write-Host "Thread $ThreadId completed $(($i + 1) * $BatchSize) payloads"
                    }
                }
            }
            finally {
                $httpClient.Dispose()
            }

            return $results
        }

        # Execute the function
        Send-BatchRequest -Url $Url -Key $Key -ThreadId $ThreadId -RequestsPerThread $RequestsPerThread -BatchSize $BatchSize -AbortOnHigh429 $AbortOnHigh429 -Abort429Percent $Abort429Percent -AbortWindowRequests $AbortWindowRequests -AbortConsecutiveWindows $AbortConsecutiveWindows
    } -ArgumentList $FunctionUrl, $FunctionKey, $threadId, $RequestsPerThread, $BatchSize, $RequestTimeoutSeconds, $LookupProbabilityPercent, $DuplicateBurstSize, $IncludeNegativeTests.IsPresent, $InterRequestDelayMs, $InterRequestJitterMs, $AbortOnHigh429.IsPresent, $Abort429Percent, $AbortWindowRequests, $AbortConsecutiveWindows
    $jobs += $job

    if ($ThreadRampUpMs -gt 0 -and $threadId -lt ($ThreadCount - 1)) {
        Start-Sleep -Milliseconds $ThreadRampUpMs
    }
}

# Wait for all jobs to complete
Write-Host "Waiting for all threads to complete..."
$allResults = @()
foreach ($job in $jobs) {
    $jobResults = Receive-Job -Job $job -Wait
    $allResults += $jobResults
    Remove-Job -Job $job
}

$totalTime = (Get-Date) - $startTime

# Calculate statistics
$totalRequests = $allResults.Count
$totalPayloads = $totalRequests * $BatchSize
$successfulRequests = ($allResults | Where-Object { $_.Success }).Count
$failedRequests = $totalRequests - $successfulRequests
$successRate = if ($totalRequests -gt 0) { ($successfulRequests / $totalRequests) * 100 } else { 0 }

$responseTimes = $allResults | Where-Object { $_.ResponseTime -ne $null } | Select-Object -ExpandProperty ResponseTime
$avgResponseTime = if ($responseTimes.Count -gt 0) { ($responseTimes | Measure-Object -Average).Average } else { 0 }
$minResponseTime = if ($responseTimes.Count -gt 0) { ($responseTimes | Measure-Object -Minimum).Minimum } else { 0 }
$maxResponseTime = if ($responseTimes.Count -gt 0) { ($responseTimes | Measure-Object -Maximum).Maximum } else { 0 }
$p50ResponseTime = Get-Percentile -Values $responseTimes -Percent 50
$p95ResponseTime = Get-Percentile -Values $responseTimes -Percent 95
$p99ResponseTime = Get-Percentile -Values $responseTimes -Percent 99
$requestsPerSecond = if ($totalTime.TotalSeconds -gt 0) { $totalRequests / $totalTime.TotalSeconds } else { 0 }
$payloadsPerSecond = if ($totalTime.TotalSeconds -gt 0) { $totalPayloads / $totalTime.TotalSeconds } else { 0 }

# Display results
Write-Host ""
Write-Host "=== Load Test Results ==="
Write-Host "Total time: $($totalTime.TotalSeconds) seconds"
Write-Host "Total requests: $totalRequests"
Write-Host "Total payloads: $totalPayloads"
Write-Host "Successful requests: $successfulRequests"
Write-Host "Failed requests: $failedRequests"
Write-Host "Success rate: $([math]::Round($successRate, 2))%"
Write-Host "Throughput: $([math]::Round($requestsPerSecond, 2)) req/s, $([math]::Round($payloadsPerSecond, 2)) payloads/s"
Write-Host ""
Write-Host "Response time statistics (ms):"
Write-Host "Average: $([math]::Round($avgResponseTime, 2))"
Write-Host "Minimum: $([math]::Round($minResponseTime, 2))"
Write-Host "Maximum: $([math]::Round($maxResponseTime, 2))"
Write-Host "p50: $([math]::Round($p50ResponseTime, 2))"
Write-Host "p95: $([math]::Round($p95ResponseTime, 2))"
Write-Host "p99: $([math]::Round($p99ResponseTime, 2))"
Write-Host ""

$statusGroups = $allResults |
    Group-Object -Property StatusCode |
    Sort-Object -Property Name

if ($statusGroups.Count -gt 0) {
    Write-Host "Status code breakdown:"
    foreach ($group in $statusGroups) {
        $statusLabel = if ([string]::IsNullOrWhiteSpace([string]$group.Name)) { "(no status)" } else { $group.Name }
        Write-Host "  $statusLabel : $($group.Count)"
    }
    Write-Host ""
}

# Show sample errors if any
$errors = $allResults | Where-Object { -not $_.Success } | Select-Object -First 10
if ($errors.Count -gt 0) {
    Write-Host "Sample errors:"
    foreach ($err in $errors) {
        Write-Host "Thread $($err.ThreadId), Request $($err.RequestIndex): $($err.Error)"
    }
}

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $reportDirectory = Split-Path -Path $ReportPath -Parent
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory) -and -not (Test-Path -Path $reportDirectory)) {
        New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    }

    $allResults | ConvertTo-Json -Depth 10 | Set-Content -Path $ReportPath -Encoding UTF8
    Write-Host ""
    Write-Host "Detailed report written to: $ReportPath"
}

Write-Host ""
Write-Host "Load test completed."