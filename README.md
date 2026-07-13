# 🏢 enterprise-d365-gateway

> Enterprise-grade Azure Functions integration platform — seamless data synchronization between heterogeneous systems and Microsoft Dynamics 365 Customer Engagement.

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](#license)

---

## Overview

Azure Functions host exposing **HTTP triggers** and a **Service Bus trigger** that accept batched UPSERT payloads and write them to Microsoft Dataverse, with resilience, concurrency control, caching, and early-bound entity validation built in. Includes a dedicated **SAP integration endpoint** demonstrating manual payload mapping.

### Key Capabilities

- **SOLID-decomposed pipeline** — 10 interfaces, 12 services, thin `UpsertOrchestrator`
- **HTTP + Service Bus triggers** — batch UPSERT via `POST /api/upsert` or queue-driven processing
- **SAP integration** — `POST /api/sap/account-with-contacts` with manual mapping (`ISapAccountMapper`)
- **Recursive lookup resolution** — configurable depth, cycle detection, `CreateIfNotExists`
- **In-memory cache** — KeyAttributes→Guid with sliding/absolute TTL and RAM-based size limit
- **Polly v8 resilience** — retry → circuit breaker → timeout pipeline for every Dataverse call
- **Keyed concurrency control** — `SemaphoreSlim(1,1)` per normalized `KeyAttributes` signature
- **Adaptive concurrency (AIMD)** — halve on 429, increment after sustained success
- **Rate limiting** — token-bucket per Dataverse API call (`MaxRequestsPerSecond`)
- **Early-bound validation** — `MODEL` assembly reflection for entity/attribute mapping
- **Health endpoints** — `/health/live` (liveness) + `/health/ready` (Dataverse readiness)
- **Input guards** — `MaxRequestBytes` (413), `MaxBatchItems` (400), JSON depth limit
- **Distributed tracing** — `ActivitySource` spans and structured events in all triggers
- **Startup validation** — HTTPS scheme, `[Range]` annotations, custom rules via `ValidateOnStart`
- **Plugin step bypass** — `BypassBusinessLogicExecutionStepIds` per entity for bulk operations

---

## Architecture

```
FUNC/
├── Extensions/          → DI registration (DataverseServiceCollectionExtensions.cs)
├── Functions/           → HTTP + ServiceBus + Health + SAP triggers
├── Interfaces/          → 10 service interfaces
├── Models/              → DataverseOptions, UpsertContracts, SapContracts, ErrorCategory
├── Services/            → 12 service implementations
└── Program.cs           → Host builder + DI + startup validation
MODEL/
├── Model/Entities/      → Early-bound entity classes (account, contact)
├── Model/OptionSets/    → OptionSet enums
└── Scripts/             → Code generation scripts
TESTS/
├── Unit/                → 118 unit tests (all 12 services)
└── Integration/         → 35 integration tests (FakeXrmEasy, triggers, DI)
```

**12 services** · **10 interfaces** · **153 tests** (118 unit + 35 integration) · **0 warnings**

### Pipeline

```
Request → IRequestValidator → IEarlyboundEntityMapper → ILookupResolver → IExternalIdResolver → IEntityUpsertExecutor → IResultMapper → Response
```

### Core Services

| Interface | Responsibility |
|-----------|----------------|
| `IRequestValidator` | Payload + structural validation (KeyAttributes, types, nested lookups) |
| `IEarlyboundEntityMapper` | Entity mapping from JSON to SDK Entity via MODEL reflection |
| `IExternalIdResolver` | KeyAttributes→Guid resolution with cache-first, invalidation on failure |
| `ILookupResolver` | Recursive lookup resolution with cycle detection + depth limits |
| `IEntityUpsertExecutor` | Dataverse Create/Update/Query wrapped in Polly v8 + rate limiter |
| `IAdaptiveConcurrencyLimiter` | AIMD concurrency: halve on throttle, increment on sustained success |
| `IUpsertLockCoordinator` | Keyed `SemaphoreSlim(1,1)` per normalized KeyAttributes signature |
| `IErrorClassifier` | Exception → `ErrorCategory` mapping |
| `IResultMapper` | UpsertResult construction + batch HTTP status code determination |
| `IHealthCheckService` | Liveness + readiness health checks (Dataverse connectivity) |
| `ISapAccountMapper` | SAP→Dataverse manual payload mapping (Account + Contacts) |
| `UpsertOrchestrator` | Thin pipeline coordinating all above services |

---

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download) (8.0+)
- [Azure Functions Core Tools](https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-local) v4+
- Dataverse organization URL and (optionally) a user-assigned managed identity

### Build & Test

```bash
dotnet build enterprise-d365-gateway.sln
dotnet test  TESTS/
```

### Run Locally

1. Create `FUNC/local.settings.json` (see [Configuration](#configuration) below).
2. Start the host:

```bash
func start --prefix FUNC
```

3. Send a test UPSERT request:

```bash
curl -X POST http://localhost:7071/api/upsert \
     -H "Content-Type: application/json" \
     -d '{"Payloads":[{"EntityLogicalName":"account","KeyAttributes":{"accountnumber":"ACCT-1001"},"Attributes":{"name":"Contoso"}}]}'
```

---

## Configuration

Add the following to `FUNC/local.settings.json`:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "Dataverse:Url": "https://your-org.crm4.dynamics.com/",
    "Dataverse:UserAssignedManagedIdentityClientId": "",
    "Dataverse:MaxRequestsPerSecond": "120",
    "Dataverse:MaxDegreeOfParallelism": "4",
    "Dataverse:MinDegreeOfParallelism": "1",
    "Dataverse:AdaptiveConcurrencySuccessThreshold": "20",
    "Dataverse:MaxRetries": "4",
    "Dataverse:RetryBaseDelayMs": "300",
    "Dataverse:RateLimitRetryDelaySeconds": "180",
    "Dataverse:TimeoutPerOperationSeconds": "45",
    "Dataverse:CircuitBreakerFailureThreshold": "8",
    "Dataverse:CircuitBreakerSamplingDurationSeconds": "60",
    "Dataverse:CircuitBreakerBreakDurationSeconds": "45",
    "Dataverse:CacheMemoryBudgetPercent": "20",
    "Dataverse:CacheMemoryBudgetMinMb": "64",
    "Dataverse:CacheMemoryBudgetMaxMb": "512",
    "Dataverse:CacheEntrySizeBytes": "128",
    "Dataverse:MaxBatchItems": "1000",
    "Dataverse:MaxRequestBytes": "10485760",
    "Dataverse:BypassPluginStepIds:account": "",
    "Dataverse:BypassPluginStepIds:contact": "",
    "ServiceBusConnection": "Endpoint=sb://your-servicebus-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=YOUR_KEY",
    "ServiceBusQueueName": "dataverse-upsert-queue",
    "APPLICATIONINSIGHTS_CONNECTION_STRING": ""
  },
  "Host": {
    "LocalHttpPort": 7071,
    "CORS": "*",
    "CORSCredentials": false
  }
}
```

### Configuration Reference

| Key | Description | Default |
|-----|-------------|---------|
| `Dataverse__Url` | Dataverse org URL | *(required)* |
| `Dataverse__UserAssignedManagedIdentityClientId` | Managed identity client ID | *(optional)* |
| `Dataverse__MaxRequestsPerSecond` | Token-bucket rate limit | `300` |
| `Dataverse__MaxDegreeOfParallelism` | Max parallel batch processing | `8` |
| `Dataverse__MinDegreeOfParallelism` | Floor for adaptive concurrency | `1` |
| `Dataverse__AdaptiveConcurrencySuccessThreshold` | Consecutive successes before parallelism +1 | `20` |
| `Dataverse__MaxRetries` | Retry attempts per operation | `4` |
| `Dataverse__RetryBaseDelayMs` | Base delay for exponential backoff | `200` |
| `Dataverse__RateLimitRetryDelaySeconds` | Fixed cooldown for 429 retries | `300` |
| `Dataverse__TimeoutPerOperationSeconds` | Timeout per Dataverse call | `30` |
| `Dataverse__CircuitBreakerFailureThreshold` | Min throughput before evaluating failures | `10` |
| `Dataverse__CircuitBreakerSamplingDurationSeconds` | Sampling window for circuit breaker | `60` |
| `Dataverse__CircuitBreakerBreakDurationSeconds` | Duration circuit stays open | `30` |
| `Dataverse__CacheSlidingExpirationMinutes` | Cache sliding TTL | `120` (2 h) |
| `Dataverse__CacheAbsoluteExpirationMinutes` | Cache absolute TTL (hard upper bound) | `360` (6 h) |
| `Dataverse__CacheMemoryBudgetPercent` | % of available RAM for cache | `20` |
| `Dataverse__CacheMemoryBudgetMinMb` | Lower bound for cache `SizeLimit` | `64` |
| `Dataverse__CacheMemoryBudgetMaxMb` | Upper bound for cache `SizeLimit` | `512` |
| `Dataverse__CacheEntrySizeBytes` | Estimated size per cache entry | `128` |
| `Dataverse__MaxLookupDepth` | Global max recursive lookup depth | `3` |
| `Dataverse__LookupTimeoutSeconds` | Timeout budget per lookup tree (enforced at depth-0) | `60` |
| `Dataverse__MaxBatchItems` | Maximum payloads per request | `1000` |
| `Dataverse__MaxRequestBytes` | Maximum request body size | `10485760` (10 MB) |
| `Dataverse__BypassPluginStepIds__<entity>` | Comma-separated plugin step GUIDs to bypass | *(empty)* |
| `ServiceBusConnection` | Azure Service Bus connection string | *(required for SB trigger)* |
| `ServiceBusQueueName` | Queue name | *(required for SB trigger)* |

---

## JSON Contract

### Simple Upsert

```json
{
  "Payloads": [
    {
      "EntityLogicalName": "account",
      "KeyAttributes": { "accountnumber": "ACCT-1001" },
      "Attributes": { "name": "Contoso", "description": "Enterprise gateway upsert" },
      "SourceSystem": "ERP"
    }
  ]
}
```

### Single Lookup (CreateIfNotExists)

```json
{
  "Payloads": [
    {
      "EntityLogicalName": "account",
      "KeyAttributes": { "accountnumber": "ACCT-1002" },
      "Attributes": { "name": "Adventure Works", "address1_city": "Seattle" },
      "Lookups": {
        "primarycontactid": {
          "EntityLogicalName": "contact",
          "KeyAttributes": { "emailaddress1": "john.doe@example.com" },
          "CreateIfNotExists": true,
          "CreateAttributes": {
            "firstname": "John",
            "lastname": "Doe",
            "emailaddress1": "john.doe@example.com"
          }
        }
      }
    }
  ]
}
```

### Recursive Lookup (Nested 2 Levels)

```json
{
  "Payloads": [
    {
      "EntityLogicalName": "account",
      "KeyAttributes": { "accountnumber": "ACCT-2000" },
      "Attributes": { "name": "Recursive Corp" },
      "Lookups": {
        "primarycontactid": {
          "EntityLogicalName": "contact",
          "KeyAttributes": { "emailaddress1": "nested@example.com" },
          "CreateIfNotExists": true,
          "CreateAttributes": {
            "firstname": "Nested",
            "lastname": "Contact",
            "emailaddress1": "nested@example.com"
          },
          "NestedLookups": {
            "accountid": {
              "EntityLogicalName": "account",
              "KeyAttributes": { "accountnumber": "PARENT-100" },
              "CreateIfNotExists": true,
              "CreateAttributes": { "name": "Parent Account", "accountnumber": "PARENT-100" }
            }
          }
        }
      },
      "MaxLookupDepth": 3
    }
  ]
}
```

### Lookup Resolution

Resolution order:
1. Query Dataverse by `KeyAttributes`.
2. If found → return `EntityReference`.
3. If not found and `CreateIfNotExists` → resolve `NestedLookups` recursively → create → return `EntityReference`.
4. If not found and `CreateIfNotExists` is `false` → fail with `Permanent` error.

**Safety:**
- **Max depth**: global default is 3, overridable per batch or per lookup.
- **Cycle detection**: visited-set per resolution path prevents infinite loops.
- **Cache eviction**: on failure with a cached GUID, the entry is evicted and a single fresh re-resolve is attempted.

---

## SAP Integration

The **SAP Account with Contacts** endpoint (`POST /api/sap/account-with-contacts`) demonstrates manual payload mapping via `ISapAccountMapper`. SAP sends a flat JSON request; the mapper produces a `SapMappingResult` that the trigger executes in three sequential phases to avoid race conditions.

### Three-Phase Execution

1. **Phase 1 — Account upsert**: The account is upserted first (keyed on `accountnumber`). If this phase fails, the request **short-circuits**: contacts and the primary-contact link are skipped and only the account result is returned.
2. **Phase 2 — Contacts batch**: All contacts are upserted in parallel (keyed on `emailaddress1`). The account GUID from Phase 1 is wired directly into `parentcustomerid` — no per-contact lookup round-trips.
3. **Phase 3 — Primary contact link** *(optional)*: If a contact has `IsPrimary = true`, a follow-up account update sets `primarycontactid` using the contact GUID from Phase 2. If the primary contact's upsert failed, the link is skipped and reported as an explicit error result.

This phased approach with direct GUID wiring guarantees deterministic linking (no ambiguity when an e-mail matches multiple records) and costs exactly one Dataverse round-trip per record.

**Request validation:** `AccountNumber` and `Name` are required; every contact needs a non-empty `Email` (the contact key), `FirstName` and `LastName`; contact e-mails must be unique per request; at most one contact may be `IsPrimary`; the contact count is capped at `Dataverse:MaxBatchItems`.

### Request Contract

```json
POST /api/sap/account-with-contacts
x-correlation-id: optional-correlation-id

{
  "AccountNumber": "ACCT-5000",
  "Name": "Contoso SAP",
  "City": "Berlin",
  "Street": "Alexanderplatz 1",
  "PostalCode": "10178",
  "Country": "DE",
  "Phone": "+49 30 1234567",
  "Email": "info@contoso-sap.de",
  "Website": "https://contoso-sap.de",
  "Contacts": [
    {
      "Email": "anna.schmidt@contoso-sap.de",
      "FirstName": "Anna",
      "LastName": "Schmidt",
      "Phone": "+49 30 1111111",
      "JobTitle": "CTO",
      "IsPrimary": true
    },
    {
      "Email": "max.mueller@contoso-sap.de",
      "FirstName": "Max",
      "LastName": "Müller"
    }
  ]
}
```

### Field Mapping

| SAP Field | Dataverse Entity | Dataverse Attribute |
|-----------|------------------|---------------------|
| `AccountNumber` | `account` | `accountnumber` (key) |
| `Name` | `account` | `name` |
| `City` | `account` | `address1_city` |
| `Street` | `account` | `address1_line1` |
| `PostalCode` | `account` | `address1_postalcode` |
| `Country` | `account` | `address1_country` |
| `Phone` | `account` | `telephone1` |
| `Email` | `account` | `emailaddress1` |
| `Website` | `account` | `websiteurl` |
| `Contact.Email` | `contact` | `emailaddress1` (key) |
| `Contact.FirstName` | `contact` | `firstname` |
| `Contact.LastName` | `contact` | `lastname` |
| `Contact.Phone` | `contact` | `telephone1` |
| `Contact.JobTitle` | `contact` | `jobtitle` |

The response follows the same `UpsertResult[]` format as the generic `/api/upsert` endpoint.

---

## Error Model

### ErrorCategory

| Value | Description | HTTP Mapping |
|-------|-------------|--------------|
| `None` | Success | 200 |
| `Validation` | Structural / type / KeyAttributes errors | 400 |
| `Transient` | Timeout, network, circuit breaker | 500 |
| `Permanent` | Dataverse rejection, unrecoverable | 500 |
| `Throttling` | Rate limit exceeded | 429 (+ `Retry-After` header) |
| `Cancellation` | Request cancelled | 500 (client-observed cancellations return 408) |

Dataverse service-protection faults (`FaultException<OrganizationServiceFault>` with the documented throttling error codes) are recognized by error code, retried with the server's `Retry-After` hint, and reduce the adaptive concurrency limit.

### Response

```json
{
  "Id": "00000000-0000-0000-0000-000000000000",
  "Created": true,
  "EntityLogicalName": "account",
  "UpsertKey": "account:accountnumber=ACCT-1001",
  "ErrorMessage": null,
  "ErrorCategory": "None",
  "ValidationErrors": null,
  "LookupTraces": [
    {
      "AttributeName": "primarycontactid",
      "EntityLogicalName": "contact",
      "ResolvedId": "...",
      "WasCreated": true,
      "Depth": 0,
      "NestedTraces": []
    }
  ]
}
```

### Batch Status Code Logic

| Result | HTTP Code |
|--------|-----------|
| All payloads succeeded | `200 OK` |
| Only `Validation` failures | `400 Bad Request` |
| Throttling failures (and no other technical failures) | `429 Too Many Requests` + `Retry-After` |
| Any `Transient` / `Permanent` / `Cancellation` failure | `500 Internal Server Error` |
| Request body too large (enforced on non-seekable streams too) | `413 Payload Too Large` |

All payload results are always returned in full regardless of overall status code. Enums are serialized as strings, error responses are structured JSON (`{ "Error", "Details"?, "CorrelationId" }`), and `x-correlation-id` is echoed on every response (sanitized: max 64 printable ASCII characters).

### Service Bus Poison-Message Policy

The Service Bus trigger never silently completes bad messages: malformed JSON, empty batches, oversized batches and failed upserts all **throw**, so Service Bus retries up to `MaxDeliveryCount` and then dead-letters the message with the evidence intact. Upserts are idempotent — redelivery re-applies succeeded items safely.

---

## Health Endpoints

| Endpoint | Auth | Purpose |
|----------|------|---------|
| `GET /health/live` | Anonymous | Liveness — `200` if the process is running |
| `GET /health/ready` | Function Key | Readiness — performs a real `WhoAmI` round-trip against Dataverse (a broken/poisoned client turns readiness `503`) |

```json
{
  "Status": "Healthy",
  "Checks": {
    "dataverse": { "Status": "Healthy", "Detail": "WhoAmI round trip OK (org 00000000-0000-0000-0000-000000000000)." }
  }
}
```

If Dataverse is unreachable, returns `503` with `"Status": "Unhealthy"` and a `Detail` naming the failure class (exception details stay in the logs, not the response).

---

## Integration Tests

```bash
# Run all tests
dotnet test TESTS/

# Unit tests only
dotnet test TESTS/ --filter "Category!=Integration"

# With coverage
dotnet test TESTS/ --collect:"XPlat Code Coverage"
```

Tests require no external services — integration tests use FakeXrmEasy v9 in-memory Dataverse.

### Test Breakdown

| Suite | Tests |
|-------|-------|
| Unit (services, mappers, resilience, contracts) | 202 |
| Integration (orchestrator, triggers, retry semantics, guards, DI) | 56 |
| **Total** | **258** |

---

## Load Testing

A PowerShell script `LoadTest.ps1` (repository root) load-tests the HTTP endpoint:

```powershell
# Basic usage (PowerShell 7+)
.\LoadTest.ps1 -FunctionUrl "http://localhost:7071/api/upsert" -FunctionKey "your-function-key"

# Safe profile (minimise throttling)
.\LoadTest.ps1 -FunctionUrl "http://localhost:7071/api/upsert" -Profile Safe

# Custom parameters
.\LoadTest.ps1 -FunctionUrl "https://your-function.azurewebsites.net/api/upsert" `
               -Profile Normal -FunctionKey "your-function-key" `
               -ThreadCount 8 -RequestsPerThread 40 -BatchSize 3

# SAP Account+Contacts endpoint
.\LoadTest.ps1 -FunctionUrl "http://localhost:7071/api/sap/account-with-contacts" `
               -Endpoint SapAccountWithContacts -SapContactsPerRequest 3 -Profile Safe
```

| Parameter | Default | Description |
|-----------|---------|-------------|
| `FunctionUrl` | *(required)* | Upsert endpoint URL |
| `FunctionKey` | | Azure Function key |
| `Profile` | `Normal` | `Safe` / `Normal` / `Stress` / `Custom` |
| `Endpoint` | `Upsert` | `Upsert` / `SapAccountWithContacts` |
| `ThreadCount` | `6` | Parallel threads |
| `RequestsPerThread` | `80` | Requests per thread |
| `BatchSize` | `3` | Payloads per request (Upsert endpoint) |
| `SapContactsPerRequest` | `2` | Contacts per SAP request (SAP endpoint) |
| `ReportPath` | | Optional JSON output path |

Reports p50/p95/p99 latency, throughput (req/s, payloads/s), and status-code distribution.

---

## Technology Stack

| Component | Version |
|-----------|---------|
| .NET | 8.0 LTS |
| Microsoft.PowerPlatform.Dataverse.Client | 1.x |
| Polly | 8.x |
| xUnit | 2.9.2 |
| Moq | 4.20.72 |
| FluentAssertions | 6.12.2 |
| FakeXrmEasy.v9 | 3.8.0 |

---

## License

MIT

## Disclaimer

Private project. Provided without warranty. Use at your own risk.

---

> Built with ❤️ for Microsoft Dynamics 365 integration engineers.
