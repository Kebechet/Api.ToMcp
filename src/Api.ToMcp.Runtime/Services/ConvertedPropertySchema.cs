using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The schema of a property with a <c>[JsonConverter]</c> of its own. The schema exporter describes such a property by
/// its type when the type is an object or a collection, but the serializer writes and reads it as the converter does,
/// such as the string <c>"12.50 EUR"</c> for a <c>Money</c> object, so a client that validates against that schema
/// rejects the value, and one that follows it sends a value the converter cannot read. Such a property accepts any
/// value instead, as a type with a converter of its own already does. A property of a type without members, such as an
/// enum or a <c>DateTime</c>, keeps its schema: there the exporter reads the converter, and knows those System.Text.Json
/// ships, such as <c>JsonStringEnumConverter</c>.
/// </summary>
internal static class ConvertedPropertySchema
{
    private const string PointerRoot = "#";
    private const char PointerSeparator = '/';
    private const string PropertiesKeyword = "properties";

    /// <summary>
    /// <paramref name="options"/>, or the defaults, with this schema applied before their own
    /// <see cref="AIJsonSchemaCreateOptions.TransformSchemaNode"/>, so that one can still describe the property. Each
    /// call gives options of their own, used for one schema at a time: they remember the properties they replaced until
    /// the schema's root.
    /// </summary>
    public static AIJsonSchemaCreateOptions Apply(AIJsonSchemaCreateOptions? options)
    {
        var transformSchemaNode = options?.TransformSchemaNode;
        var convertedNodes = new List<ConvertedNode>();

        return (options ?? AIJsonSchemaCreateOptions.Default) with
        {
            TransformSchemaNode = (context, schema) =>
            {
                var convertedSchema = AcceptWhatTheConverterWrites(context, schema, convertedNodes);
                var transformedSchema = transformSchemaNode is null ? convertedSchema : transformSchemaNode(context, convertedSchema);
                if (!context.Path.IsEmpty)
                {
                    return transformedSchema;
                }

                // The exporter transforms the root last, once per schema it builds: per parameter of a tool's input.
                var resolvedSchema = ResolveRefsIntoConvertedNodes(transformedSchema, convertedNodes);
                convertedNodes.Clear();

                return resolvedSchema;
            }
        };
    }

    /// <summary>
    /// The boolean schema <c>true</c>, or <c>{"description": ...}</c> when the exporter gave the property a
    /// description, for a property of an object or collection type with a converter of its own; any other schema as
    /// it is. The schema it replaces is remembered for the <c>$ref</c>s that point into it.
    /// </summary>
    private static JsonNode AcceptWhatTheConverterWrites(AIJsonSchemaCreateContext context, JsonNode schema, List<ConvertedNode> convertedNodes)
    {
        if (context.PropertyInfo?.CustomConverter is null || context.TypeInfo.Kind == JsonTypeInfoKind.None)
        {
            return schema;
        }

        convertedNodes.Add(new ConvertedNode(context.Path.ToArray(), schema));

        if (schema is JsonObject schemaObject && schemaObject[JsonSchemaKeywords.Description] is { } description)
        {
            return new JsonObject { [JsonSchemaKeywords.Description] = description.DeepClone() };
        }

        return JsonValue.Create(true);
    }

    /// <summary>
    /// The exporter writes a node it meets again as a <c>$ref</c> to its first place, which can lie inside a property
    /// replaced above, leaving the pointer with nothing to point at; a validating client then rejects the schema. The
    /// first such <c>$ref</c> to a node gets the node's original schema in its place, so the later property is described
    /// as before, and every later one points at that copy, which also keeps a recursive type finite.
    /// </summary>
    private static JsonNode ResolveRefsIntoConvertedNodes(JsonNode root, List<ConvertedNode> convertedNodes)
    {
        if (convertedNodes.Count == 0)
        {
            return root;
        }

        var original = RestoreOriginal(root, convertedNodes);
        var references = CollectRefs(original).ToList();
        if (FindPointerPrefix(references, original) is not { } prefix)
        {
            return root;
        }

        var resolver = new RefResolver(original, prefix, convertedNodes.Select(x => x.Path).ToList());
        return resolver.Resolve(root);
    }

    /// <summary>
    /// A copy of the schema with every replaced property's original schema back in its place, in which each
    /// <c>$ref</c> resolves as the exporter wrote it. A property replaced inside another one is put back after it.
    /// </summary>
    private static JsonNode RestoreOriginal(JsonNode root, List<ConvertedNode> convertedNodes)
    {
        var original = root.DeepClone();
        foreach (var convertedNode in convertedNodes.OrderBy(x => x.Path.Length))
        {
            if (Resolve(original, convertedNode.Path[..^1]) is JsonObject parent)
            {
                parent[convertedNode.Path[^1]] = convertedNode.Schema.DeepClone();
            }
        }

        return original;
    }

    /// <summary>
    /// The segments every <c>$ref</c> starts with ahead of its place in this schema: none in a schema of its own, and
    /// <c>properties/&lt;name&gt;</c> in a tool parameter's, which the MCP SDK nests in the tool's input schema. It is the
    /// one under which every <c>$ref</c> resolves.
    /// </summary>
    private static string[]? FindPointerPrefix(List<string> references, JsonNode original)
    {
        var pointers = references.Select(ParsePointer).ToList();
        List<string[]> candidates = [[]];
        if (pointers.Count > 0 && pointers[0] is [PropertiesKeyword, var name, ..])
        {
            candidates.Add([PropertiesKeyword, name]);
        }

        return candidates.FirstOrDefault(prefix => pointers.All(pointer => StartsWith(pointer, prefix) && Resolve(original, pointer[prefix.Length..]) is not null));
    }

    private static IEnumerable<string> CollectRefs(JsonNode? node)
    {
        return node switch
        {
            JsonObject jsonObject => jsonObject.SelectMany(x =>
                x.Key == JsonSchemaKeywords.Ref && x.Value is JsonValue value && value.TryGetValue<string>(out var reference)
                    ? [reference]
                    : CollectRefs(x.Value)),
            JsonArray jsonArray => jsonArray.SelectMany(CollectRefs),
            _ => []
        };
    }

    private static JsonNode? Resolve(JsonNode? node, ReadOnlySpan<string> path)
    {
        foreach (var segment in path)
        {
            node = node switch
            {
                JsonObject jsonObject => jsonObject[segment],
                JsonArray jsonArray when int.TryParse(segment, out var index) && index >= 0 && index < jsonArray.Count => jsonArray[index],
                _ => null
            };
        }

        return node;
    }

    /// <summary>
    /// The segments of a JSON pointer such as <c>#/properties/a~1b</c>, unescaped as RFC 6901 says.
    /// </summary>
    private static string[] ParsePointer(string reference)
    {
        return reference == PointerRoot
            ? []
            : reference[(PointerRoot.Length + 1)..].Split(PointerSeparator).Select(x => x.Replace("~1", "/").Replace("~0", "~")).ToArray();
    }

    private static string FormatPointer(IEnumerable<string> path)
    {
        return PointerRoot + string.Concat(path.Select(x => PointerSeparator + x.Replace("~", "~0").Replace("/", "~1")));
    }

    private static bool StartsWith(string[] path, string[] prefix)
    {
        return path.Length >= prefix.Length && path.AsSpan(0, prefix.Length).SequenceEqual(prefix);
    }

    private sealed record ConvertedNode(string[] Path, JsonNode Schema);

    /// <summary>
    /// Walks the schema and replaces each <c>$ref</c> into a replaced property, moving on into what it put in its place.
    /// </summary>
    private sealed class RefResolver(JsonNode original, string[] prefix, List<string[]> convertedPaths)
    {
        /// <summary>
        /// Where a node of the original schema now lives in the resolved one, by its place in the original.
        /// </summary>
        private readonly List<(string[] From, string[] To)> _copies = [];

        public JsonNode Resolve(JsonNode root)
        {
            return Visit(root, []);
        }

        private JsonNode Visit(JsonNode node, string[] location)
        {
            if (node is JsonObject jsonObject && jsonObject[JsonSchemaKeywords.Ref] is JsonValue value && value.TryGetValue<string>(out var reference))
            {
                var pointer = ParsePointer(reference);
                if (StartsWith(pointer, prefix) && Replace(jsonObject, pointer[prefix.Length..], location) is { } replacement)
                {
                    return Visit(replacement, location);
                }

                return node;
            }

            if (node is JsonObject children)
            {
                foreach (var (name, child) in children.ToList())
                {
                    if (child is not null)
                    {
                        children[name] = Detach(Visit(child, [.. location, name]));
                    }
                }
            }
            else if (node is JsonArray items)
            {
                for (var index = 0; index < items.Count; index++)
                {
                    if (items[index] is { } item)
                    {
                        items[index] = Detach(Visit(item, [.. location, index.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
                    }
                }
            }

            return node;
        }

        /// <summary>
        /// What a <c>$ref</c> to <paramref name="target"/> becomes: a pointer to the copy of the node when one was already
        /// made, a copy of its original schema when it lies inside a replaced property, and nothing to change otherwise.
        /// </summary>
        private JsonNode? Replace(JsonObject reference, string[] target, string[] location)
        {
            foreach (var (from, to) in _copies)
            {
                if (StartsWith(target, from))
                {
                    reference[JsonSchemaKeywords.Ref] = FormatPointer([.. prefix, .. to, .. target[from.Length..]]);
                    return null;
                }
            }

            if (!convertedPaths.Any(x => StartsWith(target, x)))
            {
                return null;
            }

            _copies.Add((target, location));
            var copy = ConvertedPropertySchema.Resolve(original, target)?.DeepClone() ?? JsonValue.Create(true);

            // A keyword the $ref carries beside the pointer fills in what the copy lacks; its property's own description
            // replaces the copy's. Others it has only as a placeholder, such as the "items": {} beside an array's $ref.
            var siblings = reference.Where(x => x.Key != JsonSchemaKeywords.Ref).ToList();
            if (siblings.Count == 0)
            {
                return copy;
            }

            var merged = copy as JsonObject ?? [];
            foreach (var (name, sibling) in siblings)
            {
                if (name == JsonSchemaKeywords.Description || !merged.ContainsKey(name))
                {
                    merged[name] = sibling?.DeepClone();
                }
            }

            return merged;
        }

        private static JsonNode Detach(JsonNode node)
        {
            return node.Parent is null ? node : node.DeepClone();
        }
    }
}
