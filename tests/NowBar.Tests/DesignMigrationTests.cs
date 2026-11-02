using NowBar.App.Models;

namespace NowBar.Tests;

public sealed class DesignMigrationTests
{
    [Fact]
    public void Old_default_design_values_move_to_new_defaults_once()
    {
        var s = new AppSettings { AccentColor = "#9EB4FF", CornerRadius = 24, DesignVersion = 0 };
        Assert.True(s.MigrateDesignDefaults());
        Assert.Equal("#1ED760", s.AccentColor);
        Assert.Equal(20, s.CornerRadius);
        Assert.Equal(AppSettings.CurrentDesignVersion, s.DesignVersion);

        // 두 번째 호출은 아무것도 바꾸지 않는다
        s.CornerRadius = 24;
        Assert.False(s.MigrateDesignDefaults());
        Assert.Equal(24, s.CornerRadius);
    }

    [Fact]
    public void User_customized_values_are_kept()
    {
        var s = new AppSettings { AccentColor = "#F3727F", CornerRadius = 14, DesignVersion = 0 };
        s.MigrateDesignDefaults();
        Assert.Equal("#F3727F", s.AccentColor);
        Assert.Equal(14, s.CornerRadius);
    }
}
