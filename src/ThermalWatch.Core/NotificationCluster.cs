using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed record NotificationCluster(
    string Id,
    Anomaly Representative,
    ImmutableArray<Anomaly> Members)
{
    public double? TotalFrpMegawatts
    {
        get
        {
            double total = 0;
            bool hasAvailableFrp = false;
            foreach (Anomaly member in Members)
            {
                if (member.FrpMegawatts is not { } frp || !double.IsFinite(frp))
                    continue;

                total += frp;
                hasAvailableFrp = true;
            }

            return hasAvailableFrp && double.IsFinite(total) ? total : null;
        }
    }
}
