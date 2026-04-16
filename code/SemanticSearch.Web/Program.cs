using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SemanticSearch.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

ConfigureServices(builder.Services, builder.HostEnvironment, builder.Configuration);

await builder.Build().RunAsync();

static void ConfigureServices(
    IServiceCollection services,
    IWebAssemblyHostEnvironment hostEnvironment,
    IConfiguration configuration)
{
    var apiBase = configuration["ApiBaseUrl"];
    var baseUri = string.IsNullOrWhiteSpace(apiBase)
        ? hostEnvironment.BaseAddress
        : apiBase.TrimEnd('/') + "/";

    services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(baseUri) });
}
