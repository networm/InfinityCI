namespace InfinityCI.Core;

public sealed class JobYamlException : Exception
{
    public JobYamlException(string message) : base(message) { }
    public JobYamlException(string message, Exception innerException) : base(message, innerException) { }
}
