using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace HistoryAurora.Shell.Components.Widgets;

public partial class AuroraChatTimeline
{
    private readonly HashSet<Button> _imageButtons = [];
    private readonly Dictionary<string, Task<BitmapSource>> _thumbnails = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _imageSlots = new(2);
    private CancellationTokenSource _imageCancellation = new();
    private int _previewGeneration;
    public Func<string, CancellationToken, Task<byte[]>>? ImageLoader { get; set; }

    private void ResetImages()
    {
        _imageCancellation.Cancel();
        _imageCancellation.Dispose();
        _imageCancellation = new();
        _thumbnails.Clear();
        foreach (var button in _imageButtons)
            if (button.DataContext is ChatImageCard card) card.Clear();
        ClosePreview();
    }

    private void ImageLoaded(object sender, RoutedEventArgs e)
    {
        _imageButtons.Add((Button)sender);
        UpdateVisibleImages();
    }

    private void ImageUnloaded(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        _imageButtons.Remove(button);
        if (button.DataContext is ChatImageCard card) card.Clear();
    }

    private bool ImageVisible(Button button)
    {
        if (!IsVisible || !button.IsVisible || !button.IsDescendantOf(Viewport)) return false;
        var bounds = button.TransformToAncestor(Viewport).TransformBounds(new Rect(button.RenderSize));
        return bounds.IntersectsWith(new Rect(0, 0, Viewport.ActualWidth, Viewport.ActualHeight));
    }

    private void UpdateVisibleImages()
    {
        foreach (var button in _imageButtons.ToArray())
        {
            if (button.DataContext is not ChatImageCard card) continue;
            if (ImageVisible(button)) _ = LoadThumbnail(button, card);
            else card.Clear();
        }
    }

    private async Task LoadThumbnail(Button button, ChatImageCard card)
    {
        if (card.Loading || card.Source != null || card.Failed) return;
        var loader = ImageLoader;
        if (card.Path.Length == 0) { card.Fail("图片原件暂不可用"); return; }
        if (loader == null) { card.Fail("图片预览不可用"); return; }
        var cancellation = _imageCancellation.Token;
        card.Loading = true;
        card.Set(null, "正在加载图片…");
        try
        {
            if (!_thumbnails.TryGetValue(card.Path, out var pending))
            {
                // 只缓存 24 张缩略图；离开可见区域的卡片释放自己的位图引用。
                if (_thumbnails.Count >= 24) _thumbnails.Remove(_thumbnails.Keys.First());
                pending = LoadBitmap(loader, card.Path, 360, cancellation);
                _thumbnails[card.Path] = pending;
            }
            var bitmap = await pending;
            if (!cancellation.IsCancellationRequested && ImageVisible(button)) card.Set(bitmap, "");
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!cancellation.IsCancellationRequested)
            {
                _thumbnails.Remove(card.Path);
                card.Fail("图片加载失败，点击重试");
            }
        }
        finally { card.Loading = false; }
    }

    private async Task<BitmapSource> LoadBitmap(Func<string, CancellationToken, Task<byte[]>> loader, string path, int size, CancellationToken cancellation)
    {
        await _imageSlots.WaitAsync(cancellation);
        try
        {
            return await Task.Run(async () =>
            {
                var bytes = await loader(path, cancellation);
                cancellation.ThrowIfCancellationRequested();
                return DecodeImage(bytes, size);
            }, cancellation);
        }
        finally { _imageSlots.Release(); }
    }

    internal static BitmapSource DecodeImage(byte[] bytes, int size)
    {
        if (bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("图片大小超限");
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || (long)frame.PixelWidth * frame.PixelHeight > 40000000)
            throw new InvalidDataException("图片尺寸超限");
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (frame.PixelWidth >= frame.PixelHeight) bitmap.DecodePixelWidth = Math.Min(size, frame.PixelWidth);
        else bitmap.DecodePixelHeight = Math.Min(size, frame.PixelHeight);
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private async void ImageClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ChatImageCard card || card.Path.Length == 0) return;
        if (card.Source == null)
        {
            card.Failed = false;
            await LoadThumbnail(button, card);
            return;
        }
        var loader = ImageLoader;
        if (loader == null) return;
        var generation = ++_previewGeneration;
        var cancellation = _imageCancellation.Token;
        Dispatcher.Invoke(() =>
        {
            ImageTitle.Text = card.Name;
            PreviewImage.Source = card.Source;
            PreviewStatus.Text = "正在加载大图…";
            ImageOverlay.Visibility = Visibility.Visible;
            CloseImage.Focus();
        });
        try
        {
            var bitmap = await LoadBitmap(loader, card.Path, 1600, cancellation);
            if (generation != _previewGeneration || cancellation.IsCancellationRequested) return;
            Dispatcher.Invoke(() =>
            {
                PreviewImage.Source = bitmap;
                PreviewStatus.Text = "";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (generation == _previewGeneration) Dispatcher.Invoke(() => PreviewStatus.Text = "大图加载失败，可关闭后重试");
        }
    }

    private void ClosePreview()
    {
        _previewGeneration++;
        ImageOverlay.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
    }
    private void CloseImageClick(object sender, RoutedEventArgs e) => ClosePreview();
    private void ImagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { ClosePreview(); e.Handled = true; }
    }
}

internal sealed class ChatImageCard(string name, string path) : INotifyPropertyChanged
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public BitmapSource? Source { get; private set; }
    public string Status { get; private set; } = path.Length == 0 ? "图片原件暂不可用" : "图片";
    public bool Loading { get; set; }
    public bool Failed { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Set(BitmapSource? source, string status)
    {
        Source = source; Status = status;
        PropertyChanged?.Invoke(this, new(nameof(Source)));
        PropertyChanged?.Invoke(this, new(nameof(Status)));
    }
    public void Fail(string status) { Failed = true; Set(null, status); }
    public void Clear() { if (Source != null) Set(null, "图片"); }
    public static IReadOnlyList<ChatImageCard> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateArray().Take(16).Select(item => new ChatImageCard(
                item.GetProperty("name").GetString() ?? "图片", item.GetProperty("path").GetString() ?? "")).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { return [new("图片数据无效", "")]; }
    }
}
