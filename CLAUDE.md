# CLAUDE.md

Guidance for Claude Code when working in this repository.

## No magic values

Never write a bare literal that names a concept: an HTTP method, an attribute or property name read by name, a header,
a route token, a JSON property, a status code. Give it a name.

- **Look for an existing name first.** Grep this repo for a constants class that already holds it, then check the
  framework (`HttpMethods`, `HeaderNames`, `StatusCodes`, `MediaTypeNames`, ...).
- **The generator cannot use ASP.NET Core's constants.** `Api.ToMcp.Generator` targets netstandard2.0 and runs inside
  the compiler, so it references only Roslyn and the BCL. ASP.NET Core's names are also often `static readonly`, which
  cannot be `switch` cases or patterns anyway. Mirror the names as `const` in a generator-internal class, the way
  `HttpMethodNames` mirrors `HttpMethods` and `McpScopeModel` mirrors `McpScope`.
- **Surrounding code that uses literals is not permission to add more.** If the code you touch already compares the
  same kind of value as literals, move all of them to the constants in the same change instead of matching the style.
- **`nameof` only for symbols the project can reference.** A name of a member the generator cannot reference, such as
  the properties of `McpExposeAttribute` it reads by name, is a `const` beside its use.

Not magic values: the C# source the emitter writes (`"await invoker.GetAsync(route, cancellationToken)"`), test inputs
and expected output, and messages meant for people (diagnostics, exceptions).
