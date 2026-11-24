namespace GameHelper.Plugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;

    public static class PluginSmokeReport
    {
        private static readonly string? Pathname = Environment.GetEnvironmentVariable("GH_PLUGIN_SMOKE_REPORT");
        private static long nextCapture;
        public static bool IsEnabled => !string.IsNullOrEmpty(Pathname) && Path.IsPathFullyQualified(Pathname);

        internal static void Capture(PluginContainer[] plugins)
        {
            if (Pathname == null || !IsEnabled || Environment.TickCount64 < nextCapture)
            {
                return;
            }

            nextCapture = Environment.TickCount64 + 1500;
            try
            {
                var data = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var plugin in plugins)
                {
                    if (plugin.Metadata.Enable && plugin.Plugin is IPluginSmokeProbe probe)
                    {
                        try { data.Add(plugin.Name, probe.CollectSmokeData()); }
                        catch (Exception ex) { data.Add(plugin.Name, new { error = ex.GetType().Name }); }
                    }
                }

                var state = Core.States.InGameStateObject;
                var report = new
                {
                    capturedAt = DateTimeOffset.UtcNow,
                    overlayPid = Environment.ProcessId,
                    clientPid = Core.Process.Pid,
                    gameState = Core.States.GameCurrentState.ToString(),
                    foreground = Core.Process.Foreground,
                    areaId = state.CurrentWorldInstance.AreaDetails.Id,
                    areaHash = state.CurrentAreaInstance.AreaHash,
                    plugins = data,
                };
                var temporaryFile = Pathname + ".tmp";
                File.WriteAllText(temporaryFile, JsonSerializer.Serialize(report));
                File.Move(temporaryFile, Pathname, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PluginSmokeReport] {ex.Message}");
            }
        }
    }
}
