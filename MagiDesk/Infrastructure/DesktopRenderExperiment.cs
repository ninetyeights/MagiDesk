namespace MagiDesk.Infrastructure;

internal static class DesktopRenderExperiment
{
    // A/B test completed: disabling label shadows did not remove the Render stalls.
    // Deliberately not persisted in user configuration; affects desktop labels only.
    internal static readonly bool DisableDesktopLabelShadow = false;
}
