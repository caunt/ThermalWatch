using System.Collections.Immutable;

namespace ThermalWatch.Core;

/// <summary>A transitive spatial and temporal group of observations selected for notification evaluation.</summary>
/// <param name="Id">The stable identifier derived from the ordered member IDs.</param>
/// <param name="Representative">The member chosen for location, acquisition time, and preview source.</param>
/// <param name="Members">The observations belonging to this cluster.</param>
public sealed record NotificationCluster(string Id, Anomaly Representative, ImmutableArray<Anomaly> Members)
{
    /// <summary>The sum of finite member FRP measurements, or null when unavailable or nonfinite.</summary>
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
