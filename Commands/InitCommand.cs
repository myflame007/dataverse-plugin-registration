namespace Dataverse.PluginRegistration;

internal static class InitCommand
{
    internal static int Run(string[] args)
    {
        // ── Config file ────────────────────────────────────────────────────────
        var configPath = ArgsResolver.GetArg(args, "--config") ?? PluginRegConfig.DefaultFileName;

        bool writeConfig = true;
        if (File.Exists(configPath))
        {
            Console.WriteLine($"Config file already exists: {configPath}");
            Console.Write("Overwrite? (y/N): ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            writeConfig = answer == "y";
        }

        if (writeConfig)
        {
            var currentDir = Path.GetFileName(Directory.GetCurrentDirectory()) ?? "MyPlugin";
            var config = PluginRegConfig.CreateDefault(currentDir, $@"bin\Debug\net462\{currentDir}.dll");
            config.Save(configPath);
            Console.WriteLine($"Created: {Path.GetFullPath(configPath)}");
        }

        // ── Attribute templates (always scaffolded / updated) ──────────────────
        ScaffoldAttributeTemplates();

        Console.WriteLine("Edit environments and assembly paths, then run: plugin-reg register --env dev");
        return 0;
    }

    private static void ScaffoldAttributeTemplates()
    {
        var attributesDir = Path.Combine(Directory.GetCurrentDirectory(), "Attributes");
        Directory.CreateDirectory(attributesDir);

        var assembly = typeof(InitCommand).Assembly;

        var templates = new[]
        {
            "CrmPluginRegistrationAttribute.cs",
            "CustomApiAttributes.cs",
        };

        foreach (var fileName in templates)
        {
            var resourceName = $"Dataverse.PluginRegistration.RequiredAttributes.{fileName}";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Console.WriteLine($"  WARN: embedded resource '{resourceName}' not found — skipping.");
                continue;
            }

            var outPath = Path.Combine(attributesDir, fileName);
            bool existed = File.Exists(outPath);

            using var fs = File.Create(outPath);
            stream.CopyTo(fs);

            Console.WriteLine(existed
                ? $"  Updated : Attributes/{fileName}"
                : $"  Created : Attributes/{fileName}");
        }
    }
}
