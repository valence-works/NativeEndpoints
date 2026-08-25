namespace NativeEndpoints;

/// <summary>
/// Declares that this assembly registers a value binder for <see cref="Type"/>.
/// </summary>
/// <remarks>
/// Value binders are registered at runtime, where a compile-time analyzer cannot see them. Without
/// this the generator would report a contract using a registered type as unbindable, which is a false
/// warning, and a fatal one in a project treating warnings as errors.
/// <para>
/// It carries no runtime behavior. Registration still happens through
/// <c>AddNativeEndpoints(o =&gt; o.ValueBinders.Add&lt;T&gt;(...))</c>; this only tells the build what
/// that call is going to do.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [assembly: EndpointValueBinder(typeof(Money))]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class EndpointValueBinderAttribute(Type type) : Attribute
{
    /// <summary>The type a registered parser produces.</summary>
    public Type Type { get; } = type;
}
