using Abstractions;
using ConsoleApp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System.Text.Json;

var config = new ConfigurationBuilder()
	.AddUserSecrets("3954f089-ad89-457b-bcc2-8f88c29c6211")
	.Build();

var options = new ApplicationImageBackup.Options();
config.Bind(options);

Log.Logger = new LoggerConfiguration()	
	.MinimumLevel.Debug()
	.WriteTo.Console()
	.WriteTo.File(LogFile(options.LocalPath), rollingInterval: RollingInterval.Day)
	.CreateLogger();

var services = new ServiceCollection()
	.AddLogging(config => config.AddSerilog(Log.Logger))
	.AddSingleton<ApplicationImageBackup>()
	.Configure<ApplicationImageBackup.Options>(config.Bind)
	.BuildServiceProvider();

var backup = services.GetRequiredService<ApplicationImageBackup>();
var result = await backup.ExecuteAsync(default);

await backup.SaveResultAsync(result);

static string LogFile(string path) => Path.Combine(path, "serilog.txt");

