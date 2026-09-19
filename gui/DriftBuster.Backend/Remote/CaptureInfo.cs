namespace DriftBuster.Backend.Remote;

public sealed record CaptureInfo(string Id, string Root, DateTimeOffset CapturedAt, string Operator, string Environment, string Reason, string Host, string Placeholder, int MaskTokenCount);
