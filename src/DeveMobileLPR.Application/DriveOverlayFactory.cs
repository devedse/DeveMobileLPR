using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application;

/// <summary>
/// Maps recognition results onto the overlay model that the renderer consumes. Live capture and
/// offline video analysis both go through here, so a detection looks and reads the same whether it
/// came from the camera or from a replayed file.
/// </summary>
public static class DriveOverlayFactory
{
    /// <summary>Per-frame OCR reads that have not yet reached consensus.</summary>
    public static IEnumerable<DriveOverlay> CreateReadingOverlays(
        IEnumerable<PlateObservation> observations,
        int sourceWidth,
        int sourceHeight) => observations.Select(observation => new DriveOverlay(
            observation.Detection.Bounds,
            sourceWidth,
            sourceHeight,
            FormatPlate(observation.Read.Text),
            $"Reading · {observation.Read.Confidence:P0}",
            observation.Detection.Confidence,
            DriveOverlayKind.Reading));

    /// <summary>
    /// Detector candidates, predicted track positions, and live track boxes. Only shown while
    /// tracking diagnostics are enabled, so the detail lines are deliberately dense.
    /// </summary>
    public static IEnumerable<DriveOverlay> CreateDiagnosticOverlays(
        RecognitionStreamDiagnostics diagnostics,
        int sourceWidth,
        int sourceHeight)
    {
        var candidates = diagnostics.Frame.Candidates.Select(candidate => new DriveOverlay(
            candidate.Detection.Bounds,
            sourceWidth,
            sourceHeight,
            string.IsNullOrWhiteSpace(candidate.ReadText) ? "Detector candidate" : FormatPlate(candidate.ReadText),
            candidate.OcrAttempted
                ? $"det {candidate.Detection.Confidence:P0} · OCR {candidate.OcrConfidence:P0} · quality {candidate.Quality:P0}"
                : $"det {candidate.Detection.Confidence:P0} · OCR not attempted",
            candidate.Detection.Confidence,
            DriveOverlayKind.Candidate));

        var predictions = diagnostics.Associations
            .Where(static association => association.PredictedBounds is not null)
            .Select(association => new DriveOverlay(
                association.PredictedBounds!.Value,
                sourceWidth,
                sourceHeight,
                $"{FormatTrackId(association.TrackId)} · prediction",
                AssociationDiagnosticsFormatter.Format(association),
                association.Score ?? 0,
                DriveOverlayKind.Prediction));

        var tracks = diagnostics.Tracks.Select(track =>
        {
            var association = diagnostics.Associations.FirstOrDefault(item => item.TrackId == track.TrackId);
            return new DriveOverlay(
                track.Bounds,
                sourceWidth,
                sourceHeight,
                $"{FormatTrackId(track.TrackId)} · {FormatPlate(track.LastRead)}",
                $"{track.ObservationCount} obs · {(association is null
                    ? "not observed this frame"
                    : AssociationDiagnosticsFormatter.Format(association))}",
                track.DetectorConfidence,
                DriveOverlayKind.Track);
        });

        return candidates.Concat(predictions).Concat(tracks);
    }

    /// <summary>
    /// Overlays for a single frame of an offline analysis. Unlike live capture there is no linger
    /// window, because the frame is paused and every confirmation for it is already known.
    /// </summary>
    public static IReadOnlyList<DriveOverlay> CreateAnalyzedFrameOverlays(
        AnalyzedVideoFrame frame,
        bool includeDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var overlays = new List<DriveOverlay>();
        if (includeDiagnostics && frame.Diagnostics is { } diagnostics)
        {
            overlays.AddRange(CreateDiagnosticOverlays(diagnostics, frame.SourceWidth, frame.SourceHeight));
        }
        overlays.AddRange(frame.Reads.Select(read => new DriveOverlay(
            read.Bounds,
            frame.SourceWidth,
            frame.SourceHeight,
            FormatPlate(read.Text),
            $"Reading · {read.OcrConfidence:P0}",
            read.DetectorConfidence,
            DriveOverlayKind.Reading)));
        overlays.AddRange(frame.Confirmations.Select(confirmation => new DriveOverlay(
            confirmation.Bounds,
            frame.SourceWidth,
            frame.SourceHeight,
            confirmation.DisplayPlate,
            $"{confirmation.ObservationCount} obs · {confirmation.Confidence:P0}",
            confirmation.Confidence,
            DriveOverlayKind.Confirmed)));
        // Ordered by kind so confirmations draw over the reads and candidates they came from,
        // matching how DriveOverlayLayout orders the live drive view.
        return overlays.OrderBy(static overlay => overlay.Kind).ToArray();
    }

    public static string FormatPlate(string value)
    {
        var normalized = PlateText.Normalize(value);
        return normalized.Length == 6 ? PlateText.FormatDutchPlate(normalized) : value.ToUpperInvariant();
    }

    private static string FormatTrackId(Guid value) => $"T{value.ToString("N")[..6]}";
}
