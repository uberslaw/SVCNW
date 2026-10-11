using ServiceNowDesk.Services;

namespace ServiceNowDesk.ViewModels;

public sealed class TransferPeriodOption
{
    public TransferPeriodOption(TransferPeriod period)
    {
        Period = period;
        Label = TransferMeter.LabelOf(period);
    }

    public TransferPeriod Period { get; }
    public string Label { get; }

    public override string ToString() => Label;
}
