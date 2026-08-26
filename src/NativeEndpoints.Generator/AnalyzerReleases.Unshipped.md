; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------------------------------------------------------------------
NE0001  | NativeEndpoints | Warning | Endpoint declares no route
NE0002  | NativeEndpoints | Warning | Contract parameter type cannot be bound
NE0003  | NativeEndpoints | Warning | Configure reads constructor-injected state
NE0004  | NativeEndpoints | Warning | Contract has more than one public constructor
NE0005  | NativeEndpoints | Warning | Endpoint derives from ApiEndpointBase directly
NE0006  | NativeEndpoints | Warning | Contract binds a form on a method with no request body
