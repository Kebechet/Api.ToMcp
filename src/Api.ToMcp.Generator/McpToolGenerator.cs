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

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Step 1: Read generator.json configuration
            var configProvider = context.AdditionalTextsProvider
                .Where(file => file.Path.EndsWith(ConventionNames.ConfigFileName, StringComparison.OrdinalIgnoreCase))
                .Select((file, ct) =>
                {
                    var config = ConfigParser.Parse(file.GetText(ct)?.ToString());
                    config.FilePath = file.Path;
                    return config;
                })
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
            var combined = controllerProvider.Collect().Combine(configProvider);

            // Step 4: Generate source
            context.RegisterSourceOutput(combined, (ctx, source) =>
            {
                var (controllers, config) = source;
                var selectedActions = SelectActions(controllers, config);

                // An invalid config gets MCP001 alone: MCP006 would advise on the fallback, not on what the file says.
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
                    ReportNoToolsGenerated(ctx, controllers, config);
                }

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
            var controllerBaseName = controller.Name;
            if (controllerBaseName.EndsWith(ConventionNames.ControllerSuffix))
                controllerBaseName = controllerBaseName.Substring(0, controllerBaseName.Length - ConventionNames.ControllerSuffix.Length);

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
            GeneratorConfigModel config)
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
                CreateConfigLocation(config, default),
                config.Mode.ToString(),
                GetNoToolsAdvice(config, example.Controller, example.Action)));
        }

        private static Location CreateConfigLocation(GeneratorConfigModel config, LinePosition position)
        {
            return config.FilePath == null
                ? Location.None
                : Location.Create(config.FilePath, default, new LinePositionSpan(position, position));
        }

        /// <summary>
        /// What made the config select nothing and how to fix it, naming one of the project's own actions as the example.
        /// </summary>
        private static string GetNoToolsAdvice(GeneratorConfigModel config, string exampleController, string exampleAction)
        {
            var examples = $"controllers such as '{exampleController}' or actions such as '{exampleAction}'";

            if (config.FilePath == null)
                return $"No generator.json is among the project's AdditionalFiles, so the default config applies, whose include list is empty: create Mcp/generator.json and list {examples} in its include";

            if (config.Mode == SelectionModeModel.AllExceptExcluded)
                return "The exclude list in generator.json leaves no action to expose: remove entries from it, or mark actions with [McpExpose]";

            if (config.Include.IsEmpty)
                return $"The include list in generator.json is empty: add {examples} to it, or mark actions with [McpExpose]";

            var entries = string.Join(", ", config.Include.Select(entry => $"'{entry}'"));
            return $"None of the include entries in generator.json ({entries}) selects an action: entries are case-sensitive controller names such as '{exampleController}' or action names such as '{exampleAction}'";
        }
    }
}
