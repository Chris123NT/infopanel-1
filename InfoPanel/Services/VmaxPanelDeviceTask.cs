using InfoPanel.Drawing;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
using InfoPanel.VmaxPanel;
using Serilog;
using SkiaSharp;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class VmaxPanelDeviceTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<VmaxPanelDeviceTask>();

        private readonly VmaxPanelDevice _device;
        private int _panelWidth;
        private int _panelHeight;

        public VmaxPanelDeviceTask(VmaxPanelDevice device)
        {
            _device = device;
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            var modelInfo = _device.ModelInfo;
            if (modelInfo == null)
            {
                _device.UpdateRuntimeProperties(errorMessage: "Unknown model");
                return;
            }

            _panelWidth = modelInfo.Width;
            _panelHeight = modelInfo.Height;
            _device.UpdateRuntimeProperties(isRunning: false, errorMessage: string.Empty);
            _device.RuntimeProperties.Name = $"{modelInfo.Name} ({_panelWidth}x{_panelHeight})";

            int retryCount = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Logger.Information("VmaxPanelDevice {Device}: Opening USB device (attempt {Retry})",
                        _device, retryCount + 1);

                    using var vmaxDevice = VmaxUsbDevice.Open(_device.DeviceId);
                    if (vmaxDevice == null)
                    {
                        _device.UpdateRuntimeProperties(errorMessage: "Device not found");
                        await Task.Delay(2000, token);
                        retryCount++;
                        continue;
                    }

                    retryCount = 0;
                    await RunRenderSendLoop(vmaxDevice, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "VmaxPanelDevice {Device}: Error", _device);
                    _device.UpdateRuntimeProperties(errorMessage: ex.Message);
                    retryCount++;
                }
                finally
                {
                    _device.UpdateRuntimeProperties(isRunning: false);
                }

                if (!token.IsCancellationRequested)
                    await Task.Delay(retryCount < 3 ? 1000 : 5000, token);
            }
        }

        private async Task RunRenderSendLoop(VmaxUsbDevice vmaxDevice, CancellationToken token)
        {
            using var renderCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var renderToken = renderCts.Token;
            var frames = Channel.CreateBounded<RenderedFrame>(new BoundedChannelOptions(2)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            var renderTask = Task.Run(async () =>
            {
                try
                {
                    while (!renderToken.IsCancellationRequested)
                    {
                        var frame = GenerateBgr888Frame();
                        try
                        {
                            await frames.Writer.WriteAsync(frame, renderToken);
                        }
                        catch
                        {
                            ReturnFrame(frame);
                            throw;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    frames.Writer.TryComplete(ex);
                    return;
                }

                frames.Writer.TryComplete();
            }, System.Threading.CancellationToken.None);

            FpsCounter fpsCounter = new(30);
            var diagnosticsStopwatch = Stopwatch.StartNew();
            var diagnosticFrames = 0;
            long diagnosticFrameMs = 0;
            long diagnosticRenderMs = 0;
            long diagnosticResizeMs = 0;
            long diagnosticConvertMs = 0;
            long diagnosticSendMs = 0;
            long diagnosticScreenSwitchMs = 0;
            long diagnosticDelayMs = 0;
            _device.UpdateRuntimeProperties(isRunning: true, errorMessage: string.Empty);

            try
            {
                await foreach (var frame in frames.Reader.ReadAllAsync(token))
                {
                    try
                    {
                        var stopwatch = Stopwatch.StartNew();

                        var sendStopwatch = Stopwatch.StartNew();
                        vmaxDevice.SendRgb888Frame(frame.Buffer, frame.Length);
                        var sendMs = sendStopwatch.ElapsedMilliseconds;

                        var screenSwitchStopwatch = Stopwatch.StartNew();
                        vmaxDevice.SetScreenSwitch(_device.ScreenSwitch);
                        var screenSwitchMs = screenSwitchStopwatch.ElapsedMilliseconds;

                        fpsCounter.Update(stopwatch.ElapsedMilliseconds);
                        _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);

                        var targetFrameTime = 1000 / Math.Max(1, Math.Min(_device.TargetFrameRate, 30));
                        var delay = targetFrameTime - (int)stopwatch.ElapsedMilliseconds;

                        diagnosticFrames++;
                        diagnosticFrameMs += stopwatch.ElapsedMilliseconds;
                        diagnosticRenderMs += frame.RenderMs;
                        diagnosticResizeMs += frame.ResizeMs;
                        diagnosticConvertMs += frame.ConvertMs;
                        diagnosticSendMs += sendMs;
                        diagnosticScreenSwitchMs += screenSwitchMs;
                        diagnosticDelayMs += Math.Max(0, delay);

                        if (diagnosticsStopwatch.ElapsedMilliseconds >= 1000 && diagnosticFrames > 0)
                        {
                            Logger.Information(
                                "VmaxPanelDevice perf: target={TargetFps}fps actual={ActualFps}fps frame={FrameMs:F1}ms render={RenderMs:F1}ms resize={ResizeMs:F1}ms convert={ConvertMs:F1}ms usbWrite={UsbWriteMs:F1}ms screenSwitch={ScreenSwitchMs:F1}ms delay={DelayMs:F1}ms payload={PayloadBytes}",
                                _device.TargetFrameRate,
                                fpsCounter.FramesPerSecond,
                                (double)diagnosticFrameMs / diagnosticFrames,
                                (double)diagnosticRenderMs / diagnosticFrames,
                                (double)diagnosticResizeMs / diagnosticFrames,
                                (double)diagnosticConvertMs / diagnosticFrames,
                                (double)diagnosticSendMs / diagnosticFrames,
                                (double)diagnosticScreenSwitchMs / diagnosticFrames,
                                (double)diagnosticDelayMs / diagnosticFrames,
                                frame.Length);

                            diagnosticsStopwatch.Restart();
                            diagnosticFrames = 0;
                            diagnosticFrameMs = 0;
                            diagnosticRenderMs = 0;
                            diagnosticResizeMs = 0;
                            diagnosticConvertMs = 0;
                            diagnosticSendMs = 0;
                            diagnosticScreenSwitchMs = 0;
                            diagnosticDelayMs = 0;
                        }

                        if (delay > 0)
                            await Task.Delay(delay, token);
                    }
                    finally
                    {
                        ReturnFrame(frame);
                    }
                }
            }
            finally
            {
                renderCts.Cancel();
                while (frames.Reader.TryRead(out var pendingFrame))
                    ReturnFrame(pendingFrame);

                try
                {
                    await renderTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        private static void ReturnFrame(RenderedFrame frame)
        {
            if (frame.ReturnToPool)
                ArrayPool<byte>.Shared.Return(frame.Buffer);
        }

        private RenderedFrame GenerateBgr888Frame()
        {
            if (ConfigModel.Instance.GetProfile(_device.ProfileGuid) is Profile profile)
            {
                var renderStopwatch = Stopwatch.StartNew();
                using var bitmap = PanelDrawTask.RenderSK(profile, false,
                    colorType: SKColorType.Rgba8888,
                    alphaType: SKAlphaType.Opaque);
                var renderMs = renderStopwatch.ElapsedMilliseconds;

                var resizeStopwatch = Stopwatch.StartNew();
                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, _device.Rotation);
                var resizeMs = resizeStopwatch.ElapsedMilliseconds;

                var convertStopwatch = Stopwatch.StartNew();
                var length = resizedBitmap.Width * resizedBitmap.Height * 3;
                var output = ArrayPool<byte>.Shared.Rent(length);
                ToBgr888(resizedBitmap, output, _device.Brightness);
                var convertMs = convertStopwatch.ElapsedMilliseconds;
                return new RenderedFrame(output, length, renderMs, resizeMs, convertMs);
            }

            var blackFrame = GenerateBlackRgb888();
            return new RenderedFrame(blackFrame, blackFrame.Length, 0, 0, 0, ReturnToPool: false);
        }

        private static void ToBgr888(SKBitmap bitmap, byte[] output, int brightness)
        {
            var pixels = bitmap.GetPixelSpan();
            var srcStride = bitmap.RowBytes;
            var scale = Math.Clamp(brightness, 0, 99);
            int offset = 0;

            unsafe
            {
                fixed (byte* srcBase = pixels)
                fixed (byte* dstBase = output)
                {
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        byte* srcRow = srcBase + y * srcStride;
                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            int si = x * 4;
                            dstBase[offset++] = (byte)(srcRow[si + 2] * scale / 100);
                            dstBase[offset++] = (byte)(srcRow[si + 1] * scale / 100);
                            dstBase[offset++] = (byte)(srcRow[si + 0] * scale / 100);
                        }
                    }
                }
            }
        }

        private byte[]? _cachedBlackRgb888;

        private byte[] GenerateBlackRgb888()
        {
            _cachedBlackRgb888 ??= new byte[_panelWidth * _panelHeight * 3];
            return _cachedBlackRgb888;
        }

        private readonly record struct RenderedFrame(
            byte[] Buffer,
            int Length,
            long RenderMs,
            long ResizeMs,
            long ConvertMs,
            bool ReturnToPool = true);
    }
}
