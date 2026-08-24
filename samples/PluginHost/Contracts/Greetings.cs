namespace PluginHost.Contracts;

/// <summary>Bound from the route.</summary>
public sealed record GetGreeting(string Name);

/// <summary>The response body.</summary>
public sealed record GreetingView(string Message, string ServedBy);

/// <summary>A list of the greetings a plugin can serve.</summary>
public sealed record GreetingListView(IReadOnlyList<string> Styles, string ServedBy);
