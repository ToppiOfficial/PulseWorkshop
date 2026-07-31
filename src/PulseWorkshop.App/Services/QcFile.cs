using PulseWorkshop.Core.Services;

namespace PulseWorkshop.App.Services;

/// <summary>QC script conventions shared by the Compile tabs' file pickers.</summary>
public static class QcFile
{
    /// <summary>Open-dialog filter accepting studiomdl's <c>.qc</c> and PulseWorkshop's
    /// <c>.pulseqc</c> alias (see <see cref="ModelCompileService.PulseQcExtension"/>).</summary>
    public const string DialogFilter =
        "QC file (*.qc;*.pulseqc)|*.qc;*.pulseqc|All files (*.*)|*.*";
}
