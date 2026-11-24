using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

// This observer is loaded by VSTest, not by the App or any Worker. It does
// not load Runtime types, write during teardown, or modify test behavior.
internal static class StartupHook
{
    public static void Initialize()
    {
        var path = Environment.GetEnvironmentVariable("SUNDER_RECOVERY_LOAD_REPORT");
        if (string.IsNullOrEmpty(path) || Assembly.GetEntryAssembly()?.GetName().Name != "testhost")
            return;
        Environment.SetEnvironmentVariable("SUNDER_RECOVERY_LOAD_REPORT", null);
        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic && assembly.GetName().Name is { } name
                    && (name.StartsWith("Sunder.Runtime.", StringComparison.Ordinal) || name == "Sunder.Package.Format"))
                .Select(assembly => new { name = assembly.GetName().Name, path = assembly.Location,
                    mvid = assembly.ManifestModule.ModuleVersionId }).ToArray();
            using var report = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(report, new { pid = Environment.ProcessId,
                runtimeIdentifier = RuntimeInformation.RuntimeIdentifier,
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), assemblies });
        };
    }
}
