using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Api.ToMcp.Generator.Diagnostics;
using Api.ToMcp.Generator.Emitting;
using Api.ToMcp.Generator.Models;
using Api.ToMcp.Generator.Parsing;

namespace Api.ToMcp.Generator
{
    [Generator(LanguageNames.CSharp)]
    public sealed class McpToolGenerator : IIncrementalGenerator
    {
        private const string IncludeListName = "include";
        private const string ExcludeListName = "exclude";
        private const string GeneratedSourceSuffix = ".g.cs";
        private const string RegistrationSourceName = "McpToolsRegistration";

        /// <summary>
        /// Where the build targets create the config when ApiToMcpConfigPath is not set, relative to the project.
        /// </summary>
        private const string DefaultConfigPath = "Mcp/generator.json";

        /// <summary>
        /// The full path of ApiToMcpConfigPath, which the build targets make a compiler-visible property.
        /// </summary>
        private const string ConfigPathBuildProperty = "build_property._McpConfigFullPath";

        private const char EntryActionSeparator = ConventionNames.EntryActionSeparator;

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Step 1: Read the config: the additional file at the path the build targets pass from ApiToMcpConfigPath,
            // whatever its name, else the first named generator.json, as a project without the targets, or with no file
            // at that path, lists its config by hand.
            var configPathProvider = context.AnalyzerConfigOptionsProvider
                .Select((options, _) => GetConfigPath(options.GlobalOptions));

            var configProvider = context.AdditionalTextsProvider
                .Combine(configPathProvider)
                .Select((source, _) => (File: source.Left, Match: MatchConfigFile(source.Left.Path, source.Right)))
                .Where(x => x.Match != ConfigFileMatch.None)
                .Select((x, ct) => (x.Match, Config: ConfigParser.Parse(x.File.GetText(ct)?.ToString()) with { FilePath = x.File.Path }))
                .Collect()
                .Combine(configPathProvider)
                .Select((source, _) => SelectConfig(source.Left, source.Right))
                .WithTrackingName(TrackingNames.Config);

            // Step 2: Find all controller classes
            var controllerProvider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (node, _) => node is ClassDeclarationSyntax,
                    transform: (ctx, ct) => ControllerScanner.TryGetControllerInfo(ctx, ct))
                .Where(info => info != null)
                .Select((info, _) => info!)
                .WithTrackingName(TrackingNames.Controllers);

            // Step 2b: Find the Minimal API endpoints the config could select, each Map* call as the actions of the class
            // that declares its handler. An endpoint's route can depend on the calls of the method it is in, in any file,
            // so this step reads the whole compilation, after every edit; it looks up the Map* calls in an index of each
            // file's invocations, which an edit builds again only for the file it changed, and analyzes only the endpoints
            // the config could select.
            var endpointProvider = context.CompilationProvider
                .Combine(configProvider)
                .Select((source, ct) => MinimalApiScanner.GetEndpoints(source.Left, source.Right, ct))
                .WithTrackingName(TrackingNames.Endpoints);

            // Step 3: Decide which tools to generate and which diagnostics to report, from the models alone. After an edit
            // that changes no controller, no endpoint and not the config, Roslyn reuses this step's output, and the sources
            // made from it unless the edit changes what step 3b proves.
            var plan = controllerProvider.Collect()
                .Combine(endpointProvider)
                .Combine(configProvider)
                .Select((source, _) => CreatePlan(source.Left.Left, source.Left.Right, source.Right))
                .WithTrackingName(TrackingNames.Plan);

            // Step 3b: Prove which output schemas need not allow null. The flow analysis of an action's body binds all of
            // it, so it reads only the bodies of the tools that get an output schema, which the plan names. An edit in any
            // file can change what a body returns, so it reads the compilation, after every edit.
            var bodiesNeverEmpty = plan
                .Select((x, _) => x.BodiesToAnalyze)
                .WithTrackingName(TrackingNames.BodiesToAnalyze)
                .Combine(context.CompilationProvider)
                .Select((source, ct) => EmptyResponseAnalyzer.GetBodiesNeverEmpty(source.Left, source.Right, ct));

            // Step 4: Generate source, one output per tool, so that an edit generates only the tools it changes again.
            var tools = plan
                .Combine(bodiesNeverEmpty)
                .SelectMany((source, _) => WithProvenOutputs(source.Left.Tools, source.Right))
                .WithTrackingName(TrackingNames.ToolSources);
            context.RegisterSourceOutput(tools, (ctx, tool) =>
                ctx.AddSource(tool.ClassName + GeneratedSourceSuffix, ToolClassEmitter.Emit(tool)));

            var toolClassNames = plan
                .Select((x, _) => new EquatableArray<string>(x.Tools.Select(tool => tool.ClassName).ToImmutableArray()))
                .WithTrackingName(TrackingNames.Registration);
            context.RegisterSourceOutput(toolClassNames, (ctx, names) =>
                ctx.AddSource(RegistrationSourceName + GeneratedSourceSuffix, ToolClassEmitter.EmitRegistration(names)));

            // Step 5: Report the diagnostics. This step reads the compilation, so it runs again after every edit: a
            // warning at an action has to point into the syntax tree of the compilation that reports it, for the file's
            // #pragma warning disable and .editorconfig severities to apply to it.
            var diagnostics = plan
                .Select((x, _) => x.Diagnostics)
                .Combine(context.CompilationProvider)
                .WithTrackingName(TrackingNames.Diagnostics);
            context.RegisterSourceOutput(diagnostics, (ctx, source) =>
            {
                var (plannedDiagnostics, compilation) = source;
                foreach (var diagnostic in plannedDiagnostics)
                {
                    ctx.ReportDiagnostic(diagnostic.ToDiagnostic(compilation));
                }
            });
        }

        /// <summary>
        /// The full path of the config the build targets pass from ApiToMcpConfigPath, or null when the project does not
        /// import them.
        /// </summary>
        private static string? GetConfigPath(AnalyzerConfigOptions options)
        {
            return options.TryGetValue(ConfigPathBuildProperty, out var configPath) && !string.IsNullOrWhiteSpace(configPath)
                ? NormalizePath(configPath)
                : null;
        }

        /// <summary>
        /// How the additional file can be the config. A path that differs from the configured one only in case counts, as
        /// on a case-sensitive file system MSBuild hands the generator such a file when the configured one is missing
        /// (MCP101), but after the configured file itself, which can be among the additional files as well. The name
        /// matches only in full: a file such as openapi-generator.json is another tool's.
        /// </summary>
        private static ConfigFileMatch MatchConfigFile(string filePath, string? configPath)
        {
            if (configPath is not null)
            {
                var fullPath = NormalizePath(filePath);
                if (string.Equals(fullPath, configPath, StringComparison.Ordinal))
                    return ConfigFileMatch.ConfiguredPath;

                if (string.Equals(fullPath, configPath, StringComparison.OrdinalIgnoreCase))
                    return ConfigFileMatch.ConfiguredPathInAnotherCase;
            }

            return string.Equals(GetFileName(filePath), ConventionNames.ConfigFileName, StringComparison.OrdinalIgnoreCase)
                ? ConfigFileMatch.FileName
                : ConfigFileMatch.None;
        }

        /// <summary>
        /// The file name, cut at the platform's separators rather than by <see cref="Path.GetFileName(string)"/>, which on
        /// .NET Framework, where an IDE can run the generator, throws for a character it does not allow in paths.
        /// </summary>
        private static string GetFileName(string path)
        {
            return path.Substring(path.LastIndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) + 1);
        }

        /// <summary>
        /// The path written one way, as the build and the IDE write the same path differently: with <c>/</c> or <c>\</c>,
        /// or with a <c>..</c> segment from a relative item. A path the platform rejects stays as it is.
        /// </summary>
        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return path;
            }
        }

        /// <summary>
        /// The config at the configured path, else at that path in another case, else the first named generator.json,
        /// else the default config. The files named generator.json elsewhere that the configured one wins over are
        /// recorded for MCP015.
        /// </summary>
        private static GeneratorConfigModel SelectConfig(
            ImmutableArray<(ConfigFileMatch Match, GeneratorConfigModel Config)> configs,
            string? configPath)
        {
            if (configs.IsEmpty)
                return new GeneratorConfigModel { ConfiguredFilePath = configPath };

            var selected = configs.OrderBy(x => x.Match).First();
            var passedOver = selected.Match == ConfigFileMatch.FileName
                ? ImmutableArray<string>.Empty
                : configs.Where(x => x.Match == ConfigFileMatch.FileName).Select(x => x.Config.FilePath!).ToImmutableArray();

            return selected.Config with
            {
                ConfiguredFilePath = configPath,
                PassedOverFilePaths = passedOver
            };
        }

        /// <summary>
        /// The tools to generate for the controllers' actions and the Minimal API endpoints the config selects, and the
        /// diagnostics to report. The endpoints of each class become the actions of one controller-like model, so a
        /// config entry names them as it names a controller's actions.
        /// </summary>
        private static GenerationPlanModel CreatePlan(
            ImmutableArray<ControllerInfoModel> scannedControllers,
            EndpointsModel endpoints,
            GeneratorConfigModel config)
        {
            var endpointClasses = GroupEndpointsByClass(endpoints.Classes).ToImmutableArray();
            var controllers = scannedControllers.AddRange(endpointClasses);
            var controllerEntryNames = GetEntryNames(scannedControllers);
            var diagnostics = new List<DiagnosticInfoModel>();
            var selectedActions = SelectActions(controllers, config, controllerEntryNames);
            var entryNames = GetEntryNames(controllers);
            var unbuildableRouteDiagnostics = new List<DiagnosticInfoModel>();
            var toolActions = SkipActionsWithoutARoute(unbuildableRouteDiagnostics, RouteEndResolver.Resolve(selectedActions, controllers));

            // An invalid config gets MCP001 instead of MCP006: MCP006 would advise on the fallback, not on what the
            // file says.
            if (config.Error != null)
            {
                diagnostics.Add(DiagnosticInfoModel.InFile(
                    DiagnosticDescriptors.InvalidConfig,
                    config.FilePath,
                    new LinePositionSpan(config.Error.Position, config.Error.Position),
                    config.Error.Fallback,
                    config.Error.Reason));
            }
            else if (toolActions.IsEmpty)
            {
                ReportNoToolsGenerated(diagnostics, controllers, config, entryNames, selectedActions, controllerEntryNames);
            }

            ReportPassedOverConfigFiles(diagnostics, config);

            // An unknown mode keeps the file's lists, so their entries are reported even next to MCP001.
            ReportUnmatchedEntries(diagnostics, controllers, config, entryNames);
            ReportIncludeEntriesNamingControllerAndEndpoints(diagnostics, config, controllerEntryNames, endpointClasses);
            diagnostics.AddRange(unbuildableRouteDiagnostics);
            ReportDuplicateToolNames(diagnostics, toolActions, config);
            ReportEndpointsWithSeveralHttpMethods(diagnostics, toolActions);
            ReportEndpointsLeftOut(diagnostics, controllers, config, controllerEntryNames, endpoints.LeftOutCount);

            var toolClassNames = ToolClassEmitter.GetUniqueToolClassNames(toolActions);
            var tools = toolActions.Select((action, i) => new ToolSourceModel
            {
                ClassName = toolClassNames[i],
                Action = action with { SourceLocation = null },
                Naming = config.Naming,
                StructuredOutput = config.StructuredOutput
            }).ToImmutableArray();

            return new GenerationPlanModel
            {
                Tools = tools,
                Diagnostics = diagnostics.ToImmutableArray(),
                BodiesToAnalyze = tools
                    .Where(x => x.Action.BodyToAnalyze is not null && ToolClassEmitter.HasOutputSchema(x.Action, x.StructuredOutput))
                    .Select(x => x.Action.BodyToAnalyze!)
                    .Distinct(StringComparer.Ordinal)
                    .ToImmutableArray()
            };
        }

        /// <summary>
        /// Warns at each additional file named generator.json that the generator passes over for the file at
        /// ApiToMcpConfigPath (MCP015): it read such a file before the build passed that path, so a project that lists its
        /// config elsewhere by hand now reads another one, such as the starter the build wrote at the default path.
        /// </summary>
        private static void ReportPassedOverConfigFiles(List<DiagnosticInfoModel> diagnostics, GeneratorConfigModel config)
        {
            foreach (var passedOverFilePath in config.PassedOverFilePaths)
            {
                diagnostics.Add(DiagnosticInfoModel.InFile(
                    DiagnosticDescriptors.PassedOverConfigFile,
                    passedOverFilePath,
                    default,
                    config.FilePath,
                    passedOverFilePath));
            }
        }

        /// <summary>
        /// The tools, with a strict output schema for each whose action's body is proven to never succeed without a body.
        /// </summary>
        private static IEnumerable<ToolSourceModel> WithProvenOutputs(EquatableArray<ToolSourceModel> tools, EquatableArray<string> bodiesNeverEmpty)
        {
            var neverEmpty = new HashSet<string>(bodiesNeverEmpty, StringComparer.Ordinal);

            return tools.Select(x => x.Action.BodyToAnalyze is not null && neverEmpty.Contains(x.Action.BodyToAnalyze)
                ? x with { Action = x.Action with { IsOutputNullable = false } }
                : x);
        }

        /// <summary>
        /// Warns about every tool name that several generated tools share, at the actions that produce it: the MCP SDK
        /// keeps one of them and drops the others without an error. The tools are still generated, as renaming one would
        /// change a name a client may already call.
        /// </summary>
        private static void ReportDuplicateToolNames(
            List<DiagnosticInfoModel> diagnostics,
            ImmutableArray<ActionInfoModel> actions,
            GeneratorConfigModel config)
        {
            // The SDK looks tools up with the default string comparer, which is ordinal.
            var duplicates = actions
                .GroupBy(x => ToolClassEmitter.FormatToolName(x, config.Naming), StringComparer.Ordinal)
                .Where(x => x.Count() > 1);

            foreach (var duplicate in duplicates)
            {
                var producers = string.Join(", ", duplicate.Select(x => $"{x.ControllerName}.{x.Name} ({x.HttpMethod} {x.RouteTemplate})"));

                diagnostics.Add(DiagnosticInfoModel.InSource(
                    DiagnosticDescriptors.DuplicateToolName,
                    duplicate.Select(x => x.SourceLocation),
                    duplicate.Key,
                    duplicate.Count(),
                    producers));
            }
        }

        /// <summary>
        /// Warns at each Minimal API endpoint that gets a tool for one of the several HTTP methods <c>MapMethods</c> maps it
        /// for (MCP011): the others have none.
        /// </summary>
        private static void ReportEndpointsWithSeveralHttpMethods(List<DiagnosticInfoModel> diagnostics, ImmutableArray<ActionInfoModel> actions)
        {
            foreach (var action in actions.Where(x => !x.OtherHttpMethods.IsEmpty))
            {
                diagnostics.Add(DiagnosticInfoModel.InSource(
                    DiagnosticDescriptors.EndpointHasSeveralHttpMethods,
                    new[] { action.SourceLocation },
                    action.ControllerName,
                    action.Name,
                    action.HttpMethod,
                    string.Join(", ", action.OtherHttpMethods)));
            }
        }

        /// <summary>
        /// The Minimal API endpoints of each class that declares handlers, as one controller-like model, whichever calls
        /// and files map them.
        /// </summary>
        private static IEnumerable<ControllerInfoModel> GroupEndpointsByClass(EquatableArray<ControllerInfoModel> endpointCalls)
        {
            return endpointCalls
                .GroupBy(x => (x.Namespace, x.Name))
                .Select(x => x.First() with
                {
                    Actions = x.SelectMany(call => call.Actions).ToImmutableArray(),
                    ActionNames = x.SelectMany(call => call.ActionNames).Distinct(StringComparer.Ordinal).ToImmutableArray()
                });
        }

        /// <summary>
        /// Leaves out each action whose route a tool cannot build, and warns about it at the action (MCP009), or at the
        /// Minimal API endpoint (MCP010): its tool would not compile or would call an endpoint the app does not serve it at.
        /// </summary>
        private static ImmutableArray<ActionInfoModel> SkipActionsWithoutARoute(
            List<DiagnosticInfoModel> diagnostics,
            ImmutableArray<ActionInfoModel> actions)
        {
            var routableActions = ImmutableArray.CreateBuilder<ActionInfoModel>();
            foreach (var action in actions)
            {
                var reason = ToolClassEmitter.GetUnbuildableRouteReason(action);
                if (reason is null)
                {
                    routableActions.Add(action);
                    continue;
                }

                diagnostics.Add(DiagnosticInfoModel.InSource(
                    action.IsMinimalApiEndpoint ? DiagnosticDescriptors.UnbuildableEndpoint : DiagnosticDescriptors.UnbuildableRoute,
                    new[] { action.SourceLocation },
                    action.ControllerName,
                    action.Name,
                    reason));
            }

            return routableActions.ToImmutable();
        }

        private static ImmutableArray<ActionInfoModel> SelectActions(
            ImmutableArray<ControllerInfoModel> controllers,
            GeneratorConfigModel config,
            HashSet<string> controllerEntryNames)
        {
            var result = ImmutableArray.CreateBuilder<ActionInfoModel>();

            foreach (var controller in controllers)
            {
                foreach (var action in controller.Actions)
                {
                    if (ShouldInclude(controller, action, config, controllerEntryNames))
                    {
                        result.Add(action);
                    }
                }
            }

            return result.ToImmutable();
        }

        private static bool ShouldInclude(
            ControllerInfoModel controller,
            ActionInfoModel action,
            GeneratorConfigModel config,
            HashSet<string> controllerEntryNames)
        {
            if (!IsSelectable(controller, action))
                return false;

            // [McpExpose] always includes
            if (action.HasMcpExposeAttribute || controller.HasMcpExposeAttribute)
                return true;

            // Apply config-based selection
            var names = GetEntryNames(controller, action);

            switch (config.Mode)
            {
                case SelectionModeModel.SelectedOnly:
                    return IsIncluded(controller, action, config, controllerEntryNames);

                case SelectionModeModel.AllExceptExcluded:
                    // Without the opt-in, the mode selects a Minimal API endpoint only as an include entry names it, so
                    // updating the package exposes no endpoint the project did not choose.
                    if (action.IsMinimalApiEndpoint && config.MinimalApis != true)
                        return IsIncluded(controller, action, config, controllerEntryNames) && !names.Any(config.Exclude.Contains);

                    return !names.Any(config.Exclude.Contains);

                default:
                    return false;
            }
        }

        /// <summary>
        /// Whether the project chose to give the action a tool, which only then counts for MCP006: an action of a
        /// controller always, a Minimal API endpoint when generator.json opts in to them, an include entry names it or
        /// <c>[McpExpose]</c> marks it. Until a project does, its endpoints change nothing it is warned about.
        /// </summary>
        private static bool IsOptedIn(ControllerInfoModel controller, ActionInfoModel action, GeneratorConfigModel config, HashSet<string> controllerEntryNames)
        {
            return !action.IsMinimalApiEndpoint ||
                config.MinimalApis == true ||
                action.HasMcpExposeAttribute ||
                controller.HasMcpExposeAttribute ||
                IsIncluded(controller, action, config, controllerEntryNames);
        }

        /// <summary>
        /// Whether an include entry names the action. An entry that names a controller or one of its actions names no
        /// Minimal API endpoint, though a class of the same name declares its handler: such an entry meant the controller
        /// before endpoints became tools, and must not expose them.
        /// </summary>
        private static bool IsIncluded(ControllerInfoModel controller, ActionInfoModel action, GeneratorConfigModel config, HashSet<string> controllerEntryNames)
        {
            return GetEntryNames(controller, action).Any(x =>
                config.Include.Contains(x) && !(action.IsMinimalApiEndpoint && controllerEntryNames.Contains(x)));
        }

        /// <summary>
        /// Tells at each include entry that names a controller, or one of its actions, and also a class that declares
        /// Minimal API endpoints, that it selects only the controller's (MCP013). An info: the entry selects what it did
        /// before endpoints became tools.
        /// </summary>
        private static void ReportIncludeEntriesNamingControllerAndEndpoints(
            List<DiagnosticInfoModel> diagnostics,
            GeneratorConfigModel config,
            HashSet<string> controllerEntryNames,
            ImmutableArray<ControllerInfoModel> endpointClasses)
        {
            for (var i = 0; i < config.Include.Length; i++)
            {
                var entry = config.Include[i];
                if (entry == null || !controllerEntryNames.Contains(entry))
                    continue;

                var endpointClass = endpointClasses.FirstOrDefault(x => GetEntryNames(x).Concat(x.ActionNames.SelectMany(action => GetEntryNames(x, action))).Contains(entry));
                if (endpointClass == null)
                    continue;

                var example = endpointClass.ActionNames
                    .SelectMany(action => GetEntryNames(endpointClass, action))
                    .FirstOrDefault(x => x.IndexOf(EntryActionSeparator) >= 0 && !controllerEntryNames.Contains(x));
                var advice = example == null
                    ? "mark its endpoints [McpExpose] to select them as well"
                    : $"to select an endpoint as well, name it by an entry no controller has, such as '{example}', or mark it [McpExpose]";

                diagnostics.Add(DiagnosticInfoModel.InFile(
                    DiagnosticDescriptors.IncludeEntryNamesControllerAndEndpoints,
                    config.FilePath,
                    i < config.IncludeSpans.Length ? config.IncludeSpans[i] : default,
                    entry,
                    endpointClass.Name,
                    advice));
            }
        }

        /// <summary>
        /// Tells a project in AllExceptExcluded mode whose generator.json does not say whether to select Minimal API
        /// endpoints how many of them the mode leaves out (MCP012). An info, as nothing is wrong: the project has to opt in.
        /// </summary>
        /// <param name="unanalyzedCount">The endpoints the generator did not analyze, as nothing could select them.</param>
        private static void ReportEndpointsLeftOut(
            List<DiagnosticInfoModel> diagnostics,
            ImmutableArray<ControllerInfoModel> controllers,
            GeneratorConfigModel config,
            HashSet<string> controllerEntryNames,
            int unanalyzedCount)
        {
            if (config.Mode != SelectionModeModel.AllExceptExcluded || config.MinimalApis is not null)
                return;

            var leftOutCount = controllers
                .SelectMany(controller => controller.Actions.Where(action =>
                    action.IsMinimalApiEndpoint &&
                    IsSelectable(controller, action) &&
                    !IsOptedIn(controller, action, config, controllerEntryNames) &&
                    !GetEntryNames(controller, action).Any(config.Exclude.Contains)))
                .Count() + unanalyzedCount;
            if (leftOutCount == 0)
                return;

            diagnostics.Add(DiagnosticInfoModel.InFile(
                DiagnosticDescriptors.EndpointsLeftOut,
                config.FilePath,
                default,
                leftOutCount,
                leftOutCount == 1 ? "endpoint" : "endpoints"));
        }

        /// <summary>
        /// The entries that select the controller as a whole: its name, with and without the <c>Controller</c> suffix.
        /// </summary>
        private static IEnumerable<string> GetEntryNames(ControllerInfoModel controller)
        {
            yield return controller.Name;

            var nameWithoutSuffix = RemoveControllerSuffix(controller.Name);
            if (nameWithoutSuffix != controller.Name)
                yield return nameWithoutSuffix;
        }

        /// <summary>
        /// The entries that select the action: its controller's, and either of them followed by the action's name.
        /// </summary>
        private static IEnumerable<string> GetEntryNames(ControllerInfoModel controller, ActionInfoModel action)
        {
            return GetEntryNames(controller, action.Name);
        }

        private static IEnumerable<string> GetEntryNames(ControllerInfoModel controller, string actionName)
        {
            var controllerNames = GetEntryNames(controller).ToList();

            return controllerNames.Concat(controllerNames.Select(x => $"{x}{EntryActionSeparator}{actionName}"));
        }

        /// <summary>
        /// Every entry that names a controller or an action of the project. An action that cannot become a tool,
        /// through <c>[McpIgnore]</c>, an <c>/mcp</c> route or HTTP methods no tool sends, is named all the same.
        /// </summary>
        private static HashSet<string> GetEntryNames(ImmutableArray<ControllerInfoModel> controllers)
        {
            return new HashSet<string>(
                controllers.SelectMany(controller => GetEntryNames(controller)
                    .Concat(controller.ActionNames.SelectMany(action => GetEntryNames(controller, action)))),
                StringComparer.Ordinal);
        }

        private static string RemoveControllerSuffix(string name)
        {
            return name.EndsWith(ConventionNames.ControllerSuffix, StringComparison.Ordinal)
                ? name.Substring(0, name.Length - ConventionNames.ControllerSuffix.Length)
                : name;
        }

        /// <summary>
        /// Whether the action can become a tool at all, by <c>[McpExpose]</c> or by the config.
        /// </summary>
        private static bool IsSelectable(ControllerInfoModel controller, ActionInfoModel action)
        {
            // Safety: Never expose MCP endpoints (loop prevention)
            if (McpEndpointRoute.Covers(action.RouteTemplate))
                return false;

            // Attribute overrides take highest precedence
            // [McpIgnore] always excludes
            return !action.HasMcpIgnoreAttribute && !controller.HasMcpIgnoreAttribute;
        }

        /// <summary>
        /// Reports MCP006 when no tool is generated, as the config selects no action or only actions MCP009 leaves without
        /// a tool, unless no action could become a tool: a project without controllers, or one that keeps every action from
        /// MCP by attribute or route, gets no tools by its own code rather than by a config mistake.
        /// </summary>
        /// <param name="selectedActions">
        /// The actions the config selects, each of which, when there are any, MCP009 leaves without a tool.
        /// </param>
        private static void ReportNoToolsGenerated(
            List<DiagnosticInfoModel> diagnostics,
            ImmutableArray<ControllerInfoModel> controllers,
            GeneratorConfigModel config,
            HashSet<string> entryNames,
            ImmutableArray<ActionInfoModel> selectedActions,
            HashSet<string> controllerEntryNames)
        {
            // The example is an action that becomes a tool if the project has one.
            var example = controllers
                .SelectMany(controller => controller.Actions
                    .Where(action => IsSelectable(controller, action) && IsOptedIn(controller, action, config, controllerEntryNames))
                    .Select(action => new
                    {
                        Controller = controller.Name,
                        Action = $"{controller.Name}.{action.Name}",
                        HasRoute = ToolClassEmitter.GetUnbuildableRouteReason(action) is null,
                        action.IsMinimalApiEndpoint
                    }))
                .OrderBy(x => x.HasRoute ? 0 : 1)
                .FirstOrDefault();

            if (example == null)
                return;

            var examples = example.IsMinimalApiEndpoint
                ? $"classes such as '{example.Controller}' or endpoints such as '{example.Action}'"
                : $"controllers such as '{example.Controller}' or actions such as '{example.Action}'";

            var selection = selectedActions.IsEmpty
                ? $"{config.Mode} mode selects none of the project's HTTP actions"
                : selectedActions.All(x => x.IsMinimalApiEndpoint)
                    ? $"{config.Mode} mode selects only Minimal API endpoints the generator cannot give a tool"
                    : $"{config.Mode} mode selects only HTTP actions whose route or request the generator cannot build";

            diagnostics.Add(DiagnosticInfoModel.InFile(
                DiagnosticDescriptors.NoToolsGenerated,
                config.FilePath,
                default,
                selection,
                GetNoToolsAdvice(config, controllers, entryNames, selectedActions, examples)));
        }

        /// <summary>
        /// What made the config select nothing and how to fix it, naming one of the project's own actions as the example.
        /// </summary>
        /// <param name="examples">The entries to suggest, such as "controllers such as 'X' or actions such as 'X.Y'".</param>
        private static string GetNoToolsAdvice(
            GeneratorConfigModel config,
            ImmutableArray<ControllerInfoModel> controllers,
            HashSet<string> entryNames,
            ImmutableArray<ActionInfoModel> selectedActions,
            string examples)
        {
            if (!selectedActions.IsEmpty && (config.FilePath == null || config.Mode != SelectionModeModel.SelectedOnly || config.Include.IsEmpty))
                return GetUnbuildableRoutesAdvice(config, examples, selectedActions);

            if (config.FilePath == null && config.ConfiguredFilePath is not null)
                return $"No config is at ApiToMcpConfigPath, so the default config applies, whose include list is empty: create {config.ConfiguredFilePath} and list {examples} in its include";

            if (config.FilePath == null)
                return $"No generator.json is among the project's AdditionalFiles, so the default config applies, whose include list is empty: create {DefaultConfigPath} and list {examples} in its include";

            if (config.Mode == SelectionModeModel.AllExceptExcluded)
                return "The exclude list in generator.json leaves no action to expose: remove entries from it, or mark actions with [McpExpose]";

            if (config.Include.IsEmpty)
                return $"The include list in generator.json is empty: add {examples} to it, or mark actions with [McpExpose]";

            // MCP008 says at each entry that names nothing what is wrong with it, and MCP009 at each action whose route
            // cannot be built, so this only points there.
            var entries = config.Include.Where(x => x != null).Distinct(StringComparer.Ordinal).ToList();
            var selectingUnbuildable = entries
                .Where(entry => controllers.Any(controller => controller.Actions.Any(action =>
                    selectedActions.Contains(action) && GetEntryNames(controller, action).Contains(entry))))
                .ToList();
            var keptFromMcp = entries.Where(x => entryNames.Contains(x) && !selectingUnbuildable.Contains(x)).ToList();
            var unmatched = entries.Where(x => !entryNames.Contains(x)).ToList();
            var reasons = new List<string>();

            if (keptFromMcp.Count > 0)
                reasons.Add($"{QuoteAll(keptFromMcp)} {(keptFromMcp.Count == 1 ? "names" : "name")} no [Http*] action without [McpIgnore] or a route at or under '{McpEndpointRoute.Route}'");

            if (unmatched.Count > 0)
                reasons.Add($"{QuoteAll(unmatched)} {(unmatched.Count == 1 ? "names" : "name")} no controller or action of the project, as {DiagnosticDescriptors.UnmatchedConfigEntry.Id} reports");

            if (selectingUnbuildable.Count > 0)
                reasons.Add(selectedActions.All(x => x.IsMinimalApiEndpoint)
                    ? $"{QuoteAll(selectingUnbuildable)} {(selectingUnbuildable.Count == 1 ? "selects" : "select")} only endpoints the generator cannot give a tool, as {DiagnosticDescriptors.UnbuildableEndpoint.Id} reports"
                    : $"{QuoteAll(selectingUnbuildable)} {(selectingUnbuildable.Count == 1 ? "selects" : "select")} only actions whose route or request the generator cannot build, as {GetUnbuildableIds(selectedActions)} {(selectedActions.All(x => !x.IsMinimalApiEndpoint) ? "reports" : "report")}");

            var why = reasons.Count == 0 ? "" : $": {string.Join(", and ", reasons)}";
            var selected = selectedActions.IsEmpty ? "an action" : "an action that becomes a tool";
            return $"No include entry in generator.json selects {selected}{why}. List {examples} in it, or mark actions with [McpExpose]";
        }

        /// <summary>
        /// The advice when the config selects actions, by its mode or by [McpExpose], but MCP009 or, for Minimal API
        /// endpoints, MCP010 leaves each without a tool: they say why at each, so this only points there and at what else
        /// to select.
        /// </summary>
        private static string GetUnbuildableRoutesAdvice(GeneratorConfigModel config, string examples, ImmutableArray<ActionInfoModel> selectedActions)
        {
            var ids = GetUnbuildableIds(selectedActions);
            var pointer = selectedActions.All(x => !x.IsMinimalApiEndpoint) || selectedActions.All(x => x.IsMinimalApiEndpoint)
                ? $"{ids} says at each of them why"
                : $"{ids} say at each of them why";

            if (config.FilePath == null)
                return $"{pointer}. Create {config.ConfiguredFilePath ?? DefaultConfigPath} and list {examples} in its include";

            if (selectedActions.All(x => x.IsMinimalApiEndpoint))
                return $"{pointer} and what to change, or select other endpoints or actions: in generator.json, or with [McpExpose]";

            if (config.Mode == SelectionModeModel.AllExceptExcluded)
                return $"{pointer}. Remove entries from the exclude list in generator.json, or mark actions with [McpExpose]";

            return $"{pointer}. Add {examples} to the include list in generator.json, or mark other actions with [McpExpose]";
        }

        /// <summary>
        /// The warnings that leave the actions without a tool: MCP009 for an action of a controller, MCP010 for a Minimal
        /// API endpoint.
        /// </summary>
        private static string GetUnbuildableIds(ImmutableArray<ActionInfoModel> actions)
        {
            var hasEndpoints = actions.Any(x => x.IsMinimalApiEndpoint);
            var hasControllerActions = actions.Any(x => !x.IsMinimalApiEndpoint);

            return hasEndpoints && hasControllerActions
                ? $"{DiagnosticDescriptors.UnbuildableRoute.Id} and {DiagnosticDescriptors.UnbuildableEndpoint.Id}"
                : hasEndpoints ? DiagnosticDescriptors.UnbuildableEndpoint.Id : DiagnosticDescriptors.UnbuildableRoute.Id;
        }

        /// <summary>
        /// Reports MCP008 at each include and exclude entry that names no controller or action of the project,
        /// whichever list the mode reads: the other takes effect once the mode changes.
        /// </summary>
        private static void ReportUnmatchedEntries(
            List<DiagnosticInfoModel> diagnostics,
            ImmutableArray<ControllerInfoModel> controllers,
            GeneratorConfigModel config,
            HashSet<string> entryNames)
        {
            ReportUnmatchedEntries(
                diagnostics, controllers, config, entryNames,
                IncludeListName, config.Include, config.IncludeSpans, "selects nothing");
            ReportUnmatchedEntries(
                diagnostics, controllers, config, entryNames,
                ExcludeListName, config.Exclude, config.ExcludeSpans, "excludes nothing");
        }

        private static void ReportUnmatchedEntries(
            List<DiagnosticInfoModel> diagnostics,
            ImmutableArray<ControllerInfoModel> controllers,
            GeneratorConfigModel config,
            HashSet<string> entryNames,
            string listName,
            EquatableArray<string> entries,
            EquatableArray<LinePositionSpan> spans,
            string effect)
        {
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry == null || entryNames.Contains(entry))
                    continue;

                diagnostics.Add(DiagnosticInfoModel.InFile(
                    DiagnosticDescriptors.UnmatchedConfigEntry,
                    config.FilePath,
                    i < spans.Length ? spans[i] : default,
                    listName,
                    entry,
                    effect,
                    GetUnmatchedEntryAdvice(entry, controllers, entryNames)));
            }
        }

        /// <summary>
        /// Why an entry names nothing, for its two usual causes: the name of a base controller, and a typo.
        /// </summary>
        private static string GetUnmatchedEntryAdvice(
            string entry,
            ImmutableArray<ControllerInfoModel> controllers,
            HashSet<string> entryNames)
        {
            var separator = entry.IndexOf(EntryActionSeparator);
            var controllerPart = separator < 0 ? entry : entry.Substring(0, separator);
            var actionPart = separator < 0 ? null : entry.Substring(separator + 1);

            if (!entryNames.Contains(controllerPart))
            {
                var baseControllerAdvice = GetBaseControllerAdvice(controllerPart, actionPart, controllers);
                if (baseControllerAdvice != null)
                    return baseControllerAdvice;
            }

            var nearestName = FindNearestName(entry, entryNames);
            if (nearestName != null)
                return $"entries are case-sensitive, and the nearest name in the project is '{nearestName}'";

            var exampleController = controllers.FirstOrDefault(x => !x.Actions.IsEmpty) ?? controllers.FirstOrDefault();
            if (exampleController == null)
                return "the project has no controller";

            var exampleAction = exampleController.Actions.IsEmpty
                ? ""
                : $", such as '{exampleController.Name}{EntryActionSeparator}{exampleController.Actions[0].Name}'";
            return $"entries are case-sensitive names of controllers, such as '{exampleController.Name}', or of their actions{exampleAction}";
        }

        /// <summary>
        /// The advice for an entry whose controller part names a base class of the project's controllers, or null when
        /// it names none. Since https://github.com/Kebechet/Api.ToMcp/pull/43 an inherited action is a tool of each
        /// controller that inherits it, named after that controller as MVC routes it, so the base's name matches
        /// nothing.
        /// </summary>
        private static string? GetBaseControllerAdvice(
            string controllerPart,
            string? actionPart,
            ImmutableArray<ControllerInfoModel> controllers)
        {
            (ControllerInfoModel Controller, int BaseClassIndex)? firstMatch = null;
            var derivedControllers = new List<ControllerInfoModel>();

            foreach (var controller in controllers)
            {
                var baseClassIndex = IndexOfBaseClass(controller, controllerPart);
                if (baseClassIndex < 0)
                    continue;

                firstMatch ??= (controller, baseClassIndex);
                derivedControllers.Add(controller);
            }

            if (firstMatch == null)
                return null;

            var (matchingController, matchingIndex) = firstMatch.Value;
            var baseClassName = matchingController.BaseClasses[matchingIndex].Name;

            // Written as the entry writes the base's name, with or without the suffix.
            var derivedNames = derivedControllers
                .Select(x => controllerPart == baseClassName ? x.Name : RemoveControllerSuffix(x.Name))
                .ToList();

            string replacement;
            if (actionPart != null)
            {
                replacement = $"list {QuoteAll(derivedNames.Select(x => $"{x}{EntryActionSeparator}{actionPart}").ToList())} instead";
            }
            else
            {
                // The methods of the base and of the classes it derives from, which the controllers inherit.
                var baseMethodNames = new HashSet<string>(
                    matchingController.BaseClasses.Skip(matchingIndex).SelectMany(x => x.MethodNames),
                    StringComparer.Ordinal);
                var exampleAction = derivedControllers[0].Actions.FirstOrDefault(x => baseMethodNames.Contains(x.Name));
                var example = exampleAction == null ? "" : $", such as '{derivedNames[0]}{EntryActionSeparator}{exampleAction.Name}'";

                replacement = $"list the controllers that inherit from it, {QuoteAll(derivedNames)}, or their actions{example}";
            }

            return $"{baseClassName} is a base class, and an inherited action is matched by the name of the controller that inherits it; {replacement}";
        }

        /// <summary>
        /// The index in the controller's base classes, nearest first, of the one the entry's controller part names, with or
        /// without the <c>Controller</c> suffix, or -1 when it names none.
        /// </summary>
        private static int IndexOfBaseClass(ControllerInfoModel controller, string controllerPart)
        {
            for (var i = 0; i < controller.BaseClasses.Length; i++)
            {
                var name = controller.BaseClasses[i].Name;
                if (name == controllerPart || RemoveControllerSuffix(name) == controllerPart)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// The name nearest to the entry, ignoring case, within a typo's distance, or null when none is that near. Of
        /// equally near names, the first in ordinal order, so that the message does not depend on the set's order.
        /// </summary>
        private static string? FindNearestName(string entry, IEnumerable<string> names)
        {
            // Two edits, or one in a short name, where longer distances suggest names that only look alike.
            var maxDistance = entry.Length <= 4 ? 1 : 2;
            var target = entry.ToUpperInvariant();
            string? nearestName = null;
            var nearestDistance = maxDistance + 1;

            foreach (var name in names)
            {
                if (Math.Abs(name.Length - entry.Length) > nearestDistance)
                    continue;

                var distance = GetEditDistance(target, name.ToUpperInvariant());
                if (distance < nearestDistance ||
                    (distance == nearestDistance && nearestName != null && string.CompareOrdinal(name, nearestName) < 0))
                {
                    nearestName = name;
                    nearestDistance = distance;
                }
            }

            return nearestName;
        }

        /// <summary>
        /// The number of single-character insertions, deletions, substitutions and swaps of neighbors that turn one
        /// string into the other (the optimal string alignment distance).
        /// </summary>
        private static int GetEditDistance(string source, string target)
        {
            var distances = new int[source.Length + 1, target.Length + 1];
            for (var i = 0; i <= source.Length; i++)
                distances[i, 0] = i;
            for (var j = 0; j <= target.Length; j++)
                distances[0, j] = j;

            for (var i = 1; i <= source.Length; i++)
            {
                for (var j = 1; j <= target.Length; j++)
                {
                    var substitution = source[i - 1] == target[j - 1] ? 0 : 1;
                    var distance = Math.Min(
                        Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
                        distances[i - 1, j - 1] + substitution);

                    if (i > 1 && j > 1 && source[i - 1] == target[j - 2] && source[i - 2] == target[j - 1])
                        distance = Math.Min(distance, distances[i - 2, j - 2] + 1);

                    distances[i, j] = distance;
                }
            }

            return distances[source.Length, target.Length];
        }

        /// <summary>
        /// The names quoted and listed as a sentence lists them: <c>'A'</c>, <c>'A' and 'B'</c>, <c>'A', 'B' and 'C'</c>.
        /// </summary>
        private static string QuoteAll(IReadOnlyList<string> names)
        {
            var quoted = names.Select(x => $"'{x}'").ToList();

            return quoted.Count == 1
                ? quoted[0]
                : $"{string.Join(", ", quoted.Take(quoted.Count - 1))} and {quoted[quoted.Count - 1]}";
        }

        /// <summary>
        /// How an additional file can be the config, in the order the generator prefers them.
        /// </summary>
        private enum ConfigFileMatch
        {
            ConfiguredPath,
            ConfiguredPathInAnotherCase,
            FileName,
            None
        }
    }
}
