using Polly.CircuitBreaker;
using Polly.Timeout;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;
using System.ServiceModel;

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
                ProtocolException ex when IsRateLimitMessage(ex.Message)
                    => ErrorCategory.Throttling,
                InvalidOperationException ex
                    when ex.Message.Contains("response is empty", StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("ThrowIfResponseIsEmpty", StringComparison.OrdinalIgnoreCase)
                    => ErrorCategory.Transient,
                InvalidOperationException ex when ex.Message.Contains("Rate limit", StringComparison.OrdinalIgnoreCase)
                    => ErrorCategory.Throttling,
                AggregateException agg when IsRateLimitException(agg)
                    => ErrorCategory.Throttling,
                AggregateException agg when agg.InnerException is TimeoutException
                    => ErrorCategory.Transient,
                AggregateException agg when agg.InnerException is OperationCanceledException
                    => ErrorCategory.Cancellation,
                _ => ErrorCategory.Permanent
            };
        }

        private static bool IsRateLimitException(Exception exception)
        {
            if (IsRateLimitMessage(exception.Message))
            {
                return true;
            }

            if (exception is AggregateException aggregate)
            {
                return aggregate.Flatten().InnerExceptions.Any(IsRateLimitException);
            }

            return exception.InnerException is not null && IsRateLimitException(exception.InnerException);
        }

        private static bool IsRateLimitMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.Contains("429", StringComparison.OrdinalIgnoreCase)
                || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || message.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
                || message.Contains("throttl", StringComparison.OrdinalIgnoreCase);
        }
    }
}
