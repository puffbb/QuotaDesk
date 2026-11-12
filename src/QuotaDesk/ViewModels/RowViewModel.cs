using QuotaDesk.Services;

namespace QuotaDesk.ViewModels;

/// <summary>一行额度的视图绑定对象（一个窗口）。</summary>
public sealed class RowViewModel : ObservableObject
{
    private string _label = "";
    private string _percentText = "";
    private double _fraction;
    private Severity _severity = Severity.Normal;
    private string? _rightText;
    private string? _statusText;
    private string? _compactSuffix;
    private string? _toolTip;
    private bool _hasData = true;

    public string Label { get => _label; private set => Set(ref _label, value); }
    public string PercentText { get => _percentText; private set => Set(ref _percentText, value); }
    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }
    public Severity Severity { get => _severity; private set => Set(ref _severity, value); }
    public string? RightText { get => _rightText; private set => Set(ref _rightText, value); }
    public string? StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string? CompactSuffix { get => _compactSuffix; private set => Set(ref _compactSuffix, value); }
    public string? ToolTip { get => _toolTip; private set => Set(ref _toolTip, value); }
    public bool HasData { get => _hasData; private set => Set(ref _hasData, value); }

    public void Update(RowDisplay d)
    {
        Label = d.Label;
        PercentText = d.PercentText;
        Fraction = d.Fraction;
        Severity = d.Severity;
        RightText = d.RightText;
        StatusText = d.StatusText;
        CompactSuffix = d.CompactSuffix;
        ToolTip = d.ToolTip;
        HasData = d.HasData;
    }
}
