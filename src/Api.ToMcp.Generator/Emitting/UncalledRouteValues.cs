using System;
using System.Collections.Generic;
using System.Linq;
using Api.ToMcp.Generator.Models;
using Api.ToMcp.Generator.Parsing;

namespace Api.ToMcp.Generator.Emitting
{
    /// <summary>
    /// The values of an action that MVC binds from the route alone, at a route its tool does not call and the route it
    /// calls lacks: the tool calls <see cref="ActionInfoModel.RouteTemplate"/> alone. See
    /// https://github.com/Kebechet/Api.ToMcp/issues/61.
    /// </summary>
    internal static class UncalledRouteValues
    {
        /// <summary>
        /// Each route MVC serves the action at for the tool's HTTP method that the tool does not call, with the values of
        /// the action MVC binds from a route value of it alone that the route the tool calls does not have. A route value
        /// no value of the action binds from the route alone is left out: middleware's, such as <c>{culture}</c>, or one
        /// MVC reads from the query string as well, where the tool sends it. So is a route at <c>/mcp</c> or under it,
        /// which no tool calls, whatever action it has.
        /// </summary>
        public static IEnumerable<(string Template, List<string> LostValues)> GetLostValues(ActionInfoModel action)
        {
            var calledRoute = RouteTemplate.Parse(action.RouteTemplate);
            var routes = action.ServedRoutes.Concat(action.OtherControllerRoutes).ToList();
            var routeValueReaders = GetRouteValueReaders(action, routes.Select(x => RouteTemplate.Parse(x.Template)).Append(calledRoute).ToList());
            var templates = new HashSet<string>(StringComparer.Ordinal) { action.RouteTemplate };

            foreach (var route in routes)
            {
                var isServedForToolMethod = route.HttpMethods.IsEmpty || route.HttpMethods.Contains(action.HttpMethod);
                if (!isServedForToolMethod || McpEndpointRoute.Covers(route.Template) || !templates.Add(route.Template))
                {
                    continue;
                }

                var lostValues = RouteTemplate.Parse(route.Template).ParameterNames
                    .Where(x => !calledRoute.HasParameter(x))
                    .SelectMany(x => routeValueReaders.Where(reader => string.Equals(reader.Key, x, StringComparison.OrdinalIgnoreCase)))
                    .Select(x => x.Value)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (lostValues.Count > 0)
                {
                    yield return (route.Template, lostValues);
                }
            }
        }

        public static string JoinWithAnd(IReadOnlyList<string> items)
        {
            return items.Count == 1 ? items[0] : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[items.Count - 1]}";
        }

        /// <summary>
        /// The values of the action MVC binds from route values alone, each with the key it reads them under and how a
        /// message names it:
        /// <list type="bullet">
        /// <item>a simple value or a collection with <c>[FromRoute]</c>, under the name it binds under;</item>
        /// <item>a simple value without a binding attribute in an <c>[ApiController]</c>, which infers <c>[FromRoute]</c>
        /// for it when a route of the action names the parameter by its own name, whatever name it binds under
        /// (<c>InferParameterBindingInfoConvention</c>);</item>
        /// <item>a property with <c>[FromRoute]</c> of a parameter MVC binds property by property, and a property without
        /// a binding attribute of a <c>[FromRoute]</c> one, under the key MVC builds for it: the property's own name, or
        /// after the parameter's own name, <c>key.Version</c>, when a binding attribute gives the parameter one. A
        /// property MVC's complex binder never binds is none (<see cref="PropertyInfoModel.IsBindable"/>).</item>
        /// </list>
        /// </summary>
        private static List<(string Key, string Value)> GetRouteValueReaders(ActionInfoModel action, List<RouteTemplate> routes)
        {
            var readers = new List<(string Key, string Value)>();
            foreach (var parameter in action.Parameters)
            {
                var isOneValue = parameter.IsSimpleType || parameter.IsCollection;
                if (isOneValue)
                {
                    var isInferredFromRoute = parameter.Source == ParameterSourceModel.Auto &&
                        action.IsApiController &&
                        parameter.IsSimpleType &&
                        routes.Any(x => x.HasParameter(parameter.Name));
                    if (parameter.Source == ParameterSourceModel.Route || isInferredFromRoute)
                    {
                        readers.Add((ToolClassEmitter.GetBinderModelName(parameter), $"'{parameter.Name}'"));
                    }

                    continue;
                }

                if (parameter.Source is not (ParameterSourceModel.Auto or ParameterSourceModel.Query or ParameterSourceModel.Form or ParameterSourceModel.Route))
                {
                    continue;
                }

                readers.AddRange(parameter.Properties
                    .Where(x => x.IsBindable)
                    .Where(x => x.Source == ParameterSourceModel.Route || (x.Source == ParameterSourceModel.Auto && parameter.Source == ParameterSourceModel.Route))
                    .Select(x => (ToolClassEmitter.GetPropertyModelName(parameter, x), $"the property {x.Name} of '{parameter.Name}'")));
            }

            return readers;
        }
    }
}
