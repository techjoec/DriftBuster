using System.Collections.Generic;

namespace DriftBuster.Gui.Services;

public interface IThemeRuntime
{
    IReadOnlyList<ThemeOption> GetAvailableThemes();

    ThemeOption GetDefaultTheme(IReadOnlyList<ThemeOption> options);

    void ApplyTheme(ThemeOption option);
}
