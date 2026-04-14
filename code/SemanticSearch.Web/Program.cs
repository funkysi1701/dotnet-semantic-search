using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SemanticSearch.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBase = builder.Configuration["ApiBaseUrl"];
var baseUri = string.IsNullOrWhiteSpace(apiBase)
    ? builder.HostEnvironment.BaseAddress
    : apiBase.TrimEnd('/') + "/";

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(baseUri) });

await builder.Build().RunAsync();
