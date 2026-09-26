using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;

namespace Fermata.Controls;

/// <summary>Presents a <see cref="SeekBar"/> to screen readers as a slider from 0 to 1 that can be set.</summary>
internal sealed class SeekBarAutomationPeer(SeekBar owner) : ControlAutomationPeer(owner), IRangeValueProvider
{
    private SeekBar Bar => (SeekBar)Owner;

    public bool IsReadOnly => !Bar.IsEnabled;
    public double Minimum => 0;
    public double Maximum => 1;
    public double Value => Bar.Value;
    public double LargeChange => 0.1;
    public double SmallChange => 0.02;

    public void SetValue(double value) => Bar.Commit(Math.Clamp(value, 0, 1));

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
}
