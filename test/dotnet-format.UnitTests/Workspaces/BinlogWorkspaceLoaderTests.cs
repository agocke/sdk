// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Tools.Tests.Utilities;
using Microsoft.CodeAnalysis.Tools.Tests.XUnit;
using Microsoft.CodeAnalysis.Tools.Utilities;
using Microsoft.CodeAnalysis.Tools.Workspaces;
using Microsoft.Extensions.Logging;

namespace Microsoft.CodeAnalysis.Tools.Tests.Workspaces
{
    public class BinlogWorkspaceLoaderTests
    {
        // Microsoft.CodeAnalysis.CSharp.ErrorCode
        private const string ERR_NameNotInContext = "CS0103";
        private const string ERR_NoEntryPoint = "CS5001";

        // Microsoft.CodeAnalysis.VisualBasic.ERRID
        private const string ERR_UndefinedType1 = "BC30002";

        private static string ProjectsPath => TestProjectsPathHelper.GetProjectsDirectory();

        protected ITestOutputHelper TestOutputHelper { get; set; }

        public BinlogWorkspaceLoaderTests(ITestOutputHelper output)
        {
            TestOutputHelper = output;
        }

        [MSBuildTheory(typeof(WindowsOnly))]
        [InlineData("winforms")]
        [InlineData("winformslib")]
        [InlineData("wpf")]
        [InlineData("wpfusercontrollib")]
        [InlineData("wpflib")]
        [InlineData("wpfcustomcontrollib")]
        public async Task CSharpTemplateProject_WindowsOnly_LoadFromBinlogWithNoDiagnostics(string templateName)
        {
            var ignoredDiagnostics = templateName switch
            {
                "wpf" => new string[] { ERR_NoEntryPoint, ERR_NameNotInContext },
                "wpfusercontrollib" => new string[] { ERR_NameNotInContext },
                _ => Array.Empty<string>(),
            };

            await AssertTemplateProjectLoadsFromBinlogCleanlyAsync(templateName, LanguageNames.CSharp, ignoredDiagnostics);
        }

        [MSBuildTheory]
        [InlineData("classlib")]
        [InlineData("console")]
        public async Task CSharpTemplateProject_LoadFromBinlogWithNoDiagnostics(string templateName)
        {
            await AssertTemplateProjectLoadsFromBinlogCleanlyAsync(templateName, LanguageNames.CSharp, Array.Empty<string>());
        }

        [MSBuildTheory]
        [InlineData("classlib")]
        [InlineData("console")]
        public async Task VisualBasicTemplateProject_LoadFromBinlogWithNoDiagnostics(string templateName)
        {
            var ignoredDiagnostics = (templateName, isWindows: OperatingSystem.IsWindows()) switch
            {
                (_, isWindows: false) => new string[] { ERR_UndefinedType1 },
                _ => Array.Empty<string>(),
            };

            await AssertTemplateProjectLoadsFromBinlogCleanlyAsync(templateName, LanguageNames.VisualBasic, ignoredDiagnostics);
        }

        [MSBuildFact]
        public async Task LoadAsync_WithMissingBinlog_ThrowsException()
        {
            var logger = new TestLogger();
            var missingBinlogPath = Path.Combine(ProjectsPath, "nonexistent.binlog");

            await Assert.ThrowsAsync<FileNotFoundException>(async () =>
                await BinlogWorkspaceLoader.LoadAsync(missingBinlogPath, logger, CancellationToken.None));
        }

        [MSBuildFact]
        public async Task LoadAsync_ExtractsMultipleProjects()
        {
            var logger = new TestLogger();
            var solutionPath = Path.Combine(ProjectsPath, "for_binlog_loader", "multi_project");
            var binlogPath = Path.Combine(solutionPath, "build.binlog");

            try
            {
                // Create a solution with multiple projects
                Directory.CreateDirectory(solutionPath);

                var project1Path = Path.Combine(solutionPath, "Project1");
                var project2Path = Path.Combine(solutionPath, "Project2");

                await DotNetHelper.NewProjectAsync("classlib", project1Path, LanguageNames.CSharp, TestOutputHelper);
                await DotNetHelper.NewProjectAsync("classlib", project2Path, LanguageNames.CSharp, TestOutputHelper);

                // Create a solution file
                var solutionFilePath = Path.Combine(solutionPath, "multi_project.sln");
                await RunDotNetAsync($"new sln -o \"{solutionPath}\" -n multi_project", TestOutputHelper);
                await RunDotNetAsync($"sln \"{solutionFilePath}\" add \"{Path.Combine(project1Path, "Project1.csproj")}\"", TestOutputHelper);
                await RunDotNetAsync($"sln \"{solutionFilePath}\" add \"{Path.Combine(project2Path, "Project2.csproj")}\"", TestOutputHelper);

                // Build with binlog
                await BuildProjectWithBinlogAsync(solutionFilePath, binlogPath, TestOutputHelper);

                var workspace = await BinlogWorkspaceLoader.LoadAsync(binlogPath, logger, CancellationToken.None);

                Assert.NotNull(workspace);
                Assert.Equal(2, workspace.CurrentSolution.Projects.Count());
            }
            finally
            {
                if (Directory.Exists(solutionPath))
                {
                    Directory.Delete(solutionPath, true);
                }
            }
        }

        [MSBuildFact]
        public async Task FormatWorkspace_WithBinlog_FormatsUnformattedProject()
        {
            var unformattedProjectPath = Path.Combine("for_code_formatter", "unformatted_project");
            var unformattedProjectFilePath = Path.Combine(unformattedProjectPath, "unformatted_project.csproj");

            await TestFormatWorkspaceWithBinlogAsync(
                unformattedProjectFilePath,
                expectedExitCode: 0,
                expectedFilesFormatted: 2,
                expectedFileCount: 6);
        }

        [MSBuildFact]
        public async Task FormatWorkspace_WithBinlog_NoChangesInFormattedProject()
        {
            var formattedProjectPath = Path.Combine("for_code_formatter", "formatted_project");
            var formattedProjectFilePath = Path.Combine(formattedProjectPath, "formatted_project.csproj");

            await TestFormatWorkspaceWithBinlogAsync(
                formattedProjectFilePath,
                expectedExitCode: 0,
                expectedFilesFormatted: 0,
                expectedFileCount: 3);
        }

        private async Task<string> TestFormatWorkspaceWithBinlogAsync(
            string workspaceFilePath,
            int expectedExitCode,
            int expectedFilesFormatted,
            int expectedFileCount)
        {
            var currentDirectory = Environment.CurrentDirectory;
            Environment.CurrentDirectory = TestProjectsPathHelper.GetProjectsDirectory();

            var workspacePath = Path.GetFullPath(workspaceFilePath);
            var binlogPath = Path.ChangeExtension(workspacePath, ".test.binlog");

            try
            {
                // Build the project to create a binlog
                await BuildProjectWithBinlogAsync(workspacePath, binlogPath, TestOutputHelper);

                var logger = new TestLogger();

                var fileMatcher = SourceFileMatcher.CreateMatcher(Array.Empty<string>(), Array.Empty<string>());
                var formatOptions = new FormatOptions(
                    workspacePath,
                    WorkspaceType.Project,
                    NoRestore: true,
                    LogLevel.Trace,
                    FixCategory.Whitespace,
                    CodeStyleSeverity: DiagnosticSeverity.Error,
                    AnalyzerSeverity: DiagnosticSeverity.Error,
                    Diagnostics: ImmutableHashSet<string>.Empty,
                    ExcludeDiagnostics: ImmutableHashSet<string>.Empty,
                    SaveFormattedFiles: false,
                    ChangesAreErrors: false,
                    fileMatcher,
                    ReportPath: string.Empty,
                    IncludeGeneratedFiles: false,
                    BinaryLogPath: null,
                    BinlogInputPath: binlogPath);

                var formatResult = await CodeFormatter.FormatWorkspaceAsync(formatOptions, logger, CancellationToken.None);

                var log = logger.GetLog();

                try
                {
                    Assert.Equal(expectedExitCode, formatResult.ExitCode);
                    Assert.Equal(expectedFilesFormatted, formatResult.FilesFormatted);
                    Assert.Equal(expectedFileCount, formatResult.FileCount);
                }
                catch
                {
                    TestOutputHelper.WriteLine(log);
                    throw;
                }

                return log;
            }
            finally
            {
                Environment.CurrentDirectory = currentDirectory;
                if (File.Exists(binlogPath))
                {
                    File.Delete(binlogPath);
                }
            }
        }

        private async Task AssertTemplateProjectLoadsFromBinlogCleanlyAsync(string templateName, string languageName, string[] ignoredDiagnostics = null)
        {
            var logger = new TestLogger();

            try
            {
                if (ignoredDiagnostics is not null && ignoredDiagnostics.Length > 0)
                {
                    TestOutputHelper.WriteLine($"Ignoring compiler diagnostics: \"{string.Join("\", \"", ignoredDiagnostics)}\"");
                }

                // Clean up previous run
                CleanupBinlogProject(templateName, languageName);

                var projectFilePath = await GenerateProjectFromTemplateAsync(templateName, languageName, TestOutputHelper);
                var binlogPath = Path.ChangeExtension(projectFilePath, ".binlog");

                // Build the project to generate a binlog
                await BuildProjectWithBinlogAsync(projectFilePath, binlogPath, TestOutputHelper);

                await AssertBinlogLoadsCleanlyAsync(binlogPath, logger, ignoredDiagnostics);

                // Clean up successful run
                CleanupBinlogProject(templateName, languageName);
            }
            catch
            {
                TestOutputHelper.WriteLine(logger.GetLog());
                throw;
            }
        }

        private static async Task<string> GenerateProjectFromTemplateAsync(string templateName, string languageName, ITestOutputHelper outputHelper)
        {
            var projectPath = GetProjectPath(templateName, languageName);
            var projectFilePath = GetProjectFilePath(projectPath, languageName);

            var exitCode = await DotNetHelper.NewProjectAsync(templateName, projectPath, languageName, outputHelper);
            Assert.Equal(0, exitCode);

            return projectFilePath;
        }

        private static async Task BuildProjectWithBinlogAsync(string projectFilePath, string binlogPath, ITestOutputHelper outputHelper)
        {
            var processInfo = ProcessRunner.CreateProcess(
                "dotnet",
                $"build \"{projectFilePath}\" -bl:\"{binlogPath}\"",
                captureOutput: true,
                displayWindow: false);
            var result = await processInfo.Result;

            outputHelper.WriteLine(string.Join(Environment.NewLine, result.OutputLines));

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(binlogPath), $"Binlog was not created at {binlogPath}");
        }

        private static async Task RunDotNetAsync(string arguments, ITestOutputHelper outputHelper)
        {
            var processInfo = ProcessRunner.CreateProcess("dotnet", arguments, captureOutput: true, displayWindow: false);
            var result = await processInfo.Result;
            outputHelper.WriteLine(string.Join(Environment.NewLine, result.OutputLines));
        }

        private static async Task AssertBinlogLoadsCleanlyAsync(string binlogPath, ILogger logger, string[] ignoredDiagnostics)
        {
            var workspace = await BinlogWorkspaceLoader.LoadAsync(binlogPath, logger, CancellationToken.None);

            Assert.NotNull(workspace);
            Assert.NotEmpty(workspace.CurrentSolution.Projects);

            var project = workspace.CurrentSolution.Projects.First();
            var compilation = await project.GetCompilationAsync();

            Assert.NotNull(compilation);

            // Unnecessary using directives are reported with a severity of Hidden
            var diagnostics = compilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity > DiagnosticSeverity.Hidden && ignoredDiagnostics?.Contains(diagnostic.Id) != true);

            Assert.Empty(diagnostics);
        }

        private static void CleanupBinlogProject(string templateName, string languageName)
        {
            var projectPath = GetProjectPath(templateName, languageName);

            if (Directory.Exists(projectPath))
            {
                Directory.Delete(projectPath, true);
            }
        }

        private static string GetProjectPath(string templateName, string languageName)
        {
            var languagePrefix = languageName.Replace("#", "Sharp").Replace(' ', '_').ToLower();
            var projectName = $"{languagePrefix}_{templateName}_binlog_project";
            return Path.Combine(ProjectsPath, "for_binlog_loader", projectName);
        }

        private static string GetProjectFilePath(string projectPath, string languageName)
        {
            var projectName = Path.GetFileName(projectPath);
            var projectExtension = languageName switch
            {
                LanguageNames.CSharp => "csproj",
                LanguageNames.VisualBasic => "vbproj",
                LanguageNames.FSharp => "fsproj",
                _ => throw new ArgumentOutOfRangeException(nameof(languageName), actualValue: languageName, message: "Only C#, VB.NET, and F# projects are supported.")
            };
            return Path.Combine(projectPath, $"{projectName}.{projectExtension}");
        }
    }
}
