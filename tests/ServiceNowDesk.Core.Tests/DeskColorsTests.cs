using ServiceNowDesk.Theme;

namespace ServiceNowDesk.Tests;

public class DeskColorsTests
{
    [Fact]
    public void PresetActiveIsAccentBlendedThirtyPercentTowardWhite()
    {
        Assert.Equal("#0A635C", DeskColors.Accent);
        Assert.Equal("#FFFFFF", DeskColors.PresetIdle);
        Assert.Equal("#54928D", DeskColors.BlendTowardWhite(DeskColors.Accent, 0.30));
        Assert.Equal(DeskColors.PresetActive, DeskColors.BlendTowardWhite(DeskColors.Accent, 0.30));
    }
}
