namespace WebFormsMigrator.Services;

public sealed class AiMigrationException : Exception
{
    public AiMigrationException(string message, bool stopAllRequests, Exception? innerException = null)
        : base(message, innerException)
    {
        StopAllRequests = stopAllRequests;
    }

    public bool StopAllRequests { get; }
}
