using Microsoft.Xrm.Sdk;
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
                FaultException<OrganizationServiceFault> fault => ClassifyOrganizationFault(fault.Detail),
                ProtocolException ex when IsRateLimitMessage(ex.Message)
                    => ErrorCategory.Throttling,
                InvalidOperationException ex
                    when ex.Message.Contains("response is empty", StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("ThrowIfResponseIsEmpty", StringComparison.OrdinalIgnoreCase)
                    => ErrorCategory.Transient,
                InvalidOperationException ex when ex.Message.Contains("Rate limit", StringComparison.OrdinalIgnoreCase)
                    => ErrorCategory.Throttling,
                AggregateException agg => ClassifyAggregate(agg),
                _ => ErrorCategory.Permanent
            };
        }

        public bool IsKeyConflict(Exception exception)
        {
            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                if (current is FaultException<OrganizationServiceFault> fault
                    && DataverseErrorCodes.IsKeyConflict(fault.Detail?.ErrorCode ?? 0))
                {
                    return true;
                }

                if (IsKeyConflictMessage(current.Message))
                {
                    return true;
                }

                if (current is AggregateException aggregate
                    && aggregate.Flatten().InnerExceptions.Any(IsKeyConflict))
                {
                    return true;
                }
            }

            return false;
        }

        private ErrorCategory ClassifyAggregate(AggregateException aggregate)
        {
            if (IsRateLimitException(aggregate))
                return ErrorCategory.Throttling;

            var categories = aggregate.Flatten().InnerExceptions
                .Select(Classify)
                .ToList();

            if (categories.Count == 0)
                return ErrorCategory.Permanent;

            if (categories.Contains(ErrorCategory.Cancellation))
                return ErrorCategory.Cancellation;
            if (categories.Contains(ErrorCategory.Throttling))
                return ErrorCategory.Throttling;
            if (categories.Contains(ErrorCategory.Transient))
                return ErrorCategory.Transient;
            if (categories.Contains(ErrorCategory.Validation))
                return ErrorCategory.Validation;

            return ErrorCategory.Permanent;
        }

        private static ErrorCategory ClassifyOrganizationFault(OrganizationServiceFault? fault)
        {
            if (fault == null)
                return ErrorCategory.Permanent;

            if (DataverseErrorCodes.IsThrottling(fault.ErrorCode))
                return ErrorCategory.Throttling;

            if (DataverseErrorCodes.IsTransient(fault.ErrorCode))
                return ErrorCategory.Transient;

            if (IsRateLimitMessage(fault.Message))
                return ErrorCategory.Throttling;

            return ErrorCategory.Permanent;
        }

        private static bool IsRateLimitException(Exception exception)
        {
            if (exception is FaultException<OrganizationServiceFault> fault
                && DataverseErrorCodes.IsThrottling(fault.Detail?.ErrorCode ?? 0))
            {
                return true;
            }

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

            return message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || message.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
                || message.Contains("429", StringComparison.OrdinalIgnoreCase)
                || message.Contains("throttl", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsKeyConflictMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Well-known Dataverse organization-service fault codes.
    /// https://learn.microsoft.com/power-apps/developer/data-platform/reference/web-service-error-codes
    /// </summary>
    internal static class DataverseErrorCodes
    {
        // Service-protection (throttling) faults
        public const int NumberOfRequestsExceeded = unchecked((int)0x80072322);      // -2147015902
        public const int ExecutionTimeExceeded = unchecked((int)0x80072321);         // -2147015903
        public const int ConcurrentRequestsExceeded = unchecked((int)0x80072326);    // -2147015898
        public const int ThrottlingBurstRequestLimitExceededError = unchecked((int)0x80072329); // -2147015895

        // Key-conflict faults (stale cache / create race symptoms)
        public const int DuplicateRecordsFound = unchecked((int)0x80040333);          // -2147220173
        public const int DuplicateAlternateKey = unchecked((int)0x80060892);          // -2147086190
        public const int ObjectDoesNotExist = unchecked((int)0x80040217);             // -2147220969
        public const int RecordNotFoundByEntityKey = unchecked((int)0x80060891);      // -2147086191

        // Transient platform faults
        public const int SqlTimeoutError = unchecked((int)0x80044151);                // -2147204783
        public const int SqlErrorGeneric = unchecked((int)0x80044150);                // -2147204784
        public const int UnexpectedError = unchecked((int)0x80040216);                // -2147220970

        public static bool IsThrottling(int errorCode) => errorCode
            is NumberOfRequestsExceeded
            or ExecutionTimeExceeded
            or ConcurrentRequestsExceeded
            or ThrottlingBurstRequestLimitExceededError;

        public static bool IsKeyConflict(int errorCode) => errorCode
            is DuplicateRecordsFound
            or DuplicateAlternateKey
            or ObjectDoesNotExist
            or RecordNotFoundByEntityKey;

        public static bool IsTransient(int errorCode) => errorCode
            is SqlTimeoutError
            or SqlErrorGeneric
            or UnexpectedError;
    }
}
