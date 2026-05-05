using InfoPanel.Drawing;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
using InfoPanel.VmaxPanel;
using Serilog;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Threading;
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
            FpsCounter fpsCounter = new(10);
            _device.UpdateRuntimeProperties(isRunning: true, errorMessage: string.Empty);

            while (!token.IsCancellationRequested)
            {
                var stopwatch = Stopwatch.StartNew();
                var rgbData = GenerateRgb888Buffer();

                vmaxDevice.SendRgb888Frame(rgbData);
                vmaxDevice.SetScreenSwitch(_device.ScreenSwitch);

                fpsCounter.Update(stopwatch.ElapsedMilliseconds);
                _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);

                var targetFrameTime = 1000 / Math.Max(1, Math.Min(_device.TargetFrameRate, 5));
                var delay = targetFrameTime - (int)stopwatch.ElapsedMilliseconds;
                if (delay > 0)
                    await Task.Delay(delay, token);
            }
        }

        private byte[] GenerateRgb888Buffer()
        {
            if (ConfigModel.Instance.GetProfile(_device.ProfileGuid) is Profile profile)
            {
                using var bitmap = PanelDrawTask.RenderSK(profile, false,
                    colorType: SKColorType.Rgba8888,
                    alphaType: SKAlphaType.Opaque);

                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, _device.Rotation);

                SKBitmap? normalized = null;
                try
                {
                    normalized = ApplyBrightness(resizedBitmap);
                    return ToRgb888(normalized);
                }
                finally
                {
                    normalized?.Dispose();
                }
            }

            return GenerateBlackRgb888();
        }

        private static byte[] ToRgb888(SKBitmap bitmap)
        {
            var output = new byte[bitmap.Width * bitmap.Height * 3];
            int offset = 0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    var color = bitmap.GetPixel(x, y);
                    output[offset++] = color.Red;
                    output[offset++] = color.Green;
                    output[offset++] = color.Blue;
                }
            }
            return output;
        }

        private SKBitmap ApplyBrightness(SKBitmap source)
        {
            float scale = Math.Clamp(_device.Brightness, 0, 99) / 100f;
            var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
            using var canvas = new SKCanvas(result);
            using var paint = new SKPaint();
            paint.ColorFilter = SKColorFilter.CreateColorMatrix(
            [
                scale, 0,     0,     0, 0,
                0,     scale, 0,     0, 0,
                0,     0,     scale, 0, 0,
                0,     0,     0,     1, 0
            ]);
            canvas.DrawBitmap(source, 0, 0, paint);
            return result;
        }

        private byte[]? _cachedBlackRgb888;

        private byte[] GenerateBlackRgb888()
        {
            _cachedBlackRgb888 ??= new byte[_panelWidth * _panelHeight * 3];
            return _cachedBlackRgb888;
        }
    }
}
