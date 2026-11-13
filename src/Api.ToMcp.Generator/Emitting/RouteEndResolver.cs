using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Api.ToMcp.Generator.Models;

namespace Api.ToMcp.Generator.Emitting
{
    /// <summary>
    /// Decides whether a tool's call may leave out the route value that ends its action's template
    /// (<see cref="RouteEndModel"/>). Without it the URL ends a segment earlier, and the app serves that URL with whichever
    /// route matching it comes first: a literal segment comes before a route value, in the first segment where the two
    /// differ. The templates of every action the generator sees are compared, of controllers and Minimal API endpoints,
    /// those kept out of MCP included, by their structure alone: literal segments ignoring case, route values by position,
    /// whatever their names, constraints and defaults. What the structure cannot settle counts as reaching another action.
    /// Not compared: an action the app serves for no HTTP method a tool sends, as <c>[Route]</c> without a verb serves
    /// every method, a Minimal API's <c>Map</c> or conventional routing; a route's <c>Order</c>; and a route the generator
    /// cannot read, which gets MCP009 or MCP010.
    /// </summary>
    internal static class RouteEndResolver
    {
        private const char PathSeparator = RouteTokens.PathSeparator;
        private const string ComplexSegmentSeparator = ".";

        private enum SegmentKind
        {
            Literal,
            Parameter,

            /// <summary>
            /// Literal text and route values in one segment, such as <c>{name}.{ext?}</c>.
            /// </summary>
            Complex
        }

        /// <summary>
        /// The tool actions, each with its <see cref="ActionInfoModel.RouteEnd"/> decided against the routes of all the
        /// actions of the controllers, the tool actions among them.
        /// </summary>
        public static ImmutableArray<ActionInfoModel> Resolve(ImmutableArray<ActionInfoModel> toolActions, IEnumerable<ControllerInfoModel> controllers)
        {
            var actions = controllers.SelectMany(x => x.Actions).Where(x => x.RouteProblem is null).ToList();
            var segmentsByTemplate = new Dictionary<string, List<Segment>>(StringComparer.Ordinal);

            return toolActions
                .Select(action =>
                {
                    var routeEnd = action.RouteProblem is null ? Resolve(action, actions, segmentsByTemplate) : RouteEndModel.AsTemplate;
                    return routeEnd == action.RouteEnd ? action : action with { RouteEnd = routeEnd };
                })
                .ToImmutableArray();
        }

        private static RouteEndModel Resolve(ActionInfoModel action, List<ActionInfoModel> actions, Dictionary<string, List<Segment>> segmentsByTemplate)
        {
            var template = RouteTemplate.Parse(action.RouteTemplate);
            var end = template.Parts.Count == 0 ? null : template.Parts[template.Parts.Count - 1];
            if (end is null || !end.IsParameter)
            {
                return RouteEndModel.AsTemplate;
            }

            var segments = GetSegments(action.RouteTemplate, segmentsByTemplate);
            var leftOut = WithoutEnd(segments);

            // Only a whole last segment is left out of the URL so: the tool sends a trailing slash in its place, which the
            // app serves as the URL without it.
            var isLeftOutByOwnRoute = !end.IsOptional &&
                segments[segments.Count - 1].Kind == SegmentKind.Parameter &&
                GetServedRoutes(action, action.HttpMethod)
                    .Where(x => x != action.RouteTemplate)
                    .Any(x => AreSameRoute(GetSegments(x, segmentsByTemplate), leftOut));
            if (!end.IsOptional && !isLeftOutByOwnRoute)
            {
                return RouteEndModel.AsTemplate;
            }

            var reachesAnotherAction = actions.Any(other =>
                GetServedRoutes(other, action.HttpMethod).Any(x => MayServe(GetSegments(x, segmentsByTemplate), leftOut)) &&
                !other.Equals(action));
            if (reachesAnotherAction)
            {
                return RouteEndModel.Required;
            }

            return isLeftOutByOwnRoute ? RouteEndModel.Optional : RouteEndModel.AsTemplate;
        }

        /// <summary>
        /// The templates of the routes the app serves the action at for the HTTP method: a controller's that MVC serves it
        /// for, or every method; a Minimal API endpoint's one route, if mapped for the method.
        /// </summary>
        private static IEnumerable<string> GetServedRoutes(ActionInfoModel action, string httpMethod)
        {
            if (action.ServedRoutes.IsEmpty)
            {
                return action.HttpMethod == httpMethod || action.OtherHttpMethods.Contains(httpMethod)
                    ? new[] { action.RouteTemplate }
                    : Enumerable.Empty<string>();
            }

            return action.ServedRoutes
                .Where(x => x.HttpMethods.IsEmpty || x.HttpMethods.Contains(httpMethod))
                .Select(x => x.Template);
        }

        /// <summary>
        /// Whether a route of the same action is the template without its last segment, its route values named alike:
        /// the URL without the value then binds the action's other route values as before.
        /// </summary>
        private static bool AreSameRoute(List<Segment> route, List<Segment> leftOut)
        {
            if (route.Count != leftOut.Count)
            {
                return false;
            }

            for (var i = 0; i < route.Count; i++)
            {
                var segment = route[i];
                var other = leftOut[i];
                var isSame = segment.Kind == other.Kind && segment.Kind switch
                {
                    SegmentKind.Literal => string.Equals(segment.Single.Text, other.Single.Text, StringComparison.OrdinalIgnoreCase),
                    SegmentKind.Parameter => string.Equals(segment.Single.Text, other.Single.Text, StringComparison.OrdinalIgnoreCase) &&
                        segment.Single.IsCatchAll == other.Single.IsCatchAll,
                    _ => false
                };
                if (!isSame)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Whether the route may serve the URL of the left-out template, ahead of the template itself; false only when its
        /// structure proves it does not. The two match up to the first segment where their kinds differ, and there the
        /// literal comes first: of the route, as it matches a value equal to it; of the template, which is the route's
        /// only when every segment before is literal in both, so that nothing else tells their order.
        /// </summary>
        private static bool MayServe(List<Segment> route, List<Segment> leftOut)
        {
            var isAllLiteralSoFar = true;
            for (var i = 0; ; i++)
            {
                if (i == leftOut.Count)
                {
                    return route.Skip(i).All(x => x.CanBeLeftOut);
                }

                if (i == route.Count)
                {
                    return false;
                }

                var segment = route[i];
                var other = leftOut[i];
                if (segment.IsCatchAll)
                {
                    // A catch-all matches the rest of the URL, after anything else of the same segment.
                    return !isAllLiteralSoFar;
                }

                if (segment.Kind == SegmentKind.Literal && other.Kind == SegmentKind.Literal)
                {
                    if (!string.Equals(segment.Single.Text, other.Single.Text, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    continue;
                }

                if (segment.Kind == SegmentKind.Literal)
                {
                    return true;
                }

                if (other.Kind == SegmentKind.Literal)
                {
                    return !isAllLiteralSoFar;
                }

                isAllLiteralSoFar = false;
            }
        }

        /// <summary>
        /// The template's segments without its last route value: without the last segment, or of <c>{name}.{ext?}</c>
        /// without <c>.{ext?}</c>, the only route value MVC lets a segment of several end in optionally.
        /// </summary>
        private static List<Segment> WithoutEnd(List<Segment> segments)
        {
            var leftOut = segments.Take(segments.Count - 1).ToList();
            var last = segments[segments.Count - 1];
            if (last.Kind == SegmentKind.Parameter)
            {
                return leftOut;
            }

            var parts = last.Parts.Take(last.Parts.Count - 1).ToList();
            var separator = parts.Count == 0 ? null : parts[parts.Count - 1];
            if (separator is not null && !separator.IsParameter && separator.Text.EndsWith(ComplexSegmentSeparator, StringComparison.Ordinal))
            {
                parts.RemoveAt(parts.Count - 1);
                var text = separator.Text.Substring(0, separator.Text.Length - ComplexSegmentSeparator.Length);
                if (text.Length > 0)
                {
                    parts.Add(RouteTemplatePart.Literal(text));
                }
            }

            if (parts.Count > 0)
            {
                leftOut.Add(new Segment(parts));
            }

            return leftOut;
        }

        private static List<Segment> GetSegments(string template, Dictionary<string, List<Segment>> segmentsByTemplate)
        {
            if (!segmentsByTemplate.TryGetValue(template, out var segments))
            {
                segments = ToSegments(RouteTemplate.Parse(template));
                segmentsByTemplate[template] = segments;
            }

            return segments;
        }

        /// <summary>
        /// The template's path segments, each of its parts, without the empty ones a leading or trailing slash gives.
        /// </summary>
        private static List<Segment> ToSegments(RouteTemplate template)
        {
            var segments = new List<Segment>();
            var parts = new List<RouteTemplatePart>();
            foreach (var part in template.Parts)
            {
                if (part.IsParameter)
                {
                    parts.Add(part);
                    continue;
                }

                var texts = part.Text.Split(PathSeparator);
                for (var i = 0; i < texts.Length; i++)
                {
                    if (i > 0 && parts.Count > 0)
                    {
                        segments.Add(new Segment(parts));
                        parts = new List<RouteTemplatePart>();
                    }

                    if (texts[i].Length > 0)
                    {
                        parts.Add(RouteTemplatePart.Literal(texts[i]));
                    }
                }
            }

            if (parts.Count > 0)
            {
                segments.Add(new Segment(parts));
            }

            return segments;
        }

        private sealed class Segment
        {
            public Segment(List<RouteTemplatePart> parts)
            {
                Parts = parts;
                Kind = parts.Count > 1 ? SegmentKind.Complex : parts[0].IsParameter ? SegmentKind.Parameter : SegmentKind.Literal;
            }

            public List<RouteTemplatePart> Parts { get; }
            public SegmentKind Kind { get; }
            public RouteTemplatePart Single => Parts[0];

            /// <summary>
            /// Whether a URL may end before the segment, which the app serves without it: an optional route value, one
            /// with a default, or a catch-all.
            /// </summary>
            public bool CanBeLeftOut => Kind == SegmentKind.Parameter && Single.IsOptional;

            public bool IsCatchAll => Kind == SegmentKind.Parameter && Single.IsCatchAll;
        }
    }
}
