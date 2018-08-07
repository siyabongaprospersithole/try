using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Clockwise;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using MLS.Agent.Tools;
using WorkspaceServer.Models.Execution;
using WorkspaceServer.Servers.Roslyn.Instrumentation;
using WorkspaceServer.Transformations;
using Workspace = WorkspaceServer.Models.Execution.Workspace;

namespace WorkspaceServer.Servers.Roslyn
{
    public static class WorkspaceBuildExtensions
    {
        public static async Task<Compilation> Compile(this WorkspaceBuild build, Workspace workspace, Budget budget)
        {
            var sourceFiles = workspace.GetSourceFiles()
                                       .Concat(await build.GetSourceFiles())
                                       .ToArray();

            var (compilation, documents) = await build.GetCompilation(sourceFiles, budget);

            var viewports = new BufferInliningTransformer().ExtractViewPorts(workspace);

            var instrumentationRegions = viewports.Where(v => v.Destination?.Name != null)
                                                  .GroupBy(v => v.Destination.Name,
                                                           v => v.Region,
                                                           (name, regions) => new InstrumentationMap(name, regions));

            if (workspace.IncludeInstrumentation)
            {
                compilation = AugmentCompilation(instrumentationRegions, compilation, documents.First().Project.Solution);
            }

            return compilation;
        }

        private static Compilation AugmentCompilation(IEnumerable<InstrumentationMap> regions, Compilation compilation, Solution solution)
        {
            var newCompilation = compilation;
            foreach (var tree in newCompilation.SyntaxTrees)
            {
                var replacementRegions = regions?.Where(r => tree.FilePath.EndsWith(r.FileToInstrument)).FirstOrDefault()?.InstrumentationRegions;

                var visitor = new InstrumentationSyntaxVisitor(solution.GetDocument(tree), replacementRegions);
                var linesWithInstrumentation = visitor.Augmentations.Data.Keys;

                var rewrite = new InstrumentationSyntaxRewriter(
                    linesWithInstrumentation,
                    new[] { visitor.VariableLocations },
                    new[] { visitor.Augmentations });
                var newRoot = rewrite.Visit(tree.GetRoot());
                var newTree = tree.WithRootAndOptions(newRoot, tree.Options);

                newCompilation = newCompilation.ReplaceSyntaxTree(tree, newTree);
            }

            // if it failed to compile, just return the original, unaugmented compilation
            var augmentedDiagnostics = newCompilation.GetDiagnostics();
            if (augmentedDiagnostics.Any(e => e.Severity == DiagnosticSeverity.Error))
            {
                throw new Exception("Augmented source failed to compile: " + string.Join(Environment.NewLine, augmentedDiagnostics));
            }

            return newCompilation;
        }
        
        public static async Task<(Compilation compilation, IReadOnlyCollection<Document> documents)> GetCompilation(
            this WorkspaceBuild build,
            IReadOnlyCollection<SourceFile> sources,
            Budget budget)
        {
            var projectId = ProjectId.CreateNewId();

            var workspace = await build.GetRoslynWorkspace(projectId);

            var currentSolution = workspace.CurrentSolution;

            foreach (var source in sources)
            {
                if (currentSolution.Projects
                                   .SelectMany(p => p.Documents)
                                   .FirstOrDefault(d => d.Name == source.Name) is Document document)
                {
                    // there's a pre-existing document, so overwrite it's contents
                    document = document.WithText(source.Text);
                    currentSolution = document.Project.Solution;
                }
                else
                {
                    var docId = DocumentId.CreateNewId(projectId, $"{build.Name}.Document");

                    currentSolution = currentSolution.AddDocument(docId, source.Name, source.Text);
                }

            }

            var project = currentSolution.GetProject(projectId);

            var compilation = await project.GetCompilationAsync().CancelIfExceeds(budget);

            return (compilation, project.Documents.ToArray());
        }

        public static async Task<AdhocWorkspace> GetRoslynWorkspace(this WorkspaceBuild build, ProjectId projectId = null)
        {
            await build.EnsureBuilt();

            projectId = projectId ?? ProjectId.CreateNewId(build.Name);

            var buildLog = build.Directory.GetFiles("msbuild.log").SingleOrDefault();

            var commandLineArgs = buildLog?.FindCompilerCommandLine()?.ToArray();

            var csharpCommandLineArguments = CSharpCommandLineParser.Default.Parse(
                commandLineArgs,
                build.Directory.FullName,
                RuntimeEnvironment.GetRuntimeDirectory());

            var projectInfo = CommandLineProject.CreateProjectInfo(
                projectId,
                build.Name,
                csharpCommandLineArguments.CompilationOptions.Language,
                csharpCommandLineArguments,
                build.Directory.FullName);

            var workspace = new AdhocWorkspace(MefHostServices.DefaultHost);

            workspace.AddProject(projectInfo);

            return workspace;
        }

        public static async Task<IEnumerable<SourceFile>> GetSourceFiles(this WorkspaceBuild build)
        {
            await build.EnsureBuilt();

            // FIX: (GetSourceFiles) include source files from compiler args

            return Enumerable.Empty<SourceFile>();
        }
    }
}