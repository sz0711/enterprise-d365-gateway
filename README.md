# enterprise-d365-gateway
A robust, enterprise-grade Azure Functions integration platform providing seamless data synchronization and transformation between heterogeneous systems and Microsoft Dynamics 365 Customer Engagement.

## Overview
This repository implements:
- HTTP trigger (`DataverseUpsertHttp`) for incoming UPSERT requests in batch format.
- Service Bus trigger (`DataverseUpsertServiceBus`) for reliable queue-driven UPSERT processes.
- Dataverse upsert orchestration via `IOrganizationServiceAsync2` and `Microsoft.PowerPlatform.Dataverse.Client`.
- User-assigned managed identity authentication.
- Concurrent processing with `Parallel.ForEachAsync` and configurable degree-of-parallelism.
- Rate limiting (token bucket), retry with exponential backoff using `Polly`, and transient-fault resilience.
- Early-bound entity conversion via existing `MODEL` assembly (e.g., `MODEL.account`).

## Architecture
- IoC (dependency injection) in `Program.cs`.
- `DataverseOptions` in `FUNC/Models/DataverseOptions.cs`.
- Data contracts in `FUNC/Models/UpsertContracts.cs`.
- `IEarlyboundEntityMapper` / `EarlyboundEntityMapper` for model binding.
- `IDataverseUpsertService` / `DataverseUpsertService` for Dataverse domain logic.
- Function entry points in `FUNC/Functions/HttpUpsertTrigger.cs` and `FUNC/Functions/ServiceBusUpsertTrigger.cs`.

## Requirements
1. .NET 9 SDK (or newer) for building and running.
2. Environment variables / `local.settings.json`:
   - `Dataverse__Url` (Dataverse org URL, e.g. `https://yourorg.crm.dynamics.com`)
   - `Dataverse__UserAssignedManagedIdentityClientId` (managed identity client id, optional)
   - `Dataverse__MaxRequestsPerSecond` (e.g. 300)
   - `Dataverse__MaxDegreeOfParallelism` (e.g. 8)
   - `Dataverse__MaxRetries` (e.g. 4)
   - `ServiceBusConnection` (Azure Service Bus connection string for queue listener)
   - `ServiceBusQueueName` (queue name)

## JSON contract examples
HTTP / ServiceBus payload:
```json
{
  "Payloads": [
    {
      "EntityLogicalName": "account",
      "Id": "00000000-0000-0000-0000-000000000000",
      "Attributes": {
        "name": "Contoso",
        "description": "Enterprise gateway upsert"
      },
      "SourceSystem": "ERP",
      "ExternalIdAttribute": "accountnumber",
      "ExternalIdValue": "ACCT-1001"
    },
    {
      "EntityLogicalName": "account",
      "Attributes": {
        "name": "Adventure Works",
        "address1_city": "Seattle"
      },
      "Lookups": {
        "primarycontactid": {
          "EntityLogicalName": "contact",
          "AlternateKeyAttributes": {
            "emailaddress1": "john.doe@example.com"
          },
          "CreateIfNotExists": true,
          "CreateAttributes": {
            "firstname": "John",
            "lastname": "Doe",
            "emailaddress1": "john.doe@example.com"
          }
        }
      },
      "ExternalIdAttribute": "accountnumber",
      "ExternalIdValue": "ACCT-1002"
    }
  ]
}
```

### Lookup Resolution
The `Lookups` property allows automatic resolution of entity references:
- **AlternateKeyAttributes**: Key-value pairs for alternate key lookup
- **CreateIfNotExists**: If true, creates the referenced entity if not found
- **CreateAttributes**: Attributes to use when creating the referenced entity
- **EntityLogicalName**: The logical name of the referenced entity

Lookups are resolved before the main entity is created/updated, ensuring all references are valid.

## Reliability posture
- Throttling is enforced to avoid exceeding Dataverse-per-second limits.
- Each upsert request uses resilient retry policy with exponential backoff.
- All code paths are async to maximize throughput and avoid thread blocking.
- ServiceBus trigger retries through platform control (Poison queue on fatal errors).
- Key operations support cancellation tokens.

## How to run locally
1. `dotnet build` (requires .NET 8 SDK).
2. `func start` in `FUNC` folder with `local.settings.json` configured.

## Response behavior
- `200 OK`: all payloads processed successfully.
- `400 Bad Request`: only validation failures (for example unknown attributes or type mismatch).
- `500 Internal Server Error`: at least one technical failure occurred.

Validation failures are returned with structured details per payload (`isValidationError`, `validationErrors`).

## Load Testing
A PowerShell script `LoadTest.ps1` (repository root) is provided for load testing the HTTP endpoint:

```powershell
# Basic usage (requires PowerShell 7+)
.\LoadTest.ps1 -FunctionUrl "http://localhost:7071/api/upsert" -FunctionKey "your-function-key"

# Advanced usage with custom parameters
.\LoadTest.ps1 -FunctionUrl "https://your-function.azurewebsites.net/api/upsert" `
              -FunctionKey "your-function-key" `
              -ThreadCount 20 `
              -RequestsPerThread 50 `
              -BatchSize 10
```

Parameters:
- `FunctionUrl`: The URL of the upsert endpoint (required)
- `FunctionKey`: Azure Function key for authentication (optional)
- `ThreadCount`: Number of parallel threads (default: 10)
- `RequestsPerThread`: Number of requests per thread (default: 100)
- `BatchSize`: Number of payloads per request (default: 5)
- `RequestTimeoutSeconds`: Per-request timeout in seconds (default: 60)
- `ReportPath`: Optional JSON output path for detailed raw results

The script generates random account data and reports:
- Success/failure totals and throughput (requests/s, payloads/s)
- Response time stats including p50/p95/p99
- Status code distribution and sample errors

### Report output examples

```powershell
# Save a report with fixed filename
.\LoadTest.ps1 -FunctionUrl "http://localhost:7071/api/upsert" `
              -ThreadCount 10 `
              -RequestsPerThread 100 `
              -BatchSize 5 `
              -ReportPath ".\reports\loadtest-latest.json"

# Save a report with timestamped filename
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportPath = ".\reports\loadtest-$timestamp.json"

.\LoadTest.ps1 -FunctionUrl "http://localhost:7071/api/upsert" `
              -ThreadCount 20 `
              -RequestsPerThread 50 `
              -BatchSize 10 `
              -ReportPath $reportPath
```

## Notes
- This design can scale to millions of requests, within Dataverse limits. Add a queue-throttling layer (Azure Front Door / API Management) for further protection.

