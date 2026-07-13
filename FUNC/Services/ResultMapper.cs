using System.Net;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Services
{
    public class ResultMapper : IResultMapper
    {
        public UpsertResult MapSuccess(
            string entityLogicalName,
            string? keySignature,
            Guid id,
            bool created,
            IList<LookupTrace>? lookupTraces = null)
        {
            return new UpsertResult
            {
                EntityLogicalName = entityLogicalName,
                UpsertKey = keySignature,
                Id = id,
                Created = created,
                ErrorCategory = ErrorCategory.None,
                LookupTraces = lookupTraces
            };
        }

        public UpsertResult MapError(
            string entityLogicalName,
            string? keySignature,
            Exception exception,
            ErrorCategory category)
        {
            var result = new UpsertResult
            {
                EntityLogicalName = entityLogicalName,
                UpsertKey = keySignature,
                Id = Guid.Empty,
                Created = false,
                ErrorCategory = category,
                ErrorMessage = exception.Message
            };

            if (exception is PayloadValidationException validationEx)
            {
                result.ValidationErrors = validationEx.ValidationErrors.ToList();
            }

            return result;
        }

        public HttpStatusCode DetermineBatchStatusCode(IReadOnlyList<UpsertResult> results)
        {
            if (results == null || results.Count == 0)
                return HttpStatusCode.OK;

            bool hasThrottling = false, hasOtherTechnical = false, hasValidation = false;
            foreach (var r in results)
            {
                switch (r.ErrorCategory)
                {
                    case ErrorCategory.Throttling:
                        hasThrottling = true;
                        break;
                    case ErrorCategory.Transient:
                    case ErrorCategory.Permanent:
                    case ErrorCategory.Cancellation:
                        hasOtherTechnical = true;
                        break;
                    case ErrorCategory.Validation:
                        hasValidation = true;
                        break;
                }
            }

            if (hasOtherTechnical)
                return HttpStatusCode.InternalServerError;

            // Only-throttled batches are retryable by the caller — say so.
            if (hasThrottling)
                return HttpStatusCode.TooManyRequests;

            if (hasValidation)
                return HttpStatusCode.BadRequest;

            return HttpStatusCode.OK;
        }
    }
}
