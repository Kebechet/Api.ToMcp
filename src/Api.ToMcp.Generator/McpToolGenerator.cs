using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Api.ToMcp.Generator.Diagnostics;
using Api.ToMcp.Generator.Emitting;
using Api.ToMcp.Generator.Models;
using Api.ToMcp.Generator.Parsing;

namespace Api.ToMcp.Generator
{
    [Generator(LanguageNames.CSharp)]
    public sealed class McpToolGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Step 1: Read generator.json configuration
            var configProvider = context.AdditionalTextsProvider
                .Where(file => file.Path.EndsWith("generator.json", StringComparison.OrdinalIgnoreCase))
                .Select((file, ct) => ConfigParser.Parse(file.GetText(ct)?.ToString()))
                .Collect()
                .Select((configs, _) => configs.FirstOrDefault() ?? new GeneratorConfigModel());

            // Step 2: Find all controller classes
            var controllerProvider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (node, _) => node is ClassDeclarationSyntax,
                    transform: (ctx, ct) => ControllerScanner.TryGetControllerInfo(ctx, ct))
                .Where(info => info != null)
                .Select((info, _) => info!);

            // Step 3: Combine config with controllers
            var combined = controllerProvider.Collect().Combine(configProvider).Combine(context.CompilationProvider);

            // Step 4: Generate source
            context.RegisterSourceOutput(combined, (ctx, source) =>
            {
                var ((controllers, config), compilation) = source;
                var selectedActions = SelectActions(controllers, config);

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
            // Safety: Never expose MCP endpoints (loop prevention)
            if (action.RouteTemplate.IndexOf("/mcp", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            // Attribute overrides take highest precedence
            // [McpIgnore] always excludes
            if (action.HasMcpIgnoreAttribute || controller.HasMcpIgnoreAttribute)
                return false;

            // [McpExpose] always includes
            if (action.HasMcpExposeAttribute || controller.HasMcpExposeAttribute)
                return true;

            // Apply config-based selection
            var controllerBaseName = controller.Name;
            if (controllerBaseName.EndsWith("Controller"))
                controllerBaseName = controllerBaseName.Substring(0, controllerBaseName.Length - "Controller".Length);

            var fullName = $"{controller.Name}.{action.Name}";
            var fullNameWithoutSuffix = $"{controllerBaseName}.{action.Name}";

            switch (config.Mode)
            {
                case SelectionModeModel.SelectedOnly:
                    return config.Include.Contains(controller.Name) ||
                           config.Include.Contains(controllerBaseName) ||
                           config.Include.Contains(fullName) ||
                           config.Include.Contains(fullNameWithoutSuffix);

                case SelectionModeModel.AllExceptExcluded:
                    return !config.Exclude.Contains(controller.Name) &&
                           !config.Exclude.Contains(controllerBaseName) &&
                           !config.Exclude.Contains(fullName) &&
                           !config.Exclude.Contains(fullNameWithoutSuffix);

                default:
                    return false;
            }
        }
    }
}
