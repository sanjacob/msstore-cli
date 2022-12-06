// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MSStore.API;
using MSStore.API.Packaged;
using MSStore.API.Packaged.Models;
using MSStore.CLI.Helpers;
using MSStore.CLI.Services;
using Spectre.Console;

namespace MSStore.CLI.ProjectConfigurators
{
    internal class FlutterProjectConfigurator : IProjectConfigurator, IProjectPackager, IProjectPublisher
    {
        private readonly IExternalCommandExecutor _externalCommandExecutor;
        private readonly IImageConverter _imageConverter;
        private readonly IBrowserLauncher _browserLauncher;
        private readonly IConsoleReader _consoleReader;
        private readonly IZipFileManager _zipFileManager;
        private readonly IFileDownloader _fileDownloader;
        private readonly IAzureBlobManager _azureBlobManager;
        private readonly ILogger _logger;

        public FlutterProjectConfigurator(IExternalCommandExecutor externalCommandExecutor, IImageConverter imageConverter, IBrowserLauncher browserLauncher, IConsoleReader consoleReader, IZipFileManager zipFileManager, IFileDownloader fileDownloader, IAzureBlobManager azureBlobManager, ILogger<FlutterProjectConfigurator> logger)
        {
            _externalCommandExecutor = externalCommandExecutor ?? throw new ArgumentNullException(nameof(externalCommandExecutor));
            _imageConverter = imageConverter ?? throw new ArgumentNullException(nameof(imageConverter));
            _browserLauncher = browserLauncher ?? throw new ArgumentNullException(nameof(browserLauncher));
            _consoleReader = consoleReader ?? throw new ArgumentNullException(nameof(consoleReader));
            _zipFileManager = zipFileManager ?? throw new ArgumentNullException(nameof(zipFileManager));
            _fileDownloader = fileDownloader ?? throw new ArgumentNullException(nameof(fileDownloader));
            _azureBlobManager = azureBlobManager ?? throw new ArgumentNullException(nameof(azureBlobManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string ConfiguratorProjectType { get; } = "Flutter";

        public string[] SupportedProjectPattern { get; } = new[] { "pubspec.yaml" };

        public bool CanConfigure(string pathOrUrl)
        {
            if (string.IsNullOrEmpty(pathOrUrl))
            {
                return false;
            }

            try
            {
                DirectoryInfo directoryPath = new DirectoryInfo(pathOrUrl);
                return SupportedProjectPattern.Any(y => directoryPath.GetFiles(y).Any());
            }
            catch
            {
                return false;
            }
        }

        private (DirectoryInfo projectRootPath, FileInfo flutterProjectFiles) GetInfo(string pathOrUrl)
        {
            DirectoryInfo projectRootPath = new DirectoryInfo(pathOrUrl);
            FileInfo[] flutterProjectFiles = projectRootPath.GetFiles(SupportedProjectPattern.First(), SearchOption.TopDirectoryOnly);

            if (flutterProjectFiles.Length == 0)
            {
                throw new MSStoreException("No pubspec.yaml file found in the project root directory.");
            }

            var flutterProjectFile = flutterProjectFiles.First();

            return (projectRootPath, flutterProjectFile);
        }

        public int? ValidateCommand(string pathOrUrl, DirectoryInfo? output, bool? commandPackage, bool? commandPublish)
        {
            return null;
        }

        public async Task<(int returnCode, DirectoryInfo? outputDirectory)> ConfigureAsync(string pathOrUrl, DirectoryInfo? output, string publisherDisplayName, DevCenterApplication app, IStorePackagedAPI storePackagedAPI, CancellationToken ct)
        {
            var (projectRootPath, flutterProjectFile) = GetInfo(pathOrUrl);

            await InstallMsixDependencyAsync(projectRootPath, ct);

            using var fileStream = flutterProjectFile.Open(FileMode.Open, FileAccess.ReadWrite);

            bool msixConfigExists = false;

            string[] yamlLines;

            using var streamReader = new StreamReader(fileStream);
            using var streamWriter = new StreamWriter(fileStream);

            var yaml = await streamReader.ReadToEndAsync(ct);

            yamlLines = yaml.Split(Environment.NewLine);

            foreach (var line in yamlLines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var indexOfMsixConfig = line.IndexOf("msix_config", StringComparison.OrdinalIgnoreCase);
                    if (indexOfMsixConfig > -1)
                    {
                        var commentStartIndex = line.IndexOf('#');
                        if (commentStartIndex == -1 || commentStartIndex > indexOfMsixConfig)
                        {
                            msixConfigExists = true;
                            break;
                        }
                    }
                }
            }

            // TODO: This only works for first initialization.
            // If msix_config section already exists, it won't work.
            if (!msixConfigExists)
            {
                var imagePath = await GenerateImageFromIcoAsync(projectRootPath, ct);

                if (imagePath == null)
                {
                    return (-2, null);
                }

                fileStream.Seek(0, SeekOrigin.End);

                if (!string.IsNullOrWhiteSpace(yamlLines.Last()))
                {
                    streamWriter.WriteLine();
                }

                streamWriter.WriteLine();

                streamWriter.WriteLine($"msix_config:");
                streamWriter.WriteLine($"  display_name: {app.PrimaryName}");
                streamWriter.WriteLine($"  publisher_display_name: {publisherDisplayName}");
                streamWriter.WriteLine($"  publisher: {app.PublisherName}");
                streamWriter.WriteLine($"  identity_name: {app.PackageIdentityName}");
                streamWriter.WriteLine($"  msix_version: 0.0.1.0");
                if (!string.IsNullOrEmpty(imagePath))
                {
                    streamWriter.WriteLine($"  logo_path: {Path.GetRelativePath(projectRootPath.FullName, imagePath)}");
                }

                streamWriter.WriteLine($"  msstore_appId: {app.Id}");

                AnsiConsole.WriteLine($"Flutter project '{flutterProjectFile.FullName}' is now configured to build to the Microsoft Store!");
                AnsiConsole.WriteLine("For more information on building your Flutter project to the Microsoft Store, see https://pub.dev/packages/msix#microsoft-store-icon-publishing-to-the-microsoft-store");
            }

            return (0, output);
        }

        private async Task<string?> GenerateImageFromIcoAsync(DirectoryInfo projectRootPath, CancellationToken ct)
        {
            try
            {
                var resourcesDirInfo = new DirectoryInfo(Path.Combine(projectRootPath.FullName, "windows", "runner", "resources"));
                if (!resourcesDirInfo.Exists)
                {
                    return string.Empty;
                }

                var icons = resourcesDirInfo.GetFiles("*.ico");
                var icon = icons.FirstOrDefault();
                if (icon == null)
                {
                    return null;
                }

                var logoPath = Path.Combine(resourcesDirInfo.FullName, Path.GetFileNameWithoutExtension(icon.Name) + ".png");

                await _imageConverter.ConvertIcoToPngAsync(icon.FullName, logoPath, ct);

                return logoPath;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error generating logo.png from icon.ico: {ex.Message}[/]");
                return null;
            }
        }

        private async Task InstallMsixDependencyAsync(DirectoryInfo projectRootPath, CancellationToken ct)
        {
            var pubGet = await RunPubGet(projectRootPath, ct);

            if (!pubGet)
            {
                throw new MSStoreException("Failed to run 'flutter pub get'.");
            }

            var msixInstalled = await AnsiConsole.Status().StartAsync("Checking if package 'msix' is already installed...", async ctx =>
            {
                try
                {
                    var result = await _externalCommandExecutor.RunAsync("flutter", "pub add --dev msix --dry-run", projectRootPath.FullName, ct);
                    if ((result.ExitCode == 0 && result.StdOut.Contains("No dependencies would change")) ||
                        (result.ExitCode == 65 && result.StdErr.Contains("\"msix\" is already in \"dev_dependencies\"")))
                    {
                        ctx.SuccessStatus("'msix' package is already installed, no need to install it again.");
                        return true;
                    }

                    ctx.SuccessStatus("'msix' package is not yet installed.");
                    return false;
                }
                catch (Exception)
                {
                    // _logger...
                    throw new MSStoreException("Failed to check if msix package is already installed..");
                }
            });

            if (msixInstalled == true)
            {
                return;
            }

            AnsiConsole.WriteLine();

            await AnsiConsole.Status().StartAsync("Installing 'msix' package...", async ctx =>
            {
                try
                {
                    var result = await _externalCommandExecutor.RunAsync("flutter", "pub add --dev msix", projectRootPath.FullName, ct);
                    if (result.ExitCode != 0)
                    {
                        throw new MSStoreException(result.StdErr);
                    }

                    ctx.SuccessStatus("'msix' package installed successfully!");
                }
                catch (Exception)
                {
                    // _logger...
                    throw new MSStoreException("Failed to install msix package.");
                }
            });
        }

        private async Task<bool> RunPubGet(DirectoryInfo projectRootPath, CancellationToken ct)
        {
            return await AnsiConsole.Status().StartAsync("Running 'flutter pub get'...", async ctx =>
            {
                try
                {
                    var result = await _externalCommandExecutor.RunAsync("flutter", "pub get", projectRootPath.FullName, ct);
                    if (result.ExitCode == 0)
                    {
                        ctx.SuccessStatus("'flutter pub get' ran successfully.");
                        return true;
                    }

                    ctx.ErrorStatus("'flutter pub get' failed.");

                    return false;
                }
                catch (Exception)
                {
                    // _logger...
                    throw new MSStoreException("Failed to run 'flutter pub get'.");
                }
            });
        }

        public async Task<(int returnCode, DirectoryInfo? outputDirectory)> PackageAsync(string pathOrUrl, DevCenterApplication? app, DirectoryInfo? output, IStorePackagedAPI storePackagedAPI, CancellationToken ct)
        {
            var (projectRootPath, flutterProjectFile) = GetInfo(pathOrUrl);

            if (app == null)
            {
                var pubGet = await RunPubGet(projectRootPath, ct);

                if (!pubGet)
                {
                    throw new MSStoreException("Failed to run 'flutter pub get'.");
                }
            }

            return await AnsiConsole.Status().StartAsync("Packaging 'msix'...", async ctx =>
            {
                try
                {
                    var args = "--store";
                    if (output != null)
                    {
                        args += $" --output-path \"{output.FullName}\"";
                    }

                    var result = await _externalCommandExecutor.RunAsync("flutter", $"pub run msix:build {args}", projectRootPath.FullName, ct);

                    if (result.ExitCode != 0)
                    {
                        throw new MSStoreException(result.StdErr);
                    }

                    ctx.SuccessStatus("Store package built successfully!");

                    result = await _externalCommandExecutor.RunAsync("flutter", $"pub run msix:pack {args}", projectRootPath.FullName, ct);

                    if (result.ExitCode != 0)
                    {
                        throw new MSStoreException(result.StdErr);
                    }

                    var cleanedStdOut = System.Text.RegularExpressions.Regex.Replace(result.StdOut, @"\e([^\[\]]|\[.*?[a-zA-Z]|\].*?\a)", string.Empty);

                    var msixLine = cleanedStdOut.Split(new string[] { "\n", Environment.NewLine }, StringSplitOptions.None).LastOrDefault(line => line.Contains("msix created:"));
                    int index;
                    if (msixLine == null || (index = msixLine.IndexOf(": ")) == -1)
                    {
                        throw new MSStoreException("Failed to find the path to the packaged msix file.");
                    }

                    var msixPath = msixLine.Substring(index + 1).Trim();

                    FileInfo? msixFile = null;
                    if (msixPath != null)
                    {
                        if (Path.IsPathFullyQualified(msixPath))
                        {
                            msixFile = new FileInfo(msixPath);
                        }
                        else
                        {
                            msixFile = new FileInfo(Path.Join(projectRootPath.FullName, msixPath));
                        }
                    }

                    return (0, msixFile?.Directory);
                }
                catch (Exception ex)
                {
                    // _logger...
                    throw new MSStoreException("Failed to generate msix package.", ex);
                }
            });
        }

        public async Task<int> PublishAsync(string pathOrUrl, DevCenterApplication? app, DirectoryInfo? inputDirectory, IStorePackagedAPI storePackagedAPI, CancellationToken ct)
        {
            var (projectRootPath, flutterProjectFile) = GetInfo(pathOrUrl);

            // Try to find AppId inside the pubspec.yaml file
            app = await storePackagedAPI.EnsureAppInitializedAsync(
                app,
                () => GetAppIdFromPubSpecAsync(flutterProjectFile, ct),
                ct);

            if (app?.Id == null)
            {
                return -1;
            }

            AnsiConsole.MarkupLine($"AppId: [green bold]{app.Id}[/]");

            if (inputDirectory == null)
            {
                inputDirectory = new DirectoryInfo(Path.Combine(projectRootPath.FullName, "build", "windows", "runner", "Release"));
            }

            var output = projectRootPath.CreateSubdirectory(Path.Join("build", "windows", "MSStore.CLI"));

            var packageFiles = inputDirectory.GetFiles("*.*", SearchOption.TopDirectoryOnly)
                                     .Where(f => f.Extension == ".msix");

            return await storePackagedAPI.PublishAsync(app, GetFirstSubmissionDataAsync, output, packageFiles, _browserLauncher, _consoleReader, _zipFileManager, _fileDownloader, _azureBlobManager, _logger, ct);
        }

        private Task<(string?, List<SubmissionImage>)> GetFirstSubmissionDataAsync(string listingLanguage, CancellationToken ct)
        {
            var description = "My Flutter App";
            var images = new List<SubmissionImage>();
            return Task.FromResult<(string?, List<SubmissionImage>)>((description, images));
        }

        private async Task<string?> GetAppIdFromPubSpecAsync(FileInfo flutterProjectFile, CancellationToken ct)
        {
            string? appId = null;
            using var fileStream = flutterProjectFile.Open(FileMode.Open, FileAccess.ReadWrite);

            string[] yamlLines;

            using var streamReader = new StreamReader(fileStream);

            var yaml = await streamReader.ReadToEndAsync(ct);

            yamlLines = yaml.Split(Environment.NewLine);

            foreach (var line in yamlLines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    try
                    {
                        var indexOfMsstoreAppId = line.IndexOf("msstore_appId", StringComparison.OrdinalIgnoreCase);
                        if (indexOfMsstoreAppId > -1)
                        {
                            var commentStartIndex = line.IndexOf('#');
                            if (commentStartIndex == -1 || commentStartIndex > indexOfMsstoreAppId)
                            {
                                if (commentStartIndex > indexOfMsstoreAppId)
                                {
                                    appId = line.Substring(0, commentStartIndex).Split(':').LastOrDefault()?.Trim();
                                }
                                else
                                {
                                    appId = line.Split(':').LastOrDefault()?.Trim();
                                }

                                if (string.IsNullOrEmpty(appId))
                                {
                                    throw new MSStoreException("Failed to find the 'msstore_appId' in the pubspec.yaml file.");
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return appId;
        }
    }
}
