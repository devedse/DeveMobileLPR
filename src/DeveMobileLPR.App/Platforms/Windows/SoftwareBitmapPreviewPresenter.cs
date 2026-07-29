using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using WinUIImage = Microsoft.UI.Xaml.Controls.Image;

namespace DeveMobileLPR.App.Platforms.Windows;

/// <summary>
/// Owns the bitmap currently presented by a WinUI <see cref="WinUIImage"/>.
/// </summary>
/// <remarks>
/// <see cref="PresentAsync"/> always takes ownership of its bitmap, including when
/// presentation fails. The previous bitmap stays alive until its replacement has
/// been accepted by <see cref="SoftwareBitmapSource"/>.
/// </remarks>
internal sealed class SoftwareBitmapPreviewPresenter : IAsyncDisposable
{
    private readonly WinUIImage _image;
    private readonly SoftwareBitmapSource _source = new();
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private SoftwareBitmap? _displayedBitmap;
    private int _presentationActive;
    private int _disposed;

    public SoftwareBitmapPreviewPresenter(WinUIImage image)
    {
        _image = image;
        _image.Source = null;
        _presentationActive = image.IsLoaded ? 1 : 0;
    }

    public bool IsPresentationActive => Volatile.Read(ref _presentationActive) != 0
        && Volatile.Read(ref _disposed) == 0;

    /// <summary>
    /// Attaches or detaches the preview source. This must be called on the UI thread.
    /// </summary>
    public void SetPresentationActive(bool active)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Volatile.Write(ref _presentationActive, active ? 1 : 0);
        _image.Source = active && Volatile.Read(ref _displayedBitmap) is not null
            ? _source
            : null;
    }

    /// <summary>
    /// Takes ownership of <paramref name="bitmap"/> and presents it.
    /// </summary>
    public async Task PresentAsync(SoftwareBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        SoftwareBitmap? ownedBitmap = bitmap;
        var gateEntered = false;
        try
        {
            await _updateGate.WaitAsync().ConfigureAwait(false);
            gateEntered = true;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            await ApplyBitmapAsync(ownedBitmap).ConfigureAwait(false);
            var previous = Interlocked.Exchange(ref _displayedBitmap, ownedBitmap);
            ownedBitmap = null;
            previous?.Dispose();
        }
        finally
        {
            if (gateEntered)
            {
                _updateGate.Release();
            }
            ownedBitmap?.Dispose();
        }
    }

    /// <summary>
    /// Replaces the video frame with a tiny transparent frame, allowing the full-size
    /// frame to be released without leaving a closed object behind the image source.
    /// </summary>
    public Task ClearAsync() => PresentAsync(new SoftwareBitmap(
        BitmapPixelFormat.Bgra8,
        1,
        1,
        BitmapAlphaMode.Premultiplied));

    private async Task ApplyBitmapAsync(SoftwareBitmap bitmap)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_image.DispatcherQueue.TryEnqueue(() =>
            _ = ApplyBitmapOnUiThreadAsync(bitmap, completion)))
        {
            throw new InvalidOperationException("The preview UI dispatcher is no longer available.");
        }

        await completion.Task.ConfigureAwait(false);
    }

    private async Task ApplyBitmapOnUiThreadAsync(
        SoftwareBitmap bitmap,
        TaskCompletionSource completion)
    {
        try
        {
            await _source.SetBitmapAsync(bitmap);
            _image.Source = IsPresentationActive ? _source : null;
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _presentationActive, 0);
        await _updateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DetachOnUiThreadAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Interlocked.Exchange(ref _displayedBitmap, null)?.Dispose();
            }
            finally
            {
                _updateGate.Release();
                _updateGate.Dispose();
            }
        }
    }

    private async Task DetachOnUiThreadAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_image.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _image.Source = null;
                    _source.Dispose();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            return;
        }

        await completion.Task.ConfigureAwait(false);
    }
}
