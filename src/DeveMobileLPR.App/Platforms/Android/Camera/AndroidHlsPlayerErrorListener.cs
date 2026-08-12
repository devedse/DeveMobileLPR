using AndroidX.Media3.Common;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class AndroidHlsPlayerErrorListener(
    Action<PlaybackException> onError) : Java.Lang.Object, IPlayerListener
{
    public void OnPlayerError(PlaybackException? error)
    {
        if (error is not null)
        {
            onError(error);
        }
    }
}
