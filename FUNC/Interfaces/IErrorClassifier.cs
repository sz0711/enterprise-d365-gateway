using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Interfaces
{
    public interface IErrorClassifier
    {
        ErrorCategory Classify(Exception exception);
    }
}
