// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.Extensions.Logging;
using MSBuildTask = Microsoft.Build.Logging.StructuredLogger.Task;
using MSBuildProject = Microsoft.Build.Logging.StructuredLogger.Project;

namespace Microsoft.CodeAnalysis.Tools.Workspaces;

internal static class BinlogWorkspaceLoader
{
    internal record CompilerInvocation(
        string ProjectName,
        string ProjectDirectory,
        string CommandLine,
        string Language);

    public static Task<Workspace?> LoadAsync(
        string binlogPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogTrace(Resources.Loading_projects_from_binlog);

        var invocations = ExtractCompilerInvocations(binlogPath);

        if (invocations.Count == 0)
        {
            logger.LogError(Resources.No_compiler_invocations_found_in_binlog);
            return Task.FromResult<Workspace?>(null);
        }

        logger.LogDebug(Resources.Found_0_compiler_invocations_in_binlog, invocations.Count);

        var workspace = new AdhocWorkspace();

        foreach (var invocation in invocations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var projectInfo = CommandLineProject.CreateProjectInfo(
                    invocation.ProjectName,
                    invocation.Language,
                    invocation.CommandLine,
                    invocation.ProjectDirectory,
                    workspace);

                workspace.AddProject(projectInfo);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to create project {ProjectName}: {Error}",
                    invocation.ProjectName, ex.Message);
            }
        }

        return Task.FromResult<Workspace?>(workspace);
    }

    private static List<CompilerInvocation> ExtractCompilerInvocations(string binlogPath)
    {
        var results = new List<CompilerInvocation>();
        var build = BinaryLog.ReadBuild(binlogPath);

        build.VisitAllChildren<MSBuildTask>(task =>
        {
            string? language = null;
            if (task.Name == "Csc")
            {
                language = LanguageNames.CSharp;
            }
            else if (task.Name == "Vbc")
            {
                language = LanguageNames.VisualBasic;
            }

            if (language != null)
            {
                var project = task.GetNearestParent<MSBuildProject>();
                var projectName = project?.Name ?? "Unknown";
                var projectDirectory = project?.ProjectDirectory ?? Environment.CurrentDirectory;

                var commandLine = GetCompilerCommandLine(task);
                if (!string.IsNullOrEmpty(commandLine))
                {
                    results.Add(new CompilerInvocation(projectName, projectDirectory, commandLine, language));
                }
            }
        });

        return results;
    }

    private static string? GetCompilerCommandLine(MSBuildTask compilerTask)
    {
        // Look for CommandLineArguments property which contains the full compiler command line
        foreach (var child in compilerTask.Children)
        {
            if (child is Property prop && prop.Name == "CommandLineArguments")
            {
                var cmdLine = prop.Value;
                // Skip the compiler executable path
                var firstSpace = cmdLine.IndexOf(' ');
                if (firstSpace > 0)
                {
                    return cmdLine.Substring(firstSpace + 1);
                }
                return cmdLine;
            }
        }

        return null;
    }
}
