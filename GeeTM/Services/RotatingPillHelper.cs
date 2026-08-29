namespace GeeTM.Services;

/// <summary>
/// Decides what each pill should currently be showing. Deliberately
/// stateless and based on wall-clock time (not a stored timer/counter), so
/// both rendering paths (the WPF floating widget and the native embedded
/// widget) can independently ask "what's showing right now?" on every
/// update tick and always agree, with no need to synchronise state between
/// two otherwise-separate rendering pipelines.
///
/// Each rotating feature (IP, Flag) is assigned its own target pill
/// independently, so a caller asks "what should THIS pill show?" and gets
/// back only the features actually assigned to it - correctly handling
/// every combination: both features on one pill while the other stays
/// fixed, one feature per pill, or everything on a single pill.
/// </summary>
public static class RotatingPillHelper
{
    public enum PillContent { Base, Ip, Flag }

    /// <summary>The rotation sequence for a specific pill: Base (that pill's
    /// normal content) is always first and always included, followed by
    /// whichever enabled features are actually assigned to this pill. A pill
    /// with nothing assigned to it just always shows Base - equivalent to
    /// rotation being off for that pill specifically.</summary>
    private static List<PillContent> GetSequenceForPill(AppSettings s, string pillName)
    {
        var seq = new List<PillContent> { PillContent.Base };
        if (s.RotatePillShowIp && string.Equals(s.IpTargetPill, pillName, StringComparison.OrdinalIgnoreCase))
            seq.Add(PillContent.Ip);
        if (s.RotatePillShowFlag && string.Equals(s.FlagTargetPill, pillName, StringComparison.OrdinalIgnoreCase))
            seq.Add(PillContent.Flag);
        return seq;
    }

    /// <summary>What the given pill should be showing right now. Returns
    /// Base if rotation is off overall, or if nothing is assigned to this
    /// particular pill.</summary>
    public static PillContent GetCurrent(AppSettings s, string pillName)
    {
        if (!s.RotatingPillEnabled) return PillContent.Base;

        var seq = GetSequenceForPill(s, pillName);
        if (seq.Count <= 1) return PillContent.Base;

        int interval = Math.Max(3, s.RotatingPillIntervalSeconds);
        long secondsSinceEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int index = (int)((secondsSinceEpoch / interval) % seq.Count);
        return seq[index];
    }

    /// <summary>True right at the moment a rotation change lands - used to
    /// trigger the fade/blink transition exactly when content actually
    /// swaps, not continuously. Both rendering paths update roughly once a
    /// second, so this fires on the specific tick the slot index changes.</summary>
    public static bool JustChanged(AppSettings s, string pillName)
    {
        if (!s.RotatingPillEnabled) return false;
        var seq = GetSequenceForPill(s, pillName);
        if (seq.Count <= 1) return false;

        int interval = Math.Max(3, s.RotatingPillIntervalSeconds);
        long secondsSinceEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return (secondsSinceEpoch % interval) == 0;
    }
}
