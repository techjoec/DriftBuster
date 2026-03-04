using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

public sealed class CatalogSortDescriptorTests
{
    [Fact]
    public void Default_uses_updated_descending()
    {
        CatalogSortDescriptor.Default.ColumnKey.Should().Be(CatalogSortColumns.Updated);
        CatalogSortDescriptor.Default.Descending.Should().BeTrue();
    }

    [Theory]
    [InlineData(CatalogSortColumns.Drift)]
    [InlineData(CatalogSortColumns.Coverage)]
    [InlineData(CatalogSortColumns.Format)]
    [InlineData(CatalogSortColumns.Severity)]
    [InlineData(CatalogSortColumns.Updated)]
    [InlineData(CatalogSortColumns.Config)]
    public void Normalize_preserves_supported_column_keys(string key)
    {
        var descriptor = CatalogSortDescriptor.Normalize($"  {key}  ", descending: false);
        descriptor.ColumnKey.Should().Be(key);
        descriptor.Descending.Should().BeFalse();
    }

    [Fact]
    public void Normalize_falls_back_to_config_for_unknown_key()
    {
        CatalogSortDescriptor.Normalize("unknown", descending: true)
            .Should()
            .Be(new CatalogSortDescriptor(CatalogSortColumns.Config, true));
    }
}
