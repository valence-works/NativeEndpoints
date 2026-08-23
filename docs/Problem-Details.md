# Problem Details

Three seams, tried in order. Each is optional, and each can be registered globally or keyed to one
group.

1. **`IEndpointFaultRenderer`** — owns a failure family end to end and writes the whole response.
2. **`IEndpointExceptionTranslator`** — turns an exception into a status and a set of messages.
3. **`IEndpointProblemWriter`** — decides the wire shape of whatever the translator produced.

Anything unhandled becomes a 500 and is logged with the contract type that failed.

## Out of the box

`AddNativeEndpoints()` registers a writer that emits RFC 9457 `ProblemDetails` through ASP.NET Core's
`IProblemDetailsService`, with each error key becoming an extension member. A host that configures
nothing still returns a sane body.

## Translating exceptions

The mapping from exception to status is domain knowledge, so it lives with the code that owns the
exception rather than in a global filter every part of the application has to agree on.

```csharp
public sealed class BillingExceptionTranslator : IEndpointExceptionTranslator
{
    public EndpointProblem? Translate(Exception exception) => exception switch
    {
        InvoiceNotFoundException      => EndpointProblem.General(404, "Invoice not found"),
        InvoiceLockedException e      => EndpointProblem.General(409, e.Message),
        InvoiceValidationException e  => new(422, e.ErrorsByField),
        _ => null
    };
}
```

```csharp
builder.Services.AddSingleton<IEndpointExceptionTranslator, BillingExceptionTranslator>();
```

Translators are consulted in registration order and the first non-null result wins. Returning null
means "not mine", so several translators can coexist without knowing about each other.

`EndpointProblem` is a status code and a dictionary of keyed messages.
`EndpointProblem.General(status, message)` puts a single message under `generalErrors`; pass a
different key when the failure belongs to a specific field.

## Owning the wire shape

The shape of an error is part of a published contract, so replace the writer rather than inheriting
one that might change under you:

```csharp
public sealed class BillingProblemWriter : IEndpointProblemWriter
{
    public Task WriteAsync(HttpContext context, EndpointProblem problem)
    {
        context.Response.StatusCode = problem.StatusCode;
        return context.Response.WriteAsJsonAsync(new { errors = problem.Errors });
    }
}
```

## Structured failure contracts

Some failures carry payloads that do not reduce to a status and a set of messages: diagnostic arrays,
stable problem-type URIs, per-endpoint titles. A fault renderer inspects the exception, and any
endpoint metadata that scopes it through `HttpContext.GetEndpoint()`, and writes the complete
response itself.

```csharp
public sealed class DraftFaultRenderer : IEndpointFaultRenderer
{
    public async ValueTask<bool> TryWriteAsync(HttpContext context, Exception exception)
    {
        if (exception is not DraftHasValidationErrorsException failure)
            return false;   // not ours; let translation proceed

        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new
        {
            type = "https://example.com/problems/draft-invalid",
            errors = failure.GroupedErrors
        });
        return true;
    }
}
```

Renderers run before translators. Returning `false` costs nothing and lets the next seam try.

## Keyed registration

In a host composing several groups, register keyed by the group name. The pipeline looks for a keyed
registration first and falls back to the unkeyed one, so a single-group host does not need keys at
all.

```csharp
builder.Services.AddKeyedSingleton<IEndpointProblemWriter, BillingProblemWriter>("Billing");
builder.Services.AddKeyedSingleton<IEndpointProblemWriter, ShippingProblemWriter>("Shipping");
```

## Cancellation

`OperationCanceledException` is rethrown rather than translated. A cancelled request is not a failure
to report, and turning it into a 500 would bury real errors under client disconnects.
