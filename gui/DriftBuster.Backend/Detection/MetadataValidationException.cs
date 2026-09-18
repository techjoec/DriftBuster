namespace DriftBuster.Backend.Detection;

/// <summary>Raised when detection metadata fails validation checks.</summary>
public sealed class MetadataValidationException : ArgumentException
{
    public MetadataValidationException(string message)
        : base(message)
    {
    }

    public MetadataValidationException()
    {
    }

    public MetadataValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
