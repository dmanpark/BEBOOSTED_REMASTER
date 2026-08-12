using BeBoosted.Application.Settings;

namespace BeBoosted.Tests.Settings;

public sealed class WindowPlacementTests
{
    [Fact]
    public void SerializeAndParse_RoundTrips()
    {
        var placement = new WindowPlacement(-120, 40, 1440, 960, IsMaximized: true);
        Assert.Equal(placement, WindowPlacement.TryParse(placement.Serialize()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not,numbers,at,all,x")]
    [InlineData("1,2,3")]
    [InlineData("1,2,0,960,0")]
    [InlineData("1,2,1440,-5,0")]
    public void TryParse_RejectsInvalidValues(string? value) => Assert.Null(WindowPlacement.TryParse(value));
}
