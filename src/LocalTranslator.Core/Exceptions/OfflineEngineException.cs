namespace LocalTranslator.Core.Exceptions;

public sealed class OfflineEngineException : Exception
{
    public OfflineEngineException(string message)
        : base(message)
    {
    }
}

