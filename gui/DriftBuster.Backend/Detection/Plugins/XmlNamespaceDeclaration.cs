using System.Runtime.InteropServices;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>A namespace declaration as written on a start tag (or defaulted by the DTD): <c>xmlns</c> when the prefix is empty.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct XmlNamespaceDeclaration(string Prefix, string Uri);
