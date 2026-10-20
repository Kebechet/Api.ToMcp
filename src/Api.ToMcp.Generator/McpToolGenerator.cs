using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        private const string McpRouteSegment = "/mcp";
        private const string IncludeListName = "include";
        private const string ExcludeListName = "exclude";

        /// <summary>
        /// Separates the controller from the action in an include or exclude entry such as <c>Products.GetById</c>.
        /// </summary>
        private const char EntryActionSeparator = '.';

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Step 1: Read generator.json configuration
            var configProvider = context.AdditionalTextsProvider
                .Where(file => file.Path.EndsWith(ConventionNames.ConfigFileName, StringComparison.OrdinalIgnoreCase))
                .Select((file, ct) => ConfigParser.Parse(file.GetText(ct)?.ToString()) with { FilePath = file.Path })
                .Collect()
                .Select((configs, _) => configs.FirstOrDefault() ?? new GeneratorConfigModel())
                .WithTrackingName(TrackingNames.Config);

            // Step 2: Find all controller classes
            var controllerProvider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (node, _) => node is ClassDeclarationSyntax,
                    transform: (ctx, ct) => ControllerScanner.TryGetControllerInfo(ctx, ct))
                .Where(info => info != null)
                .Select((info, _) => info!)
                .WithTrackingName(TrackingNames.Controllers);

            // Step 3: Combine config with controllers
            var combined = controllerProvider.Collect().Combine(configProvider).Combine(context.CompilationProvider);

            // Step 4: Generate source
            context.RegisterSourceOutput(combined, (ctx, source) =>
            {
                var ((controllers, config), compilation) = source;
                var selectedActions = SelectActions(controllers, config);
                var entryNames = GetEntryNames(controllers);

                // An invalid config gets MCP001 instead of MCP006: MCP006 would advise on the fallback, not on what the
                // file says.
                if (config.Error != null)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.InvalidConfig,
                        CreateConfigLocation(config, config.Error.Position),
                        config.Error.Fallback,
                        config.Error.Reason));
                }
                else if (selectedActions.IsEmpty)
                {
                    ReportNoToolsGenerated(ctx, controllers, config, entryNames);
                }

                // An unknown mode keeps the file's lists, so their entries are reported even next to MCP001.
                ReportUnmatchedEntries(ctx, controllers, config, compilation, entryNames);
                selectedActions = SkipActionsWithoutARoute(ctx, selectedActions, compilation);
                ReportDuplicateToolNames(ctx, selectedActions, config, compilation);

                // Track seen tool class names to handle overloads
                var seenToolClassNames = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach (var action in selectedActions)
                {
                    var baseClassName = action.ToolClassName;
                    string uniqueClassName;

                    if (seenToolClassNames.TryGetValue(baseClassName, out var count))
                    {
                        // Overload detected, add suffix
                        seenToolClassNames[baseClassName] = count + 1;
                        uniqueClassName = $"{baseClassName}_{count + 1}";
                    }
                    else
                    {
                        seenToolClassNames[baseClassName] = 1;
                        uniqueClassName = baseClassName;
                    }

                    var generatedCode = ToolClassEmitter.Emit(action, config, uniqueClassName);
                    ctx.AddSource($"{uniqueClassName}.g.cs", generatedCode);
                }

                ctx.AddSource("McpToolsRegistration.g.cs",
                    ToolClassEmitter.EmitRegistration(selectedActions, seenToolClassNames));
            });
        }

        /// <summary>
        /// Warns about every tool name that several generated tools share, at the actions that produce it: the MCP SDK
        /// keeps one of them and drops the others without an error. The tools are still generated, as renaming one would
        /// change a name a client may already call.
        /// </summary>
        private static void ReportDuplicateToolNames(
            SourceProductionContext ctx,
            ImmutableArray<ActionInfoModel> actions,
            GeneratorConfigModel config,
            Compilation compilation)
        {
            // The SDK looks tools up with the default string comparer, which is ordinal.
            var duplicates = actions
                .GroupBy(x => ToolClassEmitter.FormatToolName(x, config.Naming), StringComparer.Ordinal)
                .Where(x => x.Count() > 1);

            foreach (var duplicate in duplicates)
            {
                var locations = duplicate
                    .Select(x => x.SourceLocation?.ToLocation(compilation))
                    .OfType<Location>()
                    .Distinct()
                    .ToList();
                var producers = string.Join(", ", duplicate.Select(x => $"{x.ControllerName}.{x.Name} ({x.HttpMethod} {x.RouteTemplate})"));

                ctx.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.DuplicateToolName,
                    locations.FirstOrDefault() ?? Location.None,
                    locations.Skip(1),
                    duplicate.Key,
                    duplicate.Count(),
                    producers));
            }
        }

        /// <summary>
        /// Leaves out each action whose route a tool cannot build, and warns about it at the action (MCP009): its tool would
        /// not compile or would call an endpoint MVC does not serve it at.
        /// </summary>
        private static ImmutableArray<ActionInfoModel> SkipActionsWithoutARoute(
            SourceProductionContext ctx,
            ImmutableArray<ActionInfoModel> actions,
            Compilation compilation)
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

                ctx.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.UnbuildableRoute,
                    action.SourceLocation?.ToLocation(compilation) ?? Location.None,
                    action.ControllerName,
                    action.Name,
                    reason));
            }

            return routableActions.ToImmutable();
        }

        private static ImmutableArray<ActionInfoModel> SelectActions(
            ImmutableArray<ControllerInfoModel> controllers,
            GeneratorConfigModel config)
        {
            var result = ImmutableArray.CreateBuilder<ActionInfoModel>();

            foreach (var controller in controllers)
            {
                foreach (var action in controller.Actions)
                {
                    if (ShouldInclude(controller, action, config))
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
            GeneratorConfigModel config)
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
                    return names.Any(config.Include.Contains);

                case SelectionModeModel.AllExceptExcluded:
                    return !names.Any(config.Exclude.Contains);

                default:
                    return false;
            }
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
            var controllerNames = GetEntryNames(controller).ToList();

            return controllerNames.Concat(controllerNames.Select(x => $"{x}{EntryActionSeparator}{action.Name}"));
        }

        /// <summary>
        /// Every entry that names a controller or an action of the project. An action that cannot become a tool,
        /// through <c>[McpIgnore]</c> or an <c>/mcp</c> route, is named all the same.
        /// </summary>
        private static HashSet<string> GetEntryNames(ImmutableArray<ControllerInfoModel> controllers)
        {
            return new HashSet<string>(
                controllers.SelectMany(controller => GetEntryNames(controller)
                    .Concat(controller.Actions.SelectMany(action => GetEntryNames(controller, action)))),
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
            if (action.RouteTemplate.IndexOf(McpRouteSegment, StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            // Attribute overrides take highest precedence
            // [McpIgnore] always excludes
            return !action.HasMcpIgnoreAttribute && !controller.HasMcpIgnoreAttribute;
        }

        /// <summary>
        /// Reports MCP006, unless no action could become a tool: a project without controllers, or one that keeps every
        /// action from MCP by attribute or route, gets no tools by its own code rather than by a config mistake.
        /// </summary>
        private static void ReportNoToolsGenerated(
            SourceProductionContext context,
            ImmutableArray<ControllerInfoModel> controllers,
            GeneratorConfigModel config,
            HashSet<string> entryNames)
        {
            var example = controllers
                .SelectMany(controller => controller.Actions
                    .Where(action => IsSelectable(controller, action))
                    .Select(action => new { Controller = controller.Name, Action = $"{controller.Name}.{action.Name}" }))
                .FirstOrDefault();

            if (example == null)
                return;

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.NoToolsGenerated,
                CreateConfigLocation(config, default(LinePosition)),
                config.Mode.ToString(),
                GetNoToolsAdvice(config, entryNames, example.Controller, example.Action)));
        }

        private static Location CreateConfigLocation(GeneratorConfigModel config, LinePosition position)
        {
            return CreateConfigLocation(config, new LinePositionSpan(position, position));
        }

        private static Location CreateConfigLocation(GeneratorConfigModel config, LinePositionSpan span)
        {
            return config.FilePath == null
                ? Location.None
                : Location.Create(config.FilePath, default, span);
        }

        /// <summary>
        /// What made the config select nothing and how to fix it, naming one of the project's own actions as the example.
        /// </summary>
        private static string GetNoToolsAdvice(
            GeneratorConfigModel config,
            HashSet<string> entryNames,
            string exampleController,
            string exampleAction)
        {
            var examples = $"controllers such as '{exampleController}' or actions such as '{exampleAction}'";

            if (config.FilePath == null)
                return $"No generator.json is among the project's AdditionalFiles, so the default config applies, whose include list is empty: create Mcp/generator.json and list {examples} in its include";

            if (config.Mode == SelectionModeModel.AllExceptExcluded)
                return "The exclude list in generator.json leaves no action to expose: remove entries from it, or mark actions with [McpExpose]";

            if (config.Include.IsEmpty)
                return $"The include list in generator.json is empty: add {examples} to it, or mark actions with [McpExpose]";

            // MCP008 says at each entry that names nothing what is wrong with it, so this only points there.
            var entries = config.Include.Where(x => x != null).Distinct(StringComparer.Ordinal).ToList();
            var keptFromMcp = entries.Where(entryNames.Contains).ToList();
            var unmatched = entries.Where(x => !entryNames.Contains(x)).ToList();
            var reasons = new List<string>();

            if (keptFromMcp.Count > 0)
                reasons.Add($"{QuoteAll(keptFromMcp)} {(keptFromMcp.Count == 1 ? "names" : "name")} no [Http*] action without [McpIgnore] or '{McpRouteSegment}' in its route");

            if (unmatched.Count > 0)
                reasons.Add($"{QuoteAll(unmatched)} {(unmatched.Count == 1 ? "names" : "name")} no controller or action of the project, as {DiagnosticDescriptors.UnmatchedConfigEntry.Id} reports");

            var why = reasons.Count == 0 ? "" : $": {string.Join(", and ", reasons)}";
            return $"No include entry in generator.json selects an action{why}. List {examples} in it, or mark actions with [McpExpose]";
        }

        /// <summary>
        /// Reports MCP008 at each include and exclude entry that names no controller or action of the project,
        /// whichever list the mode reads: the other takes effect once the mode changes.
        /// </summary>
        private static void ReportUnmatchedEntries(
            SourceProductionContext context,
            ImmutableArray<ControllerInfoModel> controllers,
            GeneratorConfigModel config,
            Compilation compilation,
            HashSet<string> entryNames)
        {
            ReportUnmatchedEntries(
                context, controllers, config, compilation, entryNames,
                IncludeListName, config.Include, config.IncludeSpans, "selects nothing");
            ReportUnmatchedEntries(
                context, controllers, config, compilation, entryNames,
                ExcludeListName, config.Exclude, config.ExcludeSpans, "excludes nothing");
        }

        private static void ReportUnmatchedEntries(
            SourceProductionContext context,
            ImmutableArray<ControllerInfoModel> controllers,
            GeneratorConfigModel config,
            Compilation compilation,
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

                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.UnmatchedConfigEntry,
                    CreateConfigLocation(config, i < spans.Length ? spans[i] : default),
                    listName,
                    entry,
                    effect,
                    GetUnmatchedEntryAdvice(entry, controllers, compilation, entryNames)));
            }
        }

        /// <summary>
        /// Why an entry names nothing, for its two usual causes: the name of a base controller, and a typo.
        /// </summary>
        private static string GetUnmatchedEntryAdvice(
            string entry,
            ImmutableArray<ControllerInfoModel> controllers,
            Compilation compilation,
            HashSet<string> entryNames)
        {
            var separator = entry.IndexOf(EntryActionSeparator);
            var controllerPart = separator < 0 ? entry : entry.Substring(0, separator);
            var actionPart = separator < 0 ? null : entry.Substring(separator + 1);

            if (!entryNames.Contains(controllerPart))
            {
                var baseControllerAdvice = GetBaseControllerAdvice(controllerPart, actionPart, controllers, compilation);
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
            ImmutableArray<ControllerInfoModel> controllers,
            Compilation compilation)
        {
            INamedTypeSymbol? baseClass = null;
            var derivedControllers = new List<ControllerInfoModel>();

            foreach (var controller in controllers)
            {
                var matchingBase = GetBaseClasses(controller, compilation)
                    .FirstOrDefault(x => x.Name == controllerPart || RemoveControllerSuffix(x.Name) == controllerPart);
                if (matchingBase == null)
                    continue;

                baseClass ??= matchingBase;
                derivedControllers.Add(controller);
            }

            if (baseClass == null)
                return null;

            // Written as the entry writes the base's name, with or without the suffix.
            var derivedNames = derivedControllers
                .Select(x => controllerPart == baseClass.Name ? x.Name : RemoveControllerSuffix(x.Name))
                .ToList();

            string replacement;
            if (actionPart != null)
            {
                replacement = $"list {QuoteAll(derivedNames.Select(x => $"{x}{EntryActionSeparator}{actionPart}").ToList())} instead";
            }
            else
            {
                var baseMethodNames = new HashSet<string>(
                    GetSelfAndBaseClasses(baseClass).SelectMany(x => x.GetMembers().OfType<IMethodSymbol>()).Select(x => x.Name),
                    StringComparer.Ordinal);
                var exampleAction = derivedControllers[0].Actions.FirstOrDefault(x => baseMethodNames.Contains(x.Name));
                var example = exampleAction == null ? "" : $", such as '{derivedNames[0]}{EntryActionSeparator}{exampleAction.Name}'";

                replacement = $"list the controllers that inherit from it, {QuoteAll(derivedNames)}, or their actions{example}";
            }

            return $"{baseClass.Name} is a base class, and an inherited action is matched by the name of the controller that inherits it; {replacement}";
        }

        /// <summary>
        /// The classes a controller inherits actions from, nearest first, up to the framework's <c>ControllerBase</c>.
        /// </summary>
        private static IEnumerable<INamedTypeSymbol> GetBaseClasses(ControllerInfoModel controller, Compilation compilation)
        {
            // The pipeline's models hold no symbols, so the controller is found again by name: a public, top-level,
            // non-generic class of the project, as the scanner requires.
            var type = compilation
                .GetSymbolsWithName(controller.Name, SymbolFilter.Type)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault(x => x.ContainingType == null &&
                    !x.IsGenericType &&
                    (x.ContainingNamespace?.ToDisplayString() ?? "") == controller.Namespace);

            return type?.BaseType == null ? Enumerable.Empty<INamedTypeSymbol>() : GetSelfAndBaseClasses(type.BaseType);
        }

        private static IEnumerable<INamedTypeSymbol> GetSelfAndBaseClasses(INamedTypeSymbol type)
        {
            for (var current = type; current != null && !ActionMethodAnalyzer.IsFrameworkBaseType(current); current = current.BaseType)
                yield return current;
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
    }
}
