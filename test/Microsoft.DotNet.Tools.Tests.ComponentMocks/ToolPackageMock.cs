// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Text.Json;
using Microsoft.DotNet.Cli;
using Microsoft.DotNet.Cli.ToolPackage;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.Extensions.EnvironmentAbstractions;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace Microsoft.DotNet.Tools.Tests.ComponentMocks
{
    internal class ToolPackageMock : IToolPackage
    {
        private IFileSystem _fileSystem;
        private Lazy<RestoredCommand> _command;
        private IEnumerable<string> _warnings;
        private readonly IReadOnlyList<FilePath> _packagedShims;

        public ToolPackageMock(
            IFileSystem fileSystem,
            PackageId id,
            NuGetVersion version,
            DirectoryPath packageDirectory,
            IEnumerable<string> warnings = null,
            IReadOnlyList<FilePath> packagedShims = null,
            IEnumerable<NuGetFramework> frameworks = null)
        {
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            Id = id;
            Version = version ?? throw new ArgumentNullException(nameof(version));
            PackageDirectory = packageDirectory;
            _command = new Lazy<RestoredCommand>(GetCommand);
            _warnings = warnings ?? new List<string>();
            _packagedShims = packagedShims ?? new List<FilePath>();
            Frameworks = frameworks ?? new List<NuGetFramework>();
        }

        public PackageId Id { get; private set; }

        public NuGetVersion Version { get; private set; }
        public DirectoryPath PackageDirectory { get; private set; }

        public RestoredCommand Command
        {
            get
            {
                return _command.Value;
            }
        }

        public IEnumerable<string> Warnings => _warnings;

        public IReadOnlyList<FilePath> PackagedShims
        {
            get
            {
                return _packagedShims;
            }
        }

        public IEnumerable<NuGetFramework> Frameworks { get; private set; }

        private RestoredCommand GetCommand()
        {
            try
            {
                // The mock restorer wrote the path to the executable into project.assets.json (not a real assets file)
                // Currently only "dotnet" commands are supported
                var executablePath = _fileSystem.File.ReadAllText(Path.Combine(PackageDirectory.Value, "project.assets.json"));

                var fakeSettingFile = _fileSystem.File.ReadAllText(Path.Combine(PackageDirectory.Value, ProjectRestorerMock.FakeCommandSettingsFileName));

                string name;
                using (JsonDocument doc = JsonDocument.Parse(fakeSettingFile))
                {
                    JsonElement root = doc.RootElement;
                    name = root.GetProperty("Name").GetString();
                }

                return new RestoredCommand(
                        new ToolCommandName(name),
                        "dotnet",
                        PackageDirectory.WithFile(executablePath));
            }
            catch (IOException ex)
            {
                throw new ToolPackageException(
                    string.Format(
                        CliStrings.FailedToRetrieveToolConfiguration,
                        Id,
                        ex.Message),
                    ex);
            }
        }
    }
}
