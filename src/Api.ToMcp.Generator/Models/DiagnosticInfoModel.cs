using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Api.ToMcp.Generator.Models
{
    /// <summary>
    /// A diagnostic to report, with the places it points at as plain values. The run that reports it turns them into
    /// locations with its own compilation, as one in source has to point into that compilation's syntax tree of its file.
    /// </summary>
    internal sealed record DiagnosticInfoModel
    {
        private DiagnosticInfoModel(DiagnosticDescriptor descriptor, EquatableArray<object?> messageArguments)
        {
            Descriptor = descriptor;
            MessageArguments = messageArguments;
        }

        public DiagnosticDescriptor Descriptor { get; }

        /// <summary>
        /// The places in source the diagnostic points at: the first is its location, the others are additional ones.
        /// </summary>
        public EquatableArray<SourceLocationModel> SourceLocations { get; private init; } = EquatableArray<SourceLocationModel>.Empty;

        /// <summary>
        /// The place in a file outside the compilation the diagnostic points at, such as generator.json, or null.
        /// </summary>
        public FileLocationModel? FileLocation { get; private init; }

        public EquatableArray<object?> MessageArguments { get; }

        /// <summary>
        /// A diagnostic at the places in source, or at none when no place is given, as for an action of a referenced
        /// assembly.
        /// </summary>
        public static DiagnosticInfoModel InSource(DiagnosticDescriptor descriptor, IEnumerable<SourceLocationModel?> locations, params object?[] messageArguments)
        {
            return new DiagnosticInfoModel(descriptor, messageArguments.ToImmutableArray())
            {
                SourceLocations = locations.OfType<SourceLocationModel>().ToImmutableArray()
            };
        }

        /// <summary>
        /// A diagnostic at the span of the file, or at none when there is no file, as for the default config of a project
        /// without a generator.json.
        /// </summary>
        public static DiagnosticInfoModel InFile(DiagnosticDescriptor descriptor, string? filePath, LinePositionSpan span, params object?[] messageArguments)
        {
            return new DiagnosticInfoModel(descriptor, messageArguments.ToImmutableArray())
            {
                FileLocation = filePath == null ? null : new FileLocationModel { FilePath = filePath, LineSpan = span }
            };
        }

        public Diagnostic ToDiagnostic(Compilation compilation)
        {
            if (FileLocation != null)
            {
                return Diagnostic.Create(Descriptor, FileLocation.ToLocation(), MessageArguments.ToArray());
            }

            // Each place once: the tools of the controllers that inherit an action share the place of the base's action.
            var locations = SourceLocations.Select(x => x.ToLocation(compilation)).Distinct().ToList();

            return Diagnostic.Create(Descriptor, locations.FirstOrDefault() ?? Location.None, locations.Skip(1), MessageArguments.ToArray());
        }
    }

    /// <summary>
    /// A place in a file outside the compilation, such as generator.json, as plain values.
    /// </summary>
    internal sealed record FileLocationModel
    {
        public string FilePath { get; init; } = "";
        public LinePositionSpan LineSpan { get; init; }

        public Location ToLocation()
        {
            return Location.Create(FilePath, default, LineSpan);
        }
    }
}
