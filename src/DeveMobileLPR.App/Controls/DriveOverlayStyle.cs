using DeveMobileLPR.Application;

namespace DeveMobileLPR.App.Controls;

/// <summary>
/// The visual contract for detection overlays: one entry per <see cref="DriveOverlayKind"/>, so a
/// kind cannot be given one appearance in the live view and another in analysis review.
/// </summary>
internal sealed record DriveOverlayStyle(
    Color Accent,
    Color LabelBackground,
    Color TitleColor,
    Color DetailColor,
    float StrokeSize,
    float[]? StrokeDashPattern = null)
{
    private static readonly Color Reading = Color.FromArgb("#58E0C2");
    private static readonly Color Confirmed = Color.FromArgb("#F5C542");
    private static readonly Color Known = Color.FromArgb("#D77BFF");
    private static readonly Color Candidate = Color.FromArgb("#55A7FF");
    private static readonly Color Prediction = Color.FromArgb("#FF9F43");
    private static readonly Color DarkLabel = Color.FromArgb("#E80B0D10");
    private static readonly Color LightText = Colors.White;

    /// <summary>Corner radius shared by detection boxes and their labels.</summary>
    public const float CornerRadius = 8;

    public static DriveOverlayStyle For(DriveOverlayKind kind) => kind switch
    {
        DriveOverlayKind.Candidate => new(
            Candidate, Color.FromArgb("#E8172A42"), LightText, Color.FromArgb("#C8DEFF"), 2f),
        DriveOverlayKind.Prediction => new(
            Prediction, Color.FromArgb("#DD4A2A0B"), LightText, Color.FromArgb("#FFD9A8"), 2f, [3, 3]),
        DriveOverlayKind.Reading => new(
            Reading, DarkLabel, LightText, Color.FromArgb("#BECDD6"), 2.25f),
        DriveOverlayKind.Track => new(
            Known, Color.FromArgb("#E834163D"), LightText, Color.FromArgb("#D6BEFF"), 2f, [6, 4]),
        DriveOverlayKind.Confirmed => new(
            Confirmed, Color.FromArgb("#F2F5C542"), Color.FromArgb("#141105"), Color.FromArgb("#2D260A"), 3.5f),
        DriveOverlayKind.ConfirmedKnown => new(
            Known, Color.FromArgb("#F22A163F"), LightText, Color.FromArgb("#D6BEFF"), 3.5f),
        // Every kind is styled above. This arm exists so that adding a kind without styling it
        // degrades to a plain box mid-drive instead of throwing out of the render pass.
        _ => new(Reading, DarkLabel, LightText, Color.FromArgb("#BECDD6"), 2.25f)
    };
}
