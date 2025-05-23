// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.NET.Build.Containers.Resources;

namespace Microsoft.NET.Build.Containers;

/// <summary>
/// The class builds new image based on the base image.
/// </summary>
internal sealed class ImageBuilder
{
    // a snapshot of the manifest that this builder is based on
    private readonly ManifestV2 _baseImageManifest;

    // the mutable internal manifest that we're building by modifying the base and applying customizations
    private readonly ManifestV2 _manifest;
    private readonly ImageConfig _baseImageConfig;
    private readonly ILogger _logger;

    /// <summary>
    /// This is a parser for ASPNETCORE_URLS based on https://github.com/dotnet/aspnetcore/blob/main/src/Http/Http/src/BindingAddress.cs
    /// We can cut corners a bit here because we really only care about ports, if they exist.
    /// </summary>
    internal static Regex aspnetPortRegex = new(@"(?<scheme>\w+)://(?<domain>([*+]|).+):(?<port>\d+)");

    public ImageConfig BaseImageConfig => _baseImageConfig;

    /// <summary>
    /// MediaType of the output manifest. By default, this will be the same as the base image manifest.
    /// </summary>
    public string ManifestMediaType { get; set; }

    internal ImageBuilder(ManifestV2 manifest, string manifestMediaType, ImageConfig baseImageConfig, ILogger logger)
    {
        _baseImageManifest = manifest;
        _manifest = new ManifestV2() { SchemaVersion = manifest.SchemaVersion, Config = manifest.Config, Layers = new(manifest.Layers), MediaType = manifest.MediaType };
        ManifestMediaType = manifestMediaType;
        _baseImageConfig = baseImageConfig;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether the base image is has a Windows operating system.
    /// </summary>
    public bool IsWindows => _baseImageConfig.IsWindows;

    // For tests
    internal string ManifestConfigDigest => _manifest.Config.digest;

    /// <summary>
    /// Builds the image configuration <see cref="BuiltImage"/> ready for further processing.
    /// </summary>
    internal BuiltImage Build()
    {
        // before we build, we need to make sure that any image customizations occur
        AssignUserFromEnvironment();
        AssignPortsFromEnvironment();

        string imageJsonStr = _baseImageConfig.BuildConfig();
        string imageSha = DigestUtils.GetSha(imageJsonStr);
        string imageDigest = DigestUtils.GetDigestFromSha(imageSha);
        long imageSize = Encoding.UTF8.GetBytes(imageJsonStr).Length;

        ManifestConfig newManifestConfig = _manifest.Config with
        {
            digest = imageDigest,
            size = imageSize,
            mediaType = ManifestMediaType switch
            {
                SchemaTypes.OciManifestV1 => SchemaTypes.OciImageConfigV1,
                SchemaTypes.DockerManifestV2 => SchemaTypes.DockerContainerV1,
                _ => SchemaTypes.OciImageConfigV1 // opinion - defaulting to modern here, but really this should never happen
            }
        };

        ManifestV2 newManifest = new ManifestV2()
        {
            Config = newManifestConfig,
            SchemaVersion = _manifest.SchemaVersion,
            MediaType = ManifestMediaType,
            Layers = _manifest.Layers
        };

        return new BuiltImage()
        {
            Config = imageJsonStr,
            ImageDigest = imageDigest,
            ImageSha = imageSha,
            Manifest = JsonSerializer.SerializeToNode(newManifest)?.ToJsonString() ?? "",
            ManifestDigest = newManifest.GetDigest(),
            ManifestMediaType = ManifestMediaType,
            Layers = _manifest.Layers
        };
    }

    /// <summary>
    /// Adds a <see cref="Layer"/> to a base image.
    /// </summary>
    internal void AddLayer(Layer l)
    {
        _manifest.Layers.Add(new(l.Descriptor.MediaType, l.Descriptor.Size, l.Descriptor.Digest, l.Descriptor.Urls));
        _baseImageConfig.AddLayer(l);
    }

    internal (string name, string value) AddBaseImageDigestLabel()
    {
        var label = ("org.opencontainers.image.base.digest", _baseImageManifest.GetDigest());
        AddLabel(label.Item1, label.Item2);
        return label;
    }

    /// <summary>
    /// Adds a label to a base image.
    /// </summary>
    internal void AddLabel(string name, string value) => _baseImageConfig.AddLabel(name, value);

    /// <summary>
    /// Adds environment variables to a base image.
    /// </summary>
    internal void AddEnvironmentVariable(string envVarName, string value) => _baseImageConfig.AddEnvironmentVariable(envVarName, value);

    /// <summary>
    /// Exposes additional port.
    /// </summary>
    internal void ExposePort(int number, PortType type) => _baseImageConfig.ExposePort(number, type);

    /// <summary>
    /// Sets working directory for the image.
    /// </summary>
    internal void SetWorkingDirectory(string workingDirectory) => _baseImageConfig.SetWorkingDirectory(workingDirectory);

    /// <summary>
    /// Sets the ENTRYPOINT and CMD for the image.
    /// </summary>
    internal void SetEntrypointAndCmd(string[] entrypoint, string[] cmd) => _baseImageConfig.SetEntrypointAndCmd(entrypoint, cmd);

    /// <summary>
    /// Sets the USER for the image.
    /// </summary>
    internal void SetUser(string user, bool isExplicitUserInteraction = true) => _baseImageConfig.SetUser(user, isExplicitUserInteraction);

    internal static (string[] entrypoint, string[] cmd) DetermineEntrypointAndCmd(
        string[] entrypoint,
        string[] entrypointArgs,
        string[] cmd,
        string[] appCommand,
        string[] appCommandArgs,
        string appCommandInstruction,
        string[]? baseImageEntrypoint,
        Action<string> logWarning,
        Action<string, string?> logError)
    {
        bool setsEntrypoint = entrypoint.Length > 0 || entrypointArgs.Length > 0;
        bool setsCmd = cmd.Length > 0;

        baseImageEntrypoint ??= Array.Empty<string>();
        // Some (Microsoft) base images set 'dotnet' as the ENTRYPOINT. We mustn't use it.
        if (baseImageEntrypoint.Length == 1 && (baseImageEntrypoint[0] == "dotnet" || baseImageEntrypoint[0] == "/usr/bin/dotnet"))
        {
            baseImageEntrypoint = Array.Empty<string>();
        }

        if (string.IsNullOrEmpty(appCommandInstruction))
        {
            if (setsEntrypoint)
            {
                // Backwards-compatibility: before 'AppCommand'/'Cmd' was added, only 'Entrypoint' was available.
                if (!setsCmd && appCommandArgs.Length == 0 && entrypoint.Length == 0)
                {
                    // Copy over the values for starting the application from AppCommand.
                    entrypoint = appCommand;
                    appCommand = Array.Empty<string>();

                    // Use EntrypointArgs as cmd.
                    cmd = entrypointArgs;
                    entrypointArgs = Array.Empty<string>();

                    if (entrypointArgs.Length > 0)
                    {
                        // Log warning: Instead of ContainerEntrypointArgs, use ContainerAppCommandArgs for arguments that must always be set, or ContainerDefaultArgs for default arguments that the user override when creating the container.
                        logWarning(nameof(Strings.EntrypointArgsSetPreferAppCommandArgs));
                    }

                    appCommandInstruction = KnownAppCommandInstructions.None;
                }
                else
                {
                    // There's an Entrypoint. Use DefaultArgs for the AppCommand.
                    appCommandInstruction = KnownAppCommandInstructions.DefaultArgs;
                }
            }
            else
            {
                // Default to use an Entrypoint.
                // If the base image defines an ENTRYPOINT, print a warning.
                if (baseImageEntrypoint.Length > 0)
                {
                    logWarning(nameof(Strings.BaseEntrypointOverwritten));
                }
                appCommandInstruction = KnownAppCommandInstructions.Entrypoint;
            }
        }

        if (entrypointArgs.Length > 0 && entrypoint.Length == 0)
        {
            logError(nameof(Strings.EntrypointArgsSetNoEntrypoint), null);
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        if (appCommandArgs.Length > 0 && appCommand.Length == 0)
        {
            logError(nameof(Strings.AppCommandArgsSetNoAppCommand), null);
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        switch (appCommandInstruction)
        {
            case KnownAppCommandInstructions.None:
                if (appCommand.Length > 0 || appCommandArgs.Length > 0)
                {
                    logError(nameof(Strings.AppCommandSetNotUsed), appCommandInstruction);
                    return (Array.Empty<string>(), Array.Empty<string>());
                }
                break;
            case KnownAppCommandInstructions.DefaultArgs:
                cmd = appCommand.Concat(appCommandArgs).Concat(cmd).ToArray();
                break;
            case KnownAppCommandInstructions.Entrypoint:
                if (setsEntrypoint)
                {
                    logError(nameof(Strings.EntrypointConflictAppCommand), appCommandInstruction);
                    return (Array.Empty<string>(), Array.Empty<string>());
                }
                entrypoint = appCommand;
                entrypointArgs = appCommandArgs;
                break;
            default:
                throw new NotSupportedException(
                    Resource.FormatString(
                        nameof(Strings.UnknownAppCommandInstruction),
                        appCommandInstruction,
                        string.Join(",", KnownAppCommandInstructions.SupportedAppCommandInstructions)));
        }

        return (entrypoint.Length > 0 ? entrypoint.Concat(entrypointArgs).ToArray() : baseImageEntrypoint, cmd);
    }

    /// <summary>
    /// The APP_UID environment variable is a convention used to set the user in a data-driven manner. we should respect it if it's present.
    /// </summary>
    internal void AssignUserFromEnvironment()
    {
        // it's a common convention to apply custom users with the APP_UID convention - we check and apply that here
        if (_baseImageConfig.EnvironmentVariables.TryGetValue(EnvironmentVariables.APP_UID, out string? appUid))
        {
            _logger.LogTrace("Setting user from APP_UID environment variable");
            SetUser(appUid, isExplicitUserInteraction: false);
        }
    }

    /// <summary>
    /// ASP.NET can have urls/ports set via three environment variables - if we see any of them we should create ExposedPorts for them
    /// to ensure tooling can automatically create port mappings.
    /// </summary>
    internal void AssignPortsFromEnvironment()
    {
        // asp.net images control port bindings via three environment variables. we should check for those variables and ensure that ports are created for them.
        // precendence is captured at https://github.com/dotnet/aspnetcore/blob/f49c1c7f7467c184ffb630086afac447772096c6/src/Hosting/Hosting/src/GenericHost/GenericWebHostService.cs#L68-L119
        // ASPNETCORE_URLS is the most specific and is the only one used if present, followed by ASPNETCORE_HTTPS_PORT and ASPNETCORE_HTTP_PORT together

        // https://learn.microsoft.com//aspnet/core/fundamentals/host/web-host?view=aspnetcore-8.0#server-urls - the format of ASPNETCORE_URLS has been stable for many years now
        if (_baseImageConfig.EnvironmentVariables.TryGetValue(EnvironmentVariables.ASPNETCORE_URLS, out string? urls))
        {
            foreach (var url in Split(urls))
            {
                _logger.LogTrace("Setting ports from ASPNETCORE_URLS environment variable");
                var match = aspnetPortRegex.Match(url);
                if (match.Success && int.TryParse(match.Groups["port"].Value, out int port))
                {
                    _logger.LogTrace("Added port {port}", port);
                    ExposePort(port, PortType.tcp);
                }
            }
            return; // we're done here - ASPNETCORE_URLS is the most specific and overrides the other two
        }

        // port-specific
        // https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-8.0#specify-ports-only - new for .NET 8 - allows just changing port(s) easily
        if (_baseImageConfig.EnvironmentVariables.TryGetValue(EnvironmentVariables.ASPNETCORE_HTTP_PORTS, out string? httpPorts))
        {
            _logger.LogTrace("Setting ports from ASPNETCORE_HTTP_PORTS environment variable");
            foreach (var port in Split(httpPorts))
            {
                if (int.TryParse(port, out int parsedPort))
                {
                    _logger.LogTrace("Added port {port}", parsedPort);
                    ExposePort(parsedPort, PortType.tcp);
                }
                else
                {
                    _logger.LogTrace("Skipped port {port} because it could not be parsed as an integer", port);
                }
            }
        }

        if (_baseImageConfig.EnvironmentVariables.TryGetValue(EnvironmentVariables.ASPNETCORE_HTTPS_PORTS, out string? httpsPorts))
        {
            _logger.LogTrace("Setting ports from ASPNETCORE_HTTPS_PORTS environment variable");
            foreach (var port in Split(httpsPorts))
            {
                if (int.TryParse(port, out int parsedPort))
                {
                    _logger.LogTrace("Added port {port}", parsedPort);
                    ExposePort(parsedPort, PortType.tcp);
                }
                else
                {
                    _logger.LogTrace("Skipped port {port} because it could not be parsed as an integer", port);
                }
            }
        }

        static string[] Split(string input)
        {
            return input.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    internal static class EnvironmentVariables
    {
        public static readonly string APP_UID = nameof(APP_UID);
        public static readonly string ASPNETCORE_URLS = nameof(ASPNETCORE_URLS);
        public static readonly string ASPNETCORE_HTTP_PORTS = nameof(ASPNETCORE_HTTP_PORTS);
        public static readonly string ASPNETCORE_HTTPS_PORTS = nameof(ASPNETCORE_HTTPS_PORTS);
    }

}
