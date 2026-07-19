using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace InfoPanel.JonsboPanel
{
    public class JonsboPanelDiscoveryInfo
    {
        public string DeviceId { get; set; } = "";
        public string DeviceLocation { get; set; } = ""; // COM port name (e.g. "COM5")
        public int VendorId { get; set; }
        public int ProductId { get; set; }
        public JonsboPanelModel Model { get; set; }
        public JonsboPanelModelInfo? ModelInfo { get; set; }
    }

    public static partial class JonsboPanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(JonsboPanelHelper));

        /// <summary>
        /// Scans Win32_SerialPort for CDC-ACM devices matching Jonsbo VID/PID.
        /// </summary>
        public static List<JonsboPanelDiscoveryInfo> ScanDevices()
        {
            var devices = new List<JonsboPanelDiscoveryInfo>();

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_SerialPort");
                foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                {
                    string? comPort = obj["DeviceID"]?.ToString();
                    string? pnpDeviceId = obj["PNPDeviceID"]?.ToString();

                    if (comPort == null || pnpDeviceId == null) continue;
                    if (!TryParseVidPid(pnpDeviceId, out var vid, out var pid)) continue;

                    var modelInfo = JonsboPanelModelDatabase.GetModelByVidPid(vid, pid);
                    if (modelInfo == null) continue;

                    Logger.Information("JonsboPanelHelper: Found {Name} on {Port} (PNP={Pnp})",
                        modelInfo.Name, comPort, pnpDeviceId);

                    devices.Add(new JonsboPanelDiscoveryInfo
                    {
                        DeviceId = pnpDeviceId,
                        DeviceLocation = comPort,
                        VendorId = vid,
                        ProductId = pid,
                        Model = modelInfo.Model,
                        ModelInfo = modelInfo,
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "JonsboPanelHelper: Error scanning serial ports");
            }

            Logger.Information("JonsboPanelHelper: Found {Count} Jonsbo panel device(s)", devices.Count);
            return devices;
        }

        private static bool TryParseVidPid(string pnpDeviceId, out int vid, out int pid)
        {
            vid = 0; pid = 0;
            var match = VidPidRegex().Match(pnpDeviceId);
            if (!match.Success) return false;
            vid = Convert.ToInt32(match.Groups[1].Value, 16);
            pid = Convert.ToInt32(match.Groups[2].Value, 16);
            return true;
        }

        [GeneratedRegex(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})")]
        private static partial Regex VidPidRegex();
    }
}
