namespace DriftBuster.Backend.Detection;

/// <summary>Contract implemented by format plugins.</summary>
public interface IFormatPlugin
{
    string Name { get; }

    string Version { get; }

    int Priority { get; }

    /// <summary>Inspects a bounded <paramref name="sample"/> of the file at <paramref name="path"/>; <paramref name="text"/> is null when the sample did not decode as text.</summary>
    DetectionMatch? Detect(string path, byte[] sample, string? text);
}
