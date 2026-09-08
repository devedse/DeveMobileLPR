using AVFoundation;
using CoreAnimation;
using UIKit;

namespace DeveMobileLPR.App;

/// <summary>iOS visual host only; camera acquisition is composed by the input factory.</summary>
internal sealed class IosCameraPreviewHost : UIView
{
    private CALayer? _preview;

    public IosCameraPreviewHost() => BackgroundColor = UIColor.FromRGB(11, 13, 16);

    public void Attach(AVCaptureSession session)
    {
        var preview = AVCaptureVideoPreviewLayer.FromSession(session);
        preview.VideoGravity = AVLayerVideoGravity.ResizeAspectFill;
        Attach(preview);
    }

    public void Attach(AVPlayer player)
    {
        var preview = AVPlayerLayer.FromPlayer(player);
        preview.VideoGravity = AVLayerVideoGravity.ResizeAspect;
        Attach(preview);
    }

    private void Attach(CALayer preview)
    {
        _preview?.RemoveFromSuperLayer();
        _preview?.Dispose();
        _preview = preview;
        Layer.InsertSublayer(preview, 0);
        SetNeedsLayout();
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        if (_preview is not null) _preview.Frame = Bounds;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _preview?.RemoveFromSuperLayer();
            _preview?.Dispose();
            _preview = null;
        }
        base.Dispose(disposing);
    }
}
