using Polly.CircuitBreaker;
using Polly.Timeout;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Services
{
    public class ErrorClassifier : IErrorClassifier
    {
        public ErrorCategory Classify(Exception exception)
        {
            return exception switch
            {
                PayloadValidationException => ErrorCategory.Validation,
                ArgumentException => ErrorCategory.Validation,
                OperationCanceledException => ErrorCategory.Cancellation,
                TimeoutException => ErrorCategory.Transient,
                TimeoutRejectedException => ErrorCategory.Transient,
                BrokenCircuitException => ErrorCategory.Transient,
                HttpRequestException => ErrorCategory.Transient,
                InvalidOperationException ex
                    when ex.Message.Contains("response is empty", StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("ThrowIfResponseIsEmpty", StringComparison.OrdinalIgnoreCase)
                    => ErrorCategory.Transient,
                InvalidOperationException ex when ex.Message.Contains("Rate limit", StringComparison.OrdinalIgnoreCase)
                    => ErrorCategory.Throttling,
                AggregateException agg when agg.InnerException is TimeoutException
                    => ErrorCategory.Transient,
                AggregateException agg when agg.InnerException is OperationCanceledException
                    => ErrorCategory.Cancellation,
                _ => ErrorCategory.Permanent
            };
        }
    }
}
