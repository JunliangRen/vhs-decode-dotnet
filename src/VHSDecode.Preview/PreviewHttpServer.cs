using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VHSDecode.Preview;

public sealed class PreviewHttpServer : IAsyncDisposable
{
    private const string HlsContentType = "application/vnd.apple.mpegurl";
    private const string Fmp4ContentType = "video/mp4";
    private readonly WebApplication _application;
    private readonly PreviewSegmentCache _cache;
    private bool _disposed;

    private PreviewHttpServer(
        WebApplication application,
        PreviewSegmentCache cache,
        Uri baseAddress,
        PreviewMediaInfo mediaInfo)
    {
        _application = application;
        _cache = cache;
        BaseAddress = baseAddress;
        MediaInfo = mediaInfo;
    }

    public Uri BaseAddress { get; }

    public Uri PlaylistAddress => new(BaseAddress, "hls/index.m3u8");

    public PreviewMediaInfo MediaInfo { get; }

    public static async Task<PreviewHttpServer> StartAsync(
        IPreviewSegmentProvider provider,
        PreviewServerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var cache = new PreviewSegmentCache(provider, options.CacheWindowCount);
        try
        {
            int lastPort = options.Port == 0
                ? 0
                : (int)Math.Min(
                    IPEndPoint.MaxPort,
                    (long)options.Port + options.PortFallbackCount);
            for (int candidatePort = options.Port;
                 candidatePort <= lastPort;
                 candidatePort++)
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
                {
                    ApplicationName = typeof(PreviewHttpServer).Assembly.FullName,
                    Args = []
                });
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(server =>
                    server.Listen(IPAddress.Loopback, candidatePort));
                WebApplication app = builder.Build();

                app.Use(async (context, next) =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    try
                    {
                        await next(context).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                    {
                        // The media player abandoned this request, usually after a seek.
                    }
                    catch (Exception ex) when (!context.Response.HasStarted)
                    {
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsJsonAsync(
                            new { error = ex.Message },
                            context.RequestAborted).ConfigureAwait(false);
                    }
                });

                app.MapGet("/", () => Results.Content(PlayerHtml, "text/html", Encoding.UTF8));
                app.MapGet("/health", () => Results.Json(new { status = "ok" }));
                app.MapGet("/api/info", () => Results.Json(new
                {
                    provider.MediaInfo.SourceKind,
                    provider.MediaInfo.System,
                    provider.MediaInfo.FramesPerSecond,
                    provider.MediaInfo.DurationSeconds,
                    provider.MediaInfo.Width,
                    provider.MediaInfo.Height,
                    provider.MediaInfo.Crf,
                    provider.MediaInfo.Interlaced,
                    provider.MediaInfo.DecodeBackend,
                    provider.MediaInfo.AccuracyProfile,
                    provider.MediaInfo.EncodeBackend,
                    provider.Timeline.SegmentCount,
                    provider.Timeline.WindowCount,
                    WindowSeconds = provider.Timeline.WindowCount > 1
                        ? provider.Timeline.WindowStartSeconds(1)
                        : provider.Timeline.DurationSeconds
                }));
                app.MapGet("/hls/master.m3u8", () => Results.Text(
                    HlsPlaylistBuilder.BuildMaster(provider.MediaInfo),
                    HlsContentType,
                    Encoding.UTF8));
                app.MapGet("/hls/index.m3u8", () => Results.Text(
                    HlsPlaylistBuilder.BuildMedia(provider.Timeline),
                    HlsContentType,
                    Encoding.UTF8));
                app.MapGet("/hls/window/{windowIndex:int}/init.mp4", async (
                    int windowIndex,
                    HttpContext context) =>
                {
                    if ((uint)windowIndex >= (uint)provider.Timeline.WindowCount)
                    {
                        return Results.NotFound();
                    }

                    PreviewSegmentWindow window = await cache.GetWindowAsync(
                        windowIndex,
                        context.RequestAborted).ConfigureAwait(false);
                    return Results.Bytes(window.InitializationSegment, Fmp4ContentType);
                });
                app.MapGet("/hls/window/{windowIndex:int}/segment/{localIndex:int}.m4s", async (
                    int windowIndex,
                    int localIndex,
                    HttpContext context) =>
                {
                    if ((uint)windowIndex >= (uint)provider.Timeline.WindowCount
                        || (uint)localIndex >= (uint)provider.Timeline.SegmentCountInWindow(windowIndex))
                    {
                        return Results.NotFound();
                    }

                    PreviewSegmentWindow window = await cache.GetWindowAsync(
                        windowIndex,
                        context.RequestAborted).ConfigureAwait(false);
                    PreviewMediaSegment? segment = window.Segments.FirstOrDefault(item =>
                        item.LocalIndex == localIndex);
                    return segment is null
                        ? Results.NotFound()
                        : Results.Bytes(segment.Data, Fmp4ContentType);
                });

                try
                {
                    await app.StartAsync(cancellationToken).ConfigureAwait(false);
                    IServer server = app.Services.GetRequiredService<IServer>();
                    IServerAddressesFeature addresses = server.Features
                        .Get<IServerAddressesFeature>()
                        ?? throw new InvalidOperationException("Kestrel did not publish a preview address.");
                    Uri baseAddress = addresses.Addresses
                        .Select(address => new Uri(address.EndsWith('/', StringComparison.Ordinal)
                            ? address
                            : address + "/"))
                        .First(address => IPAddress.TryParse(address.Host, out IPAddress? ip)
                            && IPAddress.IsLoopback(ip));
                    return new PreviewHttpServer(app, cache, baseAddress, provider.MediaInfo);
                }
                catch (Exception ex) when (candidatePort < lastPort && IsAddressInUse(ex))
                {
                    await app.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    await app.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            throw new IOException("No preview server port candidate could be started.");
        }
        catch
        {
            await cache.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsAddressInUse(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AddressInUseException
                || current is SocketException socket
                    && socket.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return true;
            }
        }

        return false;
    }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
        => _application.WaitForShutdownAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Task stopTask = _application.StopAsync();
        Task cacheDisposeTask = _cache.DisposeAsync().AsTask();
        try
        {
            await Task.WhenAll(stopTask, cacheDisposeTask).ConfigureAwait(false);
        }
        finally
        {
            await _application.DisposeAsync().ConfigureAwait(false);
        }
    }

    private const string PlayerHtml = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>VHS Decode Preview</title>
          <style>
            :root { color-scheme: dark; font-family: system-ui, sans-serif; background: #111; color: #eee; }
            body { margin: 0; }
            main { width: min(960px, calc(100vw - 32px)); margin: 24px auto; }
            h1 { font-size: 20px; font-weight: 600; }
            video { display: block; width: 100%; max-height: 72vh; background: #000; }
            .timeline { display: grid; grid-template-columns: 1fr auto; gap: 12px; align-items: center; margin-top: 14px; }
            input[type="range"] { width: 100%; }
            p { color: #bbb; }
            a { color: #8ab4f8; }
          </style>
        </head>
        <body>
          <main>
            <h1>VHS Decode Preview</h1>
            <video id="preview" controls autoplay muted playsinline preload="none"></video>
            <div class="timeline">
              <input id="timeline" type="range" min="0" max="0" step="0.1" value="0" aria-label="Preview position">
              <output id="time">00:00 / 00:00</output>
            </div>
            <p id="status">Loading preview timeline...</p>
            <p><a href="/hls/index.m3u8">Open the HLS playlist in mpv or VLC</a></p>
          </main>
          <script>
            const video = document.getElementById('preview');
            const timeline = document.getElementById('timeline');
            const time = document.getElementById('time');
            const status = document.getElementById('status');
            const codec = 'video/mp4; codecs="avc1.4d401f"';
            const diagnostics = window.previewDiagnostics = {
              ready: false,
              lastRequestedWindow: -1,
              lastLoadedWindow: -1,
              requestStartedAt: 0,
              requestCompletedAt: 0,
              errors: []
            };
            let info;
            let mediaSource;
            let sourceBuffer;
            let appendQueue = [];
            let dragging = false;
            let nativeSeekTimer;
            let initialPlaybackAttempted = false;
            const windowRequests = new Map();

            const startInitialPlayback = () => {
              if (initialPlaybackAttempted) return;
              initialPlaybackAttempted = true;
              video.play().catch(() => {
                status.textContent = 'Ready. Press Play to start the preview.';
              });
            };

            const formatTime = value => {
              const seconds = Math.max(0, Math.floor(Number.isFinite(value) ? value : 0));
              const hours = Math.floor(seconds / 3600);
              const minutes = Math.floor((seconds % 3600) / 60);
              const remainder = seconds % 60;
              return hours > 0
                ? `${hours}:${String(minutes).padStart(2, '0')}:${String(remainder).padStart(2, '0')}`
                : `${String(minutes).padStart(2, '0')}:${String(remainder).padStart(2, '0')}`;
            };

            const updateClock = value => {
              const position = Number.isFinite(value) ? value : 0;
              time.textContent = `${formatTime(position)} / ${formatTime(info?.durationSeconds ?? 0)}`;
            };

            const appendNext = () => {
              if (!sourceBuffer || sourceBuffer.updating || appendQueue.length === 0) return;
              const item = appendQueue.shift();
              const failAppend = error => {
                sourceBuffer.removeEventListener('updateend', completeAppend);
                sourceBuffer.removeEventListener('error', failAppend);
                if (windowRequests.get(item.windowIndex) === item.controller) {
                  windowRequests.delete(item.windowIndex);
                }
                appendQueue = appendQueue.filter(queued => queued.windowIndex !== item.windowIndex);
                diagnostics.errors.push(String(error));
                status.textContent = `Preview buffer error: ${error}`;
                appendNext();
              };
              const completeAppend = () => {
                sourceBuffer.removeEventListener('error', failAppend);
                if (item.kind === 'media') {
                  if (windowRequests.get(item.windowIndex) === item.controller) {
                    windowRequests.delete(item.windowIndex);
                  }
                  diagnostics.lastLoadedWindow = item.windowIndex;
                  diagnostics.requestCompletedAt = performance.now();
                  diagnostics.ready = true;
                  status.textContent = `Ready at ${formatTime(item.windowIndex * info.windowSeconds)}. Drag either progress bar to seek.`;
                  startInitialPlayback();
                }
                appendNext();
              };
              sourceBuffer.addEventListener('updateend', completeAppend, { once: true });
              sourceBuffer.addEventListener('error', failAppend, { once: true });
              try {
                sourceBuffer.appendBuffer(item.data);
              } catch (error) {
                failAppend(error);
              }
            };

            const isWindowBuffered = windowIndex => {
              if (!sourceBuffer || !info) return false;
              const start = windowIndex * info.windowSeconds;
              const end = Math.min(info.durationSeconds, start + info.windowSeconds);
              const probe = Math.min(end - 0.001, start + Math.min(0.05, info.windowSeconds / 2));
              for (let range = 0; range < sourceBuffer.buffered.length; range++) {
                if (sourceBuffer.buffered.start(range) <= probe
                    && sourceBuffer.buffered.end(range) > probe) return true;
              }
              return false;
            };

            const prioritizeWindow = windowIndex => {
              for (const [requestedWindow, controller] of windowRequests) {
                if (requestedWindow === windowIndex) continue;
                controller.abort();
                windowRequests.delete(requestedWindow);
              }
              appendQueue = appendQueue.filter(item => item.windowIndex === windowIndex);
            };

            const ensureWindow = async windowIndex => {
              if (!info || !sourceBuffer || windowIndex < 0 || windowIndex >= info.windowCount
                  || windowRequests.has(windowIndex) || isWindowBuffered(windowIndex)) return;
              const controller = new AbortController();
              windowRequests.set(windowIndex, controller);
              diagnostics.lastRequestedWindow = windowIndex;
              diagnostics.requestStartedAt = performance.now();
              status.textContent = `Decoding preview at ${formatTime(windowIndex * info.windowSeconds)}...`;
              try {
                const [initResponse, mediaResponse] = await Promise.all([
                  fetch(`/hls/window/${windowIndex}/init.mp4`, { signal: controller.signal }),
                  fetch(`/hls/window/${windowIndex}/segment/0.m4s`, { signal: controller.signal })
                ]);
                if (!initResponse.ok || !mediaResponse.ok) {
                  throw new Error(`HTTP ${initResponse.status}/${mediaResponse.status}`);
                }
                const [initData, mediaData] = await Promise.all([
                  initResponse.arrayBuffer(),
                  mediaResponse.arrayBuffer()
                ]);
                if (controller.signal.aborted
                    || windowRequests.get(windowIndex) !== controller) return;
                appendQueue.push(
                  { kind: 'init', windowIndex, controller, data: initData },
                  { kind: 'media', windowIndex, controller, data: mediaData });
                appendNext();
              } catch (error) {
                if (windowRequests.get(windowIndex) === controller) {
                  windowRequests.delete(windowIndex);
                }
                if (error?.name === 'AbortError') return;
                diagnostics.errors.push(String(error));
                status.textContent = `Preview error: ${error}`;
              }
            };

            const windowForTime = value => Math.min(
              info.windowCount - 1,
              Math.max(0, Math.floor(value / info.windowSeconds)));

            const ensurePlaybackWindows = windowIndex => {
              ensureWindow(windowIndex);
              ensureWindow(windowIndex + 1);
              ensureWindow(windowIndex + 2);
            };

            const seekTo = value => {
              const target = Math.min(info.durationSeconds - 0.001, Math.max(0, value));
              const targetWindow = windowForTime(target);
              prioritizeWindow(targetWindow);
              video.currentTime = target;
              timeline.value = String(target);
              updateClock(target);
              ensureWindow(targetWindow);
            };

            timeline.addEventListener('pointerdown', () => { dragging = true; });
            timeline.addEventListener('pointerup', () => { dragging = false; });
            timeline.addEventListener('input', () => updateClock(Number(timeline.value)));
            timeline.addEventListener('change', () => seekTo(Number(timeline.value)));
            video.addEventListener('seeking', () => {
              clearTimeout(nativeSeekTimer);
              nativeSeekTimer = setTimeout(() => {
                const targetWindow = windowForTime(video.currentTime);
                prioritizeWindow(targetWindow);
                ensureWindow(targetWindow);
              }, 150);
            });
            video.addEventListener('play', () => {
              const currentWindow = windowForTime(video.currentTime);
              ensurePlaybackWindows(currentWindow);
            });
            video.addEventListener('timeupdate', () => {
              if (!dragging) timeline.value = String(video.currentTime);
              updateClock(video.currentTime);
              if (video.seeking) return;
              const currentWindow = windowForTime(video.currentTime);
              ensurePlaybackWindows(currentWindow);
            });
            video.addEventListener('waiting', () => ensurePlaybackWindows(windowForTime(video.currentTime)));

            (async () => {
              try {
                info = await fetch('/api/info').then(response => {
                  if (!response.ok) throw new Error(`HTTP ${response.status}`);
                  return response.json();
                });
                timeline.max = String(info.durationSeconds);
                updateClock(0);
                if ('MediaSource' in window && MediaSource.isTypeSupported(codec)) {
                  mediaSource = new MediaSource();
                  video.src = URL.createObjectURL(mediaSource);
                  await new Promise(resolve => mediaSource.addEventListener('sourceopen', resolve, { once: true }));
                  sourceBuffer = mediaSource.addSourceBuffer(codec);
                  mediaSource.duration = info.durationSeconds;
                  sourceBuffer.addEventListener('error', () => {
                    diagnostics.errors.push('SourceBuffer error');
                    status.textContent = 'Preview error: browser rejected an fMP4 segment.';
                  });
                  await ensureWindow(0);
                } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
                  video.src = '/hls/index.m3u8';
                  diagnostics.ready = true;
                  status.textContent = 'Ready. Use the native progress bar to seek.';
                  startInitialPlayback();
                } else {
                  status.textContent = 'This browser has no fMP4/HLS support. Open the playlist link in mpv or VLC.';
                }
              } catch (error) {
                diagnostics.errors.push(String(error));
                status.textContent = `Preview startup error: ${error}`;
              }
            })();
          </script>
        </body>
        </html>
        """;
}
