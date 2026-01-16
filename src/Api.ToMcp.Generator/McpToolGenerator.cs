using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
            var combined = controllerProvider.Collect().Combine(configProvider);

            // Step 4: Generate source
            context.RegisterSourceOutput(combined, (ctx, source) =>
            {
                var (controllers, config) = source;
                var selectedActions = SelectActions(controllers, config);

                foreach (var action in selectedActions)
                {
                    var generatedCode = ToolClassEmitter.Emit(action, config);
                    ctx.AddSource($"{action.ToolClassName}.g.cs", generatedCode);
                }

                ctx.AddSource("McpToolsRegistration.g.cs",
                    ToolClassEmitter.EmitRegistration(selectedActions));
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
