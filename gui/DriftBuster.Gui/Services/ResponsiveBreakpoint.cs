using System.Collections.Generic;

namespace DriftBuster.Gui.Services;

public sealed record ResponsiveBreakpoint(double MinWidth, IReadOnlyDictionary<string, object> Resources);
