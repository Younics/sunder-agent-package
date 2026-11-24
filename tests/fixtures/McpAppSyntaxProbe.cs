using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Sunder.App.Services;

// Use the same JSON grammar as MCP's editor without constructing a desktop.
internal static class McpAppSyntaxProbe
{
    internal static int Run(string entryPoint)
    {
        try
        {
            Verify(Path.GetFullPath(entryPoint));
            return 0;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            return 78;
        }
    }

    private static void Verify(string entryPoint)
    {
        var entryDirectory = Path.GetDirectoryName(entryPoint)!;
        using var shared = new AppSharedAssemblyRegistry();
        var context = new AppPackageLoadContext("younics.ai.agent.mcp", entryPoint, AppPackageTargetEnvironment.CurrentRid, shared, (_, _) => { });
        try
        {
            _ = context.LoadPackageEntryAssembly();
            var grammars = LoadLocal("TextMateSharp.Grammars");
            var registryType = LoadLocal("TextMateSharp").GetType("TextMateSharp.Registry.Registry", throwOnError: true)!;
            var optionsType = grammars.GetType("TextMateSharp.Grammars.RegistryOptions", throwOnError: true)!;
            var themeType = grammars.GetType("TextMateSharp.Grammars.ThemeName", throwOnError: true)!;
            var options = Activator.CreateInstance(optionsType, Enum.Parse(themeType, "DarkPlus"))!;
            var registry = Activator.CreateInstance(registryType, options)!;
            var grammar = registryType.GetMethod("LoadGrammar", [typeof(string)])!.Invoke(registry, ["source.json"])!;
            const string text = """{ "mode": 42 }""";
            var tokenize = grammar.GetType().GetMethods().Single(method => method.Name == "TokenizeLine" && method.GetParameters().Length == 3);
            var line = Activator.CreateInstance(tokenize.GetParameters()[0].ParameterType, text)!;
            var result = tokenize.Invoke(grammar, [line, null, TimeSpan.FromSeconds(5)])!;
            var tokens = ((IEnumerable)result.GetType().GetProperty("Tokens")!.GetValue(result)!).Cast<object>().ToArray();
            if (!HasScope("mode", "string") || !HasScope("42", "constant.numeric"))
                throw new InvalidDataException("MCP JSON highlighting did not distinguish a property name and a numeric value.");

            _ = LoadLocal("Onigwrap");
            var nativePaths = LoadedImages()
                .Where(path => Path.GetFileName(path).StartsWith("libonigwrap.", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (nativePaths.Length != 1 || !IsLocal(nativePaths[0]))
                throw new InvalidDataException($"The App must load its projected native dependency, not a host/cache copy: [{string.Join(", ", nativePaths)}].");
            Console.WriteLine("MCP JSON highlighting passed: " + nativePaths[0]);

            bool HasScope(string value, string prefix) => tokens.Any(token =>
            {
                var type = token.GetType();
                var index = text.IndexOf(value, StringComparison.Ordinal);
                var start = (int)type.GetProperty("StartIndex")!.GetValue(token)!;
                var end = (int)type.GetProperty("EndIndex")!.GetValue(token)!;
                var scopes = (IEnumerable)type.GetProperty("Scopes")!.GetValue(token)!;
                return start <= index && end >= index + value.Length
                    && scopes.Cast<string>().Any(scope => scope.StartsWith(prefix + ".", StringComparison.Ordinal));
            });

            Assembly LoadLocal(string name)
            {
                var assembly = context.LoadFromAssemblyName(new AssemblyName(name));
                if (AssemblyLoadContext.GetLoadContext(assembly) != context || !IsLocal(assembly.Location))
                    throw new InvalidDataException($"MCP dependency '{name}' loaded outside its App projection: '{assembly.Location}'.");
                return assembly;
            }

            bool IsLocal(string path)
            {
                var relative = Path.GetRelativePath(entryDirectory, path);
                return !Path.IsPathRooted(relative) && relative != ".."
                    && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            }
        }
        finally { context.Unload(); }
    }

    private static IEnumerable<string> LoadedImages()
    {
        if (OperatingSystem.IsMacOS())
        {
            // Process.Modules on macOS does not enumerate the loaded dylibs.
            var count = DyldImageCount();
            for (uint index = 0; index < count; index++)
                if (Marshal.PtrToStringUTF8(DyldImageName(index)) is { } path)
                    yield return path;
            yield break;
        }
        using var process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
            yield return module.FileName;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_dyld_image_count")]
    private static extern uint DyldImageCount();

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_dyld_get_image_name")]
    private static extern IntPtr DyldImageName(uint index);
}
