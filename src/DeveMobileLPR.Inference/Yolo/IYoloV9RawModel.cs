namespace DeveMobileLPR.Inference.Yolo;

/// <summary>
/// Platform runtime boundary for the fixed-output YOLOv9 detector graph.
/// Implementations execute the model only; preprocessing, duplicate removal,
/// coordinate mapping, OCR, and tracking remain shared.
/// </summary>
public interface IYoloV9RawModel : IDisposable
{
    string BackendName { get; }
    YoloV9InputLayout InputLayout { get; }

    YoloV9RawModelOutput Run(float[] input);
}

public enum YoloV9InputLayout
{
    ChannelsFirst,
    ChannelsLast
}

public readonly record struct YoloV9RawModelOutput(
    float[] Boxes,
    float[] Scores);
