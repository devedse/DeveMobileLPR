using Android.Content;
using Android.Views;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class AndroidVideoTextureView(Context context) : TextureView(context)
{
    private float _videoAspectRatio;

    public void SetVideoAspectRatio(float value)
    {
        if (value <= 0 || Math.Abs(_videoAspectRatio - value) < 0.001f)
        {
            return;
        }

        _videoAspectRatio = value;
        RequestLayout();
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        var width = MeasureSpec.GetSize(widthMeasureSpec);
        var height = MeasureSpec.GetSize(heightMeasureSpec);
        if (_videoAspectRatio <= 0 || width <= 0 || height <= 0)
        {
            base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
            return;
        }

        if ((float)width / height > _videoAspectRatio)
        {
            width = Math.Max(1, (int)Math.Round(height * _videoAspectRatio));
        }
        else
        {
            height = Math.Max(1, (int)Math.Round(width / _videoAspectRatio));
        }

        SetMeasuredDimension(width, height);
    }
}
