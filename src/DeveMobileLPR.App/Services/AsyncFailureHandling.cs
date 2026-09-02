namespace DeveMobileLPR.App.Services;

internal static class AsyncFailureHandling
{
    public static async Task RunSafelyAsync(
        this Page page,
        string title,
        Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppLogService.RecordFailure("UI", exception);
            try
            {
                await page.DisplayAlertAsync(title, exception.Message, "OK");
            }
            catch (Exception alertException)
            {
                AppLogService.RecordFailure("UI", alertException);
            }
        }
    }

    public static async void ObserveFailure(this Task task, string category)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppLogService.RecordFailure(category, exception);
        }
    }
}
