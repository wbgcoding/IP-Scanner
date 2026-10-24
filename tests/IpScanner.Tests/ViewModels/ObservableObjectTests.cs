using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class ObservableObjectTests
{
    private sealed class Sample : ObservableObject
    {
        private int _x;
        public int X { get => _x; set => SetProperty(ref _x, value); }
    }

    [Fact]
    public void SetProperty_RaisesPropertyChanged()
    {
        var s = new Sample();
        string? raised = null;
        s.PropertyChanged += (_, e) => raised = e.PropertyName;
        s.X = 5;
        Assert.Equal("X", raised);
    }
}
