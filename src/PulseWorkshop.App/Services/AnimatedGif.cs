using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PulseWorkshop.App;

/// <summary>
/// Minimal, dependency-free animated-GIF playback for an <see cref="Image"/>. WPF's Image shows only
/// the first frame of a GIF, so this attached behavior decodes every frame - compositing partial
/// frames onto the full logical canvas and honoring each frame's disposal method - then cycles them
/// on a <see cref="DispatcherTimer"/> using each frame's own delay. It loops forever and stops when
/// the image leaves the visual tree.
///
/// Usage: <c>&lt;Image local:AnimatedGif.SourceUri="pack://application:,,,/Assets/foo.gif" /&gt;</c>
/// </summary>
public static class AnimatedGif
{
    public static readonly DependencyProperty SourceUriProperty =
        DependencyProperty.RegisterAttached(
            "SourceUri", typeof(Uri), typeof(AnimatedGif),
            new PropertyMetadata(null, OnSourceUriChanged));

    public static void SetSourceUri(DependencyObject o, Uri? value) => o.SetValue(SourceUriProperty, value);
    public static Uri? GetSourceUri(DependencyObject o) => (Uri?)o.GetValue(SourceUriProperty);

    // One live player per Image, so re-setting the URI (or the image unloading) tears the old one down.
    private static readonly Dictionary<Image, Player> Players = new();

    private static void OnSourceUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;

        if (Players.Remove(image, out var existing))
            existing.Dispose();

        if (e.NewValue is Uri uri)
        {
            var player = new Player(image, uri);
            Players[image] = player;
            player.Start();
        }
    }

    private sealed class Player : IDisposable
    {
        private readonly Image _image;
        private readonly List<BitmapSource> _frames = new();
        private readonly List<TimeSpan> _delays = new();
        private DispatcherTimer? _timer;
        private int _index;

        public Player(Image image, Uri uri)
        {
            _image = image;
            try { Decode(uri); }
            catch { /* Malformed/missing gif - leave whatever static source the Image already had. */ }
        }

        public void Start()
        {
            if (_frames.Count == 0) return;
            _image.Source = _frames[0];

            // Pause when the image leaves the visual tree (e.g. switching away from this tab) and
            // resume when it comes back - a TabControl unloads/reloads the non-selected tab's content,
            // so the animation must survive that round trip rather than being disposed for good.
            _image.Loaded += OnLoaded;
            _image.Unloaded += OnUnloaded;
            ResumeTimer();
        }

        private void ResumeTimer()
        {
            if (_frames.Count <= 1 || _timer is not null) return;
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = _delays[_index] };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private void PauseTimer()
        {
            if (_timer is null) return;
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            _index = (_index + 1) % _frames.Count;
            _image.Source = _frames[_index];
            _timer!.Interval = _delays[_index];
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => ResumeTimer();
        private void OnUnloaded(object sender, RoutedEventArgs e) => PauseTimer();

        private void Decode(Uri uri)
        {
            var decoder = new GifBitmapDecoder(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var screen = decoder.Metadata as BitmapMetadata;
            int canvasW = QueryInt(screen, "/logscrdesc/Width") ?? decoder.Frames[0].PixelWidth;
            int canvasH = QueryInt(screen, "/logscrdesc/Height") ?? decoder.Frames[0].PixelHeight;

            // Background the *upcoming* frame draws over, per the previous frame's disposal method.
            BitmapSource? background = null;

            foreach (var frame in decoder.Frames)
            {
                var meta = frame.Metadata as BitmapMetadata;
                int left = QueryInt(meta, "/imgdesc/Left") ?? 0;
                int top = QueryInt(meta, "/imgdesc/Top") ?? 0;
                int disposal = QueryInt(meta, "/grctlext/Disposal") ?? 0;
                int delayCs = QueryInt(meta, "/grctlext/Delay") ?? 10;

                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    if (background != null)
                        dc.DrawImage(background, new Rect(0, 0, canvasW, canvasH));
                    dc.DrawImage(frame, new Rect(left, top, frame.PixelWidth, frame.PixelHeight));
                }

                var composited = new RenderTargetBitmap(canvasW, canvasH, 96, 96, PixelFormats.Pbgra32);
                composited.Render(visual);
                composited.Freeze();
                _frames.Add(composited);

                // Browsers clamp very short delays to ~100ms; mirror that so 0/1cs exports aren't a blur.
                _delays.Add(TimeSpan.FromMilliseconds(delayCs <= 1 ? 100 : delayCs * 10));

                // Set the background for the next frame based on THIS frame's disposal method:
                // 2 = restore to background (clear), 3 = restore to previous (keep prior background),
                // 0/1 = leave in place (carry this composite forward).
                background = disposal switch
                {
                    2 => null,
                    3 => background,
                    _ => composited,
                };
            }
        }

        private static int? QueryInt(BitmapMetadata? meta, string query)
        {
            if (meta is null) return null;
            try
            {
                var value = meta.GetQuery(query);
                return value is null ? null : Convert.ToInt32(value);
            }
            catch { return null; }
        }

        public void Dispose()
        {
            _image.Loaded -= OnLoaded;
            _image.Unloaded -= OnUnloaded;
            PauseTimer();
        }
    }
}
