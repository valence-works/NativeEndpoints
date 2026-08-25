// Records compile to init-only setters, which the compiler expects this marker for. netstandard2.0
// predates it, and analyzers must target netstandard2.0 to load into the compiler.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit;
