using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.TuringPanel;
using InfoPanel.Utils;
using InfoPanel.ViewModels;
using LcdDriver.TuringSmartScreen;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class TuringPanelUsbDeviceTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<TuringPanelUsbDeviceTask>();
        private readonly TuringPanelDevice _device;
        private readonly int _panelWidth;
        private readonly int _panelHeight;
        public TuringPanelDevice Device => _device;

        private sealed class TuringUsbScreenDeviceAdapter : IUsbScreenDevice
        {
            private readonly ScreenDevice _inner;

            public TuringUsbScreenDeviceAdapter(ScreenDevice inner)
            {
                _inner = inner;
            }

            public bool Sync() => _inner.Sync();
            public bool StopMedia() => _inner.StopMedia();
            public bool SetBrightness(byte value) => _inner.SetBrightness(value);
            public bool DrawJpeg(byte[] imageBytes) => _inner.DrawJpeg(imageBytes);
            public void Dispose() => _inner.Dispose();
        }

        public TuringPanelUsbDeviceTask(TuringPanelDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            if(device.ModelInfo == null)
            {
                throw new ArgumentException("Device model info cannot be null", nameof(device));
            }

            _panelWidth = device.ModelInfo.Width;
            _panelHeight = device.ModelInfo.Height;
        }

        public byte[]? GenerateLcdBuffer()
        {
            var profileGuid = _device.ProfileGuid;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var isLianLiDevice = _device.ModelInfo.Model == TuringPanelModel.LIANLI_88INCH_USB;
                var rotation = _device.Rotation;

                // The Lian Li 8.8" panel framebuffer is natively portrait
                // (480x1920). Landscape profiles must be rotated 90 degrees into the
                // portrait frame, matching the Linux driver's render path.
                if (isLianLiDevice && rotation == LCD_ROTATION.RotateNone)
                {
                    rotation = LCD_ROTATION.Rotate90FlipNone;
                }

                using var bitmap = PanelDrawTask.RenderSK(profile, false);

                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

                using var pixmap = resizedBitmap.PeekPixels();
                var imageFormat = SKEncodedImageFormat.Jpeg;
                var jpegQuality = isLianLiDevice ? 95 : _device.JpegQuality;
                using var data = pixmap.Encode(imageFormat, jpegQuality);

                if (data == null || data.IsEmpty)
                {
                    Logger.Error("TuringPanelDevice {Device}: Failed to encode bitmap to {Format}", _device, imageFormat);
                    return null;
                }

                return data.ToArray();
            }

            return null;
        }

        private static byte[] EncodeSolidFrame(int width, int height, SKColor color, SKEncodedImageFormat format, int quality)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.Erase(color);

            using var pixmap = bitmap.PeekPixels();
            using var data = pixmap.Encode(format, quality);
            return data?.ToArray() ?? Array.Empty<byte>();
        }

        private void PrepareLianLiImageLayers(LianLiUsbScreenDevice lianLiDevice)
        {
            try
            {
                // Match the vendor ApplyTemplate() prep sequence, but keep it isolated:
                // SyncClock(true, onlySync: true), StopClock(), clear PNG layer, clear JPG layer.
                var syncClockOk = lianLiDevice.SyncClockOnly();
                Thread.Sleep(50);

                var stopClockOk = lianLiDevice.StopClock();
                Thread.Sleep(50);

                var clearPng = EncodeSolidFrame(_panelWidth, _panelHeight, SKColors.Transparent, SKEncodedImageFormat.Png, 100);
                var clearPngOk = clearPng.Length > 0 && lianLiDevice.DrawPngLayer(clearPng);
                Thread.Sleep(50);

                var clearJpeg = EncodeSolidFrame(_panelWidth, _panelHeight, SKColors.Black, SKEncodedImageFormat.Jpeg, 95);
                var clearJpegOk = clearJpeg.Length > 0 && lianLiDevice.DrawJpegLayer(clearJpeg);
                Thread.Sleep(50);

                // Linux driver ends init with SetFrameRate(30) (cmd 15 / 0x0F).
                var frameRateOk = lianLiDevice.SetFrameRate(30);

                Logger.Information(
                    "TuringPanelDevice {Device}: Lian Li prep sequence results: SyncClockOnly={SyncClockOk}, StopClock={StopClockOk}, ClearPng={ClearPngOk} ({ClearPngBytes} bytes), ClearJpeg={ClearJpegOk} ({ClearJpegBytes} bytes), SetFrameRate={FrameRateOk}",
                    _device,
                    syncClockOk,
                    stopClockOk,
                    clearPngOk,
                    clearPng.Length,
                    clearJpegOk,
                    clearJpeg.Length,
                    frameRateOk);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "TuringPanelDevice {Device}: Lian Li prep sequence failed", _device);
            }
        }

        private async Task<UsbRegistry?> FindTargetDeviceAsync()
        {
            if(_device.ModelInfo == null)
            {
                Logger.Error("TuringPanelDevice {Device}: ModelInfo is null", _device);
                return null;
            }

            foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
            {
                if (deviceReg.Vid == _device.ModelInfo.VendorId && deviceReg.Pid == _device.ModelInfo.ProductId)
                {
                    var deviceId = deviceReg.DeviceProperties["DeviceID"] as string;

                    if (string.IsNullOrEmpty(deviceId))
                    {
                        Logger.Debug("TuringPanelDevice {Device}: Unable to get DeviceId for device {DevicePath}", _device, deviceReg.DevicePath);
                        continue;
                    }

                    if(_device.IsMatching(deviceId))
                    {
                        Logger.Information("TuringPanelDevice {Device}: Found matching device with DeviceId {DeviceId}", _device, deviceId);
                        return deviceReg;
                    }
                }
            }

            return null;
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            try
            {
                var usbRegistry = await FindTargetDeviceAsync();

                if (usbRegistry == null)
                {
                    Logger.Warning("TuringPanelDevice {Device}: USB Device not found.", _device);
                    _device.UpdateRuntimeProperties(errorMessage: "Device not found");
                    return;
                }

                if (!usbRegistry.Open(out var usbDevice))
                {
                    Logger.Error("TuringPanelDevice {Device}: Failed to open USB device", _device);
                    _device.UpdateRuntimeProperties(errorMessage: "Failed to open USB device");
                    return;
                }

                var isLianLiDevice = _device.ModelInfo.Model == TuringPanelModel.LIANLI_88INCH_USB;
                using IUsbScreenDevice screenDevice = isLianLiDevice
                    ? new LianLiUsbScreenDevice(usbDevice)
                    : new TuringUsbScreenDeviceAdapter(new ScreenDevice(usbDevice));

                Logger.Information("TuringPanelDevice {Device}: Initialized successfully", _device);
                _device.UpdateRuntimeProperties(isRunning: true);

                try
                {
                    // Sync — bail if device firmware isn't ready
                    if (!screenDevice.Sync())
                    {
                        Logger.Warning("TuringPanelDevice {Device}: Sync failed (1st attempt), device not ready", _device);
                        _device.UpdateRuntimeProperties(errorMessage: "Device not ready (sync failed)");
                        return;
                    }
                    Thread.Sleep(200);

                    if (!screenDevice.Sync())
                    {
                        Logger.Warning("TuringPanelDevice {Device}: Sync failed (2nd attempt), device not ready", _device);
                        _device.UpdateRuntimeProperties(errorMessage: "Device not ready (sync failed)");
                        return;
                    }
                    Thread.Sleep(200);

                    // Stop any video playback to prevent flickering
                    screenDevice.StopMedia();
                    Thread.Sleep(200);

                    if (screenDevice is LianLiUsbScreenDevice lianLiDevice)
                    {
                        PrepareLianLiImageLayers(lianLiDevice);
                        Thread.Sleep(200);
                    }

                    // Set brightness
                    var brightness = _device.Brightness;
                    screenDevice.SetBrightness((byte)brightness);

                    if (!screenDevice.Sync())
                    {
                        Logger.Warning("TuringPanelDevice {Device}: Sync failed after brightness set, device not ready", _device);
                        _device.UpdateRuntimeProperties(errorMessage: "Device not ready (sync failed)");
                        return;
                    }
                    Thread.Sleep(200);

                    FpsCounter fpsCounter = new(60);
                    byte[]? _latestFrame = null;
                    AutoResetEvent _frameAvailable = new(false);

                    var renderCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var renderToken = renderCts.Token;

                    var renderTask = Task.Run(async () =>
                    {
                        Thread.CurrentThread.Name ??= $"TuringPanel-Render-{_device.DeviceId}";
                        var stopwatch1 = new Stopwatch();

                        while (!renderToken.IsCancellationRequested)
                        {
                            stopwatch1.Restart();
                            var frame = GenerateLcdBuffer();

                            if (frame != null)
                            {
                                var oldFrame = Interlocked.Exchange(ref _latestFrame, frame);
                                _frameAvailable.Set();
                            }

                            var targetFrameTime = 1000 / Math.Max(1, _device.TargetFrameRate);
                            var desiredFrameTime = Math.Max((int)(fpsCounter.FrameTime), targetFrameTime);
                            var adaptiveFrameTime = 0;

                            var elapsedMs = (int)stopwatch1.ElapsedMilliseconds;

                            if (elapsedMs < desiredFrameTime)
                            {
                                adaptiveFrameTime = desiredFrameTime - elapsedMs;
                            }

                            if (adaptiveFrameTime > 0)
                            {
                                await Task.Delay(adaptiveFrameTime, renderToken);
                            }
                        }
                    }, renderToken);

                    var sendTask = Task.Run(() =>
                    {
                        Thread.CurrentThread.Name ??= $"TuringPanel-Send-{_device.DeviceId}";
                        try
                        {
                            var stopwatch2 = new Stopwatch();

                            while (!token.IsCancellationRequested)
                            {
                                if (brightness != _device.Brightness)
                                {
                                    brightness = _device.Brightness;
                                    screenDevice.SetBrightness((byte)brightness);
                                    if (!screenDevice.Sync())
                                    {
                                        Logger.Warning("TuringPanelDevice {Device}: Sync failed during brightness update", _device);
                                        _device.UpdateRuntimeProperties(errorMessage: "Sync failed");
                                        break;
                                    }
                                    Thread.Sleep(200);
                                }

                                if (_frameAvailable.WaitOne(100))
                                {
                                    var frame = Interlocked.Exchange(ref _latestFrame, null);
                                    if (frame != null)
                                    {
                                        stopwatch2.Restart();
                                        if (!screenDevice.DrawJpeg(frame))
                                        {
                                            Logger.Warning("TuringPanelDevice {Device}: DrawJpeg failed", _device);
                                            _device.UpdateRuntimeProperties(errorMessage: "Draw failed");
                                            break;
                                        }

                                        fpsCounter.Update(stopwatch2.ElapsedMilliseconds);
                                        _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);
                                    }
                                }
                            }
                        }
                        catch(Exception e)
                        {
                            Logger.Error(e, "TuringPanelDevice {Device}: Error in send task", _device);
                        }
                        finally
                        {
                            renderCts.Cancel();
                        }
                    }, token);

                    await Task.WhenAll(renderTask, sendTask);
                }
                catch (TaskCanceledException)
                {
                    Logger.Debug("TuringPanelDevice {Device}: Task cancelled", _device);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "TuringPanelDevice {Device}: Exception during work", _device);
                    _device.UpdateRuntimeProperties(errorMessage: ex.Message);
                }
                finally
                {
                    try
                    {
                        screenDevice.SetBrightness(0);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "TuringPanelDevice {Device}: Exception when setting brightness to 0", _device);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Warning(e, "TuringPanelDevice {Device}: Init error", _device);
                _device.UpdateRuntimeProperties(errorMessage: e.Message);
            }
            finally
            {
                _device.UpdateRuntimeProperties(isRunning: false);
            }
        }
    }
}
