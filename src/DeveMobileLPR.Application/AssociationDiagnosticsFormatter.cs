using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application;

internal static class AssociationDiagnosticsFormatter
{
    public static string Format(PlateTrackAssociation association)
    {
        if (association.Created || association.Kind == PlateAssociationKind.NewTrack)
        {
            return "new track";
        }

        var kind = association.Kind switch
        {
            PlateAssociationKind.ExactText => "exact text",
            PlateAssociationKind.SimilarText => $"similar text (edit {association.TextEditDistance})",
            PlateAssociationKind.PredictedMotion => "predicted motion",
            _ => "legacy IoU"
        };
        var geometry = association.FrameCenterDistance is { } distance
            ? $"move {distance:P1} · scale {association.ScaleRatio:F2}"
            : $"IoU {association.IntersectionOverUnion:F2}";
        var prediction = association.PredictedIntersectionOverUnion is { } predicted
            ? $" · predicted IoU {predicted:F2}"
            : string.Empty;
        var score = association.Score is { } value
            ? $" · score {value:F2}"
            : string.Empty;
        return $"{kind} · {geometry}{prediction}{score}";
    }
}
