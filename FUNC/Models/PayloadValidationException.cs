namespace enterprise_d365_gateway.Models
{
    public sealed class PayloadValidationException : Exception
    {
        public IReadOnlyList<string> ValidationErrors { get; }

        public PayloadValidationException(IEnumerable<string> validationErrors)
            : base(string.Join("; ", validationErrors))
        {
            ValidationErrors = validationErrors.ToArray();
        }
    }
}
