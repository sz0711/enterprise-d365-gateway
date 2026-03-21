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

            var hasTechnical = results.Any(r =>
                r.ErrorCategory is ErrorCategory.Transient
                    or ErrorCategory.Permanent
                    or ErrorCategory.Throttling);

            if (hasTechnical)
                return HttpStatusCode.InternalServerError;

            var hasValidation = results.Any(r => r.ErrorCategory == ErrorCategory.Validation);
            if (hasValidation)
                return HttpStatusCode.BadRequest;

            return HttpStatusCode.OK;
        }
    }
}
