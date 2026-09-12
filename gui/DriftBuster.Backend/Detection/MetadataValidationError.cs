namespace DriftBuster.Backend.Detection;

/// <summary>Raised when detection metadata fails validation checks.</summary>
public sealed class MetadataValidationError : ArgumentException
{
    public MetadataValidationError(string message)
        : base(message)
    {
    }

    public MetadataValidationError()
    {
    }

    public MetadataValidationError(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
