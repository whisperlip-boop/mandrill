using Mandrill.Core.Shell;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.DataTransfer;

namespace Mandrill.App.Views;

/// <summary>
/// What a drag out of a pane or the search results carries (WP-3.4, WP-3.5 AC-3.20, ADR-0011): Mandrill's own path format at
/// once, and StorageItems resolved off the UI thread only when a target asks for them (S3: 2.1 s per 1,000 items).
/// </summary>
public static class DragOutData
{
    /// <summary>Fills <paramref name="data"/> with <paramref name="paths"/> for a copy or move drag.</summary>
    /// <param name="data">The drag's data package.</param>
    /// <param name="paths">Full paths of the dragged items (from one folder or several).</param>
    /// <param name="logger">Logger (perf line, skipped items, the 10 s limit, failures).</param>
    public static void Fill(DataPackage data, IReadOnlyList<string> paths, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        data.SetData(DragDropRules.PathsFormat, string.Join("\r\n", paths));
        data.SetDataProvider(StandardDataFormats.StorageItems, request =>
        {
            var deferral = request.GetDeferral();
            _ = ProvideAsync();

            async Task ProvideAsync()
            {
                try
                {
                    request.SetData(await Task.Run(() => DragOutItems.ToStorageItemsAsync(paths, logger, DragOutItems.DefaultLimit, CancellationToken.None)));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Providing the dragged items failed");
                }
                finally
                {
                    deferral.Complete();
                }
            }
        });
    }
}
