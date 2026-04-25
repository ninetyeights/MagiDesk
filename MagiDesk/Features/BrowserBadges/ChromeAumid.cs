namespace MagiDesk.Features.BrowserBadges;

/// <summary>
/// Replicates Chrome's AppUserModelID computation from a profile directory.
/// Lets us build a deterministic AUMID → profile-directory map without
/// running UIA (which flips Chrome into its expensive "accessibility mode"
/// for the whole process) or trusting command-line args (which are
/// ambiguous in multi-profile browser-process mode).
///
/// Chrome has used multiple AUMID formats over its history:
///   Legacy "_crpwin" form (older builds): "Chrome._crpwin" + dir with ' '→'_'
///     e.g. "Profile 14" → "Chrome._crpwinProfile_14"
///   Current "UserData" form (observed on live Chrome 2024+):
///     "Chrome.UserData." + dir with spaces stripped
///     e.g. "Profile 14" → "Chrome.UserData.Profile14"
///   Default profile is always just "Chrome".
///
/// We prepopulate BOTH forms per profile so the AUMID lookup hits regardless
/// of which Chrome build is running. Without this, every new browser window
/// has to fall through to title / UIA / cmdline resolution — and on the
/// event-driven SHOW path UIA is disabled, so title-less new tabs end up
/// with no badge or, before this fix, a wrong "Default" badge from cmdline.
/// </summary>
internal static class ChromeAumid
{
    public const string BaseAppId = "Chrome";

    /// <summary>Primary current-Chrome formula. Returned by
    /// <c>WindowAumid.Read</c> for every profile window observed on
    /// recent installs (logged as <c>Chrome.UserData.&lt;slug&gt;</c>).</summary>
    public static string Compute(string profileDir)
    {
        if (string.Equals(profileDir, "Default", StringComparison.OrdinalIgnoreCase))
            return BaseAppId;
        string slug = profileDir.Replace(" ", string.Empty);
        return $"{BaseAppId}.UserData.{slug}";
    }

    /// <summary>Legacy formula kept for older Chrome builds and for
    /// installations where taskbar pinning still uses the older AUMID. Both
    /// values should be prepopulated into the AUMID cache.</summary>
    public static string ComputeLegacy(string profileDir)
    {
        if (string.Equals(profileDir, "Default", StringComparison.OrdinalIgnoreCase))
            return BaseAppId;
        string slug = profileDir.Replace(' ', '_');
        return $"{BaseAppId}._crpwin{slug}";
    }
}
