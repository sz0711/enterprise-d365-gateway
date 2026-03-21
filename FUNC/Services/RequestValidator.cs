using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Services
{
    public class RequestValidator : IRequestValidator
    {
        private readonly IEarlyboundEntityMapper _mapper;

        public RequestValidator(IEarlyboundEntityMapper mapper)
        {
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        }

        public void ValidateBatch(UpsertBatchRequest request)
        {
            if (request?.Payloads == null || request.Payloads.Count == 0)
                throw new PayloadValidationException(new[] { "Payloads must contain at least one item." });

            for (int i = 0; i < request.Payloads.Count; i++)
            {
                try
                {
                    Validate(request.Payloads[i]);
                }
                catch (PayloadValidationException ex)
                {
                    throw new PayloadValidationException(
                        ex.ValidationErrors.Select(e => $"Payload[{i}]: {e}"));
                }
            }
        }

        public void Validate(UpsertPayload payload)
        {
            if (payload == null)
                throw new PayloadValidationException(new[] { "Payload must not be null." });

            var errors = new List<string>();

            if (payload.KeyAttributes == null || payload.KeyAttributes.Count == 0)
                errors.Add("KeyAttributes must contain at least one entry for the main entity.");

            if (string.IsNullOrWhiteSpace(payload.EntityLogicalName))
                errors.Add("EntityLogicalName is required.");

            if (payload.Attributes == null)
                errors.Add("Attributes are required.");

            if (payload.Lookups != null)
                ValidateLookupsRecursive(payload.Lookups, errors, "Lookups");

            if (errors.Count > 0)
                throw new PayloadValidationException(errors);

            _mapper.ValidatePayload(payload);
        }

        private static void ValidateLookupsRecursive(
            IDictionary<string, LookupDefinition> lookups,
            List<string> errors,
            string path)
        {
            foreach (var (key, lookup) in lookups)
            {
                var currentPath = $"{path}.{key}";

                if (string.IsNullOrWhiteSpace(lookup.EntityLogicalName))
                    errors.Add($"{currentPath}: EntityLogicalName is required.");

                if (lookup.KeyAttributes == null || lookup.KeyAttributes.Count == 0)
                    errors.Add($"{currentPath}: KeyAttributes must contain at least one entry.");

                if (lookup.NestedLookups != null && lookup.NestedLookups.Count > 0)
                    ValidateLookupsRecursive(lookup.NestedLookups, errors, currentPath);
            }
        }
    }
}
