using MotorBench.Sequencing;

namespace MotorBench.Tests;

public class TestProfileTests
{
    [Fact]
    public void Parses_header_comments_and_steps()
    {
        var profile = TestProfile.Parse("t", ["# warm up", "speed_rpm,load_pct,dwell_s", "", "500,0,5", " 1500 , 50 , 2.5 "]);

        Assert.Equal(2, profile.Steps.Count);
        Assert.Equal(new TestStep(2, 1500, 50, 2.5), profile.Steps[1]);
        Assert.Equal(7.5, profile.TotalDwellSeconds);
    }

    [Fact]
    public void Wrong_column_count_reports_the_line()
    {
        var ex = Assert.Throws<ProfileFormatException>(() => TestProfile.Parse("t", ["500,0,5", "1000,20"]));
        Assert.Equal(2, ex.Line);
    }

    [Fact]
    public void Out_of_range_speed_is_rejected()
    {
        Assert.Throws<ProfileFormatException>(() => TestProfile.Parse("t", ["3500,0,5"]));
    }

    [Fact]
    public void NaN_dwell_is_rejected()
    {
        Assert.Throws<ProfileFormatException>(() => TestProfile.Parse("t", ["500,0,NaN"]));
    }

    [Fact]
    public void Header_after_steps_is_an_error()
    {
        Assert.Throws<ProfileFormatException>(() => TestProfile.Parse("t", ["500,0,5", "speed,load,dwell"]));
    }

    [Fact]
    public void Empty_profile_is_rejected()
    {
        Assert.Throws<ProfileFormatException>(() => TestProfile.Parse("t", ["# nothing here"]));
    }
}
