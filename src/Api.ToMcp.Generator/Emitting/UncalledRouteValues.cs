using System;
using System.Collections.Generic;
using System.Linq;
using Api.ToMcp.Generator.Models;

namespace Api.ToMcp.Generator.Emitting
{
    /// <summary>
    /// The values of an action that MVC binds from a route its tool does not call, and from no other place the tool
    /// sends: the tool calls <see cref="ActionInfoModel.RouteTemplate"/> alone. See
    /// https://github.com/Kebechet/Api.ToMcp/issues/61.
    /// </summary>
    internal static class UncalledRouteValues
    {
        /// <summary>
        /// Each route MVC serves the action at for the tool's HTTP method that the tool does not call, with the values of
        /// the action it names that MVC binds from the route alone and that the route the tool calls does not name. A route
        /// with none is left out: a route value no parameter takes, such as <c>{culture}</c>, is middleware's, and one MVC
        /// binds from the query string too, as in a controller without <c>[ApiController]</c>, the tool sends there.
        /// </summary>
        public static IEnumerable<(string Template, List<string> LostValues)> GetLostValues(ActionInfoModel action)
        {
            var calledRoute = RouteTemplate.Parse(action.RouteTemplate);
            var templates = new HashSet<string>(StringComparer.Ordinal) { action.RouteTemplate };
            var parameters = action.Parameters.Where(x => x.Source != ParameterSourceModel.Services).ToList();

            foreach (var route in action.ServedRoutes.Concat(action.OtherControllerRoutes))
            {
                var isServedForToolMethod = route.HttpMethods.IsEmpty || route.HttpMethods.Contains(action.HttpMethod);
                if (!isServedForToolMethod || !templates.Add(route.Template))
                {
                    continue;
                }

                var lostValues = RouteTemplate.Parse(route.Template).ParameterNames
                    .Where(x => !calledRoute.HasParameter(x))
                    .Select(x => GetRouteOnlyValue(action, parameters, x))
                    .OfType<string>()
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
        /// How a message names the value of the action that MVC binds from the route value alone, or null for none: a
        /// simple parameter with <c>[FromRoute]</c>, or without a binding attribute in an <c>[ApiController]</c>, which
        /// infers <c>[FromRoute]</c> for a parameter any route of the action names; else a property of a
        /// <c>[FromRoute]</c> parameter that names no other source.
        /// </summary>
        private static string? GetRouteOnlyValue(ActionInfoModel action, List<ParameterInfoModel> parameters, string routeValue)
        {
            var parameter = parameters.FirstOrDefault(x => string.Equals(x.ModelName ?? x.Name, routeValue, StringComparison.OrdinalIgnoreCase));
            if (parameter is not null)
            {
                var isReadFromRouteAlone = parameter.IsSimpleType &&
                    (parameter.Source == ParameterSourceModel.Route || (parameter.Source == ParameterSourceModel.Auto && action.IsApiController));
                return isReadFromRouteAlone ? $"'{parameter.Name}'" : null;
            }

            foreach (var routeParameter in parameters.Where(x => x.Source == ParameterSourceModel.Route && !x.IsSimpleType))
            {
                var property = routeParameter.Properties.FirstOrDefault(x =>
                    x.Source is ParameterSourceModel.Auto or ParameterSourceModel.Route &&
                    string.Equals(x.ModelName ?? x.Name, routeValue, StringComparison.OrdinalIgnoreCase));
                if (property is not null)
                {
                    return $"the property {property.Name} of '{routeParameter.Name}'";
                }
            }

            return null;
        }
    }
}
