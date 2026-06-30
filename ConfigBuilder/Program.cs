using System.Reflection;
using System.Text.Json;

var included = new HashSet<string> { "BTCPayServer.Plugins.Tando" };
var plugins = Directory.GetDirectories("../../../../Plugins").Where(p => included.Contains(Path.GetFileName(p)));

var p = "";
foreach (var plugin in plugins)
{
    var assemblyConfigurationAttribute = typeof(Program).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>();
    var buildConfigurationName = assemblyConfigurationAttribute?.Configuration;

    p += $"{Path.GetFullPath(plugin)}/bin/{buildConfigurationName}/net10.0/{Path.GetFileName(plugin)}.dll;";
}

var content = JsonSerializer.Serialize(new
{
    DEBUG_PLUGINS = p
});

Console.WriteLine(content);
await File.WriteAllTextAsync("../../../../btcpayserver/BTCPayServer/appsettings.dev.json", content);