using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PluginHost.Host;

/// <summary>
/// A route builder that collects endpoints instead of publishing them.
/// </summary>
/// <remarks>
/// Lets the host map a plugin's endpoints with the ordinary <c>MapEndpointGroup</c> call and then
/// decide what to do with the result, rather than having them appear in the application's route
/// table immediately.
/// </remarks>
internal sealed class EndpointCollector(IServiceProvider services) : IEndpointRouteBuilder
{
    public IServiceProvider ServiceProvider { get; } = services;

    public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

    public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);

    public IReadOnlyList<Endpoint> Build() => DataSources.SelectMany(source => source.Endpoints).ToArray();
}
