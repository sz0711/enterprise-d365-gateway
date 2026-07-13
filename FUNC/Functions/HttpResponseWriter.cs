using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker.Http;

namespace enterprise_d365_gateway.Functions
{
    /// <summary>
    /// Shared response plumbing for all HTTP triggers: one serializer contract
    /// (PascalCase properties, enums as strings — matching the documented API),
    /// structured JSON error bodies, correlation-id handling and streaming
    /// serialization (no intermediate strings).
    /// </summary>
    internal static class HttpResponseWriter
    {
        /// <summary>Serializer for all response bodies. PascalCase + string enums per the documented contract.</summary>
        internal static readonly JsonSerializerOptions ResponseOptions = new()
        {
            Converters = { new JsonStringEnumConverter() }
        };

        private const int MaxCorrelationIdLength = 64;

        /// <summary>
        /// Returns the client-supplied x-correlation-id when it is safe to echo
        /// (bounded length, printable ASCII without CR/LF); otherwise a fresh id.
        /// </summary>
        internal static string ResolveCorrelationId(HttpRequestData req)
        {
            if (req.Headers.TryGetValues("x-correlation-id", out var headerValues))
            {
                var candidate = headerValues.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(candidate)
                    && candidate.Length <= MaxCorrelationIdLength
                    && candidate.All(c => c >= 0x20 && c < 0x7F))
                {
                    return candidate;
                }
            }

            return Guid.NewGuid().ToString("N");
        }

        internal static async Task<HttpResponseData> WriteJsonAsync<T>(
            HttpRequestData req,
            HttpStatusCode statusCode,
            T body,
            string correlationId,
            int? retryAfterSeconds = null)
        {
            var response = req.CreateResponse(statusCode);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            response.Headers.Add("x-correlation-id", correlationId);
            if (retryAfterSeconds.HasValue)
                response.Headers.Add("Retry-After", retryAfterSeconds.Value.ToString());

            await JsonSerializer.SerializeAsync(response.Body, body, ResponseOptions);
            return response;
        }

        internal static Task<HttpResponseData> WriteErrorAsync(
            HttpRequestData req,
            HttpStatusCode statusCode,
            string message,
            string correlationId,
            IReadOnlyList<string>? details = null)
        {
            return WriteJsonAsync(
                req,
                statusCode,
                new ErrorBody { Error = message, Details = details, CorrelationId = correlationId },
                correlationId);
        }

        private sealed class ErrorBody
        {
            public required string Error { get; init; }

            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public IReadOnlyList<string>? Details { get; init; }

            public required string CorrelationId { get; init; }
        }
    }
}
