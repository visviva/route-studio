using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using SCFarmingPathsConverter;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddSingleton(services =>
{
    var js = (IJSInProcessRuntime)services.GetRequiredService<IJSRuntime>();
    try
    {
        return new TextCatalog(js.Invoke<Dictionary<string, string>>("routeStudio.getMessages"));
    }
    catch (JSException error)
    {
        throw new InvalidOperationException("Could not load the Route Studio text catalog.", error);
    }
});

await builder.Build().RunAsync();
