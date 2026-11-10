using System;
using System.IO;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

// Targets for building Fisher against the Critter Stack .NET 11 release train's local NuGet packs, before
// the nuget.org replay (JasperFx/fisher#459): Pack, AotSmoke and TakeLocalUpstream.
partial class Build
{
    [Parameter("Package version for Pack, overriding <Version> in Directory.Build.props (e.g. 2.0.0-local.1)")]
    readonly string Version;

    [Parameter("Folder Pack copies the .nupkg files into, and TakeLocalUpstream adds as a NuGet source")]
    readonly string Feed;

    [Parameter("TakeLocalUpstream: version of the local JasperFx packages to pin (e.g. 3.0.0-local.1)")]
    readonly string UpstreamVersion;

    [Parameter("TakeLocalUpstream: version of the local Weasel packages to pin (e.g. 10.0.0-local.1)")]
    readonly string WeaselVersion;

    [Parameter("Target framework AotSmoke publishes for - Default is 'net10.0'")]
    readonly string SingleFramework = "net10.0";

    // Every shipped package; publish.yml packs the same three.
    static readonly string[] PackableProjects = ["Fisher", "Fisher.AspNetCore", "Fisher.EntityFrameworkCore"];

    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    Target Pack => _ => _
        .Executes(() =>
        {
            foreach (var name in PackableProjects)
            {
                DotNetPack(s => s
                    .SetProject(RootDirectory / "src" / name / $"{name}.csproj")
                    .SetOutputDirectory(ArtifactsDirectory)
                    .SetConfiguration("Release")
                    .When(_ => !string.IsNullOrEmpty(Version), x => x.SetProperty("Version", Version)));
            }

            if (!string.IsNullOrEmpty(Feed))
            {
                Directory.CreateDirectory(Feed);
                var pattern = string.IsNullOrEmpty(Version) ? "*.nupkg" : $"*.{Version}.nupkg";
                foreach (var package in Directory.GetFiles(ArtifactsDirectory, pattern))
                {
                    File.Copy(package, Path.Combine(Feed, Path.GetFileName(package)), true);
                }

                Serilog.Log.Information("Copied packed packages into the local feed {Feed}", Feed);
            }
        });

    // Nothing under CoreCLR can see a Native AOT failure (see CLAUDE.md): publish smoke/aot-consumer natively
    // and run the binary, as the `package` job in fisher.yml does.
    Target AotSmoke => _ => _
        .Executes(() =>
        {
            var project = RootDirectory / "smoke" / "aot-consumer" / "AotConsumer.csproj";
            var output = ArtifactsDirectory / "aot";
            DotNetPublish(s => s
                .SetProject(project)
                .SetConfiguration("Release")
                .SetFramework(SingleFramework)
                .SetOutput(output)
                .SetProperty("PublishAot", "true"));

            var binary = output / (OperatingSystem.IsWindows() ? "AotConsumer.exe" : "AotConsumer");
            Assert.FileExists(binary, "The native AOT publish produced no executable");
            var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(binary) { UseShellExecute = false })!;
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"The native AOT smoke exited with {process.ExitCode}");
        });

    // Pin the JasperFx* and Weasel* packages in Directory.Packages.props (and the smoke's source generator
    // reference) to locally packed versions, and register the local feed as a NuGet source. Run it again with
    // the released versions to put the pins back.
    Target TakeLocalUpstream => _ => _
        .Executes(() =>
        {
            Assert.True(!string.IsNullOrEmpty(UpstreamVersion) || !string.IsNullOrEmpty(WeaselVersion),
                "Pass --upstream-version (JasperFx) and/or --weasel-version");

            var path = RootDirectory / "Directory.Packages.props";
            var text = File.ReadAllText(path);
            text = pin(text, "JasperFx", UpstreamVersion);
            text = pin(text, "Weasel", WeaselVersion);
            File.WriteAllText(path, text);

            if (!string.IsNullOrEmpty(UpstreamVersion))
            {
                // The AOT smoke is outside central package management and names the generator itself.
                var smoke = RootDirectory / "smoke" / "aot-consumer" / "AotConsumer.csproj";
                var xml = File.ReadAllText(smoke);
                xml = Regex.Replace(xml,
                    "(<PackageReference Include=\"JasperFx\\.Events\\.SourceGenerator\" Version=\")[^\"]+(\")",
                    $"${{1}}{UpstreamVersion}$2");
                File.WriteAllText(smoke, xml);
            }

            if (!string.IsNullOrEmpty(Feed))
            {
                // nuget.config is gitignored: the local feed is machine state, not something to commit.
                var config = RootDirectory / "nuget.config";
                var feed = Path.GetFullPath(Feed);
                if (!File.Exists(config))
                {
                    File.WriteAllText(config, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration />\n");
                }

                if (!File.ReadAllText(config).Contains(feed))
                {
                    if (File.ReadAllText(config).Contains("critter-local"))
                        DotNet($"nuget remove source critter-local --configfile {config}");
                    DotNet($"nuget add source {feed} --name critter-local --configfile {config}");
                }
            }
        });

    static string pin(string text, string family, string version)
    {
        if (string.IsNullOrEmpty(version)) return text;

        var pattern = $"(<PackageVersion Include=\"{family}(?:\\.[A-Za-z.]+)?\" Version=\")[^\"]+(\")";
        Assert.True(Regex.IsMatch(text, pattern), $"Directory.Packages.props pins no {family} package");
        Serilog.Log.Information("Pinned {Family}* to {Version}", family, version);
        return Regex.Replace(text, pattern, $"${{1}}{version}$2");
    }
}
