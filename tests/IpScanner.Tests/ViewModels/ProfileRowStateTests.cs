using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class ProfileRowStateTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, true)]
    [InlineData(3, 2, true)]
    [InlineData(0, 1, false)]
    public void EditButton_ShowsOnlyForANamedSelection(int named, int index, bool expected)
        => Assert.Equal(expected, ProfileRowState.EditButtonVisible(named, index));

    [Fact]
    public void Switcher_AppearsWithTheFirstNamedProfile()
    {
        Assert.False(ProfileRowState.SwitcherVisible(0));
        Assert.True(ProfileRowState.SwitcherVisible(1));
    }
}
