namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Annotation a canonical parse leaves on each element and attribute: the qualified name as written and, on elements,
/// the namespace declarations the start tag made, in the order they bound.
/// </summary>
internal sealed record XmlWrittenName(string QualifiedName, IReadOnlyList<XmlNamespaceDeclaration> Declarations);
