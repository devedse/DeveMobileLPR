using AndroidX.Media3.ExoPlayer.Video;
using Media3Format = AndroidX.Media3.Common.Format;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class AndroidHlsVideoMetadataListener(
    Action<Media3Format> onFrame) : Java.Lang.Object, IVideoFrameMetadataListener
{
    public void OnVideoFrameAboutToBeRendered(
        long presentationTimeUs,
        long releaseTimeNs,
        Media3Format? format,
        global::Android.Media.MediaFormat? mediaFormat)
    {
        if (format is not null)
        {
            onFrame(format);
        }
    }
}
