using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace NativeEndpoints;

/// <summary>Binds a request contract from an incoming request.</summary>
/// <remarks>
/// The seam between the reflective binder and a generated one. Both produce an
/// <see cref="EndpointBindingResult{T}"/> with identical semantics; only how they get there differs.
/// </remarks>
public delegate ValueTask<EndpointBindingResult<T>> EndpointBinder<T>(
    HttpContext context,
    JsonSerializerOptions jsonOptions,
    EndpointBodyMode bodyMode,
    EndpointValueBinders? valueBinders);
